[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $Repository = 'Makmatoe/SessionDock',

    [string] $ReleaseReviewer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$announcementAuditFailures = [Collections.Generic.List[string]]::new()

function Add-AnnouncementAuditFailure {
    param([Parameter(Mandatory)] [string] $Message)
    $script:announcementAuditFailures.Add($Message)
    Write-Warning $Message
}

function Invoke-GhJson {
    param([Parameter(Mandatory)] [string[]] $Arguments)
    $output = @(& gh @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub API request failed: gh $($Arguments -join ' ')`n$($output -join [Environment]::NewLine)"
    }
    if ($output.Count -eq 0) { return $null }
    return ($output -join [Environment]::NewLine) | ConvertFrom-Json
}

function Get-GhPagedItems {
    param(
        [Parameter(Mandatory)] [string] $Endpoint,
        [Parameter(Mandatory)] [string] $CollectionProperty,
        [ValidateRange(1, 100)] [int] $PageSize = 100
    )

    $items = @()
    for ($page = 1; $page -le 100; $page++) {
        $response = Invoke-GhJson @(
            'api', "${Endpoint}?per_page=$PageSize&page=$page")
        $property = $response.PSObject.Properties[$CollectionProperty]
        if ($null -eq $property) {
            throw "GitHub API response from '$Endpoint' omitted '$CollectionProperty'."
        }

        $pageItems = @($property.Value)
        $items += $pageItems
        if ($pageItems.Count -lt $PageSize) {
            return $items
        }
    }

    throw "GitHub API pagination for '$Endpoint' exceeded 100 pages."
}

function Invoke-GhMutation {
    param(
        [Parameter(Mandatory)] [string] $Description,
        [Parameter(Mandatory)] [string[]] $Arguments
    )
    if ($PSCmdlet.ShouldProcess($Repository, $Description)) {
        $output = @(& gh @Arguments 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw "$Description failed:`n$($output -join [Environment]::NewLine)"
        }
    }
}

& gh auth status | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'GitHub CLI authentication is required.' }
$repositoryState = Invoke-GhJson @('api', "repos/$Repository")
if (-not $repositoryState.permissions.admin) {
    throw "The current GitHub token is not an administrator of $Repository. Re-run this script with an admin-authenticated gh session."
}

Invoke-GhMutation 'enable vulnerability alerts' @(
    'api', '--method', 'PUT', "repos/$Repository/vulnerability-alerts")
Invoke-GhMutation 'enable automated dependency security updates' @(
    'api', '--method', 'PUT', "repos/$Repository/automated-security-fixes")
Invoke-GhMutation 'enable secret scanning and push protection' @(
    'api', '--method', 'PATCH', "repos/$Repository",
    '-f', 'security_and_analysis[secret_scanning][status]=enabled',
    '-f', 'security_and_analysis[secret_scanning_push_protection][status]=enabled')
Invoke-GhMutation 'enforce read-only default workflow permissions' @(
    'api', '--method', 'PUT', "repos/$Repository/actions/permissions/workflow",
    '-f', 'default_workflow_permissions=read',
    '-F', 'can_approve_pull_request_reviews=false')

$actions = Invoke-GhJson @('api', "repos/$Repository/actions/permissions")
$selected = Invoke-GhJson @(
    'api', "repos/$Repository/actions/permissions/selected-actions")
$obsoleteActionPatterns = @(
    ('Azure/artifact-' + 'signing-action@*'),
    ('Azure/' + 'login@*'))
$patterns = @($selected.patterns_allowed | Where-Object {
        $_ -notin $obsoleteActionPatterns
    } | Sort-Object -Unique)
