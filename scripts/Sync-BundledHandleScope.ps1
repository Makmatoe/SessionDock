[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter()]
    [string]$UpstreamPath,

    [Parameter()]
    [switch]$Sync
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$expectedRepository = 'https://github.com/Makmatoe/HandleScope'
$expectedTag = 'v0.3.0'
$expectedCommit = 'ef3b926848353115296faaa9f48f1a5be8c8bae2'
$expectedSources = @(
    'HandleScope.Api/ApiHost.cs'
    'HandleScope.Api/AutomationPolicy.cs'
    'HandleScope.Api/Contracts.cs'
    'HandleScope.Api/DryRunPlanStore.cs'
    'HandleScope.Api/RobloxExecutableVerifier.cs'
    'HandleScope.Api/StrictCloseRequestReader.cs'
    'HandleScope.Core/Compatibility/ApiCompatibilityPreferenceStore.cs'
    'HandleScope.Core/Models/HandleEntry.cs'
    'HandleScope.Core/Models/ProcessIdentity.cs'
    'HandleScope.Core/Models/ProcessRow.cs'
    'HandleScope.Core/Models/ProcessSnapshot.cs'
    'HandleScope.Core/Models/RobloxAutomationRecipe.cs'
    'HandleScope.Core/Services/AutomationCommandBuilder.cs'
    'HandleScope.Core/Services/HandleService.cs'
    'HandleScope.Core/Services/NativeMethods.cs'
    'HandleScope.Core/Services/ProcessIdentityService.cs'
    'HandleScope.Core/Services/ProcessService.cs'
)

function ConvertTo-NormalizedRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BasePath,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $baseUri = [Uri]::new(([IO.Path]::GetFullPath($BasePath).TrimEnd(
        [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar))
    $pathUri = [Uri]::new([IO.Path]::GetFullPath($Path))
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString())
}

function Assert-PathInsideRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $pathFull = [IO.Path]::GetFullPath($Path)
    if (-not $pathFull.StartsWith(
            $rootFull,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes the bundled component root: $Path"
    }

    $current = $rootFull.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $relativePath = $pathFull.Substring($rootFull.Length)
    $components = $relativePath.Split(
        [char[]] @(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar),
        [StringSplitOptions]::RemoveEmptyEntries)
    if (Test-Path -LiteralPath $current) {
        $rootItem = Get-Item -LiteralPath $current -Force
        if (Test-PathEntryIsLink $rootItem) {
            throw "Bundled source paths cannot traverse a symbolic link or junction: $($rootItem.FullName)"
        }
    }
    foreach ($component in $components) {
        $current = Join-Path $current $component
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (Test-PathEntryIsLink $item) {
                throw "Bundled source paths cannot traverse a symbolic link or junction: $($item.FullName)"
            }
        }
    }

    return $pathFull
}

function Get-StrictManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or
        $manifest.component -ne 'HandleScope' -or
        $manifest.componentVersion -ne '0.3.0' -or
        $manifest.upstream.repository -ne $expectedRepository -or
        $manifest.upstream.tag -ne $expectedTag -or
        $manifest.upstream.commit -ne $expectedCommit) {
        throw 'The bundled HandleScope provenance header is invalid.'
    }

    $manifestSources = @($manifest.sources | ForEach-Object { $_.sourcePath })
    if ($manifestSources.Count -ne $expectedSources.Count -or
        (@(Compare-Object $expectedSources $manifestSources)).Count -ne 0 -or
        (@($manifestSources | Select-Object -Unique)).Count -ne $manifestSources.Count) {
        throw 'The bundled HandleScope source allowlist is invalid.'
    }

    foreach ($entry in $manifest.sources) {
        $expectedDestination = 'Upstream/' + $entry.sourcePath
        if ($entry.destinationPath -cne $expectedDestination -or
            $entry.sha256 -cnotmatch '^[a-f0-9]{64}$') {
            throw "Invalid provenance entry for $($entry.sourcePath)."
        }
    }

    return $manifest
}

function Assert-UpstreamRevision {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $resolved = (Resolve-Path -LiteralPath $Path).Path
    $inside = (& git -C $resolved rev-parse --is-inside-work-tree).Trim()
    if ($LASTEXITCODE -ne 0 -or $inside -ne 'true') {
        throw "The upstream path is not a Git worktree: $resolved"
    }

    $tagCommit = (& git -C $resolved rev-parse "$expectedTag^{commit}").Trim()
    if ($LASTEXITCODE -ne 0 -or $tagCommit -cne $expectedCommit) {
        throw "The upstream tag $expectedTag does not resolve to $expectedCommit."
    }

    $origin = (& git -C $resolved remote get-url origin).TrimEnd('/')
    if ($LASTEXITCODE -ne 0) {
        throw "The upstream origin is not $expectedRepository."
    }
    if ($origin.EndsWith('.git', [StringComparison]::Ordinal)) {
        $origin = $origin.Substring(0, $origin.Length - 4)
    }
    if ($origin -cne $expectedRepository) {
        throw "The upstream origin is not $expectedRepository."
    }

    return $resolved
}

