[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [ValidatePattern('^win-(x64|arm64)$')]
    [string] $Runtime = 'win-x64',

    [string] $OutputDirectory = 'artifacts/publish',

    [switch] $CI,

    [switch] $SkipPublish
)

. (Join-Path $PSScriptRoot 'Common.ps1')

$root = Get-RepositoryRoot
$project = Get-ApplicationProject
$output = Assert-SafeOutputDirectory (Join-Path $root $OutputDirectory)
$commonProperties = @(
    "-p:ContinuousIntegrationBuild=$($CI.IsPresent.ToString().ToLowerInvariant())",
    '-p:Deterministic=true'
)

function Resolve-PinnedNuGetFile(
    [string] $AssetsPath,
    [string] $RelativePath,
    [string] $ExpectedSha256) {
    $assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json
    $packageRoots = @($assets.packageFolders.PSObject.Properties.Name |
        ForEach-Object {
            if (-not [IO.Path]::IsPathRooted($_)) {
                throw "NuGet reported a non-absolute package directory: $_"
            }
            [IO.Path]::GetFullPath($_).TrimEnd('\', '/')
        } | Sort-Object -Unique)
    $matches = [Collections.Generic.List[string]]::new()
    foreach ($packageRoot in $packageRoots) {
        $candidate = Join-Path $packageRoot $RelativePath
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }
        if (-not [string]::IsNullOrWhiteSpace(
                [string] (Get-Item -LiteralPath $candidate -Force).LinkType)) {
            throw "A required runtime notice is a reparse point: $RelativePath"
        }
        if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -cne
            $ExpectedSha256) {
            throw "A required runtime notice does not match its pinned hash: $RelativePath"
        }
        $matches.Add($candidate)
    }
    if ($matches.Count -eq 0) {
        throw "A required runtime notice is unavailable: $RelativePath"
    }
    return $matches[0]
}

