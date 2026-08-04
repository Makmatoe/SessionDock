[CmdletBinding(DefaultParameterSetName = 'Local')]
param(
    [Parameter(ParameterSetName = 'Online')]
    [switch] $CheckOnline,

    [Parameter(ParameterSetName = 'Fixture', Mandatory)]
    [string] $MetadataPath,

    [string] $RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ExpectedChannel = '10.0'
$MetadataUri = [Uri] 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json'
$MaximumMetadataBytes = 4MB

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    . (Join-Path $PSScriptRoot 'Common.ps1')
    $RepositoryRoot = Get-RepositoryRoot
}
$root = [IO.Path]::GetFullPath($RepositoryRoot)

function Get-SingleXmlValue {
    param(
        [Parameter(Mandatory)] [xml] $Document,
        [Parameter(Mandatory)] [string] $XPath,
        [Parameter(Mandatory)] [string] $Description
    )

    $values = @($Document.SelectNodes($XPath) |
        ForEach-Object { $_.InnerText } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($values.Count -ne 1) {
        throw "Expected exactly one $Description."
    }
    return [string] $values[0]
}

function Assert-StablePatchVersion {
    param(
        [Parameter(Mandatory)] [string] $Value,
        [Parameter(Mandatory)] [string] $Description
    )

    if ($Value -cnotmatch '^10\.0\.(?:0|[1-9][0-9]*)$') {
        throw "$Description must be one exact stable .NET 10 patch version; found '$Value'."
    }
}

function Read-BoundedMetadata {
    if ($PSCmdlet.ParameterSetName -eq 'Fixture') {
        $fullPath = [IO.Path]::GetFullPath($MetadataPath)
        $info = [IO.FileInfo]::new($fullPath)
        if (-not $info.Exists -or $info.Length -le 0 -or
            $info.Length -gt $MaximumMetadataBytes) {
            throw 'The .NET release metadata fixture is missing, empty, or oversized.'
        }
        return [IO.File]::ReadAllBytes($fullPath)
    }

    if (-not $CheckOnline) {
        return $null
    }
    if ($MetadataUri.Scheme -cne 'https' -or
        $MetadataUri.Host -cne 'dotnetcli.blob.core.windows.net' -or
        -not $MetadataUri.IsDefaultPort -or
        -not [string]::IsNullOrEmpty($MetadataUri.UserInfo) -or
        -not [string]::IsNullOrEmpty($MetadataUri.Fragment)) {
        throw 'The .NET metadata endpoint is not the pinned official HTTPS endpoint.'
    }

    Add-Type -AssemblyName System.Net.Http
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(30)
    try {
        $response = $client.GetAsync(
            $MetadataUri,
            [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            if ($response.StatusCode -ne [System.Net.HttpStatusCode]::OK) {
                throw "Official .NET metadata returned HTTP $([int] $response.StatusCode)."
            }
            if ($response.Content.Headers.ContentLength -gt $MaximumMetadataBytes) {
                throw 'Official .NET metadata exceeded the response-size limit.'
            }
            $input = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            try {
                $output = [IO.MemoryStream]::new()
                try {
                    $buffer = [byte[]]::new(16384)
                    while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                        if ($output.Length + $read -gt $MaximumMetadataBytes) {
                            throw 'Official .NET metadata exceeded the response-size limit.'
                        }
                        $output.Write($buffer, 0, $read)
                    }
                    return $output.ToArray()
                }
                finally { $output.Dispose() }
            }
            finally { $input.Dispose() }
        }
        finally { $response.Dispose() }
    }
    finally {
        $client.Dispose()
        $handler.Dispose()
    }
}

function ConvertFrom-BoundedJson {
    param([Parameter(Mandatory)] [byte[]] $Bytes)

    try {
        $text = [Text.UTF8Encoding]::new($false, $true).GetString($Bytes)
    }
    catch [Text.DecoderFallbackException] {
        throw 'Official .NET metadata was not valid UTF-8.'
    }

    $depth = 0
    $maximumDepth = 0
    $insideString = $false
    $escaped = $false
    foreach ($character in $text.ToCharArray()) {
        if ($insideString) {
            if ($escaped) { $escaped = $false; continue }
            if ($character -eq '\') { $escaped = $true; continue }
            if ($character -eq '"') { $insideString = $false }
            continue
        }
        if ($character -eq '"') { $insideString = $true; continue }
        if ($character -in @('{', '[')) {
            $depth++
            $maximumDepth = [Math]::Max($maximumDepth, $depth)
            if ($maximumDepth -gt 32) {
                throw 'Official .NET metadata exceeded the JSON depth limit.'
            }
        }
        elseif ($character -in @('}', ']')) {
            $depth--
            if ($depth -lt 0) { throw 'Official .NET metadata was malformed.' }
        }
    }
    if ($insideString -or $depth -ne 0) {
        throw 'Official .NET metadata was malformed.'
    }

    try { return $text | ConvertFrom-Json }
    catch { throw 'Official .NET metadata was malformed.' }
}

$globalJsonPath = Join-Path $root 'global.json'
$projectPath = Join-Path $root 'SessionDock/SessionDock.csproj'
$lockPath = Join-Path $root 'SessionDock/packages.lock.json'
$globalJson = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
[xml] $project = Get-Content -LiteralPath $projectPath -Raw
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json

$sdkVersion = [string] $globalJson.sdk.version
$runtimeVersion = Get-SingleXmlValue $project `
    '/Project/PropertyGroup/RuntimeFrameworkVersion' 'runtime framework version'
Assert-StablePatchVersion $runtimeVersion 'RuntimeFrameworkVersion'
if ($sdkVersion -cnotmatch '^10\.0\.(?:0|[1-9][0-9]*)$' -or
    $globalJson.sdk.rollForward -cne 'disable' -or
    $globalJson.sdk.allowPrerelease -ne $false) {
    throw 'global.json must pin one stable .NET 10 SDK patch, disable roll-forward, and reject prerelease SDKs.'
}

$frameworks = @($lock.dependencies.PSObject.Properties |
    Where-Object { $_.Name -notmatch '/' })
if ($frameworks.Count -ne 1) {
    throw 'The application lockfile must contain exactly one target framework.'
}
$publishTrimmed = Get-SingleXmlValue $project `
    '/Project/PropertyGroup/PublishTrimmed' 'PublishTrimmed value'
$publishReadyToRun = Get-SingleXmlValue $project `
    '/Project/PropertyGroup/PublishReadyToRun' 'PublishReadyToRun value'
if ($publishTrimmed -cne 'false' -or $publishReadyToRun -cne 'false') {
    throw 'The transparent runtime must explicitly disable trimming and ReadyToRun.'
}

$linkerDependencies = @($frameworks[0].Value.PSObject.Properties |
    Where-Object { $_.Name -ceq 'Microsoft.NET.ILLink.Tasks' })
if ($linkerDependencies.Count -ne 0) {
    throw 'The non-trimmed transparent runtime lockfile must not contain Microsoft.NET.ILLink.Tasks.'
}

$projectText = Get-Content -LiteralPath $projectPath -Raw
$escapedRuntime = [regex]::Escape($runtimeVersion)
$licenseMatches = @([regex]::Matches(
    $projectText,
    "microsoft\.(?:netcore|windowsdesktop)\.app\.runtime\.win-x64\\(?<version>[^\\<]+)\\"))
if ($licenseMatches.Count -ne 3 -or
    @($licenseMatches | Where-Object {
        $_.Groups['version'].Value -cne $runtimeVersion
    }).Count -ne 0) {
    throw 'Bundled .NET runtime license and notice paths must match RuntimeFrameworkVersion.'
}

$metadataBytes = Read-BoundedMetadata
if ($null -ne $metadataBytes) {
    $metadata = ConvertFrom-BoundedJson $metadataBytes
    if ($metadata.'channel-version' -cne $ExpectedChannel) {
        throw 'Official .NET metadata did not describe the expected 10.0 channel.'
    }
    $latestRuntime = [string] $metadata.'latest-runtime'
    $latestSdk = [string] $metadata.'latest-sdk'
    Assert-StablePatchVersion $latestRuntime 'Official latest runtime'
    if ($latestSdk -cnotmatch '^10\.0\.(?:0|[1-9][0-9]*)$') {
        throw "Official latest SDK is not an exact stable version: '$latestSdk'."
    }
    if ([version] $runtimeVersion -lt [version] $latestRuntime) {
        throw "A newer supported .NET 10 runtime security patch exists: checked in $runtimeVersion, official $latestRuntime (SDK $latestSdk)."
    }
    if ($runtimeVersion -cne $latestRuntime -or $sdkVersion -cne $latestSdk) {
        throw "The checked-in .NET pair ($runtimeVersion / $sdkVersion) does not match official metadata ($latestRuntime / $latestSdk)."
    }
}

Write-Host "The .NET security baseline is consistent: runtime $runtimeVersion, SDK $sdkVersion."
