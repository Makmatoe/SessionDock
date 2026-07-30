[CmdletBinding()]
param(
    [Parameter()]
    [string] $AssetDirectory
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

if ([string]::IsNullOrWhiteSpace($AssetDirectory)) {
    $AssetDirectory = Join-Path $PSScriptRoot "..\docs\images\sessiondock-v2.7.0"
}

$ExpectedManifestSha256 =
    "1B2BCD38597BE4336DDA7863289E146038CDE59E342A118487094F4EB822709E"
$ExpectedBuild = "2.7.0+e30ad6acf8165befe11e00d9f1f5d1de1f7e90de"
$ExpectedOutputs = [ordered]@{
    "sessiondock-v2.7.0-full-window.png" = @(
        1048, 720,
        "3D2755C8BC61D8EA7BF2A10AC254A392A5E9CA9C368A8D94CA60244B7B31514B")
    "sessiondock-v2.7.0-batch-dialog.png" = @(
        666, 713,
        "528C2D8423CC7F580C494D0A592CADAF7062182866A5A385CFB0F0FF1FD6BC23")
    "sessiondock-v2.7.0-diagnostics-dialog.png" = @(
        706, 673,
        "F3B8F0F2BC951896F7BC12AD18290C99E0DDC6E66200F9ECBA0B377279FE5C4A")
    "sessiondock-v2.7.0-accounts-focused.png" = @(
        1200, 260,
        "F491A49D99439D2EDF0643365CD55587FC078F77DCF371CB290334EC4C4B9733")
    "sessiondock-v2.7.0-destinations-focused.png" = @(
        1200, 560,
        "90E101C1593E36272CF148EA64DF3895B17CC0AA0B3D33661BDFC774EB249A01")
    "sessiondock-v2.7.0-batch-focused.png" = @(
        1100, 840,
        "9BFFCE50304DA1AB3CFFFBB9D73627DEFC38D8DFA8A3E1E42C576E581DB1C80E")
    "sessiondock-v2.7.0-diagnostics-focused.png" = @(
        1100, 820,
        "650762A5B0CE79F9580805F75716F04DE17C8BDF930398D1B660EDFEC406B2CE")
    "sessiondock-v2.7.0-readme-overview.png" = @(
        1200, 900,
        "511CD127D3ECC4A3F79BDD07BC395F73FD5C5E69CB15EF3E3572E91B11402FAD")
    "sessiondock-v2.7.0-social-wide.png" = @(
        1600, 900,
        "51BD9CC08E0F031A701932736054AEFAE0F64B6F87D7F98C1416CED15B367F92")
    "sessiondock-v2.7.0-social-square.png" = @(
        1200, 1200,
        "309A4A3D6D952E70D819F16B5B76F1E8355D80AA11D297924065C0BE73E05CC0")
}

function Get-BigEndianUInt32 {
    param(
        [Parameter(Mandatory)]
        [byte[]] $Bytes,

        [Parameter(Mandatory)]
        [int] $Offset
    )

    if ($Offset -lt 0 -or $Offset + 4 -gt $Bytes.Length) {
        throw "PNG chunk length is out of bounds."
    }

    return [uint64]$Bytes[$Offset] * 16777216L +
        [uint64]$Bytes[$Offset + 1] * 65536L +
        [uint64]$Bytes[$Offset + 2] * 256L +
        [uint64]$Bytes[$Offset + 3]
}

function Assert-SafePngEnvelope {
    param([Parameter(Mandatory)][string] $Path)

    $bytes = [IO.File]::ReadAllBytes($Path)
    $signature = @(137, 80, 78, 71, 13, 10, 26, 10)
    if ($bytes.Length -lt 20) {
        throw "PNG is truncated: $Path"
    }
    for ($index = 0; $index -lt $signature.Count; $index++) {
        if ($bytes[$index] -ne $signature[$index]) {
            throw "PNG signature is invalid: $Path"
        }
    }

    $offset = 8
    $sawHeader = $false
    $sawImageData = $false
    $sawEnd = $false
    while ($offset -lt $bytes.Length) {
        $length = Get-BigEndianUInt32 -Bytes $bytes -Offset $offset
        if ($length -gt [int]::MaxValue) {
            throw "PNG chunk is too large: $Path"
        }

        $chunkEnd = $offset + 12 + [int]$length
        if ($chunkEnd -gt $bytes.Length) {
            throw "PNG chunk exceeds the file boundary: $Path"
        }

        $type = [Text.Encoding]::ASCII.GetString($bytes, $offset + 4, 4)
        if (-not $sawHeader -and $type -ne "IHDR") {
            throw "PNG does not begin with IHDR: $Path"
        }
        if ($type -in @("tEXt", "zTXt", "iTXt", "eXIf")) {
            throw "PNG contains disallowed text or EXIF metadata '$type': $Path"
        }

        switch ($type) {
            "IHDR" { $sawHeader = $true }
            "IDAT" { $sawImageData = $true }
            "IEND" {
                if ($length -ne 0 -or $chunkEnd -ne $bytes.Length) {
                    throw "PNG contains invalid or trailing data after IEND: $Path"
                }
                $sawEnd = $true
            }
        }

        $offset = $chunkEnd
        if ($sawEnd) {
            break
        }
    }

    if (-not $sawHeader -or -not $sawImageData -or -not $sawEnd) {
        throw "PNG is missing a required structural chunk: $Path"
    }
}

function Assert-MosaicRegion {
    param(
        [Parameter(Mandatory)]
        [Drawing.Bitmap] $Bitmap,

        [Parameter(Mandatory)]
        $Region,

        [Parameter(Mandatory)]
        [string] $ImageName
    )

    $x = [int]$Region.X
    $y = [int]$Region.Y
    $width = [int]$Region.Width
    $height = [int]$Region.Height
    if ($x -lt 0 -or $y -lt 0 -or $width -le 0 -or $height -le 0 -or
        $x + $width -gt $Bitmap.Width -or $y + $height -gt $Bitmap.Height) {
        throw "Redaction '$($Region.Name)' is outside $ImageName."
    }

    foreach ($row in $y..($y + $height - 1)) {
        foreach ($column in $x..($x + $width - 1)) {
            $pixel = $Bitmap.GetPixel($column, $row)
            $isFirst = $pixel.A -eq 255 -and $pixel.R -eq 37 -and
                $pixel.G -eq 43 -and $pixel.B -eq 53
            $isSecond = $pixel.A -eq 255 -and $pixel.R -eq 52 -and
                $pixel.G -eq 61 -and $pixel.B -eq 73
            if (-not $isFirst -and -not $isSecond) {
                throw "Redaction '$($Region.Name)' contains a non-opaque-mosaic pixel in $ImageName."
            }
        }
    }
}

$AssetDirectory = [IO.Path]::GetFullPath($AssetDirectory)
if (-not (Test-Path -LiteralPath $AssetDirectory -PathType Container)) {
    throw "Visual asset directory not found: $AssetDirectory"
}

$allowedEntryNames = @("README.md", "manifest.json") + @($ExpectedOutputs.Keys)
$directoryEntries = @(Get-ChildItem -LiteralPath $AssetDirectory -Force)
if ($directoryEntries.Count -ne $allowedEntryNames.Count) {
    throw "Visual asset directory contains missing or unexpected entries."
}
foreach ($entry in $directoryEntries) {
    if ($entry.PSIsContainer -or $entry.Name -cnotin $allowedEntryNames) {
        throw "Visual asset directory contains an unexpected entry: $($entry.Name)"
    }
}
foreach ($allowedName in $allowedEntryNames) {
    if (-not (Test-Path -LiteralPath (Join-Path $AssetDirectory $allowedName) -PathType Leaf)) {
        throw "Visual asset directory is missing the expected file: $allowedName"
    }
}

$manifestPath = Join-Path $AssetDirectory "manifest.json"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Visual asset manifest not found: $manifestPath"
}
$manifestInfo = Get-Item -LiteralPath $manifestPath -Force
if ($manifestInfo.Length -le 0 -or $manifestInfo.Length -gt 128KB) {
    throw "Visual asset manifest is unsafe or outside its size limit."
}