function Write-CombinedDotNetThirdPartyNotices(
    [string] $AssetsPath,
    [string] $Destination) {
    $netCoreNotice = Resolve-PinnedNuGetFile `
        -AssetsPath $AssetsPath `
        -RelativePath 'microsoft.netcore.app.runtime.win-x64/10.0.10/THIRD-PARTY-NOTICES.TXT' `
        -ExpectedSha256 '6D15E10A101C6BFFF2AB4429ED061BF76C456FC4B23AD6B03E0D0F8377148A21'
    $aspNetCoreNotice = Resolve-PinnedNuGetFile `
        -AssetsPath $AssetsPath `
        -RelativePath 'microsoft.aspnetcore.app.runtime.win-x64/10.0.10/THIRD-PARTY-NOTICES.TXT' `
        -ExpectedSha256 '307D014F65D8482314F1400DDEAE7A0CBABB96C2207BCC77F6233CC10588E5D9'
    $encoding = [Text.UTF8Encoding]::new($false)
    $parts = @(
        $encoding.GetBytes(
            "SessionDock .NET 10.0.10 runtime third-party notices`n`n" +
            "===== Microsoft.NETCore.App.Runtime.win-x64 10.0.10 =====`n"),
        [IO.File]::ReadAllBytes($netCoreNotice),
        $encoding.GetBytes(
            "`n===== Microsoft.AspNetCore.App.Runtime.win-x64 10.0.10 =====`n"),
        [IO.File]::ReadAllBytes($aspNetCoreNotice)
    )
    $destinationPath = [IO.Path]::GetFullPath($Destination)
    $destinationDirectory = Split-Path -Parent $destinationPath
    if (-not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
        throw "Runtime notice output directory not found: $destinationDirectory"
    }
    $stream = [IO.File]::Open(
        $destinationPath,
        [IO.FileMode]::Create,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    try {
        foreach ($part in $parts) {
            $stream.Write($part, 0, $part.Length)
        }
    }
    finally {
        $stream.Dispose()
    }
}

Push-Location $root
try {
    & (Join-Path $PSScriptRoot 'Sync-BundledHandleScope.ps1')
    & (Join-Path $PSScriptRoot 'Verify-Repository.ps1') -CI:$CI

    $projects = [Collections.Generic.List[string]]::new()
    $projects.Add($project)
    $solutionPath = Join-Path $root 'SessionDock.slnx'
    [xml] $solution = Get-Content -LiteralPath $solutionPath -Raw
    $solutionTestProjects = @($solution.SelectNodes('//Project') |
        ForEach-Object { [string] $_.Path } |
        Where-Object { $_ -match '(?i)(^|[\\/])[^\\/]+Tests\.csproj$' } |
        Sort-Object -Unique)
    if ($solutionTestProjects.Count -eq 0) {
        throw 'The solution must include at least one test project.'
    }
    foreach ($relativeTestProject in $solutionTestProjects) {
        $testProject = [IO.Path]::GetFullPath((Join-Path $root $relativeTestProject))
        $rootPrefix = $root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $testProject.StartsWith(
                $rootPrefix,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $testProject -PathType Leaf)) {
            throw "Solution test project is invalid: $relativeTestProject"
        }
        $projects.Add($testProject)
    }

    $signerProject = Join-Path $root 'SessionDock/tools/ReleaseSigner/ReleaseSigner.csproj'
    if (Test-Path -LiteralPath $signerProject -PathType Leaf) {
        $projects.Add($signerProject)
    }

    foreach ($item in $projects | Select-Object -Unique) {
        $restoreArguments = @('restore', $item, '--locked-mode', '--runtime', $Runtime)
        Invoke-CheckedCommand dotnet @restoreArguments
    }

    if ($CI) {
        & (Join-Path $PSScriptRoot 'Verify-NuGetSecurity.ps1') `
            -Project (Join-Path $root 'SessionDock.slnx')
    }

    Invoke-CheckedCommand dotnet build $project '--configuration' $Configuration '--runtime' $Runtime '--no-restore' @commonProperties

    if (Test-Path -LiteralPath $signerProject -PathType Leaf) {
        Invoke-CheckedCommand dotnet build $signerProject '--configuration' $Configuration '--runtime' $Runtime '--no-restore' @commonProperties
    }

    foreach ($testProject in $projects | Where-Object { $_ -like '*Tests.csproj' }) {
        Invoke-CheckedCommand dotnet build $testProject '--configuration' $Configuration '--runtime' $Runtime '--no-restore' @commonProperties
        Invoke-CheckedCommand dotnet test $testProject '--configuration' $Configuration '--runtime' $Runtime `
            '--no-restore' '--no-build' @commonProperties
    }

    if (-not $SkipPublish) {
        if (Test-Path -LiteralPath $output) {
            Remove-SafeOutputDirectory $output
        }
        New-Item -ItemType Directory -Path $output -Force | Out-Null
        Invoke-CheckedCommand dotnet publish $project '--configuration' $Configuration '--runtime' $Runtime `
            '--self-contained' 'true' '--no-restore' '--output' $output `
            '-p:PublishSingleFile=false' `
            '-p:IncludeNativeLibrariesForSelfExtract=false' `
            '-p:EnableCompressionInSingleFile=false' `
            '-p:PublishTrimmed=false' `
            '-p:PublishReadyToRun=false' `
            @commonProperties
        Write-CombinedDotNetThirdPartyNotices `
            -AssetsPath (Join-Path $root 'SessionDock/obj/project.assets.json') `
            -Destination (Join-Path $output 'licenses/DotNet-THIRD-PARTY-NOTICES.txt')
        if (-not (Test-Path -LiteralPath (Join-Path $output 'SessionDock.exe') -PathType Leaf)) {
            throw "Publish completed without the expected SessionDock.exe in $output."
        }
        foreach ($requiredComponent in @(
                'SessionDock.dll',
                'SessionDock.ExactWheel.dll',
                'SessionDock.HandleScope.dll',
                'SessionDock.ReleaseTrust.dll')) {
            if (-not (Test-Path -LiteralPath (Join-Path $output $requiredComponent) -PathType Leaf)) {
                throw "Transparent publish completed without required component: $requiredComponent"
            }
        }
        & (Join-Path $PSScriptRoot 'Verify-Publish.ps1') -Directory $output
    }
}
finally {
    Pop-Location
}
