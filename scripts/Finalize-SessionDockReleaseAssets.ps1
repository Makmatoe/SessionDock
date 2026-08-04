[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Directory,

    [Parameter(Mandatory)]
    [ValidatePattern('^win-x64-[a-z0-9-]+$')]
    [string] $Channel,

    [Parameter(Mandatory)]
    [string] $ApplicationDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
. (Join-Path $PSScriptRoot 'ReleaseJson.ps1')

$root = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
$applicationRoot = [IO.Path]::GetFullPath($ApplicationDirectory).TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Release output not found: $root"
}
if (-not (Test-Path -LiteralPath $applicationRoot -PathType Container)) {
    throw "Verified application input not found: $applicationRoot"
}

$generatedPortableName = "SessionDockApp-$Channel-Portable.zip"
$generatedPortablePath = Join-Path $root $generatedPortableName
$portableName = 'SessionDock-win-x64-Portable.zip'
$portablePath = Join-Path $root $portableName
if (-not (Test-Path -LiteralPath $generatedPortablePath -PathType Leaf) -or
    (Test-Path -LiteralPath $portablePath)) {
    throw 'Velopack portable-replacement preconditions failed.'
}

$assetsPath = Join-Path $root "assets.$Channel.json"
$assets = @(ConvertFrom-ReleaseJson (Get-Content -LiteralPath $assetsPath -Raw))
if ($assets.Count -ne 2) {
    throw 'Velopack must produce one portable wrapper and one full package.'
}
$portableAssets = @($assets | Where-Object {
        $_.Type -ceq 'Portable' -and
        $_.RelativeFileName -ceq $generatedPortableName
    })
$fullAssets = @($assets | Where-Object {
        $_.Type -ceq 'Full' -and
        $_.RelativeFileName -cmatch (
            '^SessionDockApp-\d+\.\d+\.\d+-' +
            [regex]::Escape($Channel) + '-full\.nupkg$')
    })
if ($portableAssets.Count -ne 1 -or $fullAssets.Count -ne 1) {
    throw 'Velopack asset inventory did not contain one exact portable wrapper and full package.'
}
$portableAsset = [pscustomobject] [ordered]@{
    RelativeFileName = $portableName
    Type = 'Portable'
}
$publicAssets = @($portableAsset, $fullAssets[0])

$applicationItems = @(Get-ChildItem -LiteralPath $applicationRoot -Recurse -Force)
if ($applicationItems | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string] $_.LinkType)
    }) {
    throw 'Verified application input must not contain reparse points.'
}
$applicationFiles = @($applicationItems | Where-Object { -not $_.PSIsContainer } |
    Sort-Object FullName)
if ($applicationFiles.Count -eq 0) {
    throw 'Verified application input is empty.'
}

# Velopack is invoked with --noInst, so no Setup executable is ever created. Its
# temporary wrapper ZIP is never published. Build the canonical portable archive
# directly from the verified application directory, then delete the wrapper ZIP.
$temporaryPortablePath = "$portablePath.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    $archiveStream = [IO.File]::Open(
        $temporaryPortablePath,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $archiveStream,
            [IO.Compression.ZipArchiveMode]::Create,
            $false)
        try {
            $applicationPrefix = $applicationRoot + [IO.Path]::DirectorySeparatorChar
            foreach ($file in $applicationFiles) {
                if (-not $file.FullName.StartsWith(
                        $applicationPrefix,
                        [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Application ZIP input escaped its verified root.'
                }
                $relativePath = $file.FullName.Substring(
                    $applicationPrefix.Length).Replace('\', '/')
                $entry = $archive.CreateEntry(
                    $relativePath,
                    [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(
                    2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $entryStream = $entry.Open()
                try {
                    $inputStream = [IO.File]::Open(
                        $file.FullName,
                        [IO.FileMode]::Open,
                        [IO.FileAccess]::Read,
                        [IO.FileShare]::Read)
                    try {
                        $inputStream.CopyTo($entryStream)
                    }
                    finally {
                        $inputStream.Dispose()
                    }
                }
                finally {
                    $entryStream.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $archiveStream.Dispose()
    }
    Move-Item -LiteralPath $temporaryPortablePath -Destination $portablePath
    Remove-Item -LiteralPath $generatedPortablePath -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryPortablePath) {
        Remove-Item -LiteralPath $temporaryPortablePath -Force
    }
}

$temporaryPath = "$assetsPath.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    $json = ConvertTo-Json -InputObject @($publicAssets) -Depth 4 -Compress
    [IO.File]::WriteAllText(
        $temporaryPath,
        "$json`n",
        [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryPath -Destination $assetsPath -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}

if (Get-ChildItem -LiteralPath $root -File -Filter '*Setup*.exe') {
    throw 'Setup executable remained after portable-only release finalization.'
}

Write-Host 'Finalized the portable-only release without creating a Setup executable; replaced Velopack''s wrapper ZIP with the transparent application ZIP.'
