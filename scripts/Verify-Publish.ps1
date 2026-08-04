[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Directory
)

. (Join-Path $PSScriptRoot 'Common.ps1')

$root = Get-RepositoryRoot
$directoryPath = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $directoryPath -PathType Container)) {
    throw "Publish directory not found: $directoryPath"
}

function Get-RelativePublishPath([string] $Path) {
    return $Path.Substring($directoryPath.Length + 1).Replace('\', '/')
}

function Get-ProjectFileVersion([string] $RelativeProjectPath) {
    $projectPath = Join-Path $root $RelativeProjectPath
    [xml] $project = Get-Content -LiteralPath $projectPath -Raw
    $versions = @($project.SelectNodes('/Project/PropertyGroup/Version') |
        ForEach-Object { [string] $_.'#text' } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique)
    if ($versions.Count -ne 1) {
        throw "Expected one project version in $RelativeProjectPath."
    }
    return $versions[0]
}

function Assert-PublishedFileVersion(
    [string] $RelativePath,
    [string] $ExpectedVersion) {
    $path = Join-Path $directoryPath $RelativePath
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion
    $parsedFileVersion = $null
    $parsedExpectedVersion = $null
    if (-not [Version]::TryParse($fileVersion, [ref] $parsedFileVersion) -or
        -not [Version]::TryParse($ExpectedVersion, [ref] $parsedExpectedVersion) -or
        $parsedFileVersion.ToString(3) -cne $parsedExpectedVersion.ToString(3)) {
        throw "Published '$RelativePath' version '$fileVersion' does not match '$ExpectedVersion'."
    }
}

$items = @(Get-ChildItem -LiteralPath $directoryPath -Recurse -Force)
if ($items | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string] $_.LinkType)
    }) {
    throw 'Publish output must not contain symbolic links, junctions, or other reparse points.'
}

$actualFiles = @($items | Where-Object { -not $_.PSIsContainer } |
    ForEach-Object { Get-RelativePublishPath $_.FullName } |
    Sort-Object)
$prohibitedExtensions = @(
    '.bat', '.cmd', '.com', '.hta', '.js', '.jse', '.lnk', '.msi', '.msp',
    '.ps1', '.psd1', '.psm1', '.reg', '.scr', '.vbe', '.vbs', '.wsf', '.wsh')
$prohibitedPayloads = @($actualFiles | Where-Object {
        $prohibitedExtensions -ccontains [IO.Path]::GetExtension($_).ToLowerInvariant()
    })
if ($prohibitedPayloads.Count -ne 0) {
    throw "Publish output contains an unexpected executable or script payload:`n$($prohibitedPayloads -join "`n")"
}

$componentImpostors = @($actualFiles | Where-Object {
        $_ -match '(?i)(^|/)(?:SessionDock\.)?(?:HandleScope|ExactWheel)(?:[./_-]|$)' -and
        $_ -cnotin @('SessionDock.HandleScope.dll', 'SessionDock.ExactWheel.dll')
    })
if ($componentImpostors.Count -ne 0) {
    throw "Publish output contains an unexpected component executable, script, or directory:`n$($componentImpostors -join "`n")"
}

$version = Get-ProjectVersion
$exactWheelVersion = Get-ProjectFileVersion `
    'SessionDock.ExactWheel/SessionDock.ExactWheel.csproj'
$handleScopeVersion = Get-ProjectFileVersion `
    'SessionDock.HandleScope/SessionDock.HandleScope.csproj'
$releaseTrustVersion = '1.0.0'

$depsRelativePath = 'SessionDock.deps.json'
$runtimeConfigRelativePath = 'SessionDock.runtimeconfig.json'
$depsPath = Join-Path $directoryPath $depsRelativePath
$runtimeConfigPath = Join-Path $directoryPath $runtimeConfigRelativePath
foreach ($requiredMetadata in @($depsPath, $runtimeConfigPath)) {
    if (-not (Test-Path -LiteralPath $requiredMetadata -PathType Leaf)) {
        throw "Transparent publish metadata is missing: $requiredMetadata"
    }
}

