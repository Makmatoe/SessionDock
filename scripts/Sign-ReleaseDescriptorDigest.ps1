[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateCount(1, 8)]
    [string[]] $DigestPath,

    [Parameter(Mandatory)]
    [ValidateCount(1, 8)]
    [string[]] $SignaturePath,

    [string] $PrivateKeyPkcs8Base64 =
        $env:UPDATE_SIGNING_PRIVATE_KEY_PKCS8_BASE64
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -cne 'Core' -or
    $PSVersionTable.PSVersion -lt [version] '7.4') {
    throw 'Release-metadata signing requires PowerShell 7.4 or later.'
}

$digestFullPaths = @($DigestPath | ForEach-Object {
        [IO.Path]::GetFullPath($_)
    })
$signatureFullPaths = @($SignaturePath | ForEach-Object {
        [IO.Path]::GetFullPath($_)
    })
if ($digestFullPaths.Count -ne $signatureFullPaths.Count) {
    throw 'Each release-metadata digest must have exactly one signature output.'
}
$digestSet = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
$signatureSet = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($path in $digestFullPaths) {
    if (-not $digestSet.Add($path) -or
        -not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw 'Release-metadata digest inputs must be unique existing files.'
    }
}
foreach ($path in $signatureFullPaths) {
    if (-not $signatureSet.Add($path) -or $digestSet.Contains($path)) {
        throw 'Release-metadata signature outputs must be unique and separate from digest inputs.'
    }
}
if ([string]::IsNullOrWhiteSpace($PrivateKeyPkcs8Base64) -or
    $PrivateKeyPkcs8Base64 -match '\s') {
    throw 'The protected release-metadata signing key is missing or malformed.'
}

$keyBytes = $null
$key = $null
try {
    $keyBytes = [Convert]::FromBase64String($PrivateKeyPkcs8Base64)
    $key = [Security.Cryptography.ECDsa]::Create()
    $bytesRead = 0
    $key.ImportPkcs8PrivateKey($keyBytes, [ref] $bytesRead)
    if ($bytesRead -ne $keyBytes.Length -or $key.KeySize -ne 256) {
        throw 'The protected release-metadata key is not one exact P-256 PKCS#8 key.'
    }

    for ($index = 0; $index -lt $digestFullPaths.Count; $index++) {
        $digestBytes = $null
        $signatureBytes = $null
        try {
            $digest = (Get-Content `
                    -LiteralPath $digestFullPaths[$index] `
                    -Raw).Trim()
            if ($digest -cnotmatch '^[A-Za-z0-9_-]{43}$') {
                throw 'A release-metadata payload digest is not canonical SHA-256 base64url.'
            }
            $digestBytes = [Convert]::FromBase64String(
                $digest.Replace('-', '+').Replace('_', '/') + '=')
            if ($digestBytes.Length -ne 32) {
                throw 'A release-metadata payload digest is not exactly one SHA-256 value.'
            }
            $canonicalDigest = [Convert]::ToBase64String($digestBytes)
            $canonicalDigest = $canonicalDigest.TrimEnd('=').Replace(
                '+', '-').Replace('/', '_')
            if ($canonicalDigest -cne $digest) {
                throw 'A release-metadata payload digest has noncanonical base64url padding bits.'
            }
            $signatureBytes = $key.SignHash(
                $digestBytes,
                [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
            if ($signatureBytes.Length -ne 64) {
                throw 'The release-metadata signer did not return one P-256 signature.'
            }
            $signature = [Convert]::ToBase64String($signatureBytes)
            $signature = $signature.TrimEnd('=').Replace('+', '-').Replace('/', '_')
            $parent = [IO.Path]::GetDirectoryName($signatureFullPaths[$index])
            if (-not [string]::IsNullOrWhiteSpace($parent)) {
                [IO.Directory]::CreateDirectory($parent) | Out-Null
            }
            Set-Content -LiteralPath $signatureFullPaths[$index] `
                -Value $signature -Encoding ascii -NoNewline
        }
        finally {
            if ($null -ne $signatureBytes) {
                [Security.Cryptography.CryptographicOperations]::ZeroMemory(
                    $signatureBytes)
            }
            if ($null -ne $digestBytes) {
                [Security.Cryptography.CryptographicOperations]::ZeroMemory(
                    $digestBytes)
            }
        }
    }
}
finally {
    if ($null -ne $key) { $key.Dispose() }
    if ($null -ne $keyBytes) {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($keyBytes)
    }
    Remove-Item Env:UPDATE_SIGNING_PRIVATE_KEY_PKCS8_BASE64 `
        -ErrorAction SilentlyContinue
}

Write-Host "Created $($digestFullPaths.Count) canonical external release-metadata signature(s)."
