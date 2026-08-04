[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ManifestPath,

    [switch] $StagedManifestOnly,

    [switch] $PassThru
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedSourceCommit = '1e3b6bfbbc5a2335af6c863cdcc32e2b70c7ffc1'
$expectedManifestSha256 = '4cc1d743ab40619b4bb48d77995534e60b611aacc0153ab80a1f7e8502e88397'
$expectedLicenseSha256 = '5944250b546861e4e616de520b7d06513fec435a5651fc49d83ae92d3cf14bf2'
$expectedLicenseGitBlob = '730ba1584a9ef1002dfca75b3b18a8d889052fbc'
$expectedCanonicalManifestSha256 =
    '83ef063c36a990a0322e867d67e9a0db151c88424af17b5bed1eada1901b6232'
$expectedProjectSha256 = '76e3be05eea91e5526965d05da043219da67afdc52a423b07707b63fdfaa1841'
$expectedProjectGitBlob = '07fe8f9ec14088750f6d2a0d835c86b678a0f76e'
$expectedProjectPath = 'SessionDock.ExactWheel/SessionDock.ExactWheel.csproj'
$expectedProjectBytes = 1311L
$expectedInventory = @(
    [pscustomobject]@{
        Path = 'ExactWheelCoordinateTransforms.cs'
        Bytes = 9007L
        Sha256 = '5e18669e74e3ab983fb354acdc77b9a041f13b9f85d06bd73a22dcabfd02a0ba'
        GitBlob = '4c30ab57a2a72b70cfdd4f5c9e1cbbb30c0d00d8'
    },
    [pscustomobject]@{
        Path = 'ExactWheelDesktopCapture.cs'
        Bytes = 14678L
        Sha256 = 'df3e5f40630e52fff747871ffef37bbdd3be326f7571c2f9cabef4c2e4f17ad1'
        GitBlob = 'f46180c9f05cfa475153af3f322bbd4c920cd4c5'
    },
    [pscustomobject]@{
        Path = 'ExactWheelMacroSerializer.cs'
        Bytes = 18076L
        Sha256 = 'b495a0dd078feabc5348bea40a48a9ef57445f705231a6a2ebb766d93cadc572'
        GitBlob = 'db76b7c1c9789a59b9540f3fff796a830ee01cff'
    },
    [pscustomobject]@{
        Path = 'ExactWheelModels.cs'
        Bytes = 6648L
        Sha256 = '926512299bf3678ea80640625229df84beca0003ac036044903f3b03cc0025f3'
        GitBlob = 'c550bc894e3e64dededee18a68082132ffbe94a6'
    },
    [pscustomobject]@{
        Path = 'ExactWheelPlaybackModels.cs'
        Bytes = 3126L
        Sha256 = '247f6c47ad36b9a2f018628a01c805c3267ab8ef013aca243294b5e338674afe'
        GitBlob = '8bf5f36bfda13077f83411ccc5fd905c9beff3c3'
    },
    [pscustomobject]@{
        Path = 'ExactWheelRecordingValidator.cs'
        Bytes = 10969L
        Sha256 = 'e48763f8fa6e6b4a2fbe46cecaad628c8be0a9f60231b542635a416874bd32ec'
        GitBlob = '9b9ece1bcddf08c41131a1e0b058180113e6c391'
    },
    [pscustomobject]@{
        Path = 'ExactWheelSession.cs'
        Bytes = 8823L
        Sha256 = '45b7f5cc45b70b1eba485bfd8a5b2dfff5caa159b2af8cc4b80dab8dab7b2934'
        GitBlob = 'a1d151ed7797fbd6d2f4327dee91a6209968b78e'
    },
    [pscustomobject]@{
        Path = 'ExactWheelTiming.cs'
        Bytes = 9698L
        Sha256 = 'dc967e6a86f2f29ed1fab3734430683489f04b8d79ae5d0dfb99d24c24068695'
        GitBlob = '940883dc0613c3af3f8dca414a5fed899cb8be95'
    },
    [pscustomobject]@{
        Path = 'Properties/AssemblyInfo.cs'
        Bytes = 92L
        Sha256 = '7e3649ff5512f75d1b01de6012ff7e76c59fd41d8d4cde76492a1cb6955ed74c'
        GitBlob = '2bdedc1b10f634805c24f30b2a8656eaf239deec'
    },
    [pscustomobject]@{
        Path = 'Windows/ExactWheelInputInjector.cs'
        Bytes = 19682L
        Sha256 = '11e4d38db740a22adbed7b4c31f517f717295ac572b96a7b869031b2ef83692f'
        GitBlob = '3e9b1e50b2780e75c2b9774c5b7dc4df19e75171'
    },
    [pscustomobject]@{
        Path = 'Windows/ExactWheelNativeMethods.cs'
        Bytes = 12568L
        Sha256 = 'eee1c108c817382905b3732d35413d235258284678d7e5a431da00b8dd86314d'
        GitBlob = '5b67434558cf94ce3b0cf5f9820cb74f5efd241b'
    },
    [pscustomobject]@{
        Path = 'Windows/ExactWheelPlaybackEngine.cs'
        Bytes = 68961L
        Sha256 = 'ff15a483bdd3f6493c533a3bc97485893183fb00c0cc598b2709abfc6e7429a2'
        GitBlob = '712d33cb50fd754d22e43b768d4f107b4733506f'
    },
    [pscustomobject]@{
        Path = 'Windows/LowLevelInputCapture.cs'
        Bytes = 33870L
        Sha256 = '29dc99a93507463aa4b924d3c754cf8a705c8296a5d1164fc0cd8dfe5fc1e318'
        GitBlob = 'f9b940af14ffd34140cad462f0912c3fde8d25ab'
    },
    [pscustomobject]@{
        Path = 'packages.lock.json'
        Bytes = 110L
        Sha256 = 'f7133f1bbac491143adf80f87c7887d28c7218986d86e5839de59867e7717780'
        GitBlob = '77d53a00eeff299d53c0d7af67c058406544fc9e'
    }
)

function Get-Sha256Hex([byte[]] $Bytes) {
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString(
            $sha256.ComputeHash($Bytes)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-FileSha256Hex([string] $Path) {
    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString(
            $sha256.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

function Get-GitBlobBytes(
    [string] $RepositoryRoot,
    [string] $BlobId) {
    if ($BlobId -cnotmatch '^[0-9a-f]{40}$') {
        throw "Refusing an invalid Git blob ID: $BlobId"
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'git'
    $startInfo.Arguments = "cat-file blob $BlobId"
    $startInfo.WorkingDirectory = $RepositoryRoot
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $memory = [IO.MemoryStream]::new()
    try {
        [void] $process.Start()
        $process.StandardOutput.BaseStream.CopyTo($memory)
        $errorText = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "Unable to read pinned Git blob '$BlobId': $errorText"
        }
        return ,$memory.ToArray()
    }
    finally {
        $memory.Dispose()
        $process.Dispose()
    }
}

function Find-RepositoryRoot([string] $StartPath) {
    $candidate = Get-Item -LiteralPath $StartPath -Force
    if (-not $candidate.PSIsContainer) {
        $candidate = $candidate.Directory
    }
    while ($null -ne $candidate) {
        if (Test-Path -LiteralPath (
                Join-Path $candidate.FullName 'SessionDock.slnx') -PathType Leaf) {
            return $candidate.FullName
        }
        $candidate = $candidate.Parent
    }
    throw 'ExactWheel provenance verification must run inside the SessionDock repository.'
}

$fullPath = [IO.Path]::GetFullPath($ManifestPath)
if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
    throw "ExactWheel provenance manifest not found: $fullPath"
}
if (-not [string]::IsNullOrWhiteSpace(
        [string] (Get-Item -LiteralPath $fullPath -Force).LinkType)) {
    throw 'ExactWheel provenance manifest must not be a reparse point.'
}
$manifestSha256 = Get-FileSha256Hex $fullPath
if ($manifestSha256 -cne $expectedManifestSha256) {
    throw 'ExactWheel provenance manifest does not match the exact reviewed bytes.'
}

try {
    $manifest = Get-Content -LiteralPath $fullPath -Raw | ConvertFrom-Json
}
catch {
    throw "ExactWheel provenance manifest is not valid JSON: $fullPath"
}

$expectedProperties = @(
    'buildDefinitionBytes'
    'buildDefinitionGitBlob'
    'buildDefinitionPath'
    'buildDefinitionSha256'
    'canonicalManifestSha256'
    'component'
    'componentVersion'
    'integrationKind'
    'license'
    'macroFormatVersion'
    'notes'
    'releaseBlockedPendingLicense'
    'schemaVersion'
    'sourceBytes'
    'sourceCommit'
    'sourceFileCount'
    'sourcePathHint'
    'sourceState'
    'sourceTag'
)
$actualProperties = @($manifest.PSObject.Properties.Name | Sort-Object)
if (@(Compare-Object `
        $expectedProperties `
        $actualProperties `
        -CaseSensitive).Count -ne 0 -or
    $manifest.schemaVersion -ne 2 -or
    $manifest.component -cne 'ExactWheel' -or
    $manifest.componentVersion -cne '1.1.0' -or
    [long] $manifest.macroFormatVersion -ne 1 -or
    $manifest.integrationKind -cne 'managed-compatible-port' -or
    $manifest.buildDefinitionPath -isnot [string] -or
    $manifest.buildDefinitionPath -cne $expectedProjectPath -or
    [long] $manifest.buildDefinitionBytes -ne $expectedProjectBytes -or
    $manifest.buildDefinitionGitBlob -isnot [string] -or
    $manifest.buildDefinitionGitBlob -cne $expectedProjectGitBlob -or
    $manifest.buildDefinitionGitBlob -cnotmatch '^[0-9a-f]{40}$' -or
    $manifest.buildDefinitionSha256 -isnot [string] -or
    $manifest.buildDefinitionSha256 -cne $expectedProjectSha256 -or
    $manifest.buildDefinitionSha256 -cnotmatch '^[0-9a-f]{64}$' -or
    $manifest.canonicalManifestSha256 -isnot [string] -or
    $manifest.canonicalManifestSha256 -cnotmatch '^[0-9a-f]{64}$' -or
    $manifest.releaseBlockedPendingLicense -isnot [bool]) {
    throw 'ExactWheel provenance manifest has an unsupported or incomplete schema.'
}

$blockingReasons = [Collections.Generic.List[string]]::new()
if ($manifest.releaseBlockedPendingLicense) {
    $blockingReasons.Add('releaseBlockedPendingLicense is true')
}
if ($manifest.license -isnot [string] -or
    [string] $manifest.license -cne 'MIT') {
    $blockingReasons.Add('license is missing or is not the repository MIT license')
}
if ($manifest.sourceState -cne 'immutable-git') {
    $blockingReasons.Add('sourceState is not immutable-git')
}
if ($manifest.sourceCommit -isnot [string] -or
    [string] $manifest.sourceCommit -cnotmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$' -or
    [string] $manifest.sourceCommit -cne $expectedSourceCommit) {
    $blockingReasons.Add('sourceCommit is missing or is not the pinned full immutable Git object ID')
}
# Repository-native ExactWheel is intentionally tagless. The schema retains
# sourceTag for backwards compatibility, but any claimed tag is rejected.
if ($null -ne $manifest.sourceTag) {
    $blockingReasons.Add('sourceTag must be null for the tagless repository snapshot')
}
if ($manifest.sourcePathHint -cne 'SessionDock.ExactWheel') {
    $blockingReasons.Add('sourcePathHint is not the repository-native component path')
}
if ([long] $manifest.sourceFileCount -ne $expectedInventory.Count -or
    [long] $manifest.sourceBytes -ne 216308L -or
    $manifest.canonicalManifestSha256 -cne $expectedCanonicalManifestSha256) {
    $blockingReasons.Add('source inventory summary does not match the pinned repository snapshot')
}

if ($blockingReasons.Count -ne 0) {
    throw (
        'ExactWheel release provenance is not release-ready: ' +
        ($blockingReasons -join '; ') +
        '. Normal builds remain available, but a public release is blocked.'
    )
}

$canonicalText = [Text.StringBuilder]::new()
foreach ($entry in $expectedInventory | Sort-Object Path) {
    $canonicalLine = '{0}  {1}  {2}  {3}' -f $entry.Sha256, `
        $entry.Bytes, $entry.GitBlob, $entry.Path
    [void] $canonicalText.Append($canonicalLine)
    [void] $canonicalText.Append("`n")
}
$actualCanonicalHash = Get-Sha256Hex (
    [Text.Encoding]::UTF8.GetBytes($canonicalText.ToString()))
if ($actualCanonicalHash -cne $expectedCanonicalManifestSha256 -or
    $actualCanonicalHash -cne $manifest.canonicalManifestSha256) {
    throw 'ExactWheel canonical source inventory hash does not match the reviewed manifest.'
}

# The protected build job performs the full Git/tree/blob/license verification
# before it creates the immutable release-input artifact. Later jobs deliberately
# have no checkout. They may validate only the staged manifest, but still against
# every hard-coded reviewed identity above; this is not a source-verification
# bypass for a checked-out build.
if ($StagedManifestOnly) {
    if ($PassThru) {
        $manifest
    }
    else {
        $verifiedMessage = (
            'Verified staged ExactWheel {0} manifest at Git commit {1}: ' +
            '{2} files, canonical SHA-256 {3}, license MIT.') -f `
            $manifest.componentVersion, $manifest.sourceCommit, `
            $manifest.sourceFileCount, $manifest.canonicalManifestSha256
        Write-Host $verifiedMessage
    }
    return
}

$repositoryRoot = Find-RepositoryRoot $fullPath
$gitRootOutput = @(& git -C $repositoryRoot rev-parse --show-toplevel 2>&1)
if ($LASTEXITCODE -ne 0 -or $gitRootOutput.Count -ne 1 -or
    [IO.Path]::GetFullPath([string] $gitRootOutput[0]).TrimEnd('\', '/') -cne
    [IO.Path]::GetFullPath($repositoryRoot).TrimEnd('\', '/')) {
    throw 'ExactWheel provenance must be verified from the owning Git repository.'
}

$componentRoot = Join-Path $repositoryRoot 'SessionDock.ExactWheel'
if (-not (Test-Path -LiteralPath $componentRoot -PathType Container)) {
    throw 'The repository-native ExactWheel source directory is missing.'
}
$componentPrefix = $componentRoot.TrimEnd('\', '/') +
    [IO.Path]::DirectorySeparatorChar
$actualSourcePaths = @(Get-ChildItem -LiteralPath $componentRoot -File -Recurse -Force |
    ForEach-Object {
        if (-not [string]::IsNullOrWhiteSpace([string] $_.LinkType)) {
            throw "ExactWheel source inventory contains a reparse point: $($_.FullName)"
        }
        $_.FullName.Substring($componentPrefix.Length).Replace('\', '/')
    } |
    Where-Object {
        $_ -cne 'exactwheel-provenance.json' -and
        $_ -cne 'SessionDock.ExactWheel.csproj' -and
        $_ -cnotmatch '^(?:bin|obj)/'
    } |
    Sort-Object)
$expectedSourcePaths = @($expectedInventory.Path | Sort-Object)
$sourceDifferences = @(Compare-Object `
    -ReferenceObject $expectedSourcePaths `
    -DifferenceObject $actualSourcePaths `
    -CaseSensitive)
if ($sourceDifferences.Count -ne 0 -or
    $actualSourcePaths.Count -ne $expectedSourcePaths.Count) {
    throw "ExactWheel source inventory contains missing or unexpected files:`n$($sourceDifferences | Out-String)"
}

foreach ($entry in $expectedInventory) {
    $repositoryPath = 'SessionDock.ExactWheel/' + $entry.Path
    $sourcePath = Join-Path $componentRoot $entry.Path
    $worktreeBlobOutput = @(& git -C $repositoryRoot hash-object `
        "--path=$repositoryPath" -- $sourcePath 2>&1)
    if ($LASTEXITCODE -ne 0 -or $worktreeBlobOutput.Count -ne 1 -or
        [string] $worktreeBlobOutput[0] -cne $entry.GitBlob) {
        throw "ExactWheel source differs from the pinned Git blob: $repositoryPath"
    }

    $blobBytes = Get-GitBlobBytes $repositoryRoot $entry.GitBlob
    if ($blobBytes.LongLength -ne $entry.Bytes -or
        (Get-Sha256Hex $blobBytes) -cne $entry.Sha256) {
        throw "ExactWheel pinned Git blob fails its independent SHA-256 identity: $repositoryPath"
    }
}

$projectPath = Join-Path $repositoryRoot $manifest.buildDefinitionPath
$projectBlobOutput = @(& git -C $repositoryRoot hash-object `
    "--path=$($manifest.buildDefinitionPath)" -- `
    $projectPath 2>&1)
$projectHash = Get-FileSha256Hex $projectPath
if ($LASTEXITCODE -ne 0 -or $projectBlobOutput.Count -ne 1 -or
    (Get-Item -LiteralPath $projectPath).Length -ne
        [long] $manifest.buildDefinitionBytes -or
    [string] $projectBlobOutput[0] -cne
        [string] $manifest.buildDefinitionGitBlob -or
    $projectHash -cne [string] $manifest.buildDefinitionSha256 -or
    [string] $projectBlobOutput[0] -cne $expectedProjectGitBlob -or
    $projectHash -cne $expectedProjectSha256) {
    throw 'ExactWheel build-definition blob or SHA-256 differs from the reviewed provenance configuration.'
}

$licensePath = Join-Path $repositoryRoot 'LICENSE.md'
if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf) -or
    -not [string]::IsNullOrWhiteSpace(
        [string] (Get-Item -LiteralPath $licensePath -Force).LinkType)) {
    throw 'The repository MIT license is missing or is a reparse point.'
}
$licenseHash = Get-FileSha256Hex $licensePath
if ($licenseHash -cne $expectedLicenseSha256) {
    throw 'The repository MIT license bytes do not match the reviewed license.'
}
$licenseBlobOutput = @(& git -C $repositoryRoot hash-object `
    '--path=LICENSE.md' -- $licensePath 2>&1)
if ($LASTEXITCODE -ne 0 -or $licenseBlobOutput.Count -ne 1 -or
    [string] $licenseBlobOutput[0] -cne $expectedLicenseGitBlob) {
    throw 'The repository MIT license does not match its pinned Git blob.'
}
$licenseBlobBytes = Get-GitBlobBytes $repositoryRoot $expectedLicenseGitBlob
if ((Get-Sha256Hex $licenseBlobBytes) -cne $expectedLicenseSha256) {
    throw 'The pinned Git license blob does not match the reviewed MIT license hash.'
}

& git -C $repositoryRoot cat-file -e "$expectedSourceCommit^{commit}" 2>$null
if ($LASTEXITCODE -ne 0) {
    throw 'The pinned ExactWheel source commit is unavailable; full provenance verification requires complete Git history.'
}
& git -C $repositoryRoot merge-base --is-ancestor $expectedSourceCommit HEAD
if ($LASTEXITCODE -ne 0) {
    throw 'The pinned ExactWheel source commit is not an ancestor of the checkout.'
}
foreach ($entry in $expectedInventory) {
    $repositoryPath = 'SessionDock.ExactWheel/' + $entry.Path
    $treeOutput = @(& git -C $repositoryRoot ls-tree `
        $expectedSourceCommit -- $repositoryPath 2>&1)
    $expectedTreePattern = '^100644 blob {0}\t{1}$' -f `
        [regex]::Escape($entry.GitBlob), [regex]::Escape($repositoryPath)
    if ($LASTEXITCODE -ne 0 -or $treeOutput.Count -ne 1 -or
        [string] $treeOutput[0] -cnotmatch $expectedTreePattern) {
        throw "Pinned source commit does not contain the reviewed ExactWheel blob: $repositoryPath"
    }
}
$licenseTreeOutput = @(& git -C $repositoryRoot ls-tree `
    $expectedSourceCommit -- LICENSE.md 2>&1)
$expectedLicenseTreePattern = '^100644 blob {0}\tLICENSE\.md$' -f `
    [regex]::Escape($expectedLicenseGitBlob)
if ($LASTEXITCODE -ne 0 -or $licenseTreeOutput.Count -ne 1 -or
    [string] $licenseTreeOutput[0] -cnotmatch $expectedLicenseTreePattern) {
    throw 'Pinned source commit does not contain the reviewed MIT license blob.'
}

if ($PassThru) {
    $manifest
}
else {
    $verifiedMessage = (
        'Verified release-ready repository-native ExactWheel {0} at Git commit {1}: ' +
        '{2} files, canonical SHA-256 {3}, license MIT.') -f `
        $manifest.componentVersion, $manifest.sourceCommit, `
        $manifest.sourceFileCount, $manifest.canonicalManifestSha256
    Write-Host $verifiedMessage
}
