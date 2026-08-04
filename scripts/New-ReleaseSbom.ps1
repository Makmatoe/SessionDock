[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Descriptor,

    [Parameter(Mandatory)]
    [string] $Project,

    [Parameter(Mandatory)]
    [string] $LockFile,

    [Parameter(Mandatory)]
    [string] $License,

    [Parameter(Mandatory)]
    [string] $BundledHandleScopeManifest,

    [Parameter(Mandatory)]
    [string] $BundledExactWheelManifest,

    [Parameter(Mandatory)]
    [string] $Output
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseJson.ps1')

function Require-File([string] $Path, [string] $Description) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "$Description not found: $fullPath"
    }
    return $fullPath
}

function New-SpdxPackage(
    [string] $Name,
    [string] $Version,
    [string] $LicenseId,
    [string] $Supplier,
    [string] $SpdxId) {
    return [ordered]@{
        name = $Name
        SPDXID = $SpdxId
        versionInfo = $Version
        downloadLocation = 'NOASSERTION'
        filesAnalyzed = $false
        licenseConcluded = 'NOASSERTION'
        licenseDeclared = $LicenseId
        copyrightText = 'NOASSERTION'
        supplier = $Supplier
        externalRefs = @(
            [ordered]@{
                referenceCategory = 'PACKAGE-MANAGER'
                referenceType = 'purl'
                referenceLocator = "pkg:nuget/$Name@$Version"
            }
        )
    }
}

$descriptorPath = Require-File $Descriptor 'Release descriptor'
$projectPath = Require-File $Project 'Application project'
$lockPath = Require-File $LockFile 'Application package lock'
[void] (Require-File $License 'Release license')
$handleScopeManifestPath = Require-File `
    $BundledHandleScopeManifest `
    'Bundled HandleScope provenance manifest'
$exactWheelManifestPath = Require-File `
    $BundledExactWheelManifest `
    'Bundled ExactWheel provenance manifest'
$outputPath = [IO.Path]::GetFullPath($Output)

$release = ConvertFrom-ReleaseJson (Get-Content -LiteralPath $descriptorPath -Raw)
Write-Verbose 'Parsed release descriptor.'
if ($release.version -cnotmatch '^\d+\.\d+\.\d+$' -or
    $release.tag -cne "v$($release.version)" -or
    $release.repository -cne 'Makmatoe/SessionDock' -or
    $release.packageSha256 -cnotmatch '^[0-9A-F]{64}$') {
    throw 'The release descriptor is not valid SBOM input.'
}
if ([IO.Path]::GetFileName($outputPath) -cne "SessionDock-$($release.version)-sbom.spdx.json") {
    throw 'The SPDX SBOM filename must contain the exact release version.'
}

[xml] $projectXml = Get-Content -LiteralPath $projectPath -Raw
$runtimeVersions = @($projectXml.SelectNodes('/Project/PropertyGroup/RuntimeFrameworkVersion') |
    ForEach-Object { $_.InnerText } | Where-Object { $_ })
if ($runtimeVersions.Count -ne 1 -or $runtimeVersions[0] -cnotmatch '^\d+\.\d+\.\d+$') {
    throw 'The project must pin exactly one three-part RuntimeFrameworkVersion.'
}
$runtimeVersion = [string] $runtimeVersions[0]

$handleScopeManifest = ConvertFrom-ReleaseJson (
    Get-Content -LiteralPath $handleScopeManifestPath -Raw)
