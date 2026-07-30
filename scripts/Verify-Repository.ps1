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

Push-Location $root
try {
    $requiredFiles = @(
        '.config/dotnet-tools.json',
        '.github/workflows/ci.yml',
        '.github/workflows/dotnet-security-maintenance.yml',
        '.github/workflows/release.yml',
        'Directory.Build.props',
        'global.json',
        'NuGet.Config',
        'LICENSE.md',
        'THIRD_PARTY_NOTICES.md',
        'docs/RELEASING.md',
        'SessionDock/SessionDock.csproj',
        'SessionDock/Assets/SessionDock.Icon.png',
        'SessionDock/Assets/SessionDock.ico',
        'SessionDock/Resources/update-public-key.pem',
        'SessionDock.ReleaseTrust/ReleaseDescriptorPolicy.cs',
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
        'scripts/Enable-HandleScope.ps1',
        'scripts/Verify-Assets.ps1',
        'scripts/Verify-Publish.ps1',
        'scripts/Verify-ReleaseLicense.ps1'
    )
    foreach ($relativePath in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $root $relativePath) -PathType Leaf)) {
            throw "Required repository file is missing: $relativePath"
        }
    }
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

    [xml] $applicationProject = Get-Content -LiteralPath `
        (Join-Path $root 'SessionDock/SessionDock.csproj') -Raw
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
    $releaseStageJob = Get-WorkflowJobBlock `
        -Contents $releaseWorkflowContents `
        -Name 'sign-attest-and-stage'
    $releasePublishJob = Get-WorkflowJobBlock `
        -Contents $releaseWorkflowContents `
        -Name 'publish-verified-release'
    $dependencyReviewJob = Get-WorkflowJobBlock `
        -Contents $ciWorkflowContents `
        -Name 'dependency-review'

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
    $releaseRuntimeSmoke =
        './scripts/Build-RuntimeSmoke.ps1 -OutputDirectory artifacts/release-runtime-smoke -TimeoutSeconds 30'
    if (-not $releaseValidateJob.Contains($releaseRuntimeSmoke)) {
        throw 'Protected release validation must execute the isolated published-executable runtime smoke.'
    }

    if ($releaseStageJob -notmatch '(?m)^    environment:\s*release\s*$' -or
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
        throw 'The protected staging job must sign only the descriptor, draft, and attest with its required permissions.'
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

    if ($releasePublishJob -notmatch '(?ms)^    needs:\s*\r?\n      - validate-and-build\s*\r?\n      - sign-attest-and-stage\s*$' -or
        $releasePublishJob -notmatch '(?m)^    environment:\s*release-publication\s*$' -or
        $releasePublishJob -notmatch '(?m)^      contents:\s*write\s*$' -or
        $releasePublishJob -notmatch '(?m)^      attestations:\s*read\s*$' -or
        $releasePublishJob -notmatch 'gh release download' -or
        $releasePublishJob -notmatch 'SHA256SUMS\.txt' -or
        $releasePublishJob -notmatch 'Compare-Object \$expectedNames \$actualNames -CaseSensitive' -or
        $releasePublishJob -notmatch '\[Collections\.Generic\.Dictionary\[string, string\]\]::new\(\s*\r?\n\s*\[StringComparer\]::Ordinal\)' -or
        $releasePublishJob -notmatch '(?s)Compare-Object\s+`\s*\r?\n\s*\$expectedChecksumNames\s+`\s*\r?\n\s*@\(\$checksumEntries\.Keys \| Sort-Object\)\s+`\s*\r?\n\s*-CaseSensitive' -or
        $releasePublishJob -notmatch '(?s)gh attestation verify \$asset\.FullName\s+`\s*\r?\n\s*--repo \$env:GITHUB_REPOSITORY\s+`\s*\r?\n\s*--signer-workflow \$env:EXPECTED_SIGNER_WORKFLOW\s+`\s*\r?\n\s*--source-ref \$env:EXPECTED_SOURCE_REF\s+`\s*\r?\n\s*--source-digest \$env:EXPECTED_SOURCE_DIGEST' -or
        $releasePublishJob -notmatch 'EXPECTED_SIGNER_WORKFLOW:\s*Makmatoe/SessionDock/\.github/workflows/release\.yml' -or
        $releasePublishJob -notmatch 'EXPECTED_SOURCE_REF:\s*\$\{\{\s*github\.ref\s*\}\}' -or
        $releasePublishJob -notmatch 'EXPECTED_SOURCE_DIGEST:\s*\$\{\{\s*github\.sha\s*\}\}' -or
        $releasePublishJob -notmatch 'gh release edit[^\r\n]*--draft=false[^\r\n]*--latest') {
        throw 'Final publication must be separately approved and must reverify the exact draft and bounded provenance before publishing it.'
    }
    if ($releasePublishJob -match '(?m)^      (?:id-token|artifact-metadata):\s*write\s*$' -or
        $releasePublishJob -match '(?m)^      attestations:\s*write\s*$' -or
        $releasePublishJob -match '\$\{\{\s*secrets\.' -or
        $releasePublishJob -match 'ReleaseSigner|private-key') {
        throw 'The final publication job must not receive signing secrets or attestation write permissions.'
    }
    if (@([regex]::Matches($releaseWorkflowContents, '--draft=false')).Count -ne 1) {
        throw 'Only the separately approved final job may make a release public.'
    }

    $releaseGuideContents = Get-Content -LiteralPath `
        (Join-Path $root 'docs/RELEASING.md') -Raw
    if ($releaseGuideContents -notmatch '`release-publication`' -or
        $releaseGuideContents -notmatch 'Build-RuntimeSmoke\.ps1' -or
        $releaseGuideContents -notmatch 'UPDATE_SIGNING_PRIVATE_KEY_PKCS8_BASE64' -or
        $releaseGuideContents -notmatch 'Unknown publisher' -or
        $releaseGuideContents -notmatch '(?i)draft[\s\S]{0,500}approval') {
        throw 'The release guide must document runtime smoke and the separate draft-publication approval.'
    }

    $descriptorKeySecret = 'UPDATE_SIGNING_PRIVATE_KEY_PKCS8_BASE64'
    $descriptorSigningContents = Get-Content -LiteralPath `
        (Join-Path $root 'scripts/Sign-ReleaseDescriptorDigest.ps1') -Raw
    if ($descriptorSigningContents -notmatch 'ImportPkcs8PrivateKey' -or
        $descriptorSigningContents -notmatch 'IeeeP1363FixedFieldConcatenation' -or
        $descriptorSigningContents -notmatch 'CryptographicOperations\]::ZeroMemory' -or
        $descriptorSigningContents -notmatch 'Remove-Item Env:UPDATE_SIGNING_PRIVATE_KEY_PKCS8_BASE64') {
        throw 'The update-descriptor signer must validate P-256 and clear decoded key material.'
    }
    $secretReferences = @([regex]::Matches(
        $releaseWorkflowContents,
        '\$\{\{\s*secrets\.([A-Za-z0-9_]+)\s*\}\}') |
        ForEach-Object { $_.Groups[1].Value } |
        Sort-Object -Unique)
    if ($secretReferences.Count -ne 1 -or
        $secretReferences[0] -cne $descriptorKeySecret) {
        throw 'The release workflow may receive only the protected update-descriptor key.'
    }

    $trackedFiles = @(& git ls-files)
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to inspect tracked repository files.'
    }

    $forbiddenPatterns = @(
        '(^|/)(bin|obj|artifacts|publish|Releases|TestResults)/',
        '(^|/)\.env($|\.)',
        '(?i)(private|secret|credential)[^/]*\.(pem|key|pfx|p12|jks|keystore)$',
        '(?i)update-private-key\.pem$',
        '(?i)(^|/)(id_rsa|id_ed25519)(\.|$)',
        '(?i)\.(robloxone-update|sessiondock-update|nupkg|snupkg)$'
    )
    foreach ($file in $trackedFiles) {
        foreach ($pattern in $forbiddenPatterns) {
            if ($file -match $pattern) {
                throw "Generated output or sensitive material must not be tracked: $file"
            }
        }
    }

    if ($trackedFiles.Count -gt 0) {
        $secretContentPattern =
            'BEGIN (RSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY|gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|\.ROBLOSECURITY|A(KIA|SIA)[0-9A-Z]{16}|xox[baprs]-|AIza[0-9A-Za-z_-]{30,}'
        & git grep -I -q -E $secretContentPattern -- . `
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
        & git grep -I -q -E $machinePathPattern -- . `
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

    $workflowDirectory = Join-Path $root '.github/workflows'
    if (Test-Path -LiteralPath $workflowDirectory -PathType Container) {
        $mutableActionRef = '(?m)^\s*-?\s*uses:\s*[^#\r\n]+@(?![0-9a-f]{40}(?:\s|#|$))'
        $exactSdkPattern = '(?m)^\s+dotnet-version:\s*[''"]?{0}[''"]?\s*$' -f
            [regex]::Escape($expectedSdk)
        foreach ($workflow in Get-ChildItem -LiteralPath $workflowDirectory -File -Include '*.yml', '*.yaml') {
            $contents = Get-Content -LiteralPath $workflow.FullName -Raw
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
                $contents -match '\$\{\{\s*secrets\.') {
                throw "Pull-request workflows must not reference repository secrets: $($workflow.Name)"
            }
            if ($workflow.Name -cne 'release.yml' -and
                $contents -match '(?m)^\s+(contents|id-token|attestations):\s*write\s*$') {
                throw "Write permissions are reserved for the protected release workflow: $($workflow.Name)"
            }
            if ($workflow.Name -ceq 'release.yml' -and
                ($contents -match '(?m)^\s*workflow_dispatch\s*:' -or
                 $contents -notmatch '(?m)^\s+environment:\s*release\s*$' -or
                 $contents -notmatch '(?m)^\s+environment:\s*release-publication\s*$' -or
                 $contents -match '--clobber')) {
                throw 'Release workflow must be tag-only, separately environment-protected, and non-clobbering.'
            }
            if ($workflow.Name -ceq 'release.yml') {
                $secretReferences = @([regex]::Matches(
                    $contents,
                    '\$\{\{\s*secrets\.([A-Za-z0-9_]+)\s*\}\}') |
                    ForEach-Object { $_.Groups[1].Value } |
                    Sort-Object -Unique)
                if ($secretReferences.Count -ne 1 -or
                    $secretReferences[0] -cne $descriptorKeySecret) {
                    throw 'The release workflow may receive only the protected update-descriptor key.'
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