$dependencies = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
$expectedRuntimeTarget = '.NETCoreApp,Version=v10.0/win-x64'
if ($dependencies.runtimeTarget.name -cne $expectedRuntimeTarget) {
    throw "Publish dependency target '$($dependencies.runtimeTarget.name)' is not '$expectedRuntimeTarget'."
}
$targetMatches = @($dependencies.targets.PSObject.Properties |
    Where-Object { $_.Name -ceq $expectedRuntimeTarget })
if ($targetMatches.Count -ne 1) {
    throw 'Publish dependency manifest must contain exactly one win-x64 runtime target.'
}
$target = $targetMatches[0].Value

$expectedLibraries = @(
    "SessionDock/$version",
    "SessionDock.ExactWheel/$exactWheelVersion",
    "SessionDock.HandleScope/$handleScopeVersion",
    "SessionDock.ReleaseTrust/$releaseTrustVersion",
    'Microsoft.Web.WebView2/1.0.4078.44',
    'Microsoft.Web.WebView2.Core/1.0.4078.44',
    'Microsoft.Web.WebView2.WinForms/1.0.4078.44',
    'Microsoft.Web.WebView2.Wpf/1.0.4078.44',
    'Velopack/1.2.0',
    'runtimepack.Microsoft.AspNetCore.App.Runtime.win-x64/10.0.10',
    'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.10',
    'runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64/10.0.10'
) | Sort-Object
$actualLibraries = @($dependencies.libraries.PSObject.Properties.Name | Sort-Object)
$libraryDifferences = @(Compare-Object `
    -ReferenceObject $expectedLibraries `
    -DifferenceObject $actualLibraries `
    -CaseSensitive)
if ($libraryDifferences.Count -ne 0 -or
    $actualLibraries.Count -ne $expectedLibraries.Count) {
    throw "Publish dependency manifest contains missing or unexpected libraries:`n$($libraryDifferences | Out-String)"
}

$runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.tfm -cne 'net10.0') {
    throw 'Publish runtime configuration does not target the pinned net10.0 runtime.'
}
$expectedFrameworks = @(
    'Microsoft.AspNetCore.App/10.0.10',
    'Microsoft.NETCore.App/10.0.10',
    'Microsoft.WindowsDesktop.App/10.0.10')
$actualFrameworks = @($runtimeConfig.runtimeOptions.includedFrameworks |
    ForEach-Object { "$($_.name)/$($_.version)" } |
    Sort-Object)
if (@(Compare-Object $expectedFrameworks $actualFrameworks -CaseSensitive).Count -ne 0 -or
    $actualFrameworks.Count -ne $expectedFrameworks.Count) {
    throw 'Publish runtime configuration does not contain the exact pinned self-contained frameworks.'
}

