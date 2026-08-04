[CmdletBinding()]
param(
    [Parameter(Mandatory, ValueFromPipeline)]
    [ValidateNotNullOrEmpty()]
    [string[]] $Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$defenderStatus = Get-MpComputerStatus
if (-not $defenderStatus.AMServiceEnabled -or
    -not $defenderStatus.AntivirusEnabled) {
    throw 'Microsoft Defender Antivirus must be enabled for release scanning.'
}

$candidateExecutables = [Collections.Generic.List[string]]::new()
$standardExecutable = Join-Path $env:ProgramFiles `
    'Windows Defender\MpCmdRun.exe'
if (Test-Path -LiteralPath $standardExecutable -PathType Leaf) {
    $candidateExecutables.Add($standardExecutable)
}
$platformRoot = Join-Path $env:ProgramData `
    'Microsoft\Windows Defender\Platform'
if (Test-Path -LiteralPath $platformRoot -PathType Container) {
    foreach ($platformDirectory in @(Get-ChildItem `
            -LiteralPath $platformRoot `
            -Directory `
            -Force | Sort-Object Name -Descending)) {
        $platformExecutable = Join-Path $platformDirectory.FullName 'MpCmdRun.exe'
        if (Test-Path -LiteralPath $platformExecutable -PathType Leaf) {
            $candidateExecutables.Add($platformExecutable)
        }
    }
}
$scanner = @($candidateExecutables | Select-Object -Unique | Select-Object -First 1)
if ($scanner.Count -ne 1) {
    throw 'Microsoft Defender command-line scanner was not found.'
}

$scanTargets = @($Path | ForEach-Object {
        $fullPath = [IO.Path]::GetFullPath($_)
        if (-not (Test-Path -LiteralPath $fullPath)) {
            throw "Defender release-scan target was not found: $fullPath"
        }
        $item = Get-Item -LiteralPath $fullPath -Force
        if (-not [string]::IsNullOrWhiteSpace([string] $item.LinkType)) {
            throw "Defender release-scan target must not be a reparse point: $fullPath"
        }
        $fullPath
    } | Sort-Object -Unique)
if ($scanTargets.Count -ne $Path.Count) {
    throw 'Defender release-scan targets must be distinct.'
}

Write-Host (
    'Microsoft Defender engine {0}; antivirus signatures {1} ({2:O}).' -f `
    $defenderStatus.AMEngineVersion,
    $defenderStatus.AntivirusSignatureVersion,
    $defenderStatus.AntivirusSignatureLastUpdated)
foreach ($target in $scanTargets) {
    & $scanner[0] `
        -Scan `
        -ScanType 3 `
        -File $target `
        -DisableRemediation
    if ($LASTEXITCODE -ne 0) {
        throw "Microsoft Defender rejected or could not scan release target '$target' (exit code $LASTEXITCODE)."
    }
    Write-Host "Microsoft Defender found no threat in release target: $target"
}