$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
if (-not $manifestHash.Equals($ExpectedManifestSha256, [StringComparison]::Ordinal)) {
    throw "Visual asset manifest does not match the reviewed v2.7.0 manifest."
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.product -ne "SessionDock" -or $manifest.version -ne "2.7.0" -or
    $manifest.build -ne $ExpectedBuild) {
    throw "Visual asset manifest identity is invalid."
}

$declaredOutputs = @($manifest.outputs)
if ($declaredOutputs.Count -ne $ExpectedOutputs.Count) {
    throw "Visual asset manifest output count is invalid."
}

foreach ($name in $ExpectedOutputs.Keys) {
    $expected = $ExpectedOutputs[$name]
    $declared = @($declaredOutputs | Where-Object file -CEQ $name)
    if ($declared.Count -ne 1 -or
        [int]$declared[0].width -ne [int]$expected[0] -or
        [int]$declared[0].height -ne [int]$expected[1] -or
        -not ([string]$declared[0].sha256).Equals(
            ([string]$expected[2]).ToLowerInvariant(),
            [StringComparison]::Ordinal)) {
        throw "Manifest entry is invalid for $name."
    }

    $path = Join-Path $AssetDirectory $name
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if (-not $hash.Equals([string]$expected[2], [StringComparison]::Ordinal)) {
        throw "Visual asset hash mismatch: $name"
    }

    Assert-SafePngEnvelope -Path $path
    $image = [Drawing.Image]::FromFile($path)
    try {
        if ($image.Width -ne [int]$expected[0] -or
            $image.Height -ne [int]$expected[1]) {
            throw "Visual asset dimensions are invalid: $name"
        }
    }
    finally {
        $image.Dispose()
    }
}

$redactionTargets = @{
    "main-window" = "sessiondock-v2.7.0-full-window.png"
    "batch-launch" = "sessiondock-v2.7.0-batch-dialog.png"
}
foreach ($source in @($manifest.sources)) {
    if (-not $redactionTargets.ContainsKey([string]$source.id)) {
        if (@($source.redactions).Count -ne 0) {
            throw "Unexpected redactions are declared for source '$($source.id)'."
        }
        continue
    }

    $imageName = $redactionTargets[[string]$source.id]
    $bitmap = [Drawing.Bitmap]::FromFile((Join-Path $AssetDirectory $imageName))
    try {
        foreach ($region in @($source.redactions)) {
            Assert-MosaicRegion -Bitmap $bitmap -Region $region -ImageName $imageName
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

[pscustomobject]@{
    AssetDirectory = $AssetDirectory
    Build = $ExpectedBuild
    ManifestSha256 = $manifestHash
    VerifiedAssets = $ExpectedOutputs.Count
    PrivacyMasksVerified = @($manifest.sources.redactions).Count
} | ConvertTo-Json