$expectedBinarySet = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
[void] $expectedBinarySet.Add('SessionDock.exe')
foreach ($library in $target.PSObject.Properties) {
    foreach ($assetKind in @('runtime', 'native')) {
        $assetProperties = @($library.Value.PSObject.Properties |
            Where-Object { $_.Name -ceq $assetKind })
        if ($assetProperties.Count -eq 0) {
            continue
        }
        if ($assetProperties.Count -ne 1) {
            throw "Dependency '$($library.Name)' contains duplicate '$assetKind' assets."
        }
        $assets = $assetProperties[0].Value
        foreach ($asset in $assets.PSObject.Properties.Name) {
            $normalizedAsset = $asset.Replace('\', '/')
            $publishedName = [IO.Path]::GetFileName($normalizedAsset)
            [void] $expectedBinarySet.Add($publishedName)
            if ($normalizedAsset -ceq 'runtimes/win-x64/native/WebView2Loader.dll') {
                [void] $expectedBinarySet.Add($normalizedAsset)
            }
        }
    }
}

# WindowsDesktop's pinned runtime pack copies these localized resources even
# though the generated deps manifest does not enumerate them.
$satelliteCultures = @(
    'cs', 'de', 'es', 'fr', 'it', 'ja', 'ko', 'pl', 'pt-BR', 'ru', 'tr',
    'zh-Hans', 'zh-Hant')
$satelliteAssemblies = @(
    'PresentationCore.resources.dll',
    'PresentationFramework.resources.dll',
    'PresentationUI.resources.dll',
    'ReachFramework.resources.dll',
    'System.Windows.Controls.Ribbon.resources.dll',
    'System.Windows.Input.Manipulations.resources.dll',
    'System.Xaml.resources.dll',
    'UIAutomationClient.resources.dll',
    'UIAutomationClientSideProviders.resources.dll',
    'UIAutomationProvider.resources.dll',
    'UIAutomationTypes.resources.dll',
    'WindowsBase.resources.dll')
foreach ($culture in $satelliteCultures) {
    foreach ($assembly in $satelliteAssemblies) {
        [void] $expectedBinarySet.Add("$culture/$assembly")
    }
}

$requiredBinaryFiles = @(
    'SessionDock.exe',
    'SessionDock.dll',
    'SessionDock.ExactWheel.dll',
    'SessionDock.HandleScope.dll',
    'SessionDock.ReleaseTrust.dll',
    'Velopack.dll',
    'Microsoft.Web.WebView2.Core.dll',
    'WebView2Loader.dll',
    'runtimes/win-x64/native/WebView2Loader.dll',
    'coreclr.dll',
    'clrjit.dll',
    'hostfxr.dll',
    'hostpolicy.dll',
    'PresentationFramework.dll',
    'WindowsBase.dll',
    'createdump.exe')
foreach ($requiredBinary in $requiredBinaryFiles) {
    if (-not $expectedBinarySet.Contains($requiredBinary)) {
        throw "Pinned dependency manifest does not declare required binary: $requiredBinary"
    }
}

$expectedNonBinaryFiles = @(
    'LICENSE.md',
    $depsRelativePath,
    $runtimeConfigRelativePath,
    'THIRD_PARTY_NOTICES.md',
    'licenses/DotNet-LICENSE.txt',
    'licenses/DotNet-THIRD-PARTY-NOTICES.txt',
    'licenses/Microsoft.Web.WebView2-LICENSE.txt',
    'licenses/Microsoft.Web.WebView2-NOTICE.txt',
    'licenses/Microsoft.WindowsDesktop-LICENSE.txt',
    'licenses/Velopack-LICENSE.txt')
$expectedFiles = @(
    @($expectedBinarySet) + $expectedNonBinaryFiles | Sort-Object)
$fileDifferences = @(Compare-Object `
    -ReferenceObject $expectedFiles `
    -DifferenceObject $actualFiles `
    -CaseSensitive)
if ($fileDifferences.Count -ne 0 -or
    $actualFiles.Count -ne $expectedFiles.Count) {
    throw "Transparent publish contains missing or unexpected files:`n$($fileDifferences | Out-String)"
}

$expectedDirectorySet = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
foreach ($relativePath in $expectedFiles) {
    $separatorIndex = $relativePath.LastIndexOf('/')
    while ($separatorIndex -gt 0) {
        $relativeDirectory = $relativePath.Substring(0, $separatorIndex)
        [void] $expectedDirectorySet.Add($relativeDirectory)
        $separatorIndex = $relativeDirectory.LastIndexOf('/')
    }
}
$actualDirectories = @($items | Where-Object { $_.PSIsContainer } |
    ForEach-Object { Get-RelativePublishPath $_.FullName } |
    Sort-Object)
$expectedDirectories = @($expectedDirectorySet | Sort-Object)
if (@(Compare-Object $expectedDirectories $actualDirectories -CaseSensitive).Count -ne 0 -or
    $actualDirectories.Count -ne $expectedDirectories.Count) {
    throw 'Transparent publish contains missing or unexpected directories.'
}

foreach ($binaryPath in @($expectedBinarySet)) {
    $path = Join-Path $directoryPath $binaryPath
    $stream = [IO.File]::OpenRead($path)
    try {
        if ($stream.Length -lt 2 -or
            $stream.ReadByte() -ne [byte][char]'M' -or
            $stream.ReadByte() -ne [byte][char]'Z') {
            throw "Published binary is not a structurally recognizable Windows PE file: $binaryPath"
        }
    }
    finally {
        $stream.Dispose()
    }
}

$applicationPath = Join-Path $directoryPath 'SessionDock.exe'
$applicationAssemblyPath = Join-Path $directoryPath 'SessionDock.dll'
$application = Get-Item -LiteralPath $applicationPath
$applicationAssembly = Get-Item -LiteralPath $applicationAssemblyPath
if ($application.Length -lt 64KB -or $application.Length -gt 4MB) {
    throw 'Published SessionDock.exe is not the expected transparent .NET app host size.'
}
if ($applicationAssembly.Length -lt 256KB -or $applicationAssembly.Length -gt 128MB) {
    throw 'Published SessionDock.dll has an invalid size.'
}
Assert-PublishedFileVersion 'SessionDock.exe' $version
Assert-PublishedFileVersion 'SessionDock.dll' $version
Assert-PublishedFileVersion 'SessionDock.ExactWheel.dll' $exactWheelVersion
Assert-PublishedFileVersion 'SessionDock.HandleScope.dll' $handleScopeVersion
Assert-PublishedFileVersion 'SessionDock.ReleaseTrust.dll' $releaseTrustVersion
Assert-PublishedFileVersion 'Velopack.dll' '1.2.0'
Assert-PublishedFileVersion 'WebView2Loader.dll' '1.0.4078.44'

if ($null -eq ('SessionDockPublishProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;

public static class SessionDockPublishProbe
{
    public static bool ContainsBytes(string path, byte[] pattern)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("The path cannot be empty.", "path");
        if (pattern == null)
            throw new ArgumentNullException("pattern");
        if (pattern.Length == 0)
            throw new ArgumentException("The pattern cannot be empty.", "pattern");

        var prefix = new int[pattern.Length];
        for (var index = 1; index < pattern.Length; index++)
        {
            var candidate = prefix[index - 1];
            while (candidate > 0 && pattern[index] != pattern[candidate])
                candidate = prefix[candidate - 1];
            if (pattern[index] == pattern[candidate])
                candidate++;
            prefix[index] = candidate;
        }

        using (var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan))
        {
            var buffer = new byte[128 * 1024];
            var matched = 0;
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var index = 0; index < count; index++)
                {
                    while (matched > 0 && buffer[index] != pattern[matched])
                        matched = prefix[matched - 1];
                    if (buffer[index] == pattern[matched])
                        matched++;
                    if (matched == pattern.Length)
                        return true;
                }
            }
        }
        return false;
    }

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    public static extern uint ExtractIconEx(
        string file,
        int index,
        IntPtr[] large,
        IntPtr[] small,
        uint count);
}
'@
}