$patternsChanged = @($selected.patterns_allowed).Count -ne $patterns.Count
if (-not $actions.sha_pinning_required) {
    Invoke-GhMutation 'require full commit SHA pinning for every Action' @(
        'api', '--method', 'PUT', "repos/$Repository/actions/permissions",
        '-F', 'enabled=true',
        '-f', 'allowed_actions=selected',
        '-F', 'sha_pinning_required=true')
}
if ($patternsChanged) {
    if ($PSCmdlet.ShouldProcess(
            $Repository,
            'remove unused managed signing Action repositories')) {
        $body = @{
            github_owned_allowed = [bool] $selected.github_owned_allowed
            verified_allowed = [bool] $selected.verified_allowed
            patterns_allowed = [string[]] $patterns
        } | ConvertTo-Json -Compress
        $output = @($body | & gh api --method PUT `
            "repos/$Repository/actions/permissions/selected-actions" `
            --input - 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw "Removing unused managed signing Action repositories failed:`n$($output -join [Environment]::NewLine)"
        }
    }
}

$rulesets = @(Invoke-GhJson @('api', "repos/$Repository/rulesets"))
$mainRuleset = @($rulesets | Where-Object {
        $_.target -eq 'branch' -and $_.name -eq 'Protect main'
    })
$tagRuleset = @($rulesets | Where-Object {
        $_.target -eq 'tag' -and $_.name -eq 'Protect release tags'
    })
if ($mainRuleset.Count -ne 1) {
    Add-AnnouncementAuditFailure 'Create exactly one active main ruleset requiring pull requests, resolved conversations, strict current status checks, and blocking deletion/non-fast-forward updates. No ruleset was invented because required check names and bypass actors are repository-specific.'
}
elseif ($mainRuleset[0].enforcement -cne 'active') {
    Add-AnnouncementAuditFailure "Ruleset 'Protect main' must have active enforcement before a release can proceed."
}
if ($tagRuleset.Count -ne 1) {
    Add-AnnouncementAuditFailure 'Create exactly one active refs/tags/v* ruleset blocking creation/update/deletion by unauthorized actors. No bypass actor was chosen automatically.'
}
elseif ($tagRuleset[0].enforcement -cne 'active') {
    Add-AnnouncementAuditFailure "Ruleset 'Protect release tags' must have active enforcement before a release can proceed."
}

$environments = @(Get-GhPagedItems `
        -Endpoint "repos/$Repository/environments" `
        -CollectionProperty 'environments')
foreach ($name in @('release', 'release-publication')) {
    $environment = @($environments | Where-Object { $_.name -ceq $name })
    if ($environment.Count -ne 1) {
        Add-AnnouncementAuditFailure "Create exactly one GitHub environment named '$name' before releasing."
        continue
    }

    $reviewerRules = @($environment[0].protection_rules | Where-Object {
            $_.type -eq 'required_reviewers'
        })
    if ($reviewerRules.Count -ne 1 -or
        @($reviewerRules[0].reviewers).Count -lt 1) {
        $reviewerGuidance = if ([string]::IsNullOrWhiteSpace($ReleaseReviewer)) {
            'Re-run with -ReleaseReviewer <GitHub-login> for owner-facing guidance; this audit will never choose or install a reviewer automatically.'
        }
        else {
            "Reviewer '$ReleaseReviewer' was supplied for guidance, but user/team IDs and self-review policy must still be confirmed by the repository owner in GitHub."
        }
        Add-AnnouncementAuditFailure "Environment '$name' must have exactly one required-reviewers protection rule with at least one explicit reviewer. $reviewerGuidance"
    }
}

$announcementEnvironmentName = 'release-announcement'
$announcementEnvironment = @($environments | Where-Object {
        $_.name -ceq $announcementEnvironmentName
    })
