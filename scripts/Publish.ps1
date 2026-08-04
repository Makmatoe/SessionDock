[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v\d+\.\d+\.\d+$')]
    [string] $Tag,

    [string] $OutputDirectory = 'artifacts/release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

throw @"
Local production release packaging is intentionally disabled. SessionDock
releases require the protected GitHub release environment, the protected P-256
update-descriptor key, complete checksums, attestations, and separate approval.
Public distribution is intentionally an unsigned transparent portable ZIP; no
installer executable is published. Local packaging is not a release fallback.
Use scripts/Verify-Release.ps1 for a non-publishing local policy check. The
tag-triggered .github/workflows/release.yml workflow is the only production
packaging and publication path.
"@