$removedSmokeArgument = '--isolated-runtime-smoke'
$containsUtf8SmokeArgument = [SessionDockPublishProbe]::ContainsBytes(
    $applicationAssemblyPath,
    [Text.Encoding]::UTF8.GetBytes($removedSmokeArgument))
$containsUnicodeSmokeArgument = [SessionDockPublishProbe]::ContainsBytes(
    $applicationAssemblyPath,
    [Text.Encoding]::Unicode.GetBytes($removedSmokeArgument))
if ($containsUtf8SmokeArgument -or $containsUnicodeSmokeArgument) {
    throw 'Production SessionDock.dll contains the test-only runtime smoke switch.'
}
foreach ($componentAssemblyName in @(
        'SessionDock.ExactWheel',
        'SessionDock.HandleScope',
        'SessionDock.ReleaseTrust')) {
    if (-not [SessionDockPublishProbe]::ContainsBytes(
            $applicationAssemblyPath,
            [Text.Encoding]::UTF8.GetBytes($componentAssemblyName))) {
        throw "Published SessionDock.dll does not reference required component '$componentAssemblyName'."
    }
}

$iconGroupCount = [SessionDockPublishProbe]::ExtractIconEx(
    $applicationPath,
    -1,
    $null,
    $null,
    0)
if ($iconGroupCount -lt 1) {
    throw 'Windows cannot extract the reviewed icon from published SessionDock.exe.'
}

