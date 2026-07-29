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

$expectedFiles = @(
    'LICENSE.md',
    'SessionDock.exe',
    'THIRD_PARTY_NOTICES.md',
    'licenses/DotNet-LICENSE.txt',
    'licenses/DotNet-THIRD-PARTY-NOTICES.txt',
    'licenses/Microsoft.Web.WebView2-LICENSE.txt',
    'licenses/Microsoft.Web.WebView2-NOTICE.txt',
    'licenses/Microsoft.WindowsDesktop-LICENSE.txt',
    'licenses/Velopack-LICENSE.txt'
)
$items = @(Get-ChildItem -LiteralPath $directoryPath -Recurse -Force)
if ($items | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string] $_.LinkType)
    }) {
    throw 'Publish output must not contain symbolic links, junctions, or other reparse points.'
}

$actualFiles = @($items | Where-Object { -not $_.PSIsContainer } | ForEach-Object {
    $_.FullName.Substring($directoryPath.Length + 1).Replace('\', '/')
} | Sort-Object)
$differences = @(Compare-Object -ReferenceObject $expectedFiles -DifferenceObject $actualFiles -CaseSensitive)
if ($differences.Count -ne 0 -or $actualFiles.Count -ne $expectedFiles.Count) {
    throw "Publish output contains missing or unexpected files:`n$($differences | Out-String)"
}

$expectedDirectories = @('licenses')
$actualDirectories = @($items | Where-Object { $_.PSIsContainer } | ForEach-Object {
    $_.FullName.Substring($directoryPath.Length + 1).Replace('\', '/')
} | Sort-Object)
$directoryDifferences = @(Compare-Object `
    -ReferenceObject $expectedDirectories `
    -DifferenceObject $actualDirectories `
    -CaseSensitive)
if ($directoryDifferences.Count -ne 0 -or
    $actualDirectories.Count -ne $expectedDirectories.Count) {
    throw 'Publish output contains missing or unexpected directories.'
}

$applicationPath = Join-Path $directoryPath 'SessionDock.exe'
$application = Get-Item -LiteralPath $applicationPath
if ($application.Length -lt 1024 * 1024 -or $application.Length -gt 1024L * 1024 * 1024) {
    throw 'Published SessionDock.exe has an invalid size.'
}
$version = Get-ProjectVersion
$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($applicationPath).FileVersion
$parsedFileVersion = $null
if (-not [Version]::TryParse($fileVersion, [ref] $parsedFileVersion) -or
    $parsedFileVersion.ToString(3) -cne $version) {
    throw "Published SessionDock.exe version '$fileVersion' does not match project version '$version'."
}

if ($null -eq ('SessionDockPublishIconProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;

public static class SessionDockPublishIconProbe
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
$containsUtf8SmokeArgument = [SessionDockPublishIconProbe]::ContainsBytes(
    $applicationPath,
    [Text.Encoding]::UTF8.GetBytes($removedSmokeArgument))
$containsUnicodeSmokeArgument = [SessionDockPublishIconProbe]::ContainsBytes(
    $applicationPath,
    [Text.Encoding]::Unicode.GetBytes($removedSmokeArgument))
if ($containsUtf8SmokeArgument -or $containsUnicodeSmokeArgument) {
    throw 'Production SessionDock.exe contains the test-only runtime smoke switch.'
}

$iconGroupCount = [SessionDockPublishIconProbe]::ExtractIconEx(
    $applicationPath,
    -1,
    $null,
    $null,
    0)
if ($iconGroupCount -lt 1) {
    throw 'Windows cannot extract the reviewed icon from published SessionDock.exe.'
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

$sources = [ordered]@{
    'LICENSE.md' = Join-Path $root 'LICENSE.md'
    'THIRD_PARTY_NOTICES.md' = Join-Path $root 'THIRD_PARTY_NOTICES.md'
    'licenses/Velopack-LICENSE.txt' = Join-Path $root 'licenses/Velopack-LICENSE.txt'
    'licenses/DotNet-LICENSE.txt' = Resolve-PinnedPackageFile `
        'microsoft.netcore.app.runtime.win-x64/10.0.10/LICENSE.TXT' `
        'D7A68596AB69B06F51CA278A6545148E4269A9381C26D597C13DF5D88E08CF5B'
    'licenses/DotNet-THIRD-PARTY-NOTICES.txt' = Resolve-PinnedPackageFile `
        'microsoft.netcore.app.runtime.win-x64/10.0.10/THIRD-PARTY-NOTICES.TXT' `
        '6D15E10A101C6BFFF2AB4429ED061BF76C456FC4B23AD6B03E0D0F8377148A21'
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

Write-Host "Verified exact production publish inventory, version, smoke-harness exclusion, and complete pinned notices for SessionDock $version."
