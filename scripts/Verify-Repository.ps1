[CmdletBinding()]
param(
    [switch] $CI
)

. (Join-Path $PSScriptRoot 'Common.ps1')

$root = Get-RepositoryRoot

function Get-WorkflowJobBlock {
    param(
        [Parameter(Mandatory)]
        [string] $Contents,

        [Parameter(Mandatory)]
        [string] $Name
    )

    $escapedName = [regex]::Escape($Name)
    $match = [regex]::Match(
        $Contents,
        "(?ms)^  ${escapedName}:\r?\n(?<block>.*?)(?=^  [A-Za-z0-9][A-Za-z0-9_-]*:\r?\n|\z)")
    if (-not $match.Success) {
        throw "Required workflow job is missing: $Name"
    }

    $match.Groups['block'].Value
}

function Get-WorkflowStepBlocks {
    param(
        [Parameter(Mandatory)]
        [string] $Contents
    )

    $stepsMatch = [regex]::Match(
        $Contents,
        '(?ms)^    steps\s*:\s*\r?\n(?<steps>.*?)(?=^    [A-Za-z_][A-Za-z0-9_-]*\s*:|\z)')
    if (-not $stepsMatch.Success) {
        throw 'Required workflow job has no steps block.'
    }

    foreach ($stepMatch in [regex]::Matches(
            $stepsMatch.Groups['steps'].Value,
            '(?ms)^      - (?<step>.*?)(?=^      - |\z)')) {
        $stepContents = $stepMatch.Value
        $nameMatch = [regex]::Match(
            $stepContents,
            '(?m)^      - name\s*:\s*(?<name>[^\r\n]+?)\s*$')
        [pscustomobject]@{
            Name = if ($nameMatch.Success) {
                $nameMatch.Groups['name'].Value.Trim()
            }
            else {
                $null
            }
            Contents = $stepContents
        }
    }
}

function Get-RequiredWorkflowStepBlock {
    param(
        [Parameter(Mandatory)]
        [string] $JobContents,

        [Parameter(Mandatory)]
        [string] $Name
    )

    $matchingSteps = @(Get-WorkflowStepBlocks -Contents $JobContents |
        Where-Object { $_.Name -ceq $Name })
    if ($matchingSteps.Count -ne 1) {
        throw "Required workflow step must appear exactly once: $Name"
    }

    $matchingSteps[0].Contents
}

function Assert-WorkflowJobIsUnconditional {
    param(
        [Parameter(Mandatory)]
        [string] $Contents,

        [Parameter(Mandatory)]
        [string] $Name
    )

    if ($Contents -match '(?m)^    (?:(?:''if''|"if"|if)\s*:|<<\s*:)' -or
        $Contents -match '(?m)^    (?:''continue-on-error''|"continue-on-error"|continue-on-error)\s*:') {
        throw "Required workflow job must be unconditional and fail-closed: $Name"
    }
}

function Assert-WorkflowStepIsUnconditional {
    param(
        [Parameter(Mandatory)]
        [string] $Contents,

        [Parameter(Mandatory)]
        [string] $Name
    )

    if ($Contents -match '(?m)^        (?:(?:''if''|"if"|if)\s*:|<<\s*:)' -or
        $Contents -match '(?m)^        (?:''continue-on-error''|"continue-on-error"|continue-on-error)\s*:') {
        throw "Required workflow step must be unconditional and fail-closed: $Name"
    }
}

function Assert-WorkflowReceiptStepGate {
    param(
        [Parameter(Mandatory)]
        [string] $Contents
    )

    $conditions = @([regex]::Matches(
            $Contents,
            '(?m)^        (?:''if''|"if"|if)\s*:\s*(?<value>[^\r\n]+?)\s*$'))
    if ($conditions.Count -ne 1 -or
        $conditions[0].Groups['value'].Value -cne 'always()' -or
        $Contents -match '(?m)^        <<\s*:' -or
        $Contents -match '(?m)^        (?:''continue-on-error''|"continue-on-error"|continue-on-error)\s*:') {
        throw 'The verified-delivery receipt step must use only if: always() and must remain fail-closed.'
    }
}

$workflowSecretReferencePattern =
    'secrets\s*(?:\.\s*(?<dotted>[A-Za-z_][A-Za-z0-9_]*)|\[\s*(?<quote>[''"])(?<indexed>[A-Za-z_][A-Za-z0-9_]*)\k<quote>\s*\])'

function Get-WorkflowSecretReferences {
    param(
        [Parameter(Mandatory)]
        [string] $Contents
    )

    foreach ($expressionMatch in [regex]::Matches(
            $Contents,
            '(?s)\$\{\{(?<expression>.*?)\}\}')) {
        $expression = $expressionMatch.Groups['expression'].Value
        $matches = @([regex]::Matches($expression, $workflowSecretReferencePattern))
        $tokenCount = @([regex]::Matches(
                $expression,
                '(?<![A-Za-z0-9_])secrets(?![A-Za-z0-9_])')).Count
        if ($matches.Count -ne $tokenCount) {
            throw 'Workflow secret references must use a static secrets.NAME or secrets["NAME"] member expression; bare and dynamic secrets access is prohibited.'
        }

        foreach ($match in $matches) {
            if ($match.Groups['dotted'].Success) {
                $match.Groups['dotted'].Value
            }
            else {
                $match.Groups['indexed'].Value
            }
        }
    }
}

function Remove-JavaScriptComments {
    param(
        [Parameter(Mandatory)]
        [string] $Contents
    )

    $builder = [Text.StringBuilder]::new($Contents.Length)
    $state = 'code'
    $escaped = $false
    for ($index = 0; $index -lt $Contents.Length; $index++) {
        $character = $Contents[$index]
        $nextCharacter = if ($index + 1 -lt $Contents.Length) {
            $Contents[$index + 1]
        }
        else {
            [char] 0
        }

        switch ($state) {
            'code' {
                if ($character -eq '/' -and $nextCharacter -eq '/') {
                    [void] $builder.Append('  ')
                    $index++
                    $state = 'line-comment'
                }
                elseif ($character -eq '/' -and $nextCharacter -eq '*') {
                    [void] $builder.Append('  ')
                    $index++
                    $state = 'block-comment'
                }
                elseif ($character -eq [char] 39) {
                    [void] $builder.Append($character)
                    $state = 'single-quoted-string'
                    $escaped = $false
                }
                elseif ($character -eq [char] 34) {
                    [void] $builder.Append($character)
                    $state = 'double-quoted-string'
                    $escaped = $false
                }
                elseif ($character -eq [char] 96) {
                    [void] $builder.Append(' ')
                    $state = 'template-string'
                    $escaped = $false
                }
                else {
                    [void] $builder.Append($character)
                }
            }
            'single-quoted-string' {
                [void] $builder.Append($character)
                if ($escaped) {
                    $escaped = $false
                }
                elseif ($character -eq [char] 92) {
                    $escaped = $true
                }
                elseif ($character -eq [char] 39) {
                    $state = 'code'
                }
            }
            'double-quoted-string' {
                [void] $builder.Append($character)
                if ($escaped) {
                    $escaped = $false
                }
                elseif ($character -eq [char] 92) {
                    $escaped = $true
                }
                elseif ($character -eq [char] 34) {
                    $state = 'code'
                }
            }
            'template-string' {
                if ($character -eq "`r" -or $character -eq "`n") {
                    [void] $builder.Append($character)
                }
                else {
                    [void] $builder.Append(' ')
                }
                if ($escaped) {
                    $escaped = $false
                }
                elseif ($character -eq [char] 92) {
                    $escaped = $true
                }
                elseif ($character -eq [char] 96) {
                    $state = 'code'
                }
            }
            'line-comment' {
                if ($character -eq "`r" -or $character -eq "`n") {
                    [void] $builder.Append($character)
                    $state = 'code'
                }
                else {
                    [void] $builder.Append(' ')
                }
            }
            'block-comment' {
                if ($character -eq '*' -and $nextCharacter -eq '/') {
                    [void] $builder.Append('  ')
                    $index++
                    $state = 'code'
                }
                elseif ($character -eq "`r" -or $character -eq "`n") {
                    [void] $builder.Append($character)
                }
                else {
                    [void] $builder.Append(' ')
                }
            }
        }
    }

    $builder.ToString()
}

function Assert-RequiredNodeTestDeclarations {
    param(
        [Parameter(Mandatory)]
        [string] $Contents,

        [Parameter(Mandatory)]
        [string[]] $Names,

        [Parameter(Mandatory)]
        [string] $Label
    )

    $code = Remove-JavaScriptComments -Contents $Contents
    if ($code -match '(?m)\.\s*(?:skip|todo)\s*\(' -or
        $code -match '(?m)\b(?:skip|todo)\s*:\s*(?:true|[''"])') {
        throw "$Label tests must not contain skipped or todo coverage."
    }
    foreach ($name in $Names) {
        $escapedName = [regex]::Escape($name)
        $declarationPattern =
            '(?m)^[ \t]*test[ \t]*\([ \t]*(?:"{0}"|''{0}'')[ \t]*,[ \t]*(?:async[ \t]+)?(?:\([^\r\n)]*\)|[A-Za-z_$][A-Za-z0-9_$]*)[ \t]*=>' -f
                $escapedName
        if (@([regex]::Matches($code, $declarationPattern)).Count -ne 1) {
            throw "$Label executable regression test must be declared exactly once and without skip options: $name"
        }
    }
}