$unsignedSigningTargets = @(
    'SessionDock.exe',
    'SessionDock.dll',
    'SessionDock.ExactWheel.dll',
    'SessionDock.HandleScope.dll',
    'SessionDock.ReleaseTrust.dll',
    'Velopack.dll')
$signedRuntimeFiles = @(@($expectedBinarySet) | Where-Object {
        $_ -cnotin $unsignedSigningTargets
    } | Sort-Object)
foreach ($signedRuntimeRelativePath in $signedRuntimeFiles) {
    $signature = Get-AuthenticodeSignature -LiteralPath (
        Join-Path $directoryPath $signedRuntimeRelativePath)
    if ($signature.Status.ToString() -cne 'Valid' -or
        $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch '(?i)(?:^|,\s*)O=Microsoft Corporation(?:,|$)') {
        throw "Pinned Microsoft runtime file has no valid Microsoft signature: $signedRuntimeRelativePath"
    }
}

$assetsPath = Join-Path $root 'SessionDock/obj/project.assets.json'
if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
    throw 'Restore assets are unavailable; publish notices cannot be verified.'
}
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$packageRoots = @($assets.packageFolders.PSObject.Properties.Name |
    ForEach-Object {
        if (-not [IO.Path]::IsPathRooted($_)) {
            throw "NuGet reported a non-absolute package directory: $_"
        }
        [IO.Path]::GetFullPath($_).TrimEnd('\', '/')
    } | Sort-Object -Unique)
if ($packageRoots.Count -lt 1 -or $packageRoots.Count -gt 8) {
    throw "Expected between one and eight NuGet package directories; found $($packageRoots.Count)."
}

function Resolve-PinnedPackageFile(
    [string] $RelativePath,
    [string] $ExpectedSha256) {
    $matches = [Collections.Generic.List[string]]::new()
    foreach ($packageRoot in $packageRoots) {
        $candidate = Join-Path $packageRoot $RelativePath
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }

        $item = Get-Item -LiteralPath $candidate -Force
        if (-not [string]::IsNullOrWhiteSpace([string] $item.LinkType)) {
            throw "A required package notice is a symbolic link: $RelativePath"
        }
        $actualHash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash
        if ($actualHash -cne $ExpectedSha256) {
            throw "A required package notice does not match its pinned upstream hash: $RelativePath"
        }
        $matches.Add($candidate)
    }

    if ($matches.Count -eq 0) {
        throw "Required package notice is unavailable from every restored package directory: $RelativePath"
    }
    return $matches[0]
}