function Copy-GitBlob {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Worktree,

        [Parameter(Mandatory = $true)]
        [string]$SourcePath,

        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    $temporaryPath = $DestinationPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'git'
    $startInfo.WorkingDirectory = $Worktree
    $startInfo.Arguments = "cat-file blob ${expectedCommit}:$SourcePath"
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        $output = [IO.File]::Open(
            $temporaryPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        try {
            $process.StandardOutput.BaseStream.CopyTo($output)
        }
        finally {
            $output.Dispose()
        }

        $errorText = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "git cat-file failed for ${SourcePath}: $errorText"
        }
        Move-Item -LiteralPath $temporaryPath -Destination $DestinationPath -Force
    }
    finally {
        $process.Dispose()
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }
}

function Get-GitBlobSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Worktree,

        [Parameter(Mandatory = $true)]
        [string]$SourcePath
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'git'
    $startInfo.WorkingDirectory = $Worktree
    $startInfo.Arguments = "cat-file blob ${expectedCommit}:$SourcePath"
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($startInfo)
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        $digest = $hash.ComputeHash($process.StandardOutput.BaseStream)
        $errorText = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "git cat-file failed for ${SourcePath}: $errorText"
        }
        return ([BitConverter]::ToString($digest).Replace('-', '').ToLowerInvariant())
    }
    finally {
        $hash.Dispose()
        $process.Dispose()
    }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$componentRoot = Join-Path $repoRoot 'SessionDock.HandleScope'
$manifestPath = Assert-PathInsideRoot `
    -Root $componentRoot `
    -Path (Join-Path $componentRoot 'handlescope-upstream.json')
$manifest = Get-StrictManifest -Path $manifestPath

if ($Sync -and [string]::IsNullOrWhiteSpace($UpstreamPath)) {
    throw '-Sync requires -UpstreamPath. No network operation is performed.'
}

$resolvedUpstream = $null
if (-not [string]::IsNullOrWhiteSpace($UpstreamPath)) {
    $resolvedUpstream = Assert-UpstreamRevision -Path $UpstreamPath
}

if ($Sync) {
    foreach ($entry in $manifest.sources) {
        $destination = Assert-PathInsideRoot -Root $componentRoot -Path (
            Join-Path $componentRoot $entry.destinationPath)
        if ($PSCmdlet.ShouldProcess($destination, 'Replace from pinned HandleScope blob')) {
            Copy-GitBlob `
                -Worktree $resolvedUpstream `
                -SourcePath $entry.sourcePath `
                -DestinationPath $destination
        }
    }
}

$expectedDestinations = @($manifest.sources | ForEach-Object {
    $_.destinationPath
})
$actualDestinations = @(Get-ChildItem `
    -LiteralPath (Join-Path $componentRoot 'Upstream') `
    -Recurse `
    -File `
    -Filter '*.cs' | ForEach-Object {
        ConvertTo-NormalizedRelativePath -BasePath $componentRoot -Path $_.FullName
    })
if ($actualDestinations.Count -ne $expectedDestinations.Count -or
    @(Compare-Object $expectedDestinations $actualDestinations).Count -ne 0) {
    throw 'Bundled HandleScope sources do not match the exact provenance allowlist.'
}

foreach ($entry in $manifest.sources) {
    $path = Assert-PathInsideRoot -Root $componentRoot -Path (
        Join-Path $componentRoot $entry.destinationPath)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Bundled source is missing: $($entry.destinationPath)"
    }
    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -cne $entry.sha256) {
        throw "SHA256 mismatch for $($entry.destinationPath)."
    }
    if ($null -ne $resolvedUpstream) {
        $upstreamHash = Get-GitBlobSha256 `
            -Worktree $resolvedUpstream `
            -SourcePath $entry.sourcePath
        if ($upstreamHash -cne $entry.sha256) {
            throw "Pinned upstream bytes differ for $($entry.sourcePath)."
        }
    }
}

Write-Host (
    "Verified HandleScope {0} from {1} ({2}) across {3} pinned source files{4}." -f
    $manifest.componentVersion,
    $manifest.upstream.tag,
    $manifest.upstream.commit,
    $manifest.sources.Count,
    $(if ($null -eq $resolvedUpstream) {
        ' offline'
    }
    else {
        ' against the immutable upstream Git objects'
    }))