$handleScopeProperties = @($handleScopeManifest.PSObject.Properties.Name | Sort-Object)
if (@(Compare-Object `
        @('component', 'componentVersion', 'schemaVersion', 'sources', 'upstream') `
        $handleScopeProperties `
        -CaseSensitive).Count -ne 0 -or
    $handleScopeManifest.schemaVersion -ne 1 -or
    $handleScopeManifest.component -cne 'HandleScope' -or
    $handleScopeManifest.componentVersion -cne '0.3.0' -or
    $handleScopeManifest.upstream.repository -cne 'https://github.com/Makmatoe/HandleScope' -or
    $handleScopeManifest.upstream.tag -cne 'v0.3.0' -or
    $handleScopeManifest.upstream.commit -cne 'ef3b926848353115296faaa9f48f1a5be8c8bae2' -or
    @($handleScopeManifest.sources).Count -le 0) {
    throw 'The bundled HandleScope provenance is not the reviewed 0.3.0 source identity.'
}

$exactWheelVerifier = Require-File `
    (Join-Path $PSScriptRoot 'Verify-ExactWheelReleaseProvenance.ps1') `
    'ExactWheel release provenance verifier'
$exactWheelManifest = & $exactWheelVerifier `
    -ManifestPath $exactWheelManifestPath `
    -StagedManifestOnly `
    -PassThru
if ($null -eq $exactWheelManifest) {
    throw 'The bundled ExactWheel provenance verifier returned no release identity.'
}

$lock = ConvertFrom-ReleaseJson (Get-Content -LiteralPath $lockPath -Raw)
Write-Verbose 'Parsed application lock file.'
$resolved = @{}
foreach ($framework in $lock.dependencies.PSObject.Properties) {
    foreach ($dependency in $framework.Value.PSObject.Properties) {
        $value = $dependency.Value
        if ($value.type -cne 'Direct' -or
            $dependency.Name -ceq 'Microsoft.NET.ILLink.Tasks') {
            continue
        }
        $id = [string] $dependency.Name
        $version = [string] $value.resolved
        if ($version -cnotmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
            throw "Dependency '$id' has an unsupported resolved version '$version'."
        }
        if ($resolved.ContainsKey($id) -and $resolved[$id] -cne $version) {
            throw "Dependency '$id' resolves to more than one version."
        }
        $resolved[$id] = $version
    }
}

$licenses = @{
    'Microsoft.Web.WebView2' = 'BSD-3-Clause'
    'Velopack' = 'MIT'
}
$suppliers = @{
    'Microsoft.Web.WebView2' = 'Organization: Microsoft Corporation'
    'Velopack' = 'Organization: Velopack Ltd'
}
$packages = [Collections.Generic.List[object]]::new()
$releasePackage = [ordered]@{
    name = [string] $release.packageFile
    SPDXID = 'SPDXRef-Package-SessionDock'
    versionInfo = [string] $release.version
    downloadLocation = "https://github.com/Makmatoe/SessionDock/releases/download/$($release.tag)/$($release.packageFile)"
    filesAnalyzed = $false
    checksums = @(
        [ordered]@{
            algorithm = 'SHA256'
            checksumValue = [string] $release.packageSha256
        }
    )
    licenseConcluded = 'MIT'
    licenseDeclared = 'MIT'
    copyrightText = 'Copyright (c) 2026 Makmatoe'
    supplier = 'Person: Makmatoe'
}
$packages.Add($releasePackage)
$handleScopePackage = [ordered]@{
    name = 'HandleScope'
    SPDXID = 'SPDXRef-Package-HandleScope'
    versionInfo = [string] $handleScopeManifest.componentVersion
    downloadLocation =
        "https://github.com/Makmatoe/HandleScope/archive/$($handleScopeManifest.upstream.commit).tar.gz"
    filesAnalyzed = $false
    licenseConcluded = 'NOASSERTION'
    licenseDeclared = 'MIT'
    copyrightText = 'Copyright (c) 2026 Makmatoe'
    supplier = 'Person: Makmatoe'
    sourceInfo =
        "Bundled reviewed source from $($handleScopeManifest.upstream.tag) at Git commit $($handleScopeManifest.upstream.commit)."
    externalRefs = @(
        [ordered]@{
            referenceCategory = 'PACKAGE-MANAGER'
            referenceType = 'purl'
            referenceLocator =
                "pkg:github/Makmatoe/HandleScope@$($handleScopeManifest.componentVersion)?vcs_url=git%2Bhttps%3A%2F%2Fgithub.com%2FMakmatoe%2FHandleScope.git%40$($handleScopeManifest.upstream.commit)"
        }
    )
}
$packages.Add($handleScopePackage)
$exactWheelPackage = [ordered]@{
    name = 'ExactWheel'
    SPDXID = 'SPDXRef-Package-ExactWheel'
    versionInfo = [string] $exactWheelManifest.componentVersion
    downloadLocation =
        "https://github.com/Makmatoe/SessionDock/archive/$($exactWheelManifest.sourceCommit).tar.gz"
    filesAnalyzed = $false
    licenseConcluded = 'NOASSERTION'
    licenseDeclared = [string] $exactWheelManifest.license
    copyrightText = 'Copyright (c) 2026 Makmatoe'
    supplier = 'Person: Makmatoe'
    sourceInfo =
        "Repository-native tagless ExactWheel source pinned to Git commit $($exactWheelManifest.sourceCommit); $($exactWheelManifest.sourceFileCount)-file canonical inventory SHA-256 $($exactWheelManifest.canonicalManifestSha256); build definition $($exactWheelManifest.buildDefinitionPath), $($exactWheelManifest.buildDefinitionBytes) bytes, Git blob $($exactWheelManifest.buildDefinitionGitBlob), SHA-256 $($exactWheelManifest.buildDefinitionSha256); license $($exactWheelManifest.license)."
}
$packages.Add($exactWheelPackage)

foreach ($dependency in $resolved.GetEnumerator() | Sort-Object Key) {
    $name = [string] $dependency.Key
    if (-not $licenses.ContainsKey($name) -or -not $suppliers.ContainsKey($name)) {
        throw "Dependency '$name' is missing an explicit SBOM license or supplier mapping."
    }
    $id = 'SPDXRef-Package-' + ($name -replace '[^A-Za-z0-9.-]', '-')
    $packages.Add((New-SpdxPackage `
        -Name $name `
        -Version ([string] $dependency.Value) `
        -LicenseId ([string] $licenses[$name]) `
        -Supplier ([string] $suppliers[$name]) `
        -SpdxId $id))
}
foreach ($runtimeName in @(
        'Microsoft.AspNetCore.App.Runtime.win-x64',
        'Microsoft.NETCore.App.Runtime.win-x64',
        'Microsoft.WindowsDesktop.App.Runtime.win-x64')) {
    $id = 'SPDXRef-Package-' + ($runtimeName -replace '[^A-Za-z0-9.-]', '-')
    $packages.Add((New-SpdxPackage `
        -Name $runtimeName `
        -Version $runtimeVersion `
        -LicenseId 'MIT' `
        -Supplier 'Organization: Microsoft Corporation' `
        -SpdxId $id))
}
Write-Verbose 'Constructed SPDX package list.'

$relationships = [Collections.Generic.List[object]]::new()
$relationships.Add([ordered]@{
    spdxElementId = 'SPDXRef-DOCUMENT'
    relationshipType = 'DESCRIBES'
    relatedSpdxElement = 'SPDXRef-Package-SessionDock'
})
foreach ($package in @($packages.ToArray() | Where-Object {
            $_.SPDXID -notin @(
                'SPDXRef-Package-SessionDock',
                'SPDXRef-Package-HandleScope',
                'SPDXRef-Package-ExactWheel')
        })) {
    $relationships.Add([ordered]@{
        spdxElementId = 'SPDXRef-Package-SessionDock'
        relationshipType = 'DEPENDS_ON'
        relatedSpdxElement = $package.SPDXID
    })
}
$relationships.Add([ordered]@{
    spdxElementId = 'SPDXRef-Package-SessionDock'
    relationshipType = 'CONTAINS'
    relatedSpdxElement = 'SPDXRef-Package-HandleScope'
})
$relationships.Add([ordered]@{
    spdxElementId = 'SPDXRef-Package-SessionDock'
    relationshipType = 'CONTAINS'
    relatedSpdxElement = 'SPDXRef-Package-ExactWheel'
})
foreach ($runtimeId in @(
        'SPDXRef-Package-Microsoft.AspNetCore.App.Runtime.win-x64',
        'SPDXRef-Package-Microsoft.NETCore.App.Runtime.win-x64')) {
    $relationships.Add([ordered]@{
        spdxElementId = 'SPDXRef-Package-HandleScope'
        relationshipType = 'DEPENDS_ON'
        relatedSpdxElement = $runtimeId
    })
}
foreach ($runtimeId in @(
        'SPDXRef-Package-Microsoft.NETCore.App.Runtime.win-x64',
        'SPDXRef-Package-Microsoft.WindowsDesktop.App.Runtime.win-x64')) {
    $relationships.Add([ordered]@{
        spdxElementId = 'SPDXRef-Package-ExactWheel'
        relationshipType = 'DEPENDS_ON'
        relatedSpdxElement = $runtimeId
    })
}
Write-Verbose 'Constructed SPDX relationships.'

$publishedAt = [DateTimeOffset]::ParseExact(
    [string] $release.publishedAt,
    'O',
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
$document = [ordered]@{
    spdxVersion = 'SPDX-2.3'
    dataLicense = 'CC0-1.0'
    SPDXID = 'SPDXRef-DOCUMENT'
    name = "SessionDock-$($release.version)-win-x64"
    documentNamespace = "https://spdx.org/spdxdocs/SessionDock-$($release.version)-$($release.packageSha256.ToLowerInvariant())"
    creationInfo = [ordered]@{
        created = $publishedAt.ToString('yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture)
        creators = @('Tool: SessionDock-New-ReleaseSbom.ps1')
        licenseListVersion = '3.26'
    }
    packages = $packages.ToArray()
    relationships = $relationships.ToArray()
}
Write-Verbose 'Constructed SPDX document.'

$outputDirectory = Split-Path -Parent $outputPath
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    throw "SBOM output directory not found: $outputDirectory"
}
$json = $document | ConvertTo-Json -Depth 6
Write-Verbose 'Serialized SPDX document.'
[IO.File]::WriteAllText(
    $outputPath,
    $json + "`n",
    [Text.UTF8Encoding]::new($false))
Write-Host "Wrote SPDX 2.3 SBOM for SessionDock $($release.version)."