function Get-CombinedDotNetNoticeSha256(
    [string] $NetCoreNotice,
    [string] $AspNetCoreNotice) {
    $encoding = [Text.UTF8Encoding]::new($false)
    $hash = [Security.Cryptography.IncrementalHash]::CreateHash(
        [Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $hash.AppendData($encoding.GetBytes(
            "SessionDock .NET 10.0.10 runtime third-party notices`n`n" +
            "===== Microsoft.NETCore.App.Runtime.win-x64 10.0.10 =====`n"))
        $hash.AppendData([IO.File]::ReadAllBytes($NetCoreNotice))
        $hash.AppendData($encoding.GetBytes(
            "`n===== Microsoft.AspNetCore.App.Runtime.win-x64 10.0.10 =====`n"))
        $hash.AppendData([IO.File]::ReadAllBytes($AspNetCoreNotice))
        return [BitConverter]::ToString($hash.GetHashAndReset()).Replace('-', '')
    }
    finally {
        $hash.Dispose()
    }
}

$netCoreNoticePath = Resolve-PinnedPackageFile `
    'microsoft.netcore.app.runtime.win-x64/10.0.10/THIRD-PARTY-NOTICES.TXT' `
    '6D15E10A101C6BFFF2AB4429ED061BF76C456FC4B23AD6B03E0D0F8377148A21'
$aspNetCoreNoticePath = Resolve-PinnedPackageFile `
    'microsoft.aspnetcore.app.runtime.win-x64/10.0.10/THIRD-PARTY-NOTICES.TXT' `
    '307D014F65D8482314F1400DDEAE7A0CBABB96C2207BCC77F6233CC10588E5D9'
[void] (Resolve-PinnedPackageFile `
    'microsoft.aspnetcore.app.runtime.win-x64/10.0.10/LICENSE.txt' `
    'D7A68596AB69B06F51CA278A6545148E4269A9381C26D597C13DF5D88E08CF5B')

$sources = [ordered]@{
    'LICENSE.md' = Join-Path $root 'LICENSE.md'
    'THIRD_PARTY_NOTICES.md' = Join-Path $root 'THIRD_PARTY_NOTICES.md'
    'licenses/Velopack-LICENSE.txt' = Join-Path $root 'licenses/Velopack-LICENSE.txt'
    'licenses/DotNet-LICENSE.txt' = Resolve-PinnedPackageFile `
        'microsoft.netcore.app.runtime.win-x64/10.0.10/LICENSE.TXT' `
        'D7A68596AB69B06F51CA278A6545148E4269A9381C26D597C13DF5D88E08CF5B'
    'licenses/Microsoft.WindowsDesktop-LICENSE.txt' = Resolve-PinnedPackageFile `
        'microsoft.windowsdesktop.app.runtime.win-x64/10.0.10/LICENSE' `
        'A89886665765362EB77E0F8E26602C924520041D1711B2EEDC136434FE4D01AB'
    'licenses/Microsoft.Web.WebView2-LICENSE.txt' = Resolve-PinnedPackageFile `
        'microsoft.web.webview2/1.0.4078.44/LICENSE.txt' `
        '0AF8F1B807512AAE39C2AC1AA4D0CAE65CABECB6FD554B8439A5162A0D6ECA55'
    'licenses/Microsoft.Web.WebView2-NOTICE.txt' = Resolve-PinnedPackageFile `
        'microsoft.web.webview2/1.0.4078.44/NOTICE.txt' `
        '106423785C5B7EBA0A8E61D1837F2132E9C828E20AD530F565D981C1DF60DD90'
}
$expectedCombinedNoticeHash = Get-CombinedDotNetNoticeSha256 `
    -NetCoreNotice $netCoreNoticePath `
    -AspNetCoreNotice $aspNetCoreNoticePath
$publishedCombinedNoticeHash = (Get-FileHash `
    -LiteralPath (Join-Path $directoryPath 'licenses/DotNet-THIRD-PARTY-NOTICES.txt') `
    -Algorithm SHA256).Hash
if ($publishedCombinedNoticeHash -cne $expectedCombinedNoticeHash) {
    throw 'Published .NET third-party notices are not the deterministic reviewed .NET Core and ASP.NET Core combination.'
}
foreach ($entry in $sources.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $entry.Value -PathType Leaf)) {
        throw "Required release notice source is unavailable: $($entry.Value)"
    }
    $publishedPath = Join-Path $directoryPath $entry.Key
    $sourceHash = (Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash
    $publishedHash = (Get-FileHash -LiteralPath $publishedPath -Algorithm SHA256).Hash
    if ($sourceHash -cne $publishedHash) {
        throw "Published notice '$($entry.Key)' does not match its pinned source."
    }
}

Write-Host (
    "Verified transparent self-contained publish inventory, explicit reviewed " +
    "SessionDock component assemblies, pinned .NET 10.0.10 runtime, Microsoft " +
    "runtime signatures, version, icon, smoke-harness exclusion, and complete " +
    "notices for SessionDock $version.")
