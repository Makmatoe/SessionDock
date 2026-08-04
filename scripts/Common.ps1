Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RepositoryRoot {
    return (Split-Path -Parent $PSScriptRoot)
}

function Get-ApplicationProject {
    return (Join-Path (Get-RepositoryRoot) 'SessionDock/SessionDock.csproj')
}

function Get-ProjectVersion {
    $projectPath = Get-ApplicationProject
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Application project not found: $projectPath"
    }

    [xml] $project = Get-Content -LiteralPath $projectPath -Raw
    $versions = @($project.SelectNodes('/Project/PropertyGroup/Version') |
        ForEach-Object { $_.InnerText } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($versions.Count -ne 1) {
        throw 'The application project must declare exactly one non-empty <Version> value.'
    }

    $version = [string] $versions[0]
    if ($version -cnotmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
        throw "Project version '$version' must use stable major.minor.patch format."
    }

    return $version
}

function Assert-LegacyReadableReleaseNotes {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Release notes are required: $Path"
    }

    $notes = Get-Content -LiteralPath $Path -Raw
    if ([string]::IsNullOrWhiteSpace($notes) -or $notes.Length -gt 65536) {
        throw 'Release notes must contain between 1 and 65,536 characters.'
    }
    if ($notes -match '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]') {
        throw 'Release notes contain unsupported control characters.'
    }

    # Version 2.3.0 displays signed notes in a plain TextBox and can update
    # directly to any later release. Keep every future notes file readable in
    # that legacy dialog even though newer clients also apply local formatting.
    $plainTextCompatibilityPatterns = [ordered] @{
        'ATX heading markers' = '(?m)^[ \t]{0,3}#{1,6}(?:[ \t]+|$)'
        'indented continuation or code lines' = '(?m)^[ \t]+\S'
        'block quote markers' = '(?m)^[ \t]{0,3}>'
        'fenced code or horizontal rules' = '(?m)^[ \t]{0,3}(?:`{3,}|~{3,}|-{3,}|\*{3,}|_{3,})[ \t]*$'
        'inline code markers' = '`'
        'emphasis markers' = '(?:\*\*|\*[^*\r\n]+\*|__|(?<!\w)_[^_\r\n]+_(?!\w))'
        'Markdown links or images' = '!?\[[^\]\r\n]*\]\([^\)\r\n]+\)'
        'raw HTML' = '<[!/A-Za-z][^>\r\n]*>'
    }
    foreach ($entry in $plainTextCompatibilityPatterns.GetEnumerator()) {
        if ($notes -match $entry.Value) {
            throw "Release notes contain $($entry.Key), which are not readable in the 2.3.0 plain-text update dialog. Use plain headings and single-line '- ' bullets."
        }
    }
}

function Read-CanonicalReleaseNotes {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $Version,

        [string] $Label = 'Canonical release notes'
    )

    if ($Version -cnotmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
        throw "Release-note version '$Version' must use stable major.minor.patch format."
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "${Label} are required: $Path"
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (Test-PathEntryIsLink -Item $item) {
        throw "${Label} must be a regular non-link file."
    }

    [byte[]] $bytes = [IO.File]::ReadAllBytes($item.FullName)
    if ($bytes.Length -eq 0 -or $bytes.Length -gt 65536) {
        throw "${Label} must contain between 1 and 65,536 UTF-8 bytes."
    }
    if ($bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and
        $bytes[2] -eq 0xBF) {
        throw "${Label} must not contain a UTF-8 byte-order mark."
    }

    try {
        $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
        $text = $strictUtf8.GetString($bytes)
    }
    catch [Text.DecoderFallbackException] {
        throw "${Label} must contain valid UTF-8."
    }
    if ($text.Contains("`r")) {
        throw "${Label} must use LF line endings."
    }
    if ($text -match '[\x00-\x08\x0B-\x1F\x7F]') {
        throw "${Label} must not contain a prohibited control character."
    }
    if (-not $text.EndsWith("`n", [StringComparison]::Ordinal) -or
        $text.EndsWith("`n`n", [StringComparison]::Ordinal)) {
        throw "${Label} must end in exactly one LF."
    }

    $withoutTerminalLf = $text.Substring(0, $text.Length - 1)
    $lines = $withoutTerminalLf.Split(
        [char[]] @([char] 10),
        [StringSplitOptions]::None)
    if ($lines.Count -lt 3 -or
        $lines[0] -cne "SessionDock $Version" -or
        $lines[1] -cne '') {
        throw "${Label} must start with 'SessionDock $Version' and a blank line."
    }
    [string[]] $descriptionLines = @($lines | Select-Object -Skip 2)
    $description = [string]::Join("`n", $descriptionLines)
    if ([string]::IsNullOrWhiteSpace($description)) {
        throw "${Label} must contain a release-note body."
    }

    return [pscustomobject] @{
        Bytes = $bytes.Length
        Description = $description
        Text = $text
    }
}

