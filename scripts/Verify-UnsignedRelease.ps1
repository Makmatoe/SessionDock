[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Directory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Unsigned release verification directory not found: $root"
}

$expectedUnsignedFiles = @(
    'SessionDock.exe'
    'SessionDock.dll'
    'SessionDock.ExactWheel.dll'
    'SessionDock.HandleScope.dll'
    'SessionDock.ReleaseTrust.dll'
    'Velopack.dll'
)

function Test-PortableExecutable([string] $Path) {
    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    try {
        if ($stream.Length -lt 64) {
            return $false
        }
        $reader = [IO.BinaryReader]::new($stream)
        try {
            if ($reader.ReadUInt16() -ne 0x5A4D) {
                return $false
            }
            $stream.Position = 0x3C
            [uint32] $peOffset = $reader.ReadUInt32()
            if ([uint64] $peOffset + 4 -gt [uint64] $stream.Length) {
                return $false
            }
            $stream.Position = $peOffset
            return $reader.ReadUInt32() -eq 0x00004550
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

foreach ($relativePath in $expectedUnsignedFiles) {
    $path = Join-Path $root $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Expected unsigned application PE is missing: $relativePath"
    }
    $file = Get-Item -LiteralPath $path -Force
    if (-not [string]::IsNullOrWhiteSpace([string] $file.LinkType)) {
        throw "Expected unsigned application PE must be a regular file: $relativePath"
    }
    if (-not (Test-PortableExecutable $path)) {
        throw "Expected unsigned application PE is not structurally valid: $relativePath"
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne
            [System.Management.Automation.SignatureStatus]::NotSigned -or
        $null -ne $signature.SignerCertificate -or
        $null -ne $signature.TimeStamperCertificate) {
        throw "Public portable input must remain explicitly unsigned: $relativePath ($($signature.Status))"
    }

    $sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "Verified unsigned PE: $relativePath ($sha256)"
}

$expectedUnsignedSet = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
foreach ($relativePath in $expectedUnsignedFiles) {
    [void] $expectedUnsignedSet.Add($relativePath)
}
foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force) {
    if (-not (Test-PortableExecutable $file.FullName)) {
        continue
    }
    $relativePath = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
    if ($expectedUnsignedSet.Contains($relativePath)) {
        continue
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($signature.Status -ne
            [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch
            '(?i)(?:^|,\s*)O=Microsoft Corporation(?:,|$)') {
        throw "PE outside the exact unsigned application set lacks a valid Microsoft signature: $relativePath ($($signature.Status))"
    }
}

Write-Host "Verified exact unsigned application PE set ($($expectedUnsignedFiles.Count) files); every other PE has a valid Microsoft signature."
