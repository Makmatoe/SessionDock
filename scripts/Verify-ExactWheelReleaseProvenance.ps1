[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ManifestPath,

    [switch] $StagedManifestOnly,

    [switch] $PassThru
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedSourceCommit = '14fef76e6639bf291c87a83db7043b91e1c3daa8'
$expectedManifestSha256 = 'de707b6b4b234a08c42c73282d1941acbe724b4c89f10b0e3000359e907fa19c'
$expectedLicenseSha256 = '5944250b546861e4e616de520b7d06513fec435a5651fc49d83ae92d3cf14bf2'
$expectedLicenseGitBlob = '730ba1584a9ef1002dfca75b3b18a8d889052fbc'
$expectedCanonicalManifestSha256 =
    '2368564b533bc5b762bceefb5b27ed4273ebdf7deee6d63a2fe7108d2659405e'
$expectedProjectSha256 = '76e3be05eea91e5526965d05da043219da67afdc52a423b07707b63fdfaa1841'
$expectedProjectGitBlob = '07fe8f9ec14088750f6d2a0d835c86b678a0f76e'
$expectedProjectPath = 'SessionDock.ExactWheel/SessionDock.ExactWheel.csproj'
$expectedProjectBytes = 1311L
$expectedInventory = @(
    [pscustomobject]@{
        Path = 'ExactWheelCoordinateTransforms.cs'
        Bytes = 8853L
        Sha256 = '66076bd513d83939cf7f8988899e455fd786b26a29bddbbf9db8c8387b65e7b5'
        GitBlob = 'c183864e0a70abbd0e05a2a0065c7c5b6dfa3f19'
    },
    [pscustomobject]@{
        Path = 'ExactWheelDesktopCapture.cs'
        Bytes = 10325L
        Sha256 = 'f079c22f6744f907806402dbdd7eb65485e0cf1db229054527aff326a384973b'
        GitBlob = '972c1a911f8bc2206058b0698c85b1644383d3d7'
    },
    [pscustomobject]@{
        Path = 'ExactWheelMacroSerializer.cs'
        Bytes = 18058L
        Sha256 = '072a6a53e759145365e5afd0884d5e158f1e59906278bdb6b4b2c8c8c67d9581'
        GitBlob = 'e847293e09b39c1e5f5d0ad92247af47cfe38686'
    },
    [pscustomobject]@{
        Path = 'ExactWheelModels.cs'
        Bytes = 5421L
        Sha256 = '8799d1ee140df719090a82e5b2e35bcbe3c9d691af1ed72cc08dd4ee6dd8ba05'
        GitBlob = 'a11c0b682fdacf91723424564876809fc16f6554'
    },
    [pscustomobject]@{
        Path = 'ExactWheelPlaybackModels.cs'
        Bytes = 3126L
        Sha256 = '247f6c47ad36b9a2f018628a01c805c3267ab8ef013aca243294b5e338674afe'
        GitBlob = '8bf5f36bfda13077f83411ccc5fd905c9beff3c3'
    },
    [pscustomobject]@{
        Path = 'ExactWheelRecordingValidator.cs'
        Bytes = 9444L
        Sha256 = '3b0f4d325c652287f4f491446db5d0b2315bcb9269b3d380883251a773e5caf9'
        GitBlob = '1cda0e63eedab520434cc4b1855e3225c52855ce'
    },
    [pscustomobject]@{
        Path = 'ExactWheelSession.cs'
        Bytes = 5358L
        Sha256 = '6f9d0b909856b8ce7c623a8f96bec7c22da80ecccb064adbeeeb4d423b37e113'
        GitBlob = '7036e31fd636767caf0f7f07472545e761a9f9bc'
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
        Bytes = 17152L
        Sha256 = '692cf142b5da9d32cfd0088349c06d8525533b728a46e964b9f5779edc0bea0d'
        GitBlob = 'aca542a8d99858cc2d6f542060fc8c5cfe8b6535'
    },
    [pscustomobject]@{
        Path = 'Windows/ExactWheelNativeMethods.cs'
        Bytes = 12568L
        Sha256 = 'eee1c108c817382905b3732d35413d235258284678d7e5a431da00b8dd86314d'
        GitBlob = '5b67434558cf94ce3b0cf5f9820cb74f5efd241b'
    },
    [pscustomobject]@{
        Path = 'Windows/ExactWheelPlaybackEngine.cs'
        Bytes = 50334L
        Sha256 = '7bd7f486b84314af33631975488eee967478ba5d5b768caba2260c31a37a3103'
        GitBlob = '96f440e76e4ca4b2203ee5dc6b6e6910662d256a'
    },
    [pscustomobject]@{
        Path = 'Windows/LowLevelInputCapture.cs'
        Bytes = 22455L
        Sha256 = '974525b67431d33400ab69d582a594dd82f29a08f3beae161034db431047f4bb'
        GitBlob = 'a71077a8529172a46fcf204f215d45f641a08d77'
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
    [long] $manifest.sourceBytes -ne 172994L -or
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
