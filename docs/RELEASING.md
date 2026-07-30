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

## HandleScope compatibility pin

SessionDock does not bundle, download, install, update, uninstall, elevate, or
start HandleScope. It never invokes HandleScope PowerShell lifecycle scripts or
uses `-ExecutionPolicy Bypass`. The panel opens only the official installation
guide pinned to immutable tag `v0.1.3`; users perform installation, startup, and
optional autostart separately.

The supported runtime is HandleScope v0.1.3 from commit
`952c16ee800a936d6d6fb48d78f8fbfe2483cee0`. Its published
`HandleScope.Api.exe` is exactly 50,275,056 bytes with SHA-256
`ca273df4b3822e358658c43fd764c70661f9279b37d883d11a470cd363ad7852`.
SessionDock embeds that identity and rejects any other executable before its
path or process can be trusted. Existing path, reparse-point, standard-user,
current-session, PID, discovery-time, strict loopback, rotating-token, and
health-policy checks remain mandatory.

Do not change the pinned setup URL, version, size, or digest until a newer
immutable HandleScope release and its integration contract have been reviewed
together. A future change must retain the manual lifecycle boundary, update the
five localized resources and current release notes, and extend the focused
boundary/runtime tests.

## Prepare and validate

The SessionDock icon is embedded in `SessionDock.exe`, so installed shortcuts,
window chrome, the taskbar, and Alt+Tab use the reviewed application mark. Do
not pass Velopack 1.2's `--icon` option while released strict updaters remain
supported: that option adds a top-level `setup.ico` entry to the full package,
and those clients intentionally reject unexpected package entries. The
bootstrap Setup executable may retain Velopack's default icon until installer
branding can be introduced without changing the trusted package topology.

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

For every release, verify all five localization dictionaries have identical,
non-empty keys and matching composite-format placeholders. Exercise singular
and plural paths, switch live through every supported language, and inspect
runtime-generated status, validation, confirmation, file-picker, tooltip, and
automation text. The current version must include release notes for `en-US`,
`nl-NL`, `de-DE`, `fr-FR`, and `es-ES`; an older English-only note must be
visibly labeled as fallback. Run the isolated runtime smoke test after these
checks so stale overlays and live-switch regressions block the release.

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