function Assert-DiscordCompatibleReleaseNotes {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $Version
    )

    $notes = Read-CanonicalReleaseNotes `
        -Path $Path `
        -Version $Version `
        -Label 'Canonical English release notes'
    $description = [string] $notes.Description
    if ($description.Length -gt 4096) {
        throw "Canonical English release notes exceed Discord's 4,096-character description limit."
    }
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)]
        [string] $Command,

        [Parameter(ValueFromRemainingArguments)]
        [string[]] $Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $Command $($Arguments -join ' ')"
    }
}

function Test-PathEntryIsLink {
    param(
        [Parameter(Mandatory)]
        [IO.FileSystemInfo] $Item
    )

    foreach ($propertyName in @('LinkType', 'LinkTarget', 'Target')) {
        $property = $Item.PSObject.Properties[$propertyName]
        if ($null -ne $property -and
            -not [string]::IsNullOrEmpty([string] $property.Value)) {
            return $true
        }
    }

    return $false
}

function Assert-SafeOutputDirectory {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $root = [IO.Path]::GetFullPath((Get-RepositoryRoot)).TrimEnd('\', '/')
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $artifactsRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts')).TrimEnd('\', '/')
    $artifactsPrefix = "$artifactsRoot$([IO.Path]::DirectorySeparatorChar)"
    if (-not $fullPath.StartsWith(
            $artifactsPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Output directory must be a child directory of $artifactsRoot. Received: $fullPath"
    }
    $relativePath = $fullPath.Substring($artifactsPrefix.Length)

    $current = $artifactsRoot
    $separators = [char[]] @(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    foreach ($component in $relativePath.Split(
            $separators,
            [StringSplitOptions]::RemoveEmptyEntries)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (Test-PathEntryIsLink $item) {
                throw "Output directory crosses a symbolic link or junction: $($item.FullName)"
            }
        }
        $current = Join-Path $current $component
    }

    if (Test-Path -LiteralPath $current) {
        $item = Get-Item -LiteralPath $current -Force
        if (Test-PathEntryIsLink $item) {
            throw "Output directory is a symbolic link or junction: $($item.FullName)"
        }
    }

    return $fullPath
}

function Remove-SafeOutputDirectory {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $fullPath = Assert-SafeOutputDirectory $Path
    if (-not (Test-Path -LiteralPath $fullPath)) {
        return
    }

    $item = Get-Item -LiteralPath $fullPath -Force
    if (-not $item.PSIsContainer) {
        throw "Output path is not a directory: $fullPath"
    }

    $linkedEntry = Get-ChildItem -LiteralPath $fullPath -Force -Recurse |
        Where-Object { Test-PathEntryIsLink $_ } |
        Select-Object -First 1
    if ($null -ne $linkedEntry) {
        throw "Refusing to recursively remove an output tree containing a symbolic link or junction: $($linkedEntry.FullName)"
    }

    Remove-Item -LiteralPath $fullPath -Recurse -Force
}
