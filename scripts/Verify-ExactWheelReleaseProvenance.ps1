[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ManifestPath,

    [switch] $PassThru
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$fullPath = [IO.Path]::GetFullPath($ManifestPath)
if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
    throw "ExactWheel provenance manifest not found: $fullPath"
}

try {
    $manifest = Get-Content -LiteralPath $fullPath -Raw | ConvertFrom-Json
}
catch {
    throw "ExactWheel provenance manifest is not valid JSON: $fullPath"
}

$expectedProperties = @(
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
    $manifest.schemaVersion -ne 1 -or
    $manifest.component -cne 'ExactWheel' -or
    $manifest.componentVersion -isnot [string] -or
    $manifest.componentVersion -cnotmatch '^\d+\.\d+\.\d+$' -or
    ($manifest.macroFormatVersion -isnot [long] -and
        $manifest.macroFormatVersion -isnot [int]) -or
    [long] $manifest.macroFormatVersion -le 0 -or
    $manifest.integrationKind -cne 'managed-compatible-port' -or
    $manifest.canonicalManifestSha256 -isnot [string] -or
    $manifest.canonicalManifestSha256 -cnotmatch '^[0-9a-f]{64}$' -or
    [long] $manifest.sourceFileCount -le 0 -or
    [long] $manifest.sourceBytes -le 0 -or
    $manifest.releaseBlockedPendingLicense -isnot [bool]) {
    throw 'ExactWheel provenance manifest has an unsupported or incomplete schema.'
}

$blockingReasons = [Collections.Generic.List[string]]::new()
if ($manifest.releaseBlockedPendingLicense) {
    $blockingReasons.Add('releaseBlockedPendingLicense is true')
}
if ($manifest.license -isnot [string] -or
    [string]::IsNullOrWhiteSpace([string] $manifest.license) -or
    [string] $manifest.license -match '^(?:NONE|NOASSERTION|UNKNOWN|UNLICENSED)$' -or
    [string] $manifest.license -cnotmatch '^[A-Za-z0-9][A-Za-z0-9 .()+-]{0,127}$') {
    $blockingReasons.Add('license is missing or is not an explicit SPDX-style expression')
}
if ($manifest.sourceState -cne 'immutable-git') {
    $blockingReasons.Add('sourceState is not immutable-git')
}
if ($manifest.sourceCommit -isnot [string] -or
    [string] $manifest.sourceCommit -cnotmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$') {
    $blockingReasons.Add('sourceCommit is missing or is not a full immutable Git object ID')
}
if ($manifest.sourceTag -isnot [string] -or
    [string]::IsNullOrWhiteSpace([string] $manifest.sourceTag) -or
    [string] $manifest.sourceTag -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._/-]{0,127}$' -or
    [string] $manifest.sourceTag -match '^(?i:HEAD|main|master|latest)$') {
    $blockingReasons.Add('sourceTag is missing or is not an immutable tag name')
}

if ($blockingReasons.Count -ne 0) {
    throw (
        'ExactWheel release provenance is not release-ready: ' +
        ($blockingReasons -join '; ') +
        '. Normal builds remain available, but a public release is blocked.'
    )
}

if ($PassThru) {
    $manifest
}
else {
    Write-Host (
        'Verified release-ready ExactWheel {0} provenance at {1} ({2}).' -f
        $manifest.componentVersion,
        $manifest.sourceTag,
        $manifest.sourceCommit)
}
