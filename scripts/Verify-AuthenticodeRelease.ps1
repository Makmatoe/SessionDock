[CmdletBinding(DefaultParameterSetName = 'Signed')]
param(
    [Parameter(Mandatory)]
    [string] $Directory,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string[]] $ExpectedRelativePath,

    [Parameter(Mandatory, ParameterSetName = 'Signed')]
    [ValidateNotNullOrEmpty()]
    [string] $ExpectedPublisherSubject,

    [Parameter(Mandatory, ParameterSetName = 'Unsigned')]
    [switch] $RequireUnsigned
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$codeSigningEku = '1.3.6.1.5.5.7.3.3'
$timeStampingEku = '1.3.6.1.5.5.7.3.8'
$root = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Authenticode verification directory not found: $root"
}

function Assert-PeFile([string] $Path) {
    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    try {
        if ($stream.Length -lt 64) {
            throw "Expected PE file is too short: $Path"
        }
        $reader = [IO.BinaryReader]::new($stream)
        try {
            if ($reader.ReadUInt16() -ne 0x5A4D) {
                throw "Expected PE file has no MZ header: $Path"
            }
            $stream.Position = 0x3C
            [uint32] $peOffset = $reader.ReadUInt32()
            if ([uint64] $peOffset + 4 -gt [uint64] $stream.Length) {
                throw "Expected PE file has an invalid PE offset: $Path"
            }
            $stream.Position = $peOffset
            if ($reader.ReadUInt32() -ne 0x00004550) {
                throw "Expected PE file has no PE signature: $Path"
            }
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-CertificateEku(
    [Security.Cryptography.X509Certificates.X509Certificate2] $Certificate,
    [string] $RequiredOid,
    [string] $Purpose,
    [string] $Path) {
    $ekuExtensions = @($Certificate.Extensions | Where-Object {
            $_.Oid.Value -ceq '2.5.29.37'
        })
    if ($ekuExtensions.Count -ne 1) {
        throw "$Purpose certificate must contain exactly one EKU extension: $Path"
    }
    $ekuExtension = [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] `
        $ekuExtensions[0]
    $ekuOids = @($ekuExtension.EnhancedKeyUsages | ForEach-Object { $_.Value })
    if ($ekuOids -cnotcontains $RequiredOid) {
        throw "$Purpose certificate is missing required EKU $RequiredOid`: $Path"
    }
}

function Assert-TrustedCertificateChain(
    [Security.Cryptography.X509Certificates.X509Certificate2] $Certificate,
    [string] $Purpose,
    [string] $Path) {
    $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
    try {
        $chain.ChainPolicy.RevocationMode =
            [Security.Cryptography.X509Certificates.X509RevocationMode]::Online
        $chain.ChainPolicy.RevocationFlag =
            [Security.Cryptography.X509Certificates.X509RevocationFlag]::ExcludeRoot
        $chain.ChainPolicy.VerificationFlags =
            [Security.Cryptography.X509Certificates.X509VerificationFlags]::NoFlag
        $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(30)
        if (-not $chain.Build($Certificate)) {
            $statuses = @($chain.ChainStatus | ForEach-Object {
                    "$($_.Status):$($_.StatusInformation.Trim())"
                }) -join '; '
            throw "$Purpose certificate chain is not trusted for '$Path': $statuses"
        }
        if ($chain.ChainElements.Count -lt 2) {
            throw "$Purpose certificate chain is unexpectedly self-issued: $Path"
        }
    }
    finally {
        $chain.Dispose()
    }
}

$comparison = [StringComparer]::Ordinal
$seen = [Collections.Generic.HashSet[string]]::new($comparison)
$verified = [Collections.Generic.List[string]]::new()
foreach ($relativePath in $ExpectedRelativePath) {
    if ([string]::IsNullOrWhiteSpace($relativePath) -or
        [IO.Path]::IsPathRooted($relativePath) -or
        $relativePath.Contains(':') -or
        $relativePath -match '(^|[\\/])\.\.([\\/]|$)') {
        throw "Expected Authenticode path must be a safe relative path: '$relativePath'"
    }
    $normalizedRelativePath = $relativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)
    if (-not $seen.Add($normalizedRelativePath)) {
        throw "Expected Authenticode path is repeated: $relativePath"
    }
    $path = [IO.Path]::GetFullPath((Join-Path $root $normalizedRelativePath))
    $rootPrefix = $root + [IO.Path]::DirectorySeparatorChar
    if (-not $path.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Expected Authenticode file is missing or outside the verification root: $relativePath"
    }
    $file = Get-Item -LiteralPath $path -Force
    if (-not [string]::IsNullOrWhiteSpace([string] $file.LinkType)) {
        throw "Expected Authenticode file must not be a reparse point: $relativePath"
    }
    Assert-PeFile -Path $path

    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($RequireUnsigned) {
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned -or
            $null -ne $signature.SignerCertificate -or
            $null -ne $signature.TimeStamperCertificate) {
            throw "Release input must be unsigned before managed signing: $relativePath ($($signature.Status))"
        }
        $verified.Add($relativePath)
        continue
    }

    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate) {
        throw "Authenticode signature is not valid: $relativePath ($($signature.Status))"
    }
    if ($signature.SignerCertificate.Subject -cne $ExpectedPublisherSubject) {
        throw "Authenticode publisher subject mismatch for '$relativePath'. Expected '$ExpectedPublisherSubject'; found '$($signature.SignerCertificate.Subject)'."
    }
    Assert-CertificateEku `
        -Certificate $signature.SignerCertificate `
        -RequiredOid $codeSigningEku `
        -Purpose 'Code-signing' `
        -Path $relativePath
    Assert-TrustedCertificateChain `
        -Certificate $signature.SignerCertificate `
        -Purpose 'Code-signing' `
        -Path $relativePath

    if ($null -eq $signature.TimeStamperCertificate) {
        throw "Authenticode signature has no trusted timestamp: $relativePath"
    }
    Assert-CertificateEku `
        -Certificate $signature.TimeStamperCertificate `
        -RequiredOid $timeStampingEku `
        -Purpose 'Timestamp' `
        -Path $relativePath
    Assert-TrustedCertificateChain `
        -Certificate $signature.TimeStamperCertificate `
        -Purpose 'Timestamp' `
        -Path $relativePath

    $sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "Verified Authenticode: $relativePath ($sha256)"
    $verified.Add($relativePath)
}

if ($verified.Count -ne $ExpectedRelativePath.Count) {
    throw 'Authenticode verification did not cover the exact configured file list.'
}

if ($RequireUnsigned) {
    Write-Host "Verified exact unsigned PE input set ($($verified.Count) files)."
}
else {
    Write-Host "Verified exact Authenticode release set ($($verified.Count) files) for '$ExpectedPublisherSubject'."
}
