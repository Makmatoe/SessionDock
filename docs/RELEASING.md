# Maintainer release guide

SessionDock releases are tag-triggered, environment-approved,
descriptor-signed, checksummed, attested, re-downloaded, and separately approved
before publication. The Windows executables and Setup are currently unsigned
because the project does not have a paid Authenticode certificate. Windows may
therefore show **Unknown publisher** or a SmartScreen warning.

Unsigned does not mean unverified. The release workflow retains the controls
that can operate without a commercial certificate: a signed update descriptor,
exact package hashes, package-content allowlists, SBOM, checksums, GitHub
attestations, immutable draft re-download, and a separate publication approval.
None of those controls provides Windows publisher identity.

## Required repository controls

Keep `main` and `v*` protected, Actions defaults read-only, SHA pinning enabled,
dependency review required, vulnerability and secret scanning enabled, and both
`release` and `release-publication` protected by an explicitly chosen reviewer.
Audit them with:

```powershell
./scripts/Configure-GitHubSecurity.ps1 -WhatIf
```

The current one-maintainer repository may allow the named reviewer to approve
their own deployment. Do not enable prevent-self-review until another trusted
reviewer exists.

## Required update-descriptor key

The protected `release` environment uses exactly one repository secret:

```text
UPDATE_SIGNING_PRIVATE_KEY_PKCS8_BASE64
```

It contains the Base64-encoded PKCS#8 form of the P-256 private key whose public
half is pinned at `SessionDock/Resources/update-public-key.pem`. GitHub never
returns a stored secret value. Keep an offline recovery copy outside the
repository and outside ordinary build machines.

The workflow exposes this secret only to the reviewer-gated staging job and
uses it only to sign SHA-256 of the canonical update-descriptor payload. The
script validates P-256, emits a fixed-width P1363 signature, verifies the
completed descriptor with the public key, removes the environment variable,
and clears decoded key bytes. The private key is never written to a release
asset or committed file. The final publication job receives no secrets.

This is less isolated than an HSM-backed signer, but preserves the updater's
cryptographic package authorization without requiring a commercial Windows
code-signing certificate. Never use this key to sign executables or HandleScope
releases.

## HandleScope release verification

The one-click integration supports the assets currently published by
`Makmatoe/HandleScope`: a stable immutable GitHub release containing the exact
`HandleScope-X.Y.Z-win-x64.zip` and `SHA256SUMS.txt` files. SessionDock requires
GitHub's SHA-256 digest and exact size for both assets, requires the checksum to
agree with the package digest, safely extracts a bounded archive, and verifies
every file against `CONTENTS.sha256` before running the per-user installer.

After installation it stores the verified manifest and a local release receipt,
then rehashes `HandleScope.Api.exe` before starting or trusting it. HandleScope
is not Authenticode-signed, and this receipt is not an independent signature:
the trust boundary is the canonical immutable GitHub repository and same-release
hashes. A process running as the same Windows user could replace both the local
program and receipt, so do not describe this as certificate-backed publisher
verification.

The stronger descriptor path remains available. If a future HandleScope release
contains `handlescope-release.json`, SessionDock requires its signature to match
a distinct key in `SessionDock/Resources/handlescope-release-public-keys.json`;
it never reuses the SessionDock update key. Do not add the descriptor asset until
the matching production public key is embedded, because descriptor presence
intentionally makes signature verification mandatory.

## Prepare and validate

Use the pinned .NET SDK 10.0.302 and self-contained runtime 10.0.10. Before
tagging:

```powershell
dotnet --info
dotnet restore SessionDock.slnx --locked-mode
./scripts/Build.ps1 -Configuration Release -Runtime win-x64 `
    -OutputDirectory artifacts/release-validation -CI
./scripts/Build-RuntimeSmoke.ps1 `
    -OutputDirectory artifacts/release-runtime-smoke -TimeoutSeconds 30
./scripts/Test-DotNetSecurityPatch.ps1 -CheckOnline
./scripts/Verify-Release.ps1 -Tag vX.Y.Z
```

Complete and record the manual keyboard, Narrator/UIA, high-contrast, text
scaling, localization, DPI, and multi-monitor checks in
[`docs/ACCESSIBILITY.md`](ACCESSIBILITY.md). Automated tests cover the
underlying contracts but do not replace assistive-technology and physical
display verification.

The smoke feature is compiled only into a separate test artifact. Production
publish verification proves the privileged smoke switch is absent.

## Protected workflow order

After an annotated `vX.Y.Z` tag is pushed from the protected `main` tip, the
workflow:

1. validates release metadata, locked restore, NuGet audit, tests, production
   publish, and the separate smoke build;
2. enters the reviewer-gated `release` environment;
3. packages the verified but unsigned production application;
4. prepares the canonical update descriptor and signs its digest with the
   protected P-256 descriptor key;
5. verifies the descriptor, exact package hash and package/portable contents;
6. generates the SBOM and complete SHA-256 checksums;
7. creates a fresh draft, uploads, re-downloads, byte-compares, and attests all
   assets;
8. waits for `release-publication` approval, then re-downloads and verifies the
   exact inventory, checksums, attestations, source tag and commit before making
   the release public.

Never mutate an executable, package, descriptor, Setup, SBOM, or checksum after
the stage that binds it. Investigate and explicitly remove only a failed
unpublished draft before retrying. Never reuse a published tag or asset.

## User verification

Before announcing a release, confirm the in-app updater accepts the signed
descriptor and the manual checksum and GitHub attestation commands in
`docs/UPDATES.md` succeed. Tell users plainly that Windows will show Unknown
publisher and that checksums or attestations should be verified before they
continue through that warning.