function Assert-RequiredNodeTestsExecute {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string[]] $Names,

        [Parameter(Mandatory)]
        [string] $Label
    )

    if ($null -eq (Get-Command node -ErrorAction SilentlyContinue)) {
        throw "$Label regression coverage requires the pinned Node.js runtime."
    }
    $namePattern = '^(?:' + (($Names | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')$'
    $testOutput = @(& node --test --test-reporter=tap `
        "--test-name-pattern=$namePattern" $Path 2>&1)
    $testExitCode = $LASTEXITCODE
    $testOutputText = $testOutput -join "`n"
    $expectedCount = $Names.Count
    $summaryCounts = @{}
    foreach ($summaryName in @('tests', 'pass', 'fail', 'cancelled', 'skipped', 'todo')) {
        $summaryMatch = [regex]::Match(
            $testOutputText,
            "(?m)^# $([regex]::Escape($summaryName)) (?<count>\d+)\s*$")
        if (-not $summaryMatch.Success) {
            $summaryCounts[$summaryName] = $null
        }
        else {
            $summaryCounts[$summaryName] = [int] $summaryMatch.Groups['count'].Value
        }
    }
    $invalidSummary = $null -eq $summaryCounts.tests -or
        $summaryCounts.tests -lt $expectedCount -or
        $summaryCounts.pass -ne $summaryCounts.tests -or
        $summaryCounts.fail -ne 0 -or
        $summaryCounts.cancelled -ne 0 -or
        $summaryCounts.skipped -ne 0 -or
        $summaryCounts.todo -ne 0
    if ($testExitCode -ne 0 -or $invalidSummary) {
        throw "$Label required regression tests must all execute and pass without skips or todos.`n$testOutputText"
    }
    $global:LASTEXITCODE = 0
}

Push-Location $root
try {
    $requiredFiles = @(
        '.config/dotnet-tools.json',
        '.github/workflows/ci.yml',
        '.github/workflows/dotnet-security-maintenance.yml',
        '.github/workflows/handlescope-upstream-review.yml',
        '.github/workflows/release.yml',
        'Directory.Build.props',
        'discord-release-bot/package.json',
        'discord-release-bot/package-lock.json',
        'discord-release-bot/.env.example',
        'discord-release-bot/README.md',
        'discord-release-bot/src/config.js',
        'discord-release-bot/src/invite.js',
        'discord-release-bot/src/release.js',
        'discord-release-bot/src/release-automation.js',
        'discord-release-bot/test/config.test.js',
        'discord-release-bot/test/release.test.js',
        'discord-release-bot/test/release-automation.test.js',
        'global.json',
        'NuGet.Config',
        'LICENSE.md',
        'THIRD_PARTY_NOTICES.md',
        'docs/RELEASING.md',
        'SessionDock/SessionDock.csproj',
        'SessionDock.HandleScope/SessionDock.HandleScope.csproj',
        'SessionDock.HandleScope/handlescope-upstream.json',
        'SessionDock/Assets/SessionDock.Icon.png',
        'SessionDock/Assets/SessionDock.ico',
        'SessionDock/Resources/handlescope-compatibility-bootstrap.json',
        'SessionDock/Resources/update-public-key.pem',
        'SessionDock.ReleaseTrust/HandleScopeCompatibilityCatalog.cs',
        'SessionDock.ReleaseTrust/HandleScopeCompatibilityCatalogPolicy.cs',
        'SessionDock.ReleaseTrust/ReleaseDescriptorPolicy.cs',
        'SessionDock/tools/ReleaseSigner/Program.cs',
        'licenses/Velopack-LICENSE.txt',
        'scripts/New-ReleaseChecksums.ps1',
        'scripts/New-ReleaseSbom.ps1',
        'scripts/Build-RuntimeSmoke.ps1',
        'scripts/Configure-GitHubSecurity.ps1',
        'scripts/Rename-SessionDockReleaseAssets.ps1',
        'scripts/ReleaseJson.ps1',
        'scripts/Sign-ReleaseDescriptorDigest.ps1',
        'scripts/Test-RuntimeSmoke.ps1',
        'scripts/Test-DotNetSecurityPatch.ps1',
        'scripts/Sync-BundledHandleScope.ps1',
        'scripts/Verify-Assets.ps1',
        'scripts/Verify-Publish.ps1',
        'scripts/Verify-ReleaseLicense.ps1'
    )
    foreach ($relativePath in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $root $relativePath) -PathType Leaf)) {
            throw "Required repository file is missing: $relativePath"
        }
    }
    $handleScopeSyncContents = Get-Content -LiteralPath `
        (Join-Path $root 'scripts/Sync-BundledHandleScope.ps1') -Raw
    if ($handleScopeSyncContents -notmatch "Common\.ps1" -or
        $handleScopeSyncContents -notmatch 'Test-PathEntryIsLink' -or
        $handleScopeSyncContents -notmatch 'foreach \(\$component in \$components\)' -or
        $handleScopeSyncContents -notmatch 'manifestPath = Assert-PathInsideRoot' -or
        $handleScopeSyncContents -notmatch 'Get-FileHash' -or
        $handleScopeSyncContents -notmatch 'Get-GitBlobSha256') {
        throw 'Bundled HandleScope verification must reject path-link traversal while allowing regular cloud-backed files and retaining byte provenance checks.'
    }
    & (Join-Path $PSScriptRoot 'Sync-BundledHandleScope.ps1')
    $velopackLicenseHash = (Get-FileHash `
        -LiteralPath (Join-Path $root 'licenses/Velopack-LICENSE.txt') `
        -Algorithm SHA256).Hash
    if ($velopackLicenseHash -cne '91845DB83551C877EBBB1118E0FB92E4E527290D23B995C55DCD438B3293943F') {
        throw 'The bundled Velopack license must match the pinned 1.2.0 upstream license exactly.'
    }
    & (Join-Path $PSScriptRoot 'Verify-ReleaseLicense.ps1') `
        -LicensePath (Join-Path $root 'LICENSE.md')

    . (Join-Path $PSScriptRoot 'ReleaseJson.ps1')
    $dateProbeText = '2026-07-21T17:06:45.1234567+00:00'
    $dateProbe = ConvertFrom-ReleaseJson ('{"publishedAt":"' + $dateProbeText + '"}')
    if ($dateProbe.publishedAt -isnot [string] -or
        $dateProbe.publishedAt -cne $dateProbeText) {
        throw 'Release JSON parsing must preserve canonical timestamp strings.'
    }

    $globalJson = Get-Content -LiteralPath (Join-Path $root 'global.json') -Raw |
        ConvertFrom-Json
    $expectedSdk = $globalJson.sdk.version
    if ($globalJson.sdk.rollForward -cne 'disable' -or
        $globalJson.sdk.allowPrerelease -ne $false) {
        throw 'global.json must disable SDK roll-forward and prerelease selection.'
    }
    $actualSdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $expectedSdk) {
        throw "The repository requires .NET SDK $expectedSdk; dotnet selected '$actualSdk'."
    }
    & (Join-Path $PSScriptRoot 'Test-DotNetSecurityPatch.ps1')

    $toolManifest = Get-Content -LiteralPath (Join-Path $root '.config/dotnet-tools.json') -Raw | ConvertFrom-Json
    if ($toolManifest.tools.vpk.version -ne '1.2.0' -or $toolManifest.tools.vpk.rollForward -ne $false) {
        throw 'The local vpk tool must remain pinned exactly to version 1.2.0 with roll-forward disabled.'
    }

    $version = Get-ProjectVersion
    Assert-LegacyReadableReleaseNotes `
        -Path (Join-Path $root "SessionDock/ReleaseNotes/$version.en-US.md")
    foreach ($culture in @('de-DE', 'en-US', 'es-ES', 'fr-FR', 'nl-NL')) {
        $localizedNotes = Join-Path $root "SessionDock/ReleaseNotes/$version.$culture.md"
        if (-not (Test-Path -LiteralPath $localizedNotes -PathType Leaf)) {
            throw "Release notes are missing for ${culture}: $localizedNotes"
        }
    }

    [xml] $applicationProject = Get-Content -LiteralPath `
        (Join-Path $root 'SessionDock/SessionDock.csproj') -Raw
    $handleScopeReferences = @($applicationProject.SelectNodes(
            "/Project/ItemGroup/ProjectReference[@Include='..\SessionDock.HandleScope\SessionDock.HandleScope.csproj']"))
    if ($handleScopeReferences.Count -ne 1) {
        throw 'SessionDock must compile the reviewed HandleScope component into its single executable.'
    }
    [xml] $handleScopeProject = Get-Content -LiteralPath `
        (Join-Path $root 'SessionDock.HandleScope/SessionDock.HandleScope.csproj') -Raw
    $handleScopeVersions = @($handleScopeProject.SelectNodes('/Project/PropertyGroup/Version') |
        ForEach-Object { $_.InnerText } | Where-Object { $_ })
    if ($handleScopeVersions.Count -ne 1 -or $handleScopeVersions[0] -cne '0.3.0') {
        throw 'The bundled HandleScope project must remain pinned to reviewed version 0.3.0.'
    }
    $compatibilityBootstrap = Get-Content -LiteralPath `
        (Join-Path $root 'SessionDock/Resources/handlescope-compatibility-bootstrap.json') `
        -Raw | ConvertFrom-Json
    if ([long] $compatibilityBootstrap.sequence -ne 3 -or
        $compatibilityBootstrap.sessionDockVersion -cne '3.0.0' -or
        $compatibilityBootstrap.recommendedVersion -cne '0.3.0') {
        throw 'The 3.0.0 compatibility bootstrap must retain sequence 3 and the external HandleScope 0.3.0 recommendation.'
    }
    $applicationIcons = @($applicationProject.SelectNodes(
            '/Project/PropertyGroup/ApplicationIcon') |
        ForEach-Object { $_.InnerText } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($applicationIcons.Count -ne 1 -or
        $applicationIcons[0] -cne 'Assets\SessionDock.ico') {
        throw 'SessionDock must use the reviewed Assets\SessionDock.ico application icon.'
    }
    $brandResources = @($applicationProject.SelectNodes(
            "/Project/ItemGroup/Resource[@Include='Assets\SessionDock.Icon.png']"))
    if ($brandResources.Count -ne 1) {
        throw 'The in-app SessionDock icon must remain an embedded WPF resource.'
    }

    $releasePolicyContents = Get-Content -LiteralPath `
        (Join-Path $root 'SessionDock.ReleaseTrust/ReleaseDescriptorPolicy.cs') -Raw
    $packageIdentityMatch = [regex]::Match(
        $releasePolicyContents,
        'public const string VelopackPackageId\s*=\s*"([A-Za-z0-9._-]+)";')
    if (-not $packageIdentityMatch.Success -or
        $packageIdentityMatch.Groups[1].Value -cne 'SessionDockApp' -or
        $packageIdentityMatch.Groups[1].Value -in @('SessionDock', 'RobloxOne')) {
        throw 'The Velopack package ID must remain SessionDockApp and must not collide with either data-directory identity.'
    }
    $publishContents = Get-Content -LiteralPath (Join-Path $root 'scripts/Publish.ps1') -Raw
    $ciWorkflowContents = Get-Content -LiteralPath `
        (Join-Path $root '.github/workflows/ci.yml') -Raw
    $releaseWorkflowContents = Get-Content -LiteralPath `
        (Join-Path $root '.github/workflows/release.yml') -Raw
    if ($releaseWorkflowContents -notmatch
            '(?ms)^concurrency:\s*\r?\n  group:\s*sessiondock-release-publication\s*\r?\n  queue:\s*max\s*\r?\n  cancel-in-progress:\s*false\s*$') {
        throw 'Release workflows must share one non-cancelling publication lane across all version tags.'
    }
    if ($releaseWorkflowContents -notmatch '--packId\s+SessionDockApp' -or
        $publishContents -notmatch 'Local production release packaging is intentionally disabled') {
        throw 'Only the protected workflow may package the non-colliding SessionDockApp production release.'
    }
    if ($releaseWorkflowContents -match '(?m)^\s*--icon(?:\s|$)' -or
        $releaseWorkflowContents -match
            'artifacts/release-input/(?:app/)?SessionDock\.ico') {
        throw 'Velopack --icon would add setup.ico and break strict legacy updater compatibility.'
    }
    if ($publishContents -match "'--framework'\s+'webview2'" -or
        $releaseWorkflowContents -match '--framework\s+webview2') {
        throw 'The update package must remain readable by the strict 2.4.0 updater; WebView2 recovery belongs in the application until every supported updater accepts runtimeDependencies metadata.'
    }

    $ciBuildJob = Get-WorkflowJobBlock -Contents $ciWorkflowContents -Name 'build-and-test'
    $releaseValidateJob = Get-WorkflowJobBlock `
        -Contents $releaseWorkflowContents `
        -Name 'validate-and-build'
    $releasePreflightJob = Get-WorkflowJobBlock `
        -Contents $releaseWorkflowContents `
        -Name 'preflight-discord-announcement'
    $releaseStageJob = Get-WorkflowJobBlock `
        -Contents $releaseWorkflowContents `
        -Name 'sign-attest-and-stage'
    $releasePublishJob = Get-WorkflowJobBlock `
        -Contents $releaseWorkflowContents `
        -Name 'publish-verified-release'
    $releaseAnnouncementJob = Get-WorkflowJobBlock `
        -Contents $releaseWorkflowContents `
        -Name 'announce-published-release'
    $dependencyReviewJob = Get-WorkflowJobBlock `
        -Contents $ciWorkflowContents `
        -Name 'dependency-review'

    foreach ($criticalJob in @(
            @{ Name = 'build-and-test'; Contents = $ciBuildJob },
            @{ Name = 'validate-and-build'; Contents = $releaseValidateJob },
            @{ Name = 'preflight-discord-announcement'; Contents = $releasePreflightJob },
            @{ Name = 'sign-attest-and-stage'; Contents = $releaseStageJob },
            @{ Name = 'publish-verified-release'; Contents = $releasePublishJob },
            @{ Name = 'announce-published-release'; Contents = $releaseAnnouncementJob }
        )) {
        Assert-WorkflowJobIsUnconditional `
            -Contents $criticalJob.Contents `
            -Name $criticalJob.Name
    }
    foreach ($sourceAnchorJob in @(
            @{ Name = 'build-and-test'; Contents = $ciBuildJob },
            @{ Name = 'validate-and-build'; Contents = $releaseValidateJob }
        )) {
        if ($sourceAnchorJob.Contents -notmatch 'https://github\.com/Makmatoe/HandleScope\.git' -or
            $sourceAnchorJob.Contents -notmatch 'refs/tags/v0\.3\.0:refs/tags/v0\.3\.0' -or
            $sourceAnchorJob.Contents -notmatch 'Sync-BundledHandleScope\.ps1 -UpstreamPath \$upstream' -or
            $sourceAnchorJob.Contents -notmatch 'git -C \$upstream fetch --no-tags --depth=1') {
            throw "Workflow job '$($sourceAnchorJob.Name)' must compare bundled HandleScope bytes with the immutable upstream tag."
        }
    }

    if ($dependencyReviewJob -notmatch "(?m)^    if:\s*github\.event_name == 'pull_request'\s*$" -or
        $dependencyReviewJob -match '(?i)vars\.|DEPENDENCY_REVIEW_ENABLED' -or
        $dependencyReviewJob -notmatch 'actions/dependency-review-action@[0-9a-f]{40}' -or
        $dependencyReviewJob -notmatch 'fail-on-severity:\s*moderate') {
        throw 'Dependency review must run fail-closed at moderate severity on every pull request.'
    }

    $ciRuntimeSmoke =
        './scripts/Build-RuntimeSmoke.ps1 -OutputDirectory artifacts/ci-runtime-smoke -TimeoutSeconds 30'
    if (-not $ciBuildJob.Contains($ciRuntimeSmoke)) {
        throw 'CI must execute the isolated published-executable runtime smoke.'
    }
    $discordCiCommands = @(
        'npm ci',
        'npm test',
        'npm run check',
        'npm audit --omit=dev --audit-level=moderate'
    )
    $ciSteps = @(Get-WorkflowStepBlocks -Contents $ciBuildJob)
    $ciNodeSetupSteps = @($ciSteps | Where-Object {
            $_.Contents -match
                '(?m)^        uses:\s*actions/setup-node@48b55a011bda9f5d6aeb4c2d9c7362e8dae4041e(?:\s+#.*)?$'
        })
    if ($ciNodeSetupSteps.Count -ne 1 -or
        $ciNodeSetupSteps[0].Contents -notmatch '(?m)^          node-version:\s*24\.18\.0\s*$' -or
        $ciNodeSetupSteps[0].Contents -notmatch '(?m)^          package-manager-cache:\s*false\s*$' -or
        @([regex]::Matches(
                $ciBuildJob,
                '(?m)^        working-directory:\s*discord-release-bot\s*$')).Count -ne 4) {
        throw 'CI must test and audit Discord release automation on the exact supported Node.js runtime.'
    }
    Assert-WorkflowStepIsUnconditional `
        -Contents $ciNodeSetupSteps[0].Contents `
        -Name 'Install the pinned Node.js runtime'
    foreach ($command in $discordCiCommands) {
        $escapedCommand = [regex]::Escape($command)
        $commandSteps = @($ciSteps | Where-Object {
                $_.Contents -match "(?m)^        run:\s*$escapedCommand\s*$"
            })
        if ($commandSteps.Count -ne 1 -or
            $commandSteps[0].Contents -notmatch
                '(?m)^        working-directory:\s*discord-release-bot\s*$') {
            throw "Discord CI command must run in its own fail-fast workflow step: $command"
        }
        Assert-WorkflowStepIsUnconditional `
            -Contents $commandSteps[0].Contents `
            -Name $command
    }
    if ($ciBuildJob -match '(?ms)^        run:\s*\|[^\r\n]*\r?\n(?:          .*\r?\n)*?          npm ') {
        throw 'Discord CI commands must not share a PowerShell block that can mask an earlier native-command failure.'
    }
    if ($ciBuildJob -match '(?m)^        continue-on-error\s*:') {
        throw 'CI validation steps must not suppress Discord dependency, test, syntax, or audit failures.'
    }
    $releaseRuntimeSmoke =
        './scripts/Build-RuntimeSmoke.ps1 -OutputDirectory artifacts/release-runtime-smoke -TimeoutSeconds 30'
    if (-not $releaseValidateJob.Contains($releaseRuntimeSmoke)) {
        throw 'Protected release validation must execute the isolated published-executable runtime smoke.'
    }
    if ($releaseValidateJob -notmatch 'actions/setup-node@48b55a011bda9f5d6aeb4c2d9c7362e8dae4041e' -or
        $releaseValidateJob -notmatch '(?m)^          node-version:\s*24\.18\.0\s*$' -or
        $releaseValidateJob -notmatch '(?m)^          package-manager-cache:\s*false\s*$' -or
        $releaseValidateJob -notmatch 'SOURCE_COMMIT:\s*\$\{\{\s*github\.sha\s*\}\}' -or
        $releaseValidateJob -notmatch 'Copy-Item SessionDock/Resources/handlescope-compatibility-bootstrap\.json artifacts/release-input/handlescope-compatibility-bootstrap\.json' -or
        $releaseValidateJob -notmatch 'Copy-Item SessionDock\.HandleScope/handlescope-upstream\.json artifacts/release-input/handlescope-upstream\.json' -or
        $releaseValidateJob -notmatch 'Copy-Item discord-release-bot/src/release-automation\.js artifacts/release-input/release-automation\.mjs' -or
        $releaseValidateJob -notmatch "'generate'" -or
        $releaseValidateJob -notmatch '''--source-commit'', \$env:SOURCE_COMMIT' -or
        $releaseValidateJob -notmatch 'SessionDock/ReleaseNotes/\$version\.en-US\.md' -or
        $releaseValidateJob -notmatch "'--output', 'artifacts/release-input/discord-announcement'" -or
        $releaseValidateJob -notmatch 'docs/images/sessiondock-v\$version' -or
        $releaseValidateJob -notmatch 'discord\.json' -or
        $releaseValidateJob -notmatch '(?m)^          retention-days:\s*7\s*$' -or
        $releaseValidateJob -match 'docs/images/sessiondock-v\d+\.\d+\.\d+' -or
        $releaseValidateJob -match 'DISCORD_RELEASE_(?:BOT_TOKEN|CHANNEL_ID|ROLE_ID)') {
        throw 'Release validation must generate one immutable announcement from only the current versioned notes and reviewed images.'
    }

    if ($releasePreflightJob -notmatch '(?m)^    needs:\s*validate-and-build\s*$' -or
        $releasePreflightJob -notmatch '(?m)^    environment:\s*release-announcement\s*$' -or
        $releasePreflightJob -notmatch '(?m)^    permissions:\s*\{\}\s*$' -or
        $releasePreflightJob -notmatch '(?m)^    timeout-minutes:\s*10\s*$' -or
        $releasePreflightJob -match 'actions/checkout@|continue-on-error|failure\(\)|cancelled\(\)' -or
        $releasePreflightJob -match '(?m)^\s+(?:contents|id-token|attestations|artifact-metadata):\s*(?:read|write)\s*$' -or
        $releasePreflightJob -match 'release-automation\.mjs post|--receipt|method:\s*["'']POST') {
        throw 'Discord readiness must be a GET-only, no-authority pre-publication environment gate.'
    }
    $preflightSteps = @(Get-WorkflowStepBlocks -Contents $releasePreflightJob)
    if ($preflightSteps.Count -ne 4) {
        throw 'Discord readiness must retain exactly its four reviewed download, runtime, verify, and preflight steps.'
    }
    $preflightDownloadStep = Get-RequiredWorkflowStepBlock `
        -JobContents $releasePreflightJob `
        -Name 'Download immutable release input'
    $preflightNodeSetupStep = Get-RequiredWorkflowStepBlock `
        -JobContents $releasePreflightJob `
        -Name 'Install the pinned Node.js runtime'
    $preflightVerifyStep = Get-RequiredWorkflowStepBlock `
        -JobContents $releasePreflightJob `
        -Name 'Verify the deterministic announcement artifact'
    $preflightProbeStep = Get-RequiredWorkflowStepBlock `
        -JobContents $releasePreflightJob `
        -Name 'Read-only preflight of Bota and the release channel'
    foreach ($requiredPreflightStep in @(
            @{ Name = 'Download immutable release input'; Contents = $preflightDownloadStep },
            @{ Name = 'Install the pinned Node.js runtime'; Contents = $preflightNodeSetupStep },
            @{ Name = 'Verify the deterministic announcement artifact'; Contents = $preflightVerifyStep },
            @{ Name = 'Read-only preflight of Bota and the release channel'; Contents = $preflightProbeStep }
        )) {
        Assert-WorkflowStepIsUnconditional `
            -Contents $requiredPreflightStep.Contents `
            -Name $requiredPreflightStep.Name
    }
    if ($preflightDownloadStep -notmatch 'actions/download-artifact@3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c' -or
        $preflightDownloadStep -notmatch 'name:\s*release-input-\$\{\{\s*github\.sha\s*\}\}' -or
        $preflightNodeSetupStep -notmatch 'actions/setup-node@48b55a011bda9f5d6aeb4c2d9c7362e8dae4041e' -or
        $preflightNodeSetupStep -notmatch '(?m)^          node-version:\s*24\.18\.0\s*$' -or
        $preflightVerifyStep -notmatch 'release-automation\.mjs verify' -or
        $preflightProbeStep -notmatch 'release-automation\.mjs preflight' -or
        $preflightProbeStep -notmatch 'DISCORD_RELEASE_BOT_TOKEN:\s*\$\{\{\s*secrets\.DISCORD_RELEASE_BOT_TOKEN\s*\}\}' -or
        $preflightProbeStep -notmatch 'DISCORD_RELEASE_BOT_ID:\s*\$\{\{\s*vars\.DISCORD_RELEASE_BOT_ID\s*\}\}' -or
        $preflightProbeStep -notmatch 'DISCORD_RELEASE_CHANNEL_ID:\s*\$\{\{\s*vars\.DISCORD_RELEASE_CHANNEL_ID\s*\}\}' -or
        $preflightProbeStep -notmatch 'DISCORD_RELEASE_ROLE_ID:\s*\$\{\{\s*vars\.DISCORD_RELEASE_ROLE_ID\s*\}\}' -or
        $preflightVerifyStep -match 'DISCORD_RELEASE_') {
        throw 'Discord readiness must verify the immutable bundle before one credential-scoped GET-only preflight.'
    }

    if ($releaseStageJob -notmatch '(?ms)^    needs:\s*\r?\n      - validate-and-build\s*\r?\n      - preflight-discord-announcement\s*$' -or
        $releaseStageJob -notmatch '(?m)^    environment:\s*release\s*$' -or
        $releaseStageJob -notmatch '(?m)^      contents:\s*write\s*$' -or
        $releaseStageJob -notmatch '(?m)^      id-token:\s*write\s*$' -or
        $releaseStageJob -notmatch '(?m)^      attestations:\s*write\s*$' -or
        $releaseStageJob -notmatch '(?m)^      artifact-metadata:\s*write\s*$' -or
        $releaseStageJob -notmatch 'gh release create[^\r\n]*--draft' -or
        $releaseStageJob -notmatch 'actions/attest@' -or
        $releaseStageJob -notmatch 'secrets\.UPDATE_SIGNING_PRIVATE_KEY_PKCS8_BASE64' -or
        $releaseStageJob -notmatch 'Sign-ReleaseDescriptorDigest\.ps1' -or
        $releaseStageJob -notmatch 'ReleaseSigner\.exe prepare' -or
        $releaseStageJob -notmatch 'ReleaseSigner\.exe complete') {
        throw 'The protected staging job must sign only release metadata, draft, and attest with its required permissions.'
    }
    $releaseMetadataSigningStep = Get-RequiredWorkflowStepBlock `
        -JobContents $releaseStageJob `
        -Name 'Sign release metadata with the protected P-256 key'
    Assert-WorkflowStepIsUnconditional `
        -Contents $releaseMetadataSigningStep `
        -Name 'Sign release metadata with the protected P-256 key'
    if (@([regex]::Matches(
                $releaseStageJob,
                "'prepare-catalog'")).Count -ne 1 -or
        @([regex]::Matches(
                $releaseStageJob,
                'ReleaseSigner\.exe complete-catalog')).Count -ne 1 -or
        @([regex]::Matches(
                $releaseStageJob,
                'ReleaseSigner\.exe verify-catalog')).Count -ne 1 -or
        @([regex]::Matches(
                $releaseStageJob,
                '--sessiondock-version')).Count -ne 3 -or
        $releaseStageJob -notmatch '''--sessiondock-version'', \$env:RELEASE_VERSION' -or
        $releaseStageJob -notmatch 'gh api' -or
        $releaseStageJob -notmatch '/releases\?per_page=100&page=\$page' -or
        $releaseStageJob -notmatch '\$page -gt 100' -or
        $releaseStageJob -notmatch '\$release\.draft -or \$release\.prerelease' -or
        $releaseStageJob -notmatch '\$releaseCatalogAssets\.Count -eq 1' -or
        $releaseStageJob -notmatch 'HashSet\[long\]' -or
        $releaseStageJob -notmatch '\$catalogAssetIds\.Add\(\$assetId\)' -or
        $releaseStageJob -notmatch '\^sha256:\[0-9a-f\]\{64\}\$' -or
        $releaseStageJob -notmatch "\$env:RELEASE_VERSION -cne '2\.8\.0'" -or
        $releaseStageJob -notmatch 'Invoke-WebRequest' -or
        $releaseStageJob -notmatch '/releases/assets/\$\(\$historicalAsset\.AssetId\)' -or
        $releaseStageJob -notmatch 'Get-FileHash' -or
        $releaseStageJob -notmatch '\$downloadedDigest -cne' -or
        $releaseStageJob -notmatch '\$catalogHistory\.Count' -or
        $releaseStageJob -notmatch '''--prior-directory'', \$priorCatalogDirectory' -or
        $releaseStageJob -notmatch '''--public-key'', ''\./release-input/update-public-key\.pem''' -or
        $releaseStageJob -notmatch 'ReleaseSigner\.exe @catalogArguments' -or
        $releaseStageJob -notmatch '--output ./release-output/sessiondock-handlescope-compatibility\.json' -or
        $releaseStageJob -notmatch '-CompatibilityCatalog ./release-output/sessiondock-handlescope-compatibility\.json' -or
        $releaseStageJob -notmatch '-ReleaseSigner ./release-input/signer/ReleaseSigner\.exe' -or
        $releaseStageJob -notmatch '-PublicKey ./release-input/update-public-key\.pem' -or
        $releaseMetadataSigningStep -notmatch "'\./descriptor\.digest\.base64url'" -or
        $releaseMetadataSigningStep -notmatch "'\./handlescope-catalog\.digest\.base64url'" -or
        $releaseMetadataSigningStep -notmatch "'\./descriptor\.signature\.base64url'" -or
        $releaseMetadataSigningStep -notmatch "'\./handlescope-catalog\.signature\.base64url'" -or
        $releaseMetadataSigningStep -notmatch '-DigestPath \$digests' -or
        $releaseMetadataSigningStep -notmatch '-SignaturePath \$signatures' -or
        $releaseStageJob -match 'Sort-Object PublishedAt' -or
        $releaseStageJob -match '''--prior-manifest''') {
        throw 'The protected staging job must prepare, jointly sign, complete, and verify the version-bound HandleScope catalog.'
    }
    $retiredAuthenticodeVerifier = 'Test-' + 'AuthenticodeSignature\.ps1'
    if ($releaseStageJob -match 'Azure/(?:login|artifact-signing-action)@' -or
        $releaseStageJob -match $retiredAuthenticodeVerifier -or
        $releaseStageJob -match '--private-key') {
        throw 'The unsigned release path must not claim Authenticode or pass the descriptor key to repository-built executables.'
    }
    if ($releaseStageJob -match 'gh release edit|--draft=false') {
        throw 'The protected staging job must not publish the verified draft.'
    }

    if ($releasePublishJob -notmatch '(?ms)^    needs:\s*\r?\n      - validate-and-build\s*\r?\n      - preflight-discord-announcement\s*\r?\n      - sign-attest-and-stage\s*$' -or
        $releasePublishJob -notmatch '(?m)^    environment:\s*release-publication\s*$' -or
        $releasePublishJob -notmatch '(?m)^      actions:\s*read\s*$' -or
        $releasePublishJob -notmatch '(?m)^      contents:\s*write\s*$' -or
        $releasePublishJob -notmatch '(?m)^      attestations:\s*read\s*$' -or
        $releasePublishJob -notmatch 'gh release download' -or
        $releasePublishJob -notmatch 'SHA256SUMS\.txt' -or
        $releasePublishJob -notmatch "'sessiondock-handlescope-compatibility\.json'" -or
        @([regex]::Matches(
                $releasePublishJob,
                'ReleaseSigner\.exe')).Count -ne 2 -or
        @([regex]::Matches(
                $releasePublishJob,
                'ReleaseSigner\.exe verify-catalog')).Count -ne 2 -or
        $releasePublishJob -notmatch '--manifest ./approved-assets/sessiondock-handlescope-compatibility\.json' -or
        $releasePublishJob -notmatch '--manifest \$finalCatalogs\[0\]\.FullName' -or
        $releasePublishJob -notmatch '--public-key ./release-verification/update-public-key\.pem' -or
        $releasePublishJob -notmatch '--sessiondock-version \$env:RELEASE_VERSION' -or
        $releasePublishJob -notmatch 'Compare-Object \$expectedNames \$actualNames -CaseSensitive' -or
        $releasePublishJob -notmatch '\[Collections\.Generic\.Dictionary\[string, string\]\]::new\(\s*\r?\n\s*\[StringComparer\]::Ordinal\)' -or
        $releasePublishJob -notmatch '(?s)Compare-Object\s+`\s*\r?\n\s*\$expectedChecksumNames\s+`\s*\r?\n\s*@\(\$checksumEntries\.Keys \| Sort-Object\)\s+`\s*\r?\n\s*-CaseSensitive' -or
        $releasePublishJob -notmatch '(?s)gh attestation verify \$asset\.FullName\s+`\s*\r?\n\s*--repo \$env:GITHUB_REPOSITORY\s+`\s*\r?\n\s*--signer-workflow \$env:EXPECTED_SIGNER_WORKFLOW\s+`\s*\r?\n\s*--source-ref \$env:EXPECTED_SOURCE_REF\s+`\s*\r?\n\s*--source-digest \$env:EXPECTED_SOURCE_DIGEST' -or
        $releasePublishJob -notmatch 'EXPECTED_SIGNER_WORKFLOW:\s*Makmatoe/SessionDock/\.github/workflows/release\.yml' -or
        $releasePublishJob -notmatch 'EXPECTED_SOURCE_REF:\s*\$\{\{\s*github\.ref\s*\}\}' -or
        $releasePublishJob -notmatch 'EXPECTED_SOURCE_DIGEST:\s*\$\{\{\s*github\.sha\s*\}\}' -or
        $releasePublishJob -notmatch 'RELEASE_RUN_ATTEMPT:\s*\$\{\{\s*github\.run_attempt\s*\}\}' -or
        @([regex]::Matches($releasePublishJob, '--json body,isDraft,isPrerelease,tagName,name')).Count -ne 3 -or
        @([regex]::Matches($releasePublishJob, 'release-verification/notes\.md')).Count -lt 4 -or
        $releasePublishJob -notmatch 'approved release notes differ from the immutable release notes' -or
        $releasePublishJob -notmatch 'published release does not exactly match the reverified release' -or
        $releasePublishJob -notmatch 'gh release edit[^\r\n]*--draft=false[^\r\n]*--latest') {
        throw 'Final publication must be separately approved and must reverify the exact draft and bounded provenance before publishing it.'
    }
    $releasePublishSteps = @(Get-WorkflowStepBlocks -Contents $releasePublishJob)
    $expectedReleasePublishStepNames = @(
        'Create guarded publication intent'
        'Download immutable verification tools'
        'Preserve the guarded publication intent'
        'Publish the reverified draft'
        'Re-download and verify the approved draft'
        'Verify an earlier guarded publication intent'
    ) | Sort-Object
    $actualReleasePublishStepNames = @($releasePublishSteps.Name | Sort-Object)
    if (Compare-Object `
            $expectedReleasePublishStepNames `
            $actualReleasePublishStepNames `
            -CaseSensitive) {
        throw 'The final publication job must contain only its six reviewed verification and publication steps.'
    }
    $releaseReverifyStep = Get-RequiredWorkflowStepBlock `
        -JobContents $releasePublishJob `
        -Name 'Re-download and verify the approved draft'
    $releaseIntentStep = Get-RequiredWorkflowStepBlock `
        -JobContents $releasePublishJob `
        -Name 'Create guarded publication intent'
    $releaseIntentArtifactStep = Get-RequiredWorkflowStepBlock `
        -JobContents $releasePublishJob `
        -Name 'Preserve the guarded publication intent'
    $releasePriorIntentStep = Get-RequiredWorkflowStepBlock `
        -JobContents $releasePublishJob `
        -Name 'Verify an earlier guarded publication intent'
    $releaseFinalPublishStep = Get-RequiredWorkflowStepBlock `
        -JobContents $releasePublishJob `
        -Name 'Publish the reverified draft'
    Assert-WorkflowStepIsUnconditional `
        -Contents $releaseReverifyStep `
        -Name 'Re-download and verify the approved draft'
    Assert-WorkflowStepIsUnconditional `
        -Contents $releaseFinalPublishStep `
        -Name 'Publish the reverified draft'
    if ($releaseReverifyStep -notmatch '(?m)^        id:\s*verify-release\s*$' -or
        $releaseReverifyStep -notmatch '\"state=\$releaseState\"[^\r\n]*GITHUB_OUTPUT' -or
        $releaseIntentStep -notmatch "(?m)^        if:\s*steps\.verify-release\.outputs\.state == 'draft'\s*$" -or
        $releaseIntentStep -notmatch "schema = 'sessiondock-release-publication-intent/v1'" -or
        $releaseIntentStep -notmatch 'workflowRunId = \$env:GITHUB_RUN_ID' -or
        $releaseIntentStep -notmatch 'sourceCommit = \$env:EXPECTED_SOURCE_DIGEST' -or
        $releaseIntentStep -notmatch 'notesSha256 = \$notesSha256' -or
        $releaseIntentArtifactStep -notmatch "(?m)^        if:\s*steps\.verify-release\.outputs\.state == 'draft'\s*$" -or
        $releaseIntentArtifactStep -notmatch 'actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a' -or
        $releaseIntentArtifactStep -notmatch 'name:\s*release-publication-intent-\$\{\{\s*github\.sha\s*\}\}-\$\{\{\s*github\.run_attempt\s*\}\}' -or
        $releaseIntentArtifactStep -notmatch 'path:\s*guarded-publication-intent/intent\.json' -or
        $releaseIntentArtifactStep -notmatch '(?m)^          overwrite:\s*false\s*$' -or
        $releasePriorIntentStep -notmatch '(?m)^        id:\s*prior-publication-intent\s*$' -or
        $releasePriorIntentStep -notmatch "(?m)^        if:\s*steps\.verify-release\.outputs\.state == 'public'\s*$" -or
        $releasePriorIntentStep -notmatch '/actions/runs/\$env:GITHUB_RUN_ID/artifacts\?per_page=100&page=\$page' -or
        $releasePriorIntentStep -notmatch 'artifactAttempt -lt \$runAttempt' -or
        $releasePriorIntentStep -notmatch 'gh run download \$env:GITHUB_RUN_ID' -or
        $releasePriorIntentStep -notmatch 'No earlier guarded publication-intent artifact exists for this tag commit' -or
        $releasePriorIntentStep -notmatch "'verified=true'[^\r\n]*GITHUB_OUTPUT" -or
        $releaseFinalPublishStep -notmatch 'PRIOR_PUBLICATION_INTENT_VERIFIED:\s*\$\{\{\s*steps\.prior-publication-intent\.outputs\.verified\s*\}\}' -or
        $releaseFinalPublishStep -notmatch 'gh release download \$env:RELEASE_TAG' -or
        $releaseFinalPublishStep -notmatch '--dir ./final-catalog-verification' -or
        $releaseFinalPublishStep -notmatch '\$finalCatalogHash -cne \$approvedCatalogHash' -or
        $releaseFinalPublishStep -notmatch 'ReleaseSigner\.exe verify-catalog' -or
        $releaseFinalPublishStep -notmatch "PRIOR_PUBLICATION_INTENT_VERIFIED -cne 'true'" -or
        $releaseFinalPublishStep -notmatch 'already-public release lacks verified evidence from an earlier guarded publication attempt') {
        throw 'Publication recovery must require exact, durable evidence from an earlier fully reverified attempt.'
    }
    $finalCatalogVerificationIndex = $releaseFinalPublishStep.IndexOf(
        'ReleaseSigner.exe verify-catalog',
        [StringComparison]::Ordinal)
    $releasePublicationMutationIndex = $releaseFinalPublishStep.IndexOf(
        'gh release edit',
        [StringComparison]::Ordinal)
    if ($finalCatalogVerificationIndex -lt 0 -or
        $releasePublicationMutationIndex -lt 0 -or
        $finalCatalogVerificationIndex -ge $releasePublicationMutationIndex) {
        throw 'The freshly downloaded HandleScope catalog must be signature-verified immediately before publication.'
    }
    $requiredPriorIntentVerificationPatterns = @(
        '\$intentPrefix = "release-publication-intent-\$env:EXPECTED_SOURCE_DIGEST-"'
        '\$intentFiles\.Count -ne 1'
        '\$intentFiles\[0\]\.Name -cne ''intent\.json'''
        'Compare-Object \$expectedProperties \$actualProperties -CaseSensitive'
        '\$intent\.schema -cne ''sessiondock-release-publication-intent/v1'''
        '\$intent\.repository -cne \$env:GITHUB_REPOSITORY'
        '\$intent\.workflowRunId -cne \$env:GITHUB_RUN_ID'
        '\[int\] \$intent\.runAttempt -ne \$candidate\.Attempt'
        '\$intent\.releaseTag -cne \$env:RELEASE_TAG'
        '\$intent\.releaseVersion -cne \$env:RELEASE_VERSION'
        '\$intent\.sourceRef -cne \$env:EXPECTED_SOURCE_REF'
        '\$intent\.sourceCommit -cne \$env:EXPECTED_SOURCE_DIGEST'
        '\$intent\.notesSha256 -cne \$expectedNotesSha256'
    )
    foreach ($pattern in $requiredPriorIntentVerificationPatterns) {
        if ($releasePriorIntentStep -notmatch $pattern) {
            throw "Earlier publication-intent verification is missing an exact provenance contract: $pattern"
        }
    }
    foreach ($propertyName in @(
            'notesSha256',
            'releaseTag',
            'releaseVersion',
            'repository',
            'runAttempt',
            'schema',
            'sourceCommit',
            'sourceRef',
            'workflowRunId'
        )) {
        if (@([regex]::Matches(
                    $releasePriorIntentStep,
                    "(?m)^\s+'$([regex]::Escape($propertyName))'\s*$")).Count -ne 1) {
            throw "Earlier publication intent must require exactly one schema property: $propertyName"
        }
    }
    $releaseReverifyIndex = $releasePublishJob.IndexOf(
        '- name: Re-download and verify the approved draft',
        [StringComparison]::Ordinal)
    $releaseIntentIndex = $releasePublishJob.IndexOf(
        '- name: Create guarded publication intent',
        [StringComparison]::Ordinal)
    $releaseIntentArtifactIndex = $releasePublishJob.IndexOf(
        '- name: Preserve the guarded publication intent',
        [StringComparison]::Ordinal)
    $releasePriorIntentIndex = $releasePublishJob.IndexOf(
        '- name: Verify an earlier guarded publication intent',
        [StringComparison]::Ordinal)
    $releaseFinalPublishIndex = $releasePublishJob.IndexOf(
        '- name: Publish the reverified draft',
        [StringComparison]::Ordinal)
    if ($releaseReverifyIndex -lt 0 -or
        $releaseReverifyIndex -ge $releaseIntentIndex -or
        $releaseIntentIndex -ge $releaseIntentArtifactIndex -or
        $releaseIntentArtifactIndex -ge $releasePriorIntentIndex -or
        $releasePriorIntentIndex -ge $releaseFinalPublishIndex) {
        throw 'Release verification and durable publication intent must precede the guarded publication mutation.'
    }
    $releasePublishSecretReferences = @(
        Get-WorkflowSecretReferences -Contents $releasePublishJob)
    if ($releasePublishJob -match '(?m)^      (?:id-token|artifact-metadata):\s*write\s*$' -or
        $releasePublishJob -match '(?m)^      attestations:\s*write\s*$' -or
        $releasePublishSecretReferences.Count -ne 0 -or
        $releasePublishJob -match 'ReleaseSigner\.exe (?:prepare|complete|sign-local|prepare-catalog|complete-catalog)' -or
        $releasePublishJob -match 'private-key') {
        throw 'The final publication job must not receive signing secrets or attestation write permissions.'
    }
    if (@([regex]::Matches($releaseWorkflowContents, '--draft=false')).Count -ne 1) {
        throw 'Only the separately approved final job may make a release public.'
    }

    if ($releaseAnnouncementJob -notmatch '(?ms)^    needs:\s*\r?\n      - validate-and-build\s*\r?\n      - publish-verified-release\s*$' -or
        $releaseAnnouncementJob -notmatch '(?m)^    environment:\s*release-announcement\s*$' -or
        $releaseAnnouncementJob -notmatch '(?m)^    permissions:\s*\{\}\s*$' -or
        $releaseAnnouncementJob -notmatch '(?m)^    timeout-minutes:\s*10\s*$' -or
        $releaseAnnouncementJob -notmatch 'actions/download-artifact@3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c' -or
        $releaseAnnouncementJob -notmatch 'name:\s*release-input-\$\{\{\s*github\.sha\s*\}\}' -or
        $releaseAnnouncementJob -notmatch 'path:\s*release-input' -or
        $releaseAnnouncementJob -notmatch 'actions/setup-node@48b55a011bda9f5d6aeb4c2d9c7362e8dae4041e' -or
        $releaseAnnouncementJob -notmatch '(?m)^          node-version:\s*24\.18\.0\s*$' -or
        $releaseAnnouncementJob -notmatch '(?m)^          package-manager-cache:\s*false\s*$' -or
        $releaseAnnouncementJob -notmatch 'release-automation\.mjs verify' -or
        $releaseAnnouncementJob -notmatch 'release-automation\.mjs post' -or
        $releaseAnnouncementJob -notmatch '--artifact-dir release-input/discord-announcement' -or
        $releaseAnnouncementJob -notmatch '--expected-tag "\$RELEASE_TAG"' -or
        $releaseAnnouncementJob -notmatch '--expected-ref "\$SOURCE_REF"' -or
        $releaseAnnouncementJob -notmatch '--expected-commit "\$SOURCE_COMMIT"' -or
        $releaseAnnouncementJob -notmatch 'name:\s*discord-release-announcement-\$\{\{\s*github\.sha\s*\}\}-\$\{\{\s*github\.run_attempt\s*\}\}' -or
        $releaseAnnouncementJob -notmatch 'path:\s*release-input/discord-announcement' -or
        $releaseAnnouncementJob -notmatch 'cat ./release-input/discord-announcement/summary\.md >> "\$GITHUB_STEP_SUMMARY"' -or
        $releaseAnnouncementJob -notmatch 'DISCORD_RELEASE_BOT_TOKEN:\s*\$\{\{\s*secrets\.DISCORD_RELEASE_BOT_TOKEN\s*\}\}' -or
        $releaseAnnouncementJob -notmatch 'DISCORD_RELEASE_BOT_ID:\s*\$\{\{\s*vars\.DISCORD_RELEASE_BOT_ID\s*\}\}' -or
        $releaseAnnouncementJob -notmatch 'DISCORD_RELEASE_CHANNEL_ID:\s*\$\{\{\s*vars\.DISCORD_RELEASE_CHANNEL_ID\s*\}\}' -or
        $releaseAnnouncementJob -notmatch 'DISCORD_RELEASE_ROLE_ID:\s*\$\{\{\s*vars\.DISCORD_RELEASE_ROLE_ID\s*\}\}' -or
        $releaseAnnouncementJob -notmatch '--receipt discord-release-receipt/receipt\.json' -or
        $releaseAnnouncementJob -notmatch 'name:\s*discord-release-receipt-\$\{\{\s*github\.sha\s*\}\}-\$\{\{\s*github\.run_attempt\s*\}\}' -or
        $releaseAnnouncementJob -notmatch 'path:\s*discord-release-receipt/receipt\.json' -or
        $releaseAnnouncementJob -notmatch '(?m)^        if:\s*always\(\)\s*$' -or
        $releaseAnnouncementJob -notmatch '(?m)^          if-no-files-found:\s*error\s*$' -or
        @([regex]::Matches($releaseAnnouncementJob, '(?m)^          overwrite:\s*false\s*$')).Count -ne 2 -or
        @([regex]::Matches($releaseAnnouncementJob, '(?m)^          retention-days:\s*30\s*$')).Count -ne 2) {
        throw 'The post-publication job must verify, artifact, automatically send, and receipt one immutable Discord announcement.'
    }
    if ($releaseAnnouncementJob -match 'actions/checkout@' -or
        $releaseAnnouncementJob -match '(?m)^\s+(?:contents|id-token|attestations|artifact-metadata):\s*(?:read|write)\s*$' -or
        $releaseAnnouncementJob -match '\$\{\{\s*github\.token\s*\}\}' -or
        $releaseAnnouncementJob -match '(?m)^\s+GH_TOKEN:' -or
        $releaseAnnouncementJob -match '(?i)gh release|--draft=false|ReleaseSigner|UPDATE_SIGNING_PRIVATE_KEY' -or
        $releaseAnnouncementJob -match 'continue-on-error|failure\(\)|cancelled\(\)') {
        throw 'Discord publication must have no checkout, GitHub authority, release mutation, or permissive failure gate.'
    }

    $announcementSteps = @(Get-WorkflowStepBlocks -Contents $releaseAnnouncementJob)
    if ($announcementSteps.Count -ne 7) {
        throw 'The post-publication job must retain exactly its seven reviewed delivery and evidence steps.'
    }
    $announcementDownloadStep = Get-RequiredWorkflowStepBlock `
        -JobContents $releaseAnnouncementJob `
        -Name 'Download immutable release input'
    $announcementNodeSetupStep = Get-RequiredWorkflowStepBlock `
        -JobContents $releaseAnnouncementJob `
        -Name 'Install the pinned Node.js runtime'
    $announcementVerifyStep = Get-RequiredWorkflowStepBlock `
        -JobContents $releaseAnnouncementJob `
        -Name 'Verify the deterministic announcement artifact'
    $announcementArtifactStep = Get-RequiredWorkflowStepBlock `
        -JobContents $releaseAnnouncementJob `
        -Name 'Upload the deterministic announcement artifact'
    $announcementSummaryStep = Get-RequiredWorkflowStepBlock `
        -JobContents $releaseAnnouncementJob `
        -Name 'Add the immutable announcement audit summary'
    $announcementSenderStep = Get-RequiredWorkflowStepBlock `
        -JobContents $releaseAnnouncementJob `
        -Name 'Post and verify Bota''s release announcement'
    $announcementReceiptStep = Get-RequiredWorkflowStepBlock `
        -JobContents $releaseAnnouncementJob `
        -Name 'Upload the verified delivery receipt'
    foreach ($requiredAnnouncementStep in @(
            @{ Name = 'Download immutable release input'; Contents = $announcementDownloadStep },
            @{ Name = 'Install the pinned Node.js runtime'; Contents = $announcementNodeSetupStep },
            @{ Name = 'Verify the deterministic announcement artifact'; Contents = $announcementVerifyStep },
            @{ Name = 'Upload the deterministic announcement artifact'; Contents = $announcementArtifactStep },
            @{ Name = 'Add the immutable announcement audit summary'; Contents = $announcementSummaryStep },
            @{ Name = 'Post and verify Bota''s release announcement'; Contents = $announcementSenderStep }
        )) {
        Assert-WorkflowStepIsUnconditional `
            -Contents $requiredAnnouncementStep.Contents `
            -Name $requiredAnnouncementStep.Name
    }
    Assert-WorkflowReceiptStepGate -Contents $announcementReceiptStep

    $announcementVerifyCommandPattern =
        '(?ms)^        run\s*:\s*>-\s*\r?\n' +
        '          node ./release-input/release-automation\.mjs verify\s*\r?\n' +
        '          --artifact-dir release-input/discord-announcement\s*\r?\n' +
        '          --expected-tag "\$RELEASE_TAG"\s*\r?\n' +
        '          --expected-ref "\$SOURCE_REF"\s*\r?\n' +
        '          --expected-commit "\$SOURCE_COMMIT"\s*\z'
    $announcementPostCommandPattern =
        '(?ms)^        run\s*:\s*>-\s*\r?\n' +
        '          node ./release-input/release-automation\.mjs post\s*\r?\n' +
        '          --artifact-dir release-input/discord-announcement\s*\r?\n' +
        '          --expected-tag "\$RELEASE_TAG"\s*\r?\n' +
        '          --expected-ref "\$SOURCE_REF"\s*\r?\n' +
        '          --expected-commit "\$SOURCE_COMMIT"\s*\r?\n' +
        '          --receipt discord-release-receipt/receipt\.json\s*\z'
    if ($announcementDownloadStep -notmatch
            '(?m)^        uses:\s*actions/download-artifact@3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c(?:\s+#.*)?$' -or
        $announcementDownloadStep -notmatch
            '(?m)^          name:\s*release-input-\$\{\{\s*github\.sha\s*\}\}\s*$' -or
        $announcementDownloadStep -notmatch '(?m)^          path:\s*release-input\s*$' -or
        $announcementNodeSetupStep -notmatch
            '(?m)^        uses:\s*actions/setup-node@48b55a011bda9f5d6aeb4c2d9c7362e8dae4041e(?:\s+#.*)?$' -or
        $announcementNodeSetupStep -notmatch '(?m)^          node-version:\s*24\.18\.0\s*$' -or
        $announcementNodeSetupStep -notmatch '(?m)^          package-manager-cache:\s*false\s*$' -or
        $announcementVerifyStep -notmatch $announcementVerifyCommandPattern -or
        $announcementArtifactStep -notmatch
            '(?m)^        uses:\s*actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a(?:\s+#.*)?$' -or
        $announcementArtifactStep -notmatch
            '(?m)^          name:\s*discord-release-announcement-\$\{\{\s*github\.sha\s*\}\}-\$\{\{\s*github\.run_attempt\s*\}\}\s*$' -or
        $announcementArtifactStep -notmatch
            '(?m)^          path:\s*release-input/discord-announcement\s*$' -or
        $announcementSummaryStep -notmatch
            '(?m)^        run:\s*cat ./release-input/discord-announcement/summary\.md >> "\$GITHUB_STEP_SUMMARY"\s*$' -or
        $announcementSenderStep -notmatch
            'DISCORD_RELEASE_BOT_TOKEN:\s*\$\{\{\s*secrets\.DISCORD_RELEASE_BOT_TOKEN\s*\}\}' -or
        $announcementSenderStep -notmatch
            'DISCORD_RELEASE_BOT_ID:\s*\$\{\{\s*vars\.DISCORD_RELEASE_BOT_ID\s*\}\}' -or
        $announcementSenderStep -notmatch
            'DISCORD_RELEASE_CHANNEL_ID:\s*\$\{\{\s*vars\.DISCORD_RELEASE_CHANNEL_ID\s*\}\}' -or
        $announcementSenderStep -notmatch
            'DISCORD_RELEASE_ROLE_ID:\s*\$\{\{\s*vars\.DISCORD_RELEASE_ROLE_ID\s*\}\}' -or
        $announcementSenderStep -notmatch $announcementPostCommandPattern -or
        $announcementReceiptStep -notmatch
            '(?m)^        uses:\s*actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a(?:\s+#.*)?$' -or
        $announcementReceiptStep -notmatch
            '(?m)^          name:\s*discord-release-receipt-\$\{\{\s*github\.sha\s*\}\}-\$\{\{\s*github\.run_attempt\s*\}\}\s*$' -or
        $announcementReceiptStep -notmatch
            '(?m)^          path:\s*discord-release-receipt/receipt\.json\s*$') {
        throw 'Each Discord announcement step must contain its reviewed action or exact fail-closed command.'
    }
    $announcementVerifyIndex = $releaseAnnouncementJob.IndexOf(
        '- name: Verify the deterministic announcement artifact',
        [StringComparison]::Ordinal)
    $announcementArtifactIndex = $releaseAnnouncementJob.IndexOf(
        '- name: Upload the deterministic announcement artifact',
        [StringComparison]::Ordinal)
    $announcementSummaryIndex = $releaseAnnouncementJob.IndexOf(
        '- name: Add the immutable announcement audit summary',
        [StringComparison]::Ordinal)
    $announcementSenderIndex = $releaseAnnouncementJob.IndexOf(
        '- name: Post and verify Bota''s release announcement',
        [StringComparison]::Ordinal)
    $announcementReceiptIndex = $releaseAnnouncementJob.IndexOf(
        '- name: Upload the verified delivery receipt',
        [StringComparison]::Ordinal)
    if ($announcementVerifyIndex -lt 0 -or
        $announcementVerifyIndex -ge $announcementArtifactIndex -or
        $announcementArtifactIndex -ge $announcementSummaryIndex -or
        $announcementSummaryIndex -ge $announcementSenderIndex -or
        $announcementSenderIndex -ge $announcementReceiptIndex) {
        throw 'Discord announcement verification and audit evidence must precede automatic sending and receipt upload.'
    }

    $releaseGuideContents = Get-Content -LiteralPath `
        (Join-Path $root 'docs/RELEASING.md') -Raw
    if ($releaseGuideContents -notmatch '`release-publication`' -or
        $releaseGuideContents -notmatch '`release-announcement`' -or
        $releaseGuideContents -notmatch 'Build-RuntimeSmoke\.ps1' -or
        $releaseGuideContents -notmatch 'UPDATE_SIGNING_PRIVATE_KEY_PKCS8_BASE64' -or
        $releaseGuideContents -notmatch 'DISCORD_RELEASE_BOT_TOKEN' -or
        $releaseGuideContents -notmatch 'DISCORD_RELEASE_BOT_ID' -or
        $releaseGuideContents -notmatch 'DISCORD_RELEASE_CHANNEL_ID' -or
        $releaseGuideContents -notmatch 'DISCORD_RELEASE_ROLE_ID' -or
        $releaseGuideContents -notmatch '(?i)no form' -or
        $releaseGuideContents -notmatch '(?i)before\s+tagging,\s+the\s+repository\s+owner\s+must\s+run\s+the\s+audit' -or
        $releaseGuideContents -notmatch '(?i)treat\s+every\s+failure\s+as\s+a\s+release\s+blocker' -or
        $releaseGuideContents -notmatch '(?i)GET-only\s+preflight' -or
        $releaseGuideContents -notmatch '(?i)Re-run failed jobs' -or
        $releaseGuideContents -notmatch 'Unknown publisher' -or
        $releaseGuideContents -notmatch '(?i)draft[\s\S]{0,500}approval') {
        throw 'The release guide must document release approvals and the externally configured automatic Discord path.'
    }
    if ($releaseGuideContents -notmatch '(?i)exactly one\s+custom deployment policy' -or
        $releaseGuideContents -notmatch '(?i)no required-reviewer rule' -or
        $releaseGuideContents -notmatch '(?i)exactly\s+the environment-scoped `DISCORD_RELEASE_BOT_TOKEN` secret name' -or
        $releaseGuideContents -notmatch '(?i)exactly\s+the three environment-scoped ID variable names' -or
        $releaseGuideContents -notmatch '(?i)same-named repository or\s+organization value can be selected' -or
        $releaseGuideContents -notmatch '(?i)cannot determine which scope supplied' -or
        $releaseGuideContents -notmatch '(?i)organization-admin account[\s\S]{0,150}complete independent audit' -or
        $releaseGuideContents -notmatch '(?i)violation[\s\S]{0,100}exit nonzero') {
        throw 'The release guide must document the exact automatic-environment policy and GitHub scope-provenance limits.'
    }

    $githubSecurityContents = Get-Content -LiteralPath `
        (Join-Path $root 'scripts/Configure-GitHubSecurity.ps1') -Raw
    $githubSecurityAuditPatterns = @(
        "announcementEnvironmentName\s*=\s*'release-announcement'",
        "type\s*-eq\s*'required_reviewers'",
        'reviewerRules\.Count\s*-ne\s*0',
        'custom_branch_policies',
        'protected_branches',
        'deployment-branch-policies',
        'deploymentPolicies\.Count\s*-eq\s*1',
        "policyType\s*-ceq\s*'tag'",
        "\.name\s*-ceq\s*'v\*'",
        'announcementEndpoint/secrets',
        'announcementEndpoint/variables',
        "expectedSecretName\s*=\s*'DISCORD_RELEASE_BOT_TOKEN'",
        'secretNames\s*-cnotcontains\s*\$expectedSecretName',
        "'DISCORD_RELEASE_BOT_ID'",
        "'DISCORD_RELEASE_CHANNEL_ID'",
        "'DISCORD_RELEASE_ROLE_ID'",
        'unexpectedSecretNames',
        'unexpectedSecretNames\.Count\s*-ne\s*0',
        'unexpectedVariableNames',
        'variableNames\s*-cnotcontains\s*\$requiredVariable',
        'unexpectedVariableNames\.Count\s*-ne\s*0',
        'actions/secrets',
        'actions/variables',
        'actions/organization-secrets',
        'actions/organization-variables',
        'announcementAuditFailures',
        'releaseEnvironmentEndpoint/secrets',
        'releaseEnvironmentEndpoint/variables',
        'GitHub release-announcement configuration audit failed'
    )
    foreach ($pattern in $githubSecurityAuditPatterns) {
        if ($githubSecurityContents -notmatch $pattern) {
            throw "GitHub security configuration audit is missing a release-announcement provenance contract: $pattern"
        }
    }
    if ($githubSecurityContents -notmatch '(?i)must not require a reviewer' -or
        $githubSecurityContents -notmatch '(?i)exactly one custom deployment policy' -or
        @([regex]::Matches($githubSecurityContents, '(?i)permits fallback')).Count -lt 3 -or
        $githubSecurityContents -notmatch '(?i)workflow cannot distinguish at runtime') {
        throw 'GitHub security configuration must audit automatic posting, exact tag eligibility, and broader-scope fallback.'
    }

    $descriptorKeySecret = 'UPDATE_SIGNING_PRIVATE_KEY_PKCS8_BASE64'
    $discordBotSecret = 'DISCORD_RELEASE_BOT_TOKEN'
    $descriptorSigningContents = Get-Content -LiteralPath `
        (Join-Path $root 'scripts/Sign-ReleaseDescriptorDigest.ps1') -Raw
    if ($descriptorSigningContents -notmatch 'ImportPkcs8PrivateKey' -or
        $descriptorSigningContents -notmatch 'IeeeP1363FixedFieldConcatenation' -or
        $descriptorSigningContents -notmatch 'CryptographicOperations\]::ZeroMemory' -or
        $descriptorSigningContents -notmatch '\$digestFullPaths\.Count' -or
        $descriptorSigningContents -notmatch '\$canonicalDigest -cne \$digest' -or
        $descriptorSigningContents -notmatch 'Remove-Item Env:UPDATE_SIGNING_PRIVATE_KEY_PKCS8_BASE64') {
        throw 'The release-metadata signer must validate P-256, support bounded digest batches, and clear decoded key material.'
    }
    $releaseSignerContents = Get-Content -LiteralPath `
        (Join-Path $root 'SessionDock/tools/ReleaseSigner/Program.cs') -Raw
    $verifyAssetsContents = Get-Content -LiteralPath `
        (Join-Path $root 'scripts/Verify-Assets.ps1') -Raw
    $sbomContents = Get-Content -LiteralPath `
        (Join-Path $root 'scripts/New-ReleaseSbom.ps1') -Raw
    foreach ($catalogCommand in @(
            'prepare-catalog',
            'complete-catalog',
            'verify-catalog'
        )) {
        if ($releaseSignerContents -notmatch [regex]::Escape($catalogCommand)) {
            throw "The release signer is missing the required HandleScope command: $catalogCommand"
        }
    }
    if ($releaseSignerContents -notmatch 'HandleScopeCompatibilityCatalogPolicy\.CreateCanonicalPayload' -or
        $releaseSignerContents -notmatch 'HandleScopeCompatibilityCatalogPolicy\.VerifyEmbedded' -or
        $releaseSignerContents -notmatch 'HandleScopeCompatibilityCatalogPolicy\.Verify' -or
        $releaseSignerContents -notmatch 'sessiondock-version' -or
        $releaseSignerContents -notmatch 'prior-manifest' -or
        $releaseSignerContents -notmatch 'prior-directory' -or
        $releaseSignerContents -notmatch 'foreach \(var priorPath in priorPaths\)' -or
        $releaseSignerContents -notmatch 'maximumSequence = Math\.Max' -or
        $releaseSignerContents -notmatch 'prior\.GeneratedAt > maximumGeneratedAt' -or
        $releaseSignerContents -notmatch 'candidate\.Catalog\.Sequence <= maximumSequence' -or
        $releaseSignerContents -notmatch 'candidate\.GeneratedAt <= maximumGeneratedAt' -or
        $releaseSignerContents -notmatch 'candidate\.Catalog\.Sequence != 1' -or
        $verifyAssetsContents -notmatch '\[string\] \$CompatibilityCatalog' -or
        $verifyAssetsContents -notmatch '\[string\] \$ReleaseSigner' -or
        $verifyAssetsContents -notmatch '\[string\] \$PublicKey' -or
        $verifyAssetsContents -notmatch '\$catalogName = ''sessiondock-handlescope-compatibility\.json''' -or
        $verifyAssetsContents -notmatch '(?s)\$expectedReleaseFiles = @\(.*?\$catalogName.*?\)' -or
        $verifyAssetsContents -notmatch '& \$releaseSignerPath verify-catalog' -or
        $verifyAssetsContents -notmatch '\$catalog\.sessionDockVersion -cne \[string\] \$descriptor\.version') {
        throw 'Release signing and asset verification must enforce the signed, version-bound HandleScope catalog contract.'
    }
    if ($sbomContents -notmatch 'Microsoft\.AspNetCore\.App\.Runtime\.win-x64' -or
        $sbomContents -notmatch 'SPDXRef-Package-HandleScope' -or
        $sbomContents -notmatch "relationshipType = 'CONTAINS'" -or
        $sbomContents -notmatch 'ef3b926848353115296faaa9f48f1a5be8c8bae2' -or
        $releaseWorkflowContents -notmatch '-BundledHandleScopeManifest ./release-input/handlescope-upstream\.json') {
        throw 'Release SBOM generation must identify the pinned bundled HandleScope source and ASP.NET Core runtime.'
    }
    $secretReferences = @(Get-WorkflowSecretReferences -Contents $releaseWorkflowContents)
    $uniqueSecretReferences = @($secretReferences | Sort-Object -Unique)
    $expectedReleaseSecrets = @($descriptorKeySecret, $discordBotSecret) | Sort-Object
    if ($secretReferences.Count -ne 3 -or
        @(Compare-Object $expectedReleaseSecrets $uniqueSecretReferences -CaseSensitive).Count -ne 0) {
        throw 'The release workflow may receive only the protected descriptor key and Discord bot token.'
    }
    $descriptorSecretMatches = @([regex]::Matches(
        $releaseWorkflowContents,
        '\$\{\{\s*secrets\.UPDATE_SIGNING_PRIVATE_KEY_PKCS8_BASE64\s*\}\}'))
    $discordSecretMatches = @([regex]::Matches(
        $releaseWorkflowContents,
        '\$\{\{\s*secrets\.DISCORD_RELEASE_BOT_TOKEN\s*\}\}'))
    $discordBotIdMatches = @([regex]::Matches(
        $releaseWorkflowContents,
        '\$\{\{\s*vars\.DISCORD_RELEASE_BOT_ID\s*\}\}'))
    $discordChannelMatches = @([regex]::Matches(
        $releaseWorkflowContents,
        '\$\{\{\s*vars\.DISCORD_RELEASE_CHANNEL_ID\s*\}\}'))
    $discordRoleMatches = @([regex]::Matches(
        $releaseWorkflowContents,
        '\$\{\{\s*vars\.DISCORD_RELEASE_ROLE_ID\s*\}\}'))
    $preflightSecretReferences = @(Get-WorkflowSecretReferences -Contents $releasePreflightJob)
    $preflightProbeSecretReferences = @(Get-WorkflowSecretReferences -Contents $preflightProbeStep)
    $announcementSecretReferences = @(Get-WorkflowSecretReferences -Contents $releaseAnnouncementJob)
    $announcementSenderSecretReferences = @(Get-WorkflowSecretReferences -Contents $announcementSenderStep)
    if ($descriptorSecretMatches.Count -ne 1 -or
        $discordSecretMatches.Count -ne 2 -or
        $discordBotIdMatches.Count -ne 2 -or
        $discordChannelMatches.Count -ne 2 -or
        $discordRoleMatches.Count -ne 2 -or
        $preflightSecretReferences.Count -ne 1 -or
        $preflightProbeSecretReferences.Count -ne 1 -or
        $announcementSecretReferences.Count -ne 1 -or
        $announcementSenderSecretReferences.Count -ne 1 -or
        @([regex]::Matches($releasePreflightJob, 'vars\.DISCORD_RELEASE_(?:BOT_ID|CHANNEL_ID|ROLE_ID)')).Count -ne 3 -or
        @([regex]::Matches($preflightProbeStep, 'vars\.DISCORD_RELEASE_(?:BOT_ID|CHANNEL_ID|ROLE_ID)')).Count -ne 3 -or
        @([regex]::Matches($releaseAnnouncementJob, 'vars\.DISCORD_RELEASE_(?:BOT_ID|CHANNEL_ID|ROLE_ID)')).Count -ne 3 -or
        @([regex]::Matches($announcementSenderStep, 'vars\.DISCORD_RELEASE_(?:BOT_ID|CHANNEL_ID|ROLE_ID)')).Count -ne 3) {
        throw 'Discord credentials and identity variables must appear only in the GET-only preflight and final sender steps.'
    }

    $releaseAutomationContents = Get-Content -LiteralPath `
        (Join-Path $root 'discord-release-bot/src/release-automation.js') -Raw
    $releaseAutomationTests = Get-Content -LiteralPath `
        (Join-Path $root 'discord-release-bot/test/release-automation.test.js') -Raw
    if ($releaseAutomationContents -notmatch 'const DISCORD_API = "https://discord\.com/api/v10"' -or
        $releaseAutomationContents -notmatch 'const MAX_JSON_RESPONSE_BYTES = 1024 \* 1024' -or
        $releaseAutomationContents -notmatch 'enforce_nonce:\s*true' -or
        $releaseAutomationContents -notmatch 'allowed_mentions' -or
        $releaseAutomationContents -notmatch 'mention_roles' -or
        $releaseAutomationContents -notmatch 'MAX_HISTORY_PAGES = 100' -or
        $releaseAutomationContents -notmatch 'MAX_DISCORD_OPERATION_MILLISECONDS = 180_000' -or
        $releaseAutomationContents -notmatch 'export async function preflightAnnouncement\(' -or
        $releaseAutomationContents -notmatch 'function assertBotChannelPermissions\(' -or
        $releaseAutomationContents -notmatch 'roleId === channel\.guild_id' -or
        $releaseAutomationContents -notmatch 'PERMISSION_READ_MESSAGE_HISTORY' -or
        $releaseAutomationContents -notmatch 'PERMISSION_MENTION_EVERYONE' -or
        $releaseAutomationContents -notmatch 'DELIVERY_AMBIGUOUS' -or
        $releaseAutomationContents -notmatch 'role\.managed !== false' -or
        $releaseAutomationContents -notmatch 'function reserveReceipt\(' -or
        $releaseAutomationContents -notmatch 'openSync\(resolved, "wx", 0o600\)' -or
        $releaseAutomationContents -notmatch 'renameSync\(temporary, resolved\)' -or
        $releaseAutomationContents -notmatch 'temporaryCreated && existsSync\(temporary\)' -or
        $releaseAutomationContents -notmatch 'method === "POST"[\s\S]{0,250}ambiguous: true' -or
        $releaseAutomationContents -notmatch 'Math\.max\(milliseconds, Math\.ceil\(seconds \* 1000\)\)' -or
        $releaseAutomationContents -notmatch 'remainingMilliseconds = deadline - nowImpl\(\)' -or
        $releaseAutomationContents -notmatch 'AbortSignal\.timeout\(Math\.max\(1, Math\.min\(20_000, Math\.floor\(remainingMilliseconds\)\)\)\)' -or
        $releaseAutomationContents -notmatch 'actual\?\.thumbnail !== undefined' -or
        $releaseAutomationContents -notmatch 'actual\?\.author !== undefined' -or
        $releaseAutomationContents -notmatch 'actual\?\.video !== undefined' -or
        $releaseAutomationContents -notmatch 'actual\?\.provider !== undefined' -or
        $releaseAutomationContents -notmatch 'Object\.keys\(actual\.footer\)' -or
        $releaseAutomationContents -notmatch 'Object\.hasOwn\(actual \?\? \{\}, "timestamp"\)' -or
        $releaseAutomationContents -notmatch '!isAbsentOrEmptyArray\(message\.components\)' -or
        $releaseAutomationContents -notmatch '"poll"' -or
        $releaseAutomationContents -notmatch 'message\.flags !== undefined && message\.flags !== 0' -or
        $releaseAutomationContents -notmatch 'message\.type !== 0' -or
        $releaseAutomationContents -notmatch 'message\.tts !== false' -or
        $releaseAutomationContents -notmatch 'message\.edited_timestamp !== null' -or
        $releaseAutomationContents -notmatch 'message\.pinned !== false' -or
        $releaseAutomationContents -notmatch '!isAbsentOrEmptyArray\(message\.sticker_items\)' -or
        $releaseAutomationContents -notmatch '"message_reference"' -or
        $releaseAutomationContents -notmatch '"interaction_metadata"' -or
        $releaseAutomationContents -notmatch 'actualInline !== expectedInline' -or
        $releaseAutomationContents -notmatch 'actual\.image\.url !== displayedAttachment\?\.url' -or
        $releaseAutomationContents -notmatch 'message\.id !== expectedMessageId' -or
        $releaseAutomationContents -notmatch 'async function readBoundedResponseBytes\(' -or
        $releaseAutomationContents -notmatch 'content-length' -or
        $releaseAutomationContents -notmatch 'response\.body\.getReader\(\)' -or
        $releaseAutomationContents -notmatch 'reader\.read\(\)' -or
        $releaseAutomationContents -notmatch 'reader\.cancel\(\)' -or
        $releaseAutomationContents -notmatch 'chunk\.value\.byteLength > maxBytes - totalBytes' -or
        $releaseAutomationContents -notmatch 'maxBytes:\s*MAX_JSON_RESPONSE_BYTES' -or
        $releaseAutomationContents -notmatch 'maxBytes:\s*Math\.min\(expectedBytes, MAX_IMAGE_BYTES\)' -or
        $releaseAutomationContents -match 'arrayBuffer\s*\(' -or
        $releaseAutomationContents -match '(?i)discord(?:_api|_base|_webhook)_url' -or
        $releaseAutomationContents -match 'Math\.random|Date\.now|GITHUB_RUN_ATTEMPT' -or
        @([regex]::Matches($releaseAutomationContents, 'method:\s*"POST"')).Count -ne 1) {
        throw 'Discord automation must retain its fixed endpoint, deterministic nonce, bounded reconciliation, receipt preflight, and verified-image contract.'
    }
    $requiredDiscordRegressionTests = @(
        'the staged standalone module executes workflow-shaped generate and verify commands',
        'the read-only preflight proves identity, permissions, role, and history without posting',
        'preflight rejects an early matching announcement and never posts',
        'preflight proves Read Message History through effective channel permissions',
        'effective channel permission overwrites follow Discord precedence',
        'preflight rejects @everyone in Bota''s assigned member roles',
        'the workflow-shaped preflight CLI uses the standalone staged module and makes no POST',
        'an existing announcement reread is bound to the exact history message ID',
        'a same-tag marker from different immutable inputs fails closed',
        'an ambiguous POST is reconciled without a second POST',
        'an accepted POST with an unreadable body reconciles as confirmed without reposting',
        'an accepted POST with malformed JSON stays ambiguous and is never reposted',
        'an accepted POST with an empty body reconciles as confirmed without reposting',
        'a definitive POST rate limit honors the full Retry-After before one nonce-safe retry',
        'a delayed rate-limit wakeup cannot start a POST beyond the delivery deadline',
        'all Discord preflight requests share one bounded operation deadline',
        'a POST 5xx is reconciled by history and is never blindly retried',
        'history pagination finds an existing announcement on page two',
        'a read-back payload mismatch is ambiguous and never causes a second POST',
        'a post read-back is bound to the exact accepted message ID',
        'a displayed embed image must be the same verified reviewed attachment',
        'unexpected display-bearing embed fields fail closed',
        'an oversized declared attachment is rejected before reading CDN bytes',
        'an oversized streamed attachment is canceled at the reviewed byte boundary',
        'an oversized declared POST response is rejected before its body is read and reconciled once',
        'an oversized streamed POST response is canceled and reconciled without reposting',
        'unexpected top-level display state fails closed',
        'unexpected embed timestamps and changed field inline layout fail closed',
        'an explicit false field inline value is equivalent to its Discord-default absence',
        'the configured release role must explicitly be unmanaged',
        'the CLI refuses an existing receipt before any network request',
        'the CLI refuses an unwritable receipt path before any network request',
        'the CLI finalizes a confirmed receipt and exits successfully',
        'the CLI preserves an ambiguous delivery receipt and safe exit output',
        'a confirmed delivery reports receipt finalization failure without replacing reserved evidence',
        'an ambiguous delivery keeps its classification when receipt finalization also fails'
    )
    Assert-RequiredNodeTestDeclarations `
        -Contents $releaseAutomationTests `
        -Names $requiredDiscordRegressionTests `
        -Label 'Discord automation'
    Assert-RequiredNodeTestsExecute `
        -Path (Join-Path $root 'discord-release-bot/test/release-automation.test.js') `
        -Names $requiredDiscordRegressionTests `
        -Label 'Discord automation'
    $requiredPermissionPrecedenceCases = @(
        'an assigned role allow restores an @everyone denial'
        'combined role allows win over combined role denials'
        'a member denial wins after an assigned role allowance'
        'a member allowance wins after an assigned role denial'
        'Administrator is rejected even when channel overwrites deny it'
    )
    foreach ($caseName in $requiredPermissionPrecedenceCases) {
        $casePattern = 'name:\s*"{0}"' -f [regex]::Escape($caseName)
        if (@([regex]::Matches($releaseAutomationTests, $casePattern)).Count -ne 1) {
            throw "Discord permission precedence case must appear exactly once: $caseName"
        }
    }
    if ($releaseAutomationTests -notmatch 'for \(const permissionCase of cases\)' -or
        $releaseAutomationTests -notmatch 'await t\.test\(permissionCase\.name') {
        throw 'Discord permission precedence cases must all execute through the reviewed table-driven test.'
    }

    $discordPackage = Get-Content -LiteralPath `
        (Join-Path $root 'discord-release-bot/package.json') -Raw | ConvertFrom-Json
    $discordConfigContents = Get-Content -LiteralPath `
        (Join-Path $root 'discord-release-bot/src/config.js') -Raw
    $discordReleaseContents = Get-Content -LiteralPath `
        (Join-Path $root 'discord-release-bot/src/release.js') -Raw
    $discordConfigTests = Get-Content -LiteralPath `
        (Join-Path $root 'discord-release-bot/test/config.test.js') -Raw
    $discordReleaseTests = Get-Content -LiteralPath `
        (Join-Path $root 'discord-release-bot/test/release.test.js') -Raw
    $discordReadme = Get-Content -LiteralPath `
        (Join-Path $root 'discord-release-bot/README.md') -Raw
    $discordInvite = Get-Content -LiteralPath `
        (Join-Path $root 'discord-release-bot/src/invite.js') -Raw
    if ($discordPackage.scripts.PSObject.Properties.Name -contains 'start' -or
        $discordPackage.scripts.PSObject.Properties.Name -contains 'deploy' -or
        $discordPackage.scripts.PSObject.Properties.Name -contains 'invite' -or
        $discordPackage.scripts.PSObject.Properties.Name -notcontains 'community:start' -or
        $discordPackage.scripts.PSObject.Properties.Name -notcontains 'community:deploy' -or
        $discordPackage.scripts.PSObject.Properties.Name -notcontains 'community:invite' -or
        $discordPackage.scripts.test -cne 'node --test' -or
        $discordReadme -notmatch '(?i)not used by the release workflow' -or
        $discordReadme -notmatch '(?i)must not be used to publish official' -or
        $discordReadme -notmatch '(?m)^npm ci\s*$' -or
        $discordReadme -notmatch '(?m)^npm test\s*$' -or
        $discordReadme -notmatch '(?m)^npm run check\s*$' -or
        $discordReadme -notmatch '(?m)^npm audit --omit=dev --audit-level=moderate\s*$' -or
        $discordInvite -notmatch 'PermissionFlagsBits\.ReadMessageHistory' -or
        $discordInvite -match 'PermissionFlagsBits\.MentionEveryone') {
        throw 'The interactive Discord bot must remain unmistakably noncanonical and least-privileged.'
    }
    if ($discordConfigContents -notmatch 'export const DISCORD_EMBED_TEXT_LIMIT\s*=\s*6000\s*;' -or
        $discordConfigContents -notmatch 'export const MAX_IMAGE_FILE_BYTES\s*=\s*10 \* 1024 \* 1024\s*;' -or
        $discordReleaseContents -notmatch 'countEmbedTextCharacters\(embeds\) > DISCORD_EMBED_TEXT_LIMIT' -or
        @([regex]::Matches($discordReleaseContents, '> MAX_IMAGE_FILE_BYTES')).Count -lt 2 -or
        $discordReleaseContents -notmatch 'function normalizeDiscordAttachmentUrl\(' -or
        $discordReleaseContents -notmatch 'hostname !== "cdn\.discordapp\.com"' -or
        $discordReleaseContents -notmatch 'async function readBoundedImage\(' -or
        $discordReleaseContents -notmatch 'response\.body\.getReader\(\)' -or
        $discordReleaseContents -notmatch 'redirect:\s*"error"' -or
        $discordReleaseContents -match 'arrayBuffer\s*\(') {
        throw "The optional community bot must enforce bounded, trusted Discord image retrieval and embed limits."
    }
    $requiredCommunityRegressionTests = @(
        'the release command stores an owner-bound draft before presenting its modal',
        'the configured footer and aggregate image limits have safe exact boundaries',
        'the checked-in dotenv example preserves the hash-prefixed embed color',
        'release embed text has an exact 6000-character aggregate budget',
        'aggregate embed text counting includes author and field names and values',
        'image drafts enforce exact per-file and hard aggregate boundaries',
        'downloadImages rejects unsupported response types and spoofed image bytes',
        'downloadImages enforces the 10 MiB boundary before and after download',
        'a sent community release stays successful when only its private acknowledgement fails'
    )
    Assert-RequiredNodeTestDeclarations `
        -Contents ($discordConfigTests + "`n" + $discordReleaseTests) `
        -Names $requiredCommunityRegressionTests `
        -Label 'Community Discord bot'

    $repositoryFiles = @(& git ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to inspect tracked and candidate repository files.'
    }

    $allowedEnvironmentExample = 'discord-release-bot/.env.example'
    if ($repositoryFiles -notcontains $allowedEnvironmentExample) {
        throw "Required environment example is missing from the repository file view: $allowedEnvironmentExample"
    }
    $forbiddenPatterns = @(
        '(^|/)(bin|obj|artifacts|publish|Releases|TestResults)/',
        '(?i)(private|secret|credential)[^/]*\.(pem|key|pfx|p12|jks|keystore)$',
        '(?i)update-private-key\.pem$',
        '(?i)(^|/)(id_rsa|id_ed25519)(\.|$)',
        '(?i)\.(robloxone-update|sessiondock-update|nupkg|snupkg)$'
    )
    foreach ($file in $repositoryFiles) {
        if ($file -match '(^|/)\.env(?:$|\.)' -and
            $file -cne $allowedEnvironmentExample) {
            throw "Only the reviewed environment example may be present: $file"
        }
        foreach ($pattern in $forbiddenPatterns) {
            if ($file -match $pattern) {
                throw "Generated output or sensitive material must not be present: $file"
            }
        }
    }

    $environmentExampleContents = Get-Content -LiteralPath `
        (Join-Path $root $allowedEnvironmentExample) -Raw
    if ($environmentExampleContents -notmatch
            '(?m)^RELEASE_EMBED_COLOR=(?:"#5865F2"|''#5865F2'')\r?$') {
        throw 'Environment example must quote the hash-prefixed Discord embed color for dotenv.'
    }
    foreach ($emptyPlaceholder in @('DISCORD_CLIENT_ID', 'DISCORD_TOKEN', 'DISCORD_GUILD_ID')) {
        if ($environmentExampleContents -notmatch
                "(?m)^$([regex]::Escape($emptyPlaceholder))=[ \t]*\r?$") {
            throw "Environment example must keep $emptyPlaceholder empty."
        }
    }
    if ($environmentExampleContents -match
            '(?m)^[A-Z0-9_]*(?:TOKEN|SECRET|PASSWORD|PRIVATE_KEY)[A-Z0-9_]*[ \t]*=[ \t]*[^ \t\r\n]') {
        throw 'Environment example must not contain a sensitive example value.'
    }

    if ($repositoryFiles.Count -gt 0) {
        $secretContentPattern =
            'BEGIN (RSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY|gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|\.ROBLOSECURITY|A(KIA|SIA)[0-9A-Z]{16}|xox[baprs]-|AIza[0-9A-Za-z_-]{30,}'
        & git grep --untracked -I -q -E $secretContentPattern -- . `
            ':(exclude)scripts/Verify-Repository.ps1'
        $secretScanExitCode = $LASTEXITCODE
        if ($secretScanExitCode -eq 0) {
            throw 'A tracked file matches a prohibited credential or private-key pattern.'
        }
        if ($secretScanExitCode -ne 1) {
            throw "Tracked-content secret scan failed with exit code $secretScanExitCode."
        }
        $global:LASTEXITCODE = 0

        $machinePathPattern = '([A-Za-z]:\\Users\\[^\\/[:space:]]+|/home/[^/[:space:]]+|/Users/[^/[:space:]]+)'
        & git grep --untracked -I -q -E $machinePathPattern -- . `
            ':(exclude)scripts/Verify-Repository.ps1'
        $pathScanExitCode = $LASTEXITCODE
        if ($pathScanExitCode -eq 0) {
            throw 'A tracked file contains a machine-specific user path.'
        }
        if ($pathScanExitCode -ne 1) {
            throw "Tracked-content path scan failed with exit code $pathScanExitCode."
        }
        $global:LASTEXITCODE = 0
    }

    $projects = @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.csproj' |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
    foreach ($projectFile in $projects) {
        [xml] $projectXml = Get-Content -LiteralPath $projectFile.FullName -Raw
        foreach ($reference in @($projectXml.SelectNodes('//PackageReference'))) {
            $declaredVersion = if ($reference.Version) {
                [string] $reference.Version
            }
            else {
                $versionNode = $reference.SelectSingleNode('Version')
                if ($null -eq $versionNode) { '' } else { [string] $versionNode.InnerText }
            }
            if ($declaredVersion -cnotmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
                throw "Package '$($reference.Include)' in $($projectFile.Name) must use an exact stable version."
            }
        }
    }
    [xml] $applicationProject = Get-Content -LiteralPath (Get-ApplicationProject) -Raw
    $applicationIdentity = @{
        AssemblyName = 'SessionDock'
        RootNamespace = 'SessionDock'
        Product = 'SessionDock'
        RepositoryUrl = 'https://github.com/Makmatoe/SessionDock'
    }
    foreach ($identity in $applicationIdentity.GetEnumerator()) {
        $values = @($applicationProject.SelectNodes(
                "/Project/PropertyGroup/$($identity.Key)") |
            ForEach-Object { $_.InnerText } |
            Where-Object { $_ })
        if ($values.Count -ne 1 -or $values[0] -cne $identity.Value) {
            throw "The application $($identity.Key) must be '$($identity.Value)'."
        }
    }
    $runtimeVersions = @($applicationProject.SelectNodes('/Project/PropertyGroup/RuntimeFrameworkVersion') |
        ForEach-Object { $_.InnerText } | Where-Object { $_ })
    if ($runtimeVersions.Count -ne 1 -or $runtimeVersions[0] -cne '10.0.10') {
        throw 'The self-contained .NET runtime and shipped notices must remain pinned to 10.0.10.'
    }
    $directoryBuildContents = Get-Content -LiteralPath `
        (Join-Path $root 'Directory.Build.props') -Raw
    if ($directoryBuildContents -notmatch '<NuGetAuditMode>all</NuGetAuditMode>' -or
        $directoryBuildContents -notmatch '<NuGetAuditLevel>moderate</NuGetAuditLevel>' -or
        $directoryBuildContents -notmatch 'NU1902;NU1903;NU1904') {
        throw 'NuGet auditing must include transitives and fail for moderate-or-higher vulnerabilities.'
    }
    $applicationProjectText = Get-Content -LiteralPath (Get-ApplicationProject) -Raw
    if ($applicationProjectText -notmatch 'EnableRuntimeSmokeHarness' -or
        $applicationProjectText -notmatch 'SESSIONDOCK_SMOKE_HARNESS' -or
        $applicationProjectText -notmatch 'Compile Remove="Services\\RuntimeSmokeTestOptions\.cs"') {
        throw 'The isolated runtime smoke harness must remain compile-time test-only.'
    }
    $publishVerifierContents = Get-Content -LiteralPath `
        (Join-Path $root 'scripts/Verify-Publish.ps1') -Raw
    if ($publishVerifierContents -notmatch '--isolated-runtime-smoke' -or
        $publishVerifierContents -notmatch 'Production SessionDock\.exe contains') {
        throw 'Production publish verification must prove the privileged smoke switch is absent.'
    }
    $buildContents = Get-Content -LiteralPath `
        (Join-Path $root 'scripts/Build.ps1') -Raw
    if ($buildContents -notmatch 'Sync-BundledHandleScope\.ps1' -or
        $buildContents -notmatch 'Write-CombinedDotNetThirdPartyNotices' -or
        $buildContents -notmatch 'microsoft\.aspnetcore\.app\.runtime\.win-x64/10\.0\.10/THIRD-PARTY-NOTICES\.TXT' -or
        $publishVerifierContents -notmatch 'SessionDock\.HandleScope\.dll' -or
        $publishVerifierContents -notmatch 'microsoft\.aspnetcore\.app\.runtime\.win-x64/10\.0\.10/THIRD-PARTY-NOTICES\.TXT' -or
        $publishVerifierContents -notmatch 'component sidecar') {
        throw 'Production builds must verify bundled HandleScope identity, reject sidecars, and combine pinned ASP.NET Core notices.'
    }

    $workflowDirectory = Join-Path $root '.github/workflows'
    if (Test-Path -LiteralPath $workflowDirectory -PathType Container) {
        $mutableActionRef = '(?m)^\s*-?\s*uses:\s*[^#\r\n]+@(?![0-9a-f]{40}(?:\s|#|$))'
        $exactSdkPattern = '(?m)^\s+dotnet-version:\s*[''"]?{0}[''"]?\s*$' -f
            [regex]::Escape($expectedSdk)
        $workflowFiles = @(Get-ChildItem -LiteralPath $workflowDirectory -File |
            Where-Object { $_.Extension -in @('.yml', '.yaml') })
        foreach ($workflow in $workflowFiles) {
            $contents = Get-Content -LiteralPath $workflow.FullName -Raw
            $workflowSecretReferences = @(
                Get-WorkflowSecretReferences -Contents $contents)
            if ($contents -match $mutableActionRef) {
                throw "Workflow action references must use full commit SHAs: $($workflow.Name)"
            }
            if ($contents -match '(?m)^\s*pull_request_target\s*:') {
                throw "pull_request_target is intentionally prohibited: $($workflow.Name)"
            }
            if ($contents -match 'actions/checkout@' -and
                $contents -notmatch '(?m)^\s+persist-credentials:\s*false\s*$') {
                throw "Workflow checkout must disable persisted credentials: $($workflow.Name)"
            }
            if ($contents -match '(?m)^\s*pull_request\s*:' -and
                $workflowSecretReferences.Count -ne 0) {
                throw "Pull-request workflows must not reference repository secrets: $($workflow.Name)"
            }
            if ($contents -match '(?m)^\s*secrets\s*:\s*inherit\s*$') {
                throw "Workflow secret inheritance is intentionally prohibited: $($workflow.Name)"
            }
            if ($workflow.Name -cne 'release.yml' -and
                ($workflowSecretReferences.Count -ne 0 -or
                 $contents -match '(?m)^\s+environment\s*:' -or
                 $contents -match 'UPDATE_SIGNING_PRIVATE_KEY_PKCS8_BASE64|DISCORD_RELEASE_(?:BOT_TOKEN|BOT_ID|CHANNEL_ID|ROLE_ID)')) {
                throw "Secrets and protected environments are reserved exclusively for release.yml: $($workflow.Name)"
            }
            if ($workflow.Name -cne 'release.yml' -and
                $contents -match '(?m)^\s+(contents|id-token|attestations):\s*write\s*$') {
                throw "Write permissions are reserved for the protected release workflow: $($workflow.Name)"
            }
            if ($workflow.Name -ceq 'release.yml' -and
                ($contents -match '(?m)^\s*workflow_dispatch\s*:' -or
                 @([regex]::Matches($contents, '(?m)^\s+environment:\s*release\s*$')).Count -ne 1 -or
                 @([regex]::Matches($contents, '(?m)^\s+environment:\s*release-publication\s*$')).Count -ne 1 -or
                 @([regex]::Matches($contents, '(?m)^\s+environment:\s*release-announcement\s*$')).Count -ne 2 -or
                 $contents -match '--clobber')) {
                throw 'Release workflow must be tag-only, separately environment-protected, and non-clobbering.'
            }
            if ($workflow.Name -ceq 'release.yml') {
                $uniqueWorkflowSecretReferences = @(
                    $workflowSecretReferences | Sort-Object -Unique)
                if ($workflowSecretReferences.Count -ne 3 -or
                    @(Compare-Object `
                            $expectedReleaseSecrets `
                            $uniqueWorkflowSecretReferences `
                            -CaseSensitive).Count -ne 0) {
                    throw 'The release workflow may receive only the protected descriptor key and Discord bot token.'
                }
                if ($contents -notmatch '(?m)^\s+artifact-metadata:\s*write\s*$' -or
                    $contents -notmatch 'actions/attest@') {
                    throw 'The release staging job must retain GitHub artifact attestation permissions.'
                }
            }
            if ($contents -match 'actions/setup-dotnet@' -and
                $contents -notmatch $exactSdkPattern) {
                throw "Workflow must install the exact repository SDK ${expectedSdk}: $($workflow.Name)"
            }
            if ($contents -match '(?m)^\s+global-json-file:') {
                throw "Workflow must use an exact dotnet-version, not setup-dotnet's feature-band global-json behavior: $($workflow.Name)"
            }
        }
    }

    $maintenanceWorkflow = Get-Content -LiteralPath `
        (Join-Path $root '.github/workflows/dotnet-security-maintenance.yml') -Raw
    if ($maintenanceWorkflow -notmatch '(?m)^\s*schedule:\s*$' -or
        $maintenanceWorkflow -notmatch 'Test-DotNetSecurityPatch\.ps1 -CheckOnline' -or
        $maintenanceWorkflow -notmatch '(?m)^permissions:\s*\r?\n\s+contents:\s*read\s*$') {
        throw 'A scheduled fail-closed official .NET patch check is required.'
    }

    $handleScopeReviewWorkflow = Get-Content -LiteralPath `
        (Join-Path $root '.github/workflows/handlescope-upstream-review.yml') -Raw
    if ($handleScopeReviewWorkflow -notmatch '(?m)^\s*schedule:\s*$' -or
        $handleScopeReviewWorkflow -notmatch '(?m)^\s*workflow_dispatch:\s*$' -or
        $handleScopeReviewWorkflow -notmatch '(?m)^\s+contents:\s*read\s*$' -or
        $handleScopeReviewWorkflow -notmatch '(?m)^\s+issues:\s*write\s*$' -or
        $handleScopeReviewWorkflow -notmatch 'Sync-BundledHandleScope\.ps1 -UpstreamPath \$upstream' -or
        $handleScopeReviewWorkflow -notmatch 'refs/tags/v0\.3\.0:refs/tags/v0\.3\.0' -or
        $handleScopeReviewWorkflow -notmatch '/repos/Makmatoe/HandleScope/releases/latest' -or
        $handleScopeReviewWorkflow -notmatch 'gh issue create' -or
        $handleScopeReviewWorkflow -match '(?i)gh\s+(?:release|pr\s+merge)|git\s+push|contents:\s*write|pull_request_target') {
        throw 'The scheduled HandleScope review must verify locally, report through an issue, and remain unable to merge or release.'
    }

    if ($CI) {
        foreach ($projectFile in $projects) {
            $lockFile = Join-Path $projectFile.DirectoryName 'packages.lock.json'
            if (-not (Test-Path -LiteralPath $lockFile -PathType Leaf)) {
                throw "CI requires a committed lock file beside every project: $lockFile"
            }
        }
    }

    Write-Host "Repository validation passed for SessionDock $version."
}
finally {
    Pop-Location
}
