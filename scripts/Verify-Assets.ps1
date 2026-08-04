[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Directory,

    [Parameter(Mandatory)]
    [string] $Manifest,

    [Parameter(Mandatory)]
    [string] $CompatibilityCatalog,

    [Parameter(Mandatory)]
    [string] $ReleaseSigner,

    [Parameter(Mandatory)]
    [string] $PublicKey,

    [Parameter(Mandatory)]
    [string] $PublishedApplicationDirectory,

    [string] $ExpectedRepository = 'Makmatoe/SessionDock',

    [string] $ExpectedChannel = 'win-x64-sessiondock',

    [string] $ExpectedTag
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
. (Join-Path $PSScriptRoot 'ReleaseJson.ps1')

function Get-RelativeFiles([string] $Root) {
    $trimmedRoot = $Root.TrimEnd('\', '/')
    return @(Get-ChildItem -LiteralPath $trimmedRoot -Recurse -File -Force |
        ForEach-Object {
            $_.FullName.Substring($trimmedRoot.Length + 1).Replace('\', '/')
        } | Sort-Object)
}

function Assert-ExactSet(
    [string[]] $Expected,
    [string[]] $Actual,
    [string] $Description) {
    $differences = @(Compare-Object `
        -ReferenceObject @($Expected | Sort-Object) `
        -DifferenceObject @($Actual | Sort-Object) `
        -CaseSensitive)
    if ($differences.Count -ne 0 -or $Expected.Count -ne $Actual.Count) {
        throw "$Description contains missing or unexpected entries:`n$($differences | Out-String)"
    }
}

function Get-NormalizedNotes([string] $Value) {
    return $Value.Replace("`r`n", "`n").Replace("`r", "`n").Trim()
}

function Invoke-ReleaseDescriptorVerification(
    [string] $SignerPath,
    [string] $DescriptorPath,
    [string] $PackagePath,
    [string] $KeyPath) {
    & $SignerPath verify `
        --manifest $DescriptorPath `
        --package $PackagePath `
        --public-key $KeyPath
    if ($LASTEXITCODE -ne 0) {
        throw 'Release descriptor cryptographic verification failed.'
    }
}

function Assert-FileHashEqual([string] $Expected, [string] $Actual, [string] $Description) {
    $expectedHash = (Get-FileHash -LiteralPath $Expected -Algorithm SHA256).Hash
    $actualHash = (Get-FileHash -LiteralPath $Actual -Algorithm SHA256).Hash
    if ($expectedHash -cne $actualHash) {
        throw "$Description does not match the verified publish input."
    }
}

function Assert-ExecutableVersion(
    [string] $Path,
    [string] $ExpectedVersion) {
    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    if ($versionInfo.FileVersion -cne "$ExpectedVersion.0" -or
        $versionInfo.ProductVersion -cnotmatch
            ('^' + [regex]::Escape($ExpectedVersion) + '(\+[0-9a-f]{40})?$')) {
        throw "Unexpected executable version for $([IO.Path]::GetFileName($Path))."
    }
}

function Assert-PortableExecutable([string] $Path) {
    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    try {
        $header = [byte[]]::new(64)
        if ($stream.Read($header, 0, $header.Length) -ne $header.Length -or
            $header[0] -ne [byte][char]'M' -or
            $header[1] -ne [byte][char]'Z') {
            throw "Release executable is not a valid PE file: $([IO.Path]::GetFileName($Path))"
        }
        $peOffset = [BitConverter]::ToInt32($header, 60)
        if ($peOffset -lt $header.Length -or $peOffset -gt $stream.Length - 4) {
            throw "Release executable has an invalid PE offset: $([IO.Path]::GetFileName($Path))"
        }
        $stream.Position = $peOffset
        $signature = [byte[]]::new(4)
        if ($stream.Read($signature, 0, $signature.Length) -ne $signature.Length -or
            $signature[0] -ne [byte][char]'P' -or
            $signature[1] -ne [byte][char]'E' -or
            $signature[2] -ne 0 -or
            $signature[3] -ne 0) {
            throw "Release executable has an invalid PE signature: $([IO.Path]::GetFileName($Path))"
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-PortableExecutableIdentity([string] $Path) {
    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 512) {
            throw "Release executable is too small to contain reviewed PE sections: $([IO.Path]::GetFileName($Path))"
        }
        $stream.Position = 0x3c
        [uint32] $peOffset = $reader.ReadUInt32()
        if ([uint64] $peOffset + 24 -gt [uint64] $stream.Length) {
            throw "Release executable has an invalid PE header: $([IO.Path]::GetFileName($Path))"
        }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "Release executable has an invalid PE signature: $([IO.Path]::GetFileName($Path))"
        }
        $machine = $reader.ReadUInt16()
        $sectionCount = $reader.ReadUInt16()
        $stream.Position = $peOffset + 20
        $optionalHeaderSize = $reader.ReadUInt16()
        $fileCharacteristics = $reader.ReadUInt16()
        $stream.Position = $peOffset + 24
        $optionalHeaderMagic = $reader.ReadUInt16()
        $stream.Position = $peOffset + 40
        $entryPoint = $reader.ReadUInt32()
        if ($sectionCount -lt 1 -or $sectionCount -gt 32 -or
            [uint64] $peOffset + 24 + $optionalHeaderSize +
                ([uint64] $sectionCount * 40) -gt [uint64] $stream.Length) {
            throw "Release executable has an invalid PE section table: $([IO.Path]::GetFileName($Path))"
        }

        $sections = [Collections.Generic.List[object]]::new()
        $sectionTableOffset = [uint64] $peOffset + 24 + $optionalHeaderSize
        for ($index = 0; $index -lt $sectionCount; $index++) {
            $sectionOffset = $sectionTableOffset + ([uint64] $index * 40)
            $stream.Position = [long] $sectionOffset
            $name = [Text.Encoding]::ASCII.GetString(
                $reader.ReadBytes(8)).TrimEnd([char] 0)
            $virtualSize = $reader.ReadUInt32()
            $virtualAddress = $reader.ReadUInt32()
            $rawSize = $reader.ReadUInt32()
            $rawOffset = $reader.ReadUInt32()
            $stream.Position = [long] $sectionOffset + 36
            $characteristics = $reader.ReadUInt32()
            if ([string]::IsNullOrWhiteSpace($name) -or $rawSize -eq 0 -or
                [uint64] $rawOffset + $rawSize -gt [uint64] $stream.Length) {
                throw "Release executable contains an invalid PE section: $([IO.Path]::GetFileName($Path))"
            }
            $stream.Position = $rawOffset
            $bytes = $reader.ReadBytes([int] $rawSize)
            if ($bytes.Length -ne $rawSize) {
                throw "Release executable PE section is truncated: $([IO.Path]::GetFileName($Path))"
            }
            $sha256 = [Security.Cryptography.SHA256]::Create()
            try {
                $sectionHash = [BitConverter]::ToString(
                    $sha256.ComputeHash($bytes)).Replace('-', '')
            }
            finally {
                $sha256.Dispose()
            }
            $sections.Add([pscustomobject]@{
                    Name = $name
                    VirtualSize = [long] $virtualSize
                    VirtualAddress = [long] $virtualAddress
                    RawSize = [long] $rawSize
                    RawOffset = [long] $rawOffset
                    Characteristics = ('{0:X8}' -f $characteristics)
                    Sha256 = $sectionHash
                })
        }
        $lastRawByte = ($sections | ForEach-Object {
                $_.RawOffset + $_.RawSize
            } | Measure-Object -Maximum).Maximum
        if ([long] $lastRawByte -ne $stream.Length) {
            throw "Release executable contains an unexpected overlay after its PE sections: $([IO.Path]::GetFileName($Path))"
        }
        return [pscustomobject]@{
            Machine = ('{0:X4}' -f $machine)
            OptionalHeaderMagic = ('{0:X4}' -f $optionalHeaderMagic)
            FileCharacteristics = ('{0:X4}' -f $fileCharacteristics)
            EntryPoint = [long] $entryPoint
            Sections = @($sections)
        }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Get-XmlChildText([Xml.XmlElement] $Parent, [string] $Name) {
    $node = $Parent.SelectSingleNode("*[local-name()='$Name']")
    if ($null -eq $node) {
        throw "Velopack package metadata is missing '$Name'."
    }
    return [string] $node.InnerText
}

$directoryPath = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
$manifestPath = [IO.Path]::GetFullPath($Manifest)
$catalogPath = [IO.Path]::GetFullPath($CompatibilityCatalog)
$releaseSignerPath = [IO.Path]::GetFullPath($ReleaseSigner)
$publicKeyPath = [IO.Path]::GetFullPath($PublicKey)
$applicationPath = [IO.Path]::GetFullPath($PublishedApplicationDirectory).TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $directoryPath -PathType Container)) {
    throw "Release directory not found: $directoryPath"
}
if (-not (Test-Path -LiteralPath $applicationPath -PathType Container)) {
    throw "Published application directory not found: $applicationPath"
}
$expectedManifestPath = Join-Path $directoryPath 'sessiondock-release.json'
if (-not $manifestPath.Equals($expectedManifestPath, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'The release descriptor must be the top-level sessiondock-release.json asset.'
}
$catalogName = 'sessiondock-handlescope-compatibility.json'
$expectedCatalogPath = Join-Path $directoryPath $catalogName
if (-not $catalogPath.Equals($expectedCatalogPath, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $catalogPath -PathType Leaf)) {
    throw "The HandleScope compatibility catalog must be the top-level $catalogName asset."
}
foreach ($trustedVerifierInput in @($releaseSignerPath, $publicKeyPath)) {
    if (-not (Test-Path -LiteralPath $trustedVerifierInput -PathType Leaf) -or
        -not [string]::IsNullOrWhiteSpace(
            [string] (Get-Item -LiteralPath $trustedVerifierInput).LinkType)) {
        throw 'The release metadata verifier and public key must be regular staged files.'
    }
}
$releaseItems = @(Get-ChildItem -LiteralPath $directoryPath -Force)
if ($releaseItems | Where-Object {
        $_.PSIsContainer -or
        -not [string]::IsNullOrWhiteSpace([string] $_.LinkType)
    }) {
    throw 'Release output must contain regular top-level files only.'
}
$applicationItems = @(Get-ChildItem -LiteralPath $applicationPath -Recurse -Force)
if ($applicationItems | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string] $_.LinkType)
    }) {
    throw 'Published application input must not contain reparse points.'
}

$applicationRelativeFiles = Get-RelativeFiles $applicationPath
$requiredApplicationFiles = @(
    'LICENSE.md',
    'SessionDock.exe',
    'SessionDock.dll',
    'SessionDock.deps.json',
    'SessionDock.runtimeconfig.json',
    'SessionDock.ExactWheel.dll',
    'SessionDock.HandleScope.dll',
    'SessionDock.ReleaseTrust.dll',
    'THIRD_PARTY_NOTICES.md',
    'Velopack.dll',
    'licenses/DotNet-LICENSE.txt',
    'licenses/DotNet-THIRD-PARTY-NOTICES.txt',
    'licenses/Microsoft.Web.WebView2-LICENSE.txt',
    'licenses/Microsoft.Web.WebView2-NOTICE.txt',
    'licenses/Microsoft.WindowsDesktop-LICENSE.txt',
    'licenses/Velopack-LICENSE.txt'
)
foreach ($requiredApplicationFile in $requiredApplicationFiles) {
    if ($applicationRelativeFiles -cnotcontains $requiredApplicationFile) {
        throw "Published application input is missing required file '$requiredApplicationFile'."
    }
}
$prohibitedComponentSidecars = @($applicationRelativeFiles | Where-Object {
        $_ -match '(?i)(^|/)HandleScope(?:\.Api)?(?:\.|/|$)' -or
        ($_ -match '(?i)(^|/)SessionDock\.HandleScope(?:\.|/|$)' -and
            $_ -cne 'SessionDock.HandleScope.dll')
    })
if ($prohibitedComponentSidecars.Count -ne 0) {
    throw 'Published application input contains a prohibited HandleScope sidecar.'
}
$sourceComparableApplicationFiles = $applicationRelativeFiles
& (Join-Path $PSScriptRoot 'Verify-UnsignedRelease.ps1') `
    -Directory $applicationPath
& (Join-Path $PSScriptRoot 'Verify-ReleaseLicense.ps1') `
    -LicensePath (Join-Path $applicationPath 'LICENSE.md')

$manifestInfo = Get-Item -LiteralPath $manifestPath
if ($manifestInfo.Length -le 0 -or $manifestInfo.Length -gt 128 * 1024) {
    throw 'Release descriptor must be between 1 byte and 128 KiB.'
}
$descriptor = ConvertFrom-ReleaseJson (Get-Content -LiteralPath $manifestPath -Raw)
$requiredDescriptorFields = @(
    'schemaVersion', 'product', 'repository', 'channel', 'keyId', 'version', 'tag',
    'publishedAt', 'packageFile', 'packageSize', 'packageSha256', 'releaseNotes', 'signature'
)
$actualDescriptorFields = @($descriptor.PSObject.Properties.Name)
Assert-ExactSet `
    -Expected $requiredDescriptorFields `
    -Actual $actualDescriptorFields `
    -Description 'Release descriptor'
if ($descriptor.schemaVersion -ne 1 -or
    $descriptor.product -cne 'SessionDock' -or
    $descriptor.keyId -cne 'sessiondock-release-2026-01' -or
    $descriptor.repository -cne $ExpectedRepository -or
    $descriptor.channel -cne $ExpectedChannel) {
    throw 'Descriptor schema, product, repository, channel, or signing key is not recognized.'
}
if ($descriptor.version -cnotmatch '^\d+\.\d+\.\d+$' -or
    $descriptor.tag -cne "v$($descriptor.version)" -or
    ($ExpectedTag -and $descriptor.tag -cne $ExpectedTag)) {
    throw 'Descriptor version and tag are not aligned stable versions.'
}
try {
    $publishedAt = [DateTimeOffset]::ParseExact(
        [string] $descriptor.publishedAt,
        'O',
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind)
}
catch [FormatException] {
    throw 'Descriptor publication time is invalid.'
}
if ($publishedAt.Offset -ne [TimeSpan]::Zero -or
    $publishedAt -gt [DateTimeOffset]::UtcNow.AddHours(24)) {
    throw 'Descriptor publication time is invalid.'
}
if ([string]::IsNullOrWhiteSpace([string] $descriptor.releaseNotes) -or
    $descriptor.releaseNotes.Length -gt 64 * 1024 -or
    $descriptor.releaseNotes.Contains("`r") -or
    $descriptor.releaseNotes -match '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]') {
    throw 'Descriptor release notes are invalid.'
}
if ($descriptor.packageSha256 -cnotmatch '^[0-9A-F]{64}$' -or
    $descriptor.packageSize -lt 1024 * 1024 -or
    $descriptor.packageSize -gt 1024L * 1024 * 1024) {
    throw 'Descriptor package digest or size is invalid.'
}
try {
    $signatureBytes = [Convert]::FromBase64String([string] $descriptor.signature)
}
catch [FormatException] {
    throw 'Descriptor signature is not valid Base64.'
}
if ($signatureBytes.Length -ne 64) {
    throw 'Descriptor signature must be one P-256 signature.'
}

$catalogInfo = Get-Item -LiteralPath $catalogPath
if ($catalogInfo.Length -le 0 -or $catalogInfo.Length -gt 256 * 1024) {
    throw 'HandleScope compatibility catalog must be between 1 byte and 256 KiB.'
}
$catalog = ConvertFrom-ReleaseJson (Get-Content -LiteralPath $catalogPath -Raw)
$requiredCatalogFields = @(
    'schemaVersion', 'product', 'repository', 'keyId', 'sequence', 'generatedAt',
    'expiresAt', 'sessionDockVersion', 'recommendedVersion', 'releases', 'signature'
)
Assert-ExactSet `
    -Expected $requiredCatalogFields `
    -Actual @($catalog.PSObject.Properties.Name) `
    -Description 'HandleScope compatibility catalog'
$catalogReleases = @($catalog.releases)
if ($catalog.schemaVersion -ne 1 -or
    $catalog.product -cne 'SessionDock.HandleScopeCompatibility' -or
    $catalog.repository -cne $ExpectedRepository -or
    $catalog.keyId -cne 'sessiondock-release-2026-01' -or
    [long] $catalog.sequence -le 0 -or
    $catalog.sessionDockVersion -cne [string] $descriptor.version -or
    $catalog.recommendedVersion -cnotmatch '^\d+\.\d+\.\d+$' -or
    $catalogReleases.Count -le 0 -or
    $catalogReleases.Count -gt 32 -or
    @($catalogReleases | Where-Object {
            $_.version -ceq $catalog.recommendedVersion -and
            $_.status -ceq 'supported'
        }).Count -ne 1) {
    throw 'HandleScope compatibility catalog identity or release binding is invalid.'
}
try {
    $catalogSignatureBytes = [Convert]::FromBase64String(
        [string] $catalog.signature)
}
catch [FormatException] {
    throw 'HandleScope compatibility catalog signature is not valid Base64.'
}
if ($catalogSignatureBytes.Length -ne 64 -or
    [Convert]::ToBase64String($catalogSignatureBytes) -cne
        [string] $catalog.signature) {
    throw 'HandleScope compatibility catalog signature must be one canonical P-256 signature.'
}
& $releaseSignerPath verify-catalog `
    --manifest $catalogPath `
    --public-key $publicKeyPath `
    --sessiondock-version ([string] $descriptor.version)
if ($LASTEXITCODE -ne 0) {
    throw 'HandleScope compatibility catalog cryptographic verification failed.'
}

$packageName = "SessionDockApp-$($descriptor.version)-$ExpectedChannel-full.nupkg"
$portableName = 'SessionDock-win-x64-Portable.zip'
$sbomName = "SessionDock-$($descriptor.version)-sbom.spdx.json"
if ($descriptor.packageFile -cne $packageName) {
    throw 'Descriptor package filename does not match the exact release convention.'
}
$expectedReleaseFiles = @(
    "RELEASES-$ExpectedChannel",
    'SHA256SUMS.txt',
    "assets.$ExpectedChannel.json",
    $packageName,
    $sbomName,
    $portableName,
    "releases.$ExpectedChannel.json",
    $catalogName,
    'sessiondock-release.json'
)
Assert-ExactSet `
    -Expected $expectedReleaseFiles `
    -Actual @($releaseItems.Name) `
    -Description 'Release output'

$packagePath = Join-Path $directoryPath $packageName
$portablePath = Join-Path $directoryPath $portableName
$packageInfo = Get-Item -LiteralPath $packagePath
if ([long] $descriptor.packageSize -ne $packageInfo.Length -or
    (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -cne $descriptor.packageSha256) {
    throw 'Descriptor size or SHA-256 does not match the full package.'
}
Invoke-ReleaseDescriptorVerification `
    -SignerPath $releaseSignerPath `
    -DescriptorPath $manifestPath `
    -PackagePath $packagePath `
    -KeyPath $publicKeyPath

$expectedAssets = [Collections.Generic.Dictionary[string, string]]::new(
    [StringComparer]::Ordinal)
$expectedAssets.Add($packageName, 'Full')
$expectedAssets.Add($portableName, 'Portable')
$parsedAssetsDocument = ConvertFrom-ReleaseJson (Get-Content `
        -LiteralPath (Join-Path $directoryPath "assets.$ExpectedChannel.json") `
        -Raw)
$assetsDocument = @($parsedAssetsDocument)
if ($assetsDocument.Count -ne $expectedAssets.Count) {
    throw 'Velopack asset inventory has an unexpected number of entries.'
}
foreach ($asset in $assetsDocument) {
    Assert-ExactSet `
        -Expected @('RelativeFileName', 'Type') `
        -Actual @($asset.PSObject.Properties.Name) `
        -Description 'Velopack asset inventory entry'
    if (-not $expectedAssets.ContainsKey([string] $asset.RelativeFileName) -or
        $expectedAssets[[string] $asset.RelativeFileName] -cne [string] $asset.Type) {
        throw 'Velopack asset inventory contains an unexpected asset.'
    }
}

$releasesDocument = ConvertFrom-ReleaseJson (Get-Content `
        -LiteralPath (Join-Path $directoryPath "releases.$ExpectedChannel.json") `
        -Raw)
Assert-ExactSet `
    -Expected @('Assets') `
    -Actual @($releasesDocument.PSObject.Properties.Name) `
    -Description 'Velopack release feed'
$feedAssets = @($releasesDocument.Assets)
if ($feedAssets.Count -ne 1) {
    throw 'Velopack release feed must contain exactly one full package.'
}
$feed = $feedAssets[0]
Assert-ExactSet `
    -Expected @('PackageId', 'Version', 'Type', 'FileName', 'SHA1', 'SHA256', 'Size', 'NotesMarkdown', 'NotesHTML') `
    -Actual @($feed.PSObject.Properties.Name) `
    -Description 'Velopack release feed asset'
$packageSha1 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA1).Hash
if ($feed.PackageId -cne 'SessionDockApp' -or
    $feed.Version -cne [string] $descriptor.version -or
    $feed.Type -cne 'Full' -or
    $feed.FileName -cne $packageName -or
    $feed.SHA1 -cne $packageSha1 -or
    $feed.SHA256 -cne [string] $descriptor.packageSha256 -or
    [long] $feed.Size -ne [long] $descriptor.packageSize -or
    (Get-NormalizedNotes ([string] $feed.NotesMarkdown)) -cne [string] $descriptor.releaseNotes) {
    throw 'Velopack release feed does not match the signed descriptor and package.'
}
if ($feed.NotesHTML.Length -gt 128 * 1024 -or
    $feed.NotesHTML -match '(?i)<script|javascript:' -or
    $feed.NotesHTML -match '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]') {
    throw 'Velopack rendered release notes are unsafe.'
}
$legacyFeed = (Get-Content -LiteralPath (Join-Path $directoryPath "RELEASES-$ExpectedChannel") -Raw).Trim()
if ($legacyFeed -cne "$packageSha1 $packageName $($packageInfo.Length)") {
    throw 'Legacy Velopack release metadata does not match the full package.'
}

$expectedPackageEntries = @(
    '[Content_Types].xml'
    '_rels/.rels'
    'SessionDockApp.nuspec'
    'lib/app/SessionDock_ExecutionStub.exe'
    'lib/app/Squirrel.exe'
    'lib/app/sq.version'
    @($sourceComparableApplicationFiles | ForEach-Object { "lib/app/$_" })
)
$packageExtraction = Join-Path ([IO.Path]::GetTempPath()) ("sessiondock-package-" + [Guid]::NewGuid().ToString('N'))
try {
    $packageArchive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        Assert-ExactSet `
            -Expected $expectedPackageEntries `
            -Actual @($packageArchive.Entries.FullName) `
            -Description 'Full package'
    }
    finally {
        $packageArchive.Dispose()
    }
    [IO.Compression.ZipFile]::ExtractToDirectory($packagePath, $packageExtraction)
    foreach ($relativePath in $sourceComparableApplicationFiles) {
        Assert-FileHashEqual `
            -Expected (Join-Path $applicationPath $relativePath) `
            -Actual (Join-Path $packageExtraction "lib/app/$relativePath") `
            -Description "Packaged $relativePath"
    }
    $packagedMainExecutable = Join-Path $packageExtraction 'lib/app/SessionDock.exe'
    $packagedMainExecutableHash = (Get-FileHash `
        -LiteralPath $packagedMainExecutable `
        -Algorithm SHA256).Hash
    Assert-ExecutableVersion `
        -Path $packagedMainExecutable `
        -ExpectedVersion ([string] $descriptor.version)
    $nuspecPath = Join-Path $packageExtraction 'SessionDockApp.nuspec'
    $versionMetadataPath = Join-Path $packageExtraction 'lib/app/sq.version'
    Assert-FileHashEqual `
        -Expected $nuspecPath `
        -Actual $versionMetadataPath `
        -Description 'Velopack version metadata'
    [xml] $nuspec = Get-Content -LiteralPath $nuspecPath -Raw
    $metadata = $nuspec.package.metadata
    if ($null -ne $metadata.SelectSingleNode(
            "*[local-name()='runtimeDependencies']")) {
        throw 'The full update package is not backward-compatible with the strict 2.4.0 metadata verifier.'
    }
    if ((Get-XmlChildText $metadata 'id') -cne 'SessionDockApp' -or
        (Get-XmlChildText $metadata 'version') -cne [string] $descriptor.version -or
        (Get-XmlChildText $metadata 'channel') -cne $ExpectedChannel -or
        (Get-XmlChildText $metadata 'title') -cne 'SessionDock' -or
        (Get-XmlChildText $metadata 'authors') -cne 'Makmatoe' -or
        (Get-XmlChildText $metadata 'description') -cne 'SessionDock' -or
        (Get-XmlChildText $metadata 'mainExe') -cne 'SessionDock.exe' -or
        (Get-XmlChildText $metadata 'rid') -cne 'win-x64' -or
        (Get-XmlChildText $metadata 'machineArchitecture') -cne 'x64' -or
        (Get-XmlChildText $metadata 'shortcutAumid') -cne 'velopack.SessionDockApp' -or
        (Get-XmlChildText $metadata 'os') -cne 'win' -or
        (Get-XmlChildText $metadata 'shortcutLocations') -cne 'Desktop,StartMenuRoot' -or
        (Get-NormalizedNotes (Get-XmlChildText $metadata 'releaseNotes')) -cne [string] $descriptor.releaseNotes) {
        throw 'Velopack package metadata does not match the signed release.'
    }
    foreach ($relativePath in @(
            'lib/app/SessionDock.exe'
            'lib/app/SessionDock.dll'
            'lib/app/SessionDock.ExactWheel.dll'
            'lib/app/SessionDock.HandleScope.dll'
            'lib/app/SessionDock.ReleaseTrust.dll'
            'lib/app/Velopack.dll'
            'lib/app/SessionDock_ExecutionStub.exe'
            'lib/app/Squirrel.exe')) {
        Assert-PortableExecutable (Join-Path $packageExtraction $relativePath)
    }

    $executionStubPath = Join-Path $packageExtraction `
        'lib/app/SessionDock_ExecutionStub.exe'
    $squirrelPath = Join-Path $packageExtraction 'lib/app/Squirrel.exe'
    foreach ($unsignedVelopackHelper in @($executionStubPath, $squirrelPath)) {
        $helperSignature = Get-AuthenticodeSignature -LiteralPath $unsignedVelopackHelper
        if ($helperSignature.Status -ne
                [System.Management.Automation.SignatureStatus]::NotSigned -or
            $null -ne $helperSignature.SignerCertificate -or
            $null -ne $helperSignature.TimeStamperCertificate) {
            throw "Velopack NUPKG helper must remain explicitly unsigned: $([IO.Path]::GetFileName($unsignedVelopackHelper))"
        }
    }
    if ((Get-FileHash -LiteralPath $squirrelPath -Algorithm SHA256).Hash -cne
        '6849325F8FB57FF5D13497C984B9DE82E6B5D46DDFBC857145012D104886287F') {
        throw 'Squirrel.exe does not match the pinned Velopack 1.2.0 vendor update helper.'
    }
    $squirrelVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($squirrelPath)
    if ($squirrelVersion.FileVersion -cne '1.2.0' -or
        $squirrelVersion.ProductVersion -cne '1.2.0') {
        throw 'Squirrel.exe version does not match pinned Velopack 1.2.0.'
    }
    Assert-ExecutableVersion `
        -Path $executionStubPath `
        -ExpectedVersion ([string] $descriptor.version)

    $stubIdentity = Get-PortableExecutableIdentity $executionStubPath
    if ($stubIdentity.Machine -cne '014C' -or
        $stubIdentity.OptionalHeaderMagic -cne '010B' -or
        $stubIdentity.FileCharacteristics -cne '0122' -or
        $stubIdentity.EntryPoint -ne 229617L) {
        throw 'SessionDock execution stub PE header differs from the pinned Velopack 1.2.0 vendor stub.'
    }
    $expectedStubSections = @{
        '.text' = @(293920L, 294400L, 4096L, '60000020',
            'A28AFB163D359D681A5BE82AA4DB936C7C29CC7E63EB4D7D0F5C9DC079A1BD99')
        '.rdata' = @(77004L, 77312L, 299008L, '40000040',
            'B0F7D2E86E9714A83D9C6F64347B891F337821B359D2429AE66F67F4DE8E152F')
        '.data' = @(5416L, 3072L, 376832L, 'C0000040',
            '0D1119266AB2F166ECA9A6C169B5B49A684BC99F1BC9E2984CAAE3DFC60DA2F3')
        '.fptable' = @(128L, 512L, 385024L, 'C0000040',
            '076A27C79E5ACE2A3D47F9DD2E83E4FF6EA8872B3C2218F66C92B89B55F36560')
        '.reloc' = @(13276L, 13312L, 389120L, '42000040',
            'FF831AABBB26EF8145E98392FA02AC2005B63B83F0AA2761A65545376FDB68C9')
    }
    Assert-ExactSet `
        -Expected (@($expectedStubSections.Keys) + @('.rsrc')) `
        -Actual @($stubIdentity.Sections.Name) `
        -Description 'SessionDock execution stub PE sections'
    foreach ($sectionName in $expectedStubSections.Keys) {
        $section = @($stubIdentity.Sections | Where-Object {
                $_.Name -ceq $sectionName
            })[0]
        $expected = $expectedStubSections[$sectionName]
        if ($section.VirtualSize -ne $expected[0] -or
            $section.RawSize -ne $expected[1] -or
            $section.VirtualAddress -ne $expected[2] -or
            $section.Characteristics -cne $expected[3] -or
            $section.Sha256 -cne $expected[4]) {
            throw "SessionDock execution stub section '$sectionName' differs from the pinned Velopack 1.2.0 vendor code."
        }
    }
    $resourceSection = @($stubIdentity.Sections | Where-Object {
            $_.Name -ceq '.rsrc'
        })
    if ($resourceSection.Count -ne 1 -or
        $resourceSection[0].Characteristics -cne '40000040' -or
        $resourceSection[0].VirtualSize -le 0 -or
        $resourceSection[0].RawSize -le 0 -or
        $resourceSection[0].RawSize -gt 262144L) {
        throw 'SessionDock execution stub resource section is missing, executable, or unbounded.'
    }
}
finally {
    if (Test-Path -LiteralPath $packageExtraction) {
        Remove-Item -LiteralPath $packageExtraction -Recurse -Force
    }
}

$expectedPortableEntries = $sourceComparableApplicationFiles
$portableExtraction = Join-Path ([IO.Path]::GetTempPath()) ("sessiondock-portable-" + [Guid]::NewGuid().ToString('N'))
try {
    $portableArchive = [IO.Compression.ZipFile]::OpenRead($portablePath)
    try {
        Assert-ExactSet `
            -Expected $expectedPortableEntries `
            -Actual @($portableArchive.Entries.FullName) `
            -Description 'Portable ZIP'
    }
    finally {
        $portableArchive.Dispose()
    }
    [IO.Compression.ZipFile]::ExtractToDirectory($portablePath, $portableExtraction)
    foreach ($relativePath in $sourceComparableApplicationFiles) {
        Assert-FileHashEqual `
            -Expected (Join-Path $applicationPath $relativePath) `
            -Actual (Join-Path $portableExtraction $relativePath) `
            -Description "Portable $relativePath"
    }
    foreach ($prohibitedPortableEntry in @(
            '.portable'
            'Update.exe'
            'current/SessionDock.exe'
            'SessionDock_ExecutionStub.exe'
            'Squirrel.exe')) {
        if (Test-Path -LiteralPath (Join-Path $portableExtraction $prohibitedPortableEntry)) {
            throw "Transparent portable ZIP contains a prohibited Velopack wrapper: $prohibitedPortableEntry"
        }
    }
    $portableMainExecutable = Join-Path $portableExtraction 'SessionDock.exe'
    $portableMainExecutableHash = (Get-FileHash `
        -LiteralPath $portableMainExecutable `
        -Algorithm SHA256).Hash
    if ($portableMainExecutableHash -cne $packagedMainExecutableHash) {
        throw 'Portable SessionDock.exe does not match the verified full package.'
    }
    Assert-ExecutableVersion `
        -Path $portableMainExecutable `
        -ExpectedVersion ([string] $descriptor.version)
    foreach ($relativePath in @(
            'SessionDock.exe'
            'SessionDock.dll'
            'SessionDock.ExactWheel.dll'
            'SessionDock.HandleScope.dll'
            'SessionDock.ReleaseTrust.dll'
            'Velopack.dll')) {
        Assert-PortableExecutable (Join-Path $portableExtraction $relativePath)
    }
}
finally {
    if (Test-Path -LiteralPath $portableExtraction) {
        Remove-Item -LiteralPath $portableExtraction -Recurse -Force
    }
}
$sbomPath = Join-Path $directoryPath $sbomName
$sbomInfo = Get-Item -LiteralPath $sbomPath
if ($sbomInfo.Length -le 0 -or $sbomInfo.Length -gt 2 * 1024 * 1024) {
    throw 'Release SBOM must be between 1 byte and 2 MiB.'
}
$sbomText = Get-Content -LiteralPath $sbomPath -Raw
$windowsUsersSegment = '\Use' + 'rs\'
$unixHomeSegment = '/' + 'home/'
$unixUsersSegment = '/' + 'Users/'
$machinePathPattern = '(?i)([A-Z]:' + [regex]::Escape($windowsUsersSegment) +
    '|' + [regex]::Escape($unixHomeSegment) + '[^/]+/' +
    '|' + [regex]::Escape($unixUsersSegment) + '[^/]+/)'
if ($sbomText -match $machinePathPattern) {
    throw 'Release SBOM contains a machine-specific user path.'
}
$sbom = ConvertFrom-ReleaseJson $sbomText
if ($sbom.spdxVersion -cne 'SPDX-2.3' -or
    $sbom.dataLicense -cne 'CC0-1.0' -or
    $sbom.SPDXID -cne 'SPDXRef-DOCUMENT' -or
    $sbom.name -cne "SessionDock-$($descriptor.version)-win-x64" -or
    $sbom.documentNamespace -cne "https://spdx.org/spdxdocs/SessionDock-$($descriptor.version)-$($descriptor.packageSha256.ToLowerInvariant())") {
    throw 'Release SBOM identity does not match the signed release.'
}
$sbomPackage = @($sbom.packages | Where-Object { $_.SPDXID -ceq 'SPDXRef-Package-SessionDock' })
if ($sbomPackage.Count -ne 1 -or
    $sbomPackage[0].name -cne $packageName -or
    $sbomPackage[0].versionInfo -cne [string] $descriptor.version -or
    $sbomPackage[0].licenseConcluded -cne 'MIT' -or
    $sbomPackage[0].licenseDeclared -cne 'MIT') {
    throw 'Release SBOM does not describe the full release package.'
}
$sbomChecksum = @($sbomPackage[0].checksums | Where-Object { $_.algorithm -ceq 'SHA256' })
if ($sbomChecksum.Count -ne 1 -or
    $sbomChecksum[0].checksumValue -cne [string] $descriptor.packageSha256) {
    throw 'Release SBOM package checksum does not match the descriptor.'
}
$requiredSbomPackages = @(
    'ExactWheel',
    'HandleScope',
    'Microsoft.AspNetCore.App.Runtime.win-x64',
    'Microsoft.NETCore.App.Runtime.win-x64',
    'Microsoft.Web.WebView2',
    'Microsoft.WindowsDesktop.App.Runtime.win-x64',
    'Velopack'
)
foreach ($requiredPackage in $requiredSbomPackages) {
    if (@($sbom.packages | Where-Object { $_.name -ceq $requiredPackage }).Count -ne 1) {
        throw "Release SBOM is missing required component '$requiredPackage'."
    }
}
$handleScopeCommit = 'ef3b926848353115296faaa9f48f1a5be8c8bae2'
$handleScopeSbomPackages = @($sbom.packages | Where-Object {
        $_.SPDXID -ceq 'SPDXRef-Package-HandleScope'
    })
if ($handleScopeSbomPackages.Count -ne 1 -or
    $handleScopeSbomPackages[0].name -cne 'HandleScope' -or
    $handleScopeSbomPackages[0].versionInfo -cne '0.3.0' -or
    $handleScopeSbomPackages[0].licenseDeclared -cne 'MIT' -or
    $handleScopeSbomPackages[0].supplier -cne 'Person: Makmatoe' -or
    $handleScopeSbomPackages[0].downloadLocation -cne
        "https://github.com/Makmatoe/HandleScope/archive/${handleScopeCommit}.tar.gz" -or
    $handleScopeSbomPackages[0].sourceInfo -notmatch [regex]::Escape($handleScopeCommit) -or
    @($handleScopeSbomPackages[0].externalRefs | Where-Object {
            $_.referenceType -ceq 'purl' -and
            $_.referenceLocator -match [regex]::Escape($handleScopeCommit)
        }).Count -ne 1) {
    throw 'Release SBOM does not contain the exact reviewed HandleScope 0.3.0 source identity.'
}
$exactWheelCommit = '40023f516fe89977a35d94cc5580e790e48d54a1'
$exactWheelInventorySha256 =
    'd20c4933d8fcabbc9b00163ffb20868e74e7cca796344e72508c08e8b1118425'
$exactWheelBuildBlob = '07fe8f9ec14088750f6d2a0d835c86b678a0f76e'
$exactWheelBuildSha256 =
    '76e3be05eea91e5526965d05da043219da67afdc52a423b07707b63fdfaa1841'
$exactWheelSourceInfo =
    "Repository-native tagless ExactWheel source pinned to Git commit $exactWheelCommit; " +
    "14-file canonical inventory SHA-256 $exactWheelInventorySha256; " +
    'build definition SessionDock.ExactWheel/SessionDock.ExactWheel.csproj, ' +
    "1311 bytes, Git blob $exactWheelBuildBlob, SHA-256 $exactWheelBuildSha256; " +
    'license MIT.'
$exactWheelDownloadLocation =
    "https://github.com/Makmatoe/SessionDock/archive/$exactWheelCommit.tar.gz"
$exactWheelSbomPackages = @($sbom.packages | Where-Object {
        $_.SPDXID -ceq 'SPDXRef-Package-ExactWheel'
    })
if ($exactWheelSbomPackages.Count -ne 1 -or
    $exactWheelSbomPackages[0].name -cne 'ExactWheel' -or
    $exactWheelSbomPackages[0].versionInfo -cne '1.1.0' -or
    $exactWheelSbomPackages[0].downloadLocation -cne $exactWheelDownloadLocation -or
    $exactWheelSbomPackages[0].filesAnalyzed -ne $false -or
    $exactWheelSbomPackages[0].licenseConcluded -cne 'NOASSERTION' -or
    $exactWheelSbomPackages[0].licenseDeclared -cne 'MIT' -or
    $exactWheelSbomPackages[0].supplier -cne 'Person: Makmatoe' -or
    $exactWheelSbomPackages[0].copyrightText -cne
        'Copyright (c) 2026 Makmatoe' -or
    $exactWheelSbomPackages[0].sourceInfo -cne $exactWheelSourceInfo) {
    throw 'Release SBOM does not contain the exact reviewed repository-native ExactWheel 1.1.0 source identity.'
}
$aspNetRuntime = @($sbom.packages | Where-Object {
        $_.name -ceq 'Microsoft.AspNetCore.App.Runtime.win-x64'
    })
if ($aspNetRuntime.Count -ne 1 -or
    $aspNetRuntime[0].versionInfo -cne '10.0.10' -or
    $aspNetRuntime[0].licenseDeclared -cne 'MIT') {
    throw 'Release SBOM does not describe the pinned ASP.NET Core runtime.'
}
$containsRelationships = @($sbom.relationships | Where-Object {
        $_.spdxElementId -ceq 'SPDXRef-Package-SessionDock' -and
        $_.relationshipType -ceq 'CONTAINS' -and
        $_.relatedSpdxElement -ceq 'SPDXRef-Package-HandleScope'
    })
if ($containsRelationships.Count -ne 1 -or
    @($sbom.relationships | Where-Object {
            $_.spdxElementId -ceq 'SPDXRef-Package-SessionDock' -and
            $_.relationshipType -ceq 'DEPENDS_ON' -and
            $_.relatedSpdxElement -ceq 'SPDXRef-Package-HandleScope'
        }).Count -ne 0) {
    throw 'Release SBOM must model bundled HandleScope with exactly one CONTAINS relationship.'
}
$exactWheelContainsRelationships = @($sbom.relationships | Where-Object {
        $_.spdxElementId -ceq 'SPDXRef-Package-SessionDock' -and
        $_.relationshipType -ceq 'CONTAINS' -and
        $_.relatedSpdxElement -ceq 'SPDXRef-Package-ExactWheel'
    })
if ($exactWheelContainsRelationships.Count -ne 1 -or
    @($sbom.relationships | Where-Object {
            $_.spdxElementId -ceq 'SPDXRef-Package-SessionDock' -and
            $_.relationshipType -ceq 'DEPENDS_ON' -and
            $_.relatedSpdxElement -ceq 'SPDXRef-Package-ExactWheel'
        }).Count -ne 0) {
    throw 'Release SBOM must model bundled ExactWheel with exactly one CONTAINS relationship.'
}
foreach ($runtimeRelationship in @(
        'SPDXRef-Package-Microsoft.AspNetCore.App.Runtime.win-x64',
        'SPDXRef-Package-Microsoft.NETCore.App.Runtime.win-x64')) {
    if (@($sbom.relationships | Where-Object {
                $_.spdxElementId -ceq 'SPDXRef-Package-HandleScope' -and
                $_.relationshipType -ceq 'DEPENDS_ON' -and
                $_.relatedSpdxElement -ceq $runtimeRelationship
            }).Count -ne 1) {
        throw "Release SBOM is missing HandleScope runtime relationship '$runtimeRelationship'."
    }
}
foreach ($runtimeRelationship in @(
        'SPDXRef-Package-Microsoft.NETCore.App.Runtime.win-x64',
        'SPDXRef-Package-Microsoft.WindowsDesktop.App.Runtime.win-x64')) {
    if (@($sbom.relationships | Where-Object {
                $_.spdxElementId -ceq 'SPDXRef-Package-ExactWheel' -and
                $_.relationshipType -ceq 'DEPENDS_ON' -and
                $_.relatedSpdxElement -ceq $runtimeRelationship
            }).Count -ne 1) {
        throw "Release SBOM is missing ExactWheel runtime relationship '$runtimeRelationship'."
    }
}

$checksumPath = Join-Path $directoryPath 'SHA256SUMS.txt'
$checksumLines = @(Get-Content -LiteralPath $checksumPath)
$assetsWithoutChecksum = @($expectedReleaseFiles | Where-Object { $_ -cne 'SHA256SUMS.txt' } | Sort-Object)
if ($checksumLines.Count -ne $assetsWithoutChecksum.Count) {
    throw 'SHA256SUMS.txt does not cover every release asset exactly once.'
}
$checksumNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($line in $checksumLines) {
    if ($line -cnotmatch '^([0-9a-f]{64})  ([A-Za-z0-9][A-Za-z0-9._-]*)$') {
        throw 'SHA256SUMS.txt contains a malformed line.'
    }
    $hash = $Matches[1]
    $name = $Matches[2]
    if (-not $checksumNames.Add($name) -or $name -ceq 'SHA256SUMS.txt') {
        throw 'SHA256SUMS.txt contains a duplicate or self-referential entry.'
    }
    $actualHash = (Get-FileHash -LiteralPath (Join-Path $directoryPath $name) -Algorithm SHA256).
        Hash.ToLowerInvariant()
    if ($actualHash -cne $hash) {
        throw "SHA256SUMS.txt does not match release asset '$name'."
    }
}
Assert-ExactSet `
    -Expected $assetsWithoutChecksum `
    -Actual @($checksumNames) `
    -Description 'SHA256SUMS.txt'

Write-Host 'Verified exact portable-only release inventory, signed HandleScope catalog and update descriptor, feeds, SPDX SBOM, checksums, licenses, package contents, and executable structure.'