if ($announcementEnvironment.Count -ne 1) {
    Add-AnnouncementAuditFailure "Create the '$announcementEnvironmentName' environment, restrict it to the custom tag pattern v*, add secret DISCORD_RELEASE_BOT_TOKEN, and add variables DISCORD_RELEASE_BOT_ID, DISCORD_RELEASE_CHANNEL_ID, and DISCORD_RELEASE_ROLE_ID. This audit never creates environments or copies secret values."
}
else {
    $announcementEndpoint =
        "repos/$Repository/environments/$announcementEnvironmentName"
    $announcementState = Invoke-GhJson @('api', $announcementEndpoint)

    $reviewerRules = @($announcementState.protection_rules | Where-Object {
            $_.type -eq 'required_reviewers'
        })
    if ($reviewerRules.Count -ne 0) {
        Add-AnnouncementAuditFailure "Environment '$announcementEnvironmentName' must not require a reviewer. The separate release-publication environment is the human gate; a reviewer here would make the Discord post manual."
    }

    $deploymentPolicy = $announcementState.deployment_branch_policy
    $usesExactCustomPolicyMode = $null -ne $deploymentPolicy -and
        [bool] $deploymentPolicy.custom_branch_policies -and
        -not [bool] $deploymentPolicy.protected_branches
    if (-not $usesExactCustomPolicyMode) {
        Add-AnnouncementAuditFailure "Environment '$announcementEnvironmentName' must use custom deployment branch/tag policies, not unrestricted deployments or the generic protected-branches mode."
    }

    $deploymentPolicies = @()
    if ($usesExactCustomPolicyMode) {
        $deploymentPolicies = @(Get-GhPagedItems `
                -Endpoint "$announcementEndpoint/deployment-branch-policies" `
                -CollectionProperty 'branch_policies')
    }
    $policyTypeProperty = if ($deploymentPolicies.Count -eq 1) {
        $deploymentPolicies[0].PSObject.Properties['type']
    }
    else {
        $null
    }
    $policyType = if ($null -ne $policyTypeProperty) {
        $policyTypeProperty.Value
    }
    else {
        $null
    }
    $hasOnlyExpectedTagPolicy = $deploymentPolicies.Count -eq 1 -and
        $policyType -ceq 'tag' -and
        $deploymentPolicies[0].name -ceq 'v*'
    if (-not $hasOnlyExpectedTagPolicy) {
        Add-AnnouncementAuditFailure "Environment '$announcementEnvironmentName' must have exactly one custom deployment policy: type 'tag' with pattern 'v*'. Environment eligibility does not replace the repository's separate protected-tag ruleset."
    }

    $environmentSecrets = @(Get-GhPagedItems `
            -Endpoint "$announcementEndpoint/secrets" `
            -CollectionProperty 'secrets')
    $secretNames = @($environmentSecrets | ForEach-Object { $_.name })
    $expectedSecretName = 'DISCORD_RELEASE_BOT_TOKEN'
    if ($secretNames -cnotcontains $expectedSecretName) {
        Add-AnnouncementAuditFailure "Environment '$announcementEnvironmentName' is missing environment-scoped secret DISCORD_RELEASE_BOT_TOKEN. GitHub may otherwise resolve a same-named repository or organization secret, which the workflow cannot distinguish at runtime."
    }
    $unexpectedSecretNames = @($secretNames | Where-Object {
            $_ -cne $expectedSecretName
        })
    if ($unexpectedSecretNames.Count -ne 0) {
        Add-AnnouncementAuditFailure "Environment '$announcementEnvironmentName' has unexpected secret names: $($unexpectedSecretNames -join ', '). Keep the automatic sender environment least-privileged."
    }

    $environmentVariables = @(Get-GhPagedItems `
            -Endpoint "$announcementEndpoint/variables" `
            -CollectionProperty 'variables' `
            -PageSize 30)
    $variableNames = @($environmentVariables | ForEach-Object { $_.name })
    $expectedVariableNames = @(
        'DISCORD_RELEASE_BOT_ID',
        'DISCORD_RELEASE_CHANNEL_ID',
        'DISCORD_RELEASE_ROLE_ID')
    foreach ($requiredVariable in $expectedVariableNames) {
        if ($variableNames -cnotcontains $requiredVariable) {
            Add-AnnouncementAuditFailure "Environment '$announcementEnvironmentName' is missing environment-scoped variable $requiredVariable. GitHub may otherwise resolve a same-named repository or organization variable, which the workflow cannot distinguish at runtime."
        }
    }
    $unexpectedVariableNames = @($variableNames | Where-Object {
            $expectedVariableNames -cnotcontains $_
        })
    if ($unexpectedVariableNames.Count -ne 0) {
        Add-AnnouncementAuditFailure "Environment '$announcementEnvironmentName' has unexpected variable names: $($unexpectedVariableNames -join ', '). Keep the automatic sender environment least-privileged."
    }
}

$releaseEnvironment = @($environments | Where-Object { $_.name -ceq 'release' })
if ($releaseEnvironment.Count -eq 1) {
    $releaseEnvironmentEndpoint = "repos/$Repository/environments/release"
    $releaseSecrets = @(Get-GhPagedItems `
            -Endpoint "$releaseEnvironmentEndpoint/secrets" `
            -CollectionProperty 'secrets')
    $releaseSecretNames = @($releaseSecrets | ForEach-Object { $_.name })
    $expectedReleaseSecretNames = @(
        'UPDATE_SIGNING_PRIVATE_KEY_PKCS8_BASE64')
    $expectedReleaseSecretFingerprint =
        ($expectedReleaseSecretNames | Sort-Object) -join "`n"
    $releaseSecretFingerprint =
        ($releaseSecretNames | Sort-Object) -join "`n"
    if ($expectedReleaseSecretFingerprint -cne $releaseSecretFingerprint) {
        Add-AnnouncementAuditFailure "Environment 'release' must contain exactly the update-descriptor key secret: $($expectedReleaseSecretNames -join ', '). Remove every obsolete Azure signing secret."
    }
    if (@($releaseSecrets | Where-Object {
                $_.name -ceq 'DISCORD_RELEASE_BOT_TOKEN'
            }).Count -ne 0) {
        Add-AnnouncementAuditFailure "Remove legacy DISCORD_RELEASE_BOT_TOKEN from the 'release' environment after the credential is re-entered on 'release-announcement'."
    }
    $releaseVariables = @(Get-GhPagedItems `
            -Endpoint "$releaseEnvironmentEndpoint/variables" `
            -CollectionProperty 'variables' `
            -PageSize 30)
    $legacyDiscordVariableNames = @(
        'DISCORD_RELEASE_BOT_ID',
        'DISCORD_RELEASE_CHANNEL_ID',
        'DISCORD_RELEASE_ROLE_ID')
    $foundLegacyVariables = @($releaseVariables | Where-Object {
            $legacyDiscordVariableNames -ccontains $_.name
        } | ForEach-Object { $_.name })
    if ($foundLegacyVariables.Count -ne 0) {
        Add-AnnouncementAuditFailure "Remove legacy Discord variables from the 'release' environment after migration: $($foundLegacyVariables -join ', ')."
    }
    $releaseVariableNames = @($releaseVariables | ForEach-Object { $_.name })
    if ($releaseVariableNames.Count -ne 0) {
        Add-AnnouncementAuditFailure "Environment 'release' must contain no variables. Remove obsolete signing variables: $($releaseVariableNames -join ', ')."
    }
}

$repositorySecrets = @(Get-GhPagedItems `
        -Endpoint "repos/$Repository/actions/secrets" `
        -CollectionProperty 'secrets')
$repositorySecretNames = @($repositorySecrets | ForEach-Object { $_.name })
if ($repositorySecretNames -ccontains 'DISCORD_RELEASE_BOT_TOKEN') {
    Add-AnnouncementAuditFailure 'Remove repository-scoped secret DISCORD_RELEASE_BOT_TOKEN after the environment-scoped credential is configured. Its presence permits fallback when the environment-scoped name is missing.'
}
foreach ($releaseSecretName in @(
        'AZURE_CLIENT_ID',
        'AZURE_SUBSCRIPTION_ID',
        'AZURE_TENANT_ID',
        'UPDATE_SIGNING_PRIVATE_KEY_PKCS8_BASE64')) {
    if ($repositorySecretNames -ccontains $releaseSecretName) {
        Add-AnnouncementAuditFailure "Remove repository-scoped secret $releaseSecretName. The update-descriptor key belongs only in the release environment, and Azure signing configuration is obsolete."
    }
}

$repositoryVariables = @(Get-GhPagedItems `
        -Endpoint "repos/$Repository/actions/variables" `
        -CollectionProperty 'variables' `
        -PageSize 30)
$repositoryVariableNames = @($repositoryVariables | ForEach-Object { $_.name })
foreach ($announcementVariableName in @(
        'DISCORD_RELEASE_BOT_ID',
        'DISCORD_RELEASE_CHANNEL_ID',
        'DISCORD_RELEASE_ROLE_ID')) {
    if ($repositoryVariableNames -ccontains $announcementVariableName) {
        Add-AnnouncementAuditFailure "Remove repository-scoped variable $announcementVariableName after the environment-scoped value is configured. Its presence permits fallback when the environment-scoped name is missing."
    }
}
foreach ($releaseVariableName in @(
        'ARTIFACT_SIGNING_ACCOUNT_NAME',
        'ARTIFACT_SIGNING_CERTIFICATE_PROFILE_NAME',
        'ARTIFACT_SIGNING_ENDPOINT',
        'ARTIFACT_SIGNING_PUBLISHER_SUBJECT')) {
    if ($repositoryVariableNames -ccontains $releaseVariableName) {
        Add-AnnouncementAuditFailure "Remove obsolete repository-scoped signing variable $releaseVariableName."
    }
}

if ($repositoryState.owner.type -ceq 'Organization') {
    $organizationSecrets = @(Get-GhPagedItems `
            -Endpoint "repos/$Repository/actions/organization-secrets" `
            -CollectionProperty 'secrets')
    $organizationSecretNames = @($organizationSecrets | ForEach-Object {
            $_.name
        })
    if ($organizationSecretNames -ccontains 'DISCORD_RELEASE_BOT_TOKEN') {
        Add-AnnouncementAuditFailure 'Remove organization-scoped secret DISCORD_RELEASE_BOT_TOKEN access for this repository after the environment-scoped credential is configured. Its presence permits fallback when lower scopes are missing.'
    }
    foreach ($releaseSecretName in @(
            'AZURE_CLIENT_ID',
            'AZURE_SUBSCRIPTION_ID',
            'AZURE_TENANT_ID',
            'UPDATE_SIGNING_PRIVATE_KEY_PKCS8_BASE64')) {
        if ($organizationSecretNames -ccontains $releaseSecretName) {
            Add-AnnouncementAuditFailure "Remove organization-scoped secret $releaseSecretName access for this repository. The update-descriptor key belongs only in the release environment, and Azure signing configuration is obsolete."
        }
    }

    $organizationVariables = @(Get-GhPagedItems `
            -Endpoint "repos/$Repository/actions/organization-variables" `
            -CollectionProperty 'variables' `
            -PageSize 30)
    $organizationVariableNames = @($organizationVariables | ForEach-Object {
            $_.name
        })
    foreach ($announcementVariableName in @(
            'DISCORD_RELEASE_BOT_ID',
            'DISCORD_RELEASE_CHANNEL_ID',
            'DISCORD_RELEASE_ROLE_ID')) {
        if ($organizationVariableNames -ccontains $announcementVariableName) {
            Add-AnnouncementAuditFailure "Remove organization-scoped variable $announcementVariableName access for this repository after the environment-scoped value is configured. Its presence permits fallback when lower scopes are missing."
        }
    }
    foreach ($releaseVariableName in @(
            'ARTIFACT_SIGNING_ACCOUNT_NAME',
            'ARTIFACT_SIGNING_CERTIFICATE_PROFILE_NAME',
            'ARTIFACT_SIGNING_ENDPOINT',
            'ARTIFACT_SIGNING_PUBLISHER_SUBJECT')) {
        if ($organizationVariableNames -ccontains $releaseVariableName) {
            Add-AnnouncementAuditFailure "Remove obsolete organization-scoped signing variable $releaseVariableName access for this repository."
        }
    }
}

if ($announcementAuditFailures.Count -ne 0) {
    throw "GitHub protected release-environment configuration audit failed with $($announcementAuditFailures.Count) blocking issue(s)."
}

Write-Host 'GitHub security configuration audit completed without weakening existing rules.'
