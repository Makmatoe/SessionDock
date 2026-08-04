# Maintainer release guide

SessionDock uses a permanent unsigned, transparent Windows distribution
policy. The portable ZIP is the only beginner/manual application download; the
full NUPKG exists only for compatible installed-copy updates. A public release
has no setup executable and no Authenticode stage. The custom signed update
descriptor, hashes, SBOM, GitHub attestations, package verification, malware
scans, and human review are independent controls; none supplies Windows
publisher identity or overrides a named malware detection.

Local production publication is disabled. The protected GitHub workflow is the
only production staging and publication path.

> **Distribution hold — 2026-08-04:** the current latest release is a
> zero-asset security-hold record and no replacement public build is approved.
> The hold remains in every current user-facing guide until one reviewed release
> completes every gate below, passes separate laptop validation before
> publication, and explicitly lifts it.

## Protected GitHub environments

The workflow separates staging, human publication approval, and automatic
announcement delivery:

- `release` contains exactly the environment-scoped
  `UPDATE_SIGNING_PRIVATE_KEY_PKCS8_BASE64` secret and no variables. This key
  signs only the bounded update descriptor/catalog; it is not Windows code
  signing and supplies no publisher identity.
- `release-publication` protects the transition from the byte-verified draft
  to public release. The draft and its approval must refer to the same tag,
  commit, asset inventory, and hashes.
- `release-announcement` is automatic and has no required-reviewer rule. It has
  exactly one custom deployment policy: tag pattern `v*`. It contains exactly
  the environment-scoped `DISCORD_RELEASE_BOT_TOKEN` secret name and exactly
  the three environment-scoped ID variable names
  `DISCORD_RELEASE_BOT_ID`, `DISCORD_RELEASE_CHANNEL_ID`, and
  `DISCORD_RELEASE_ROLE_ID`.

Before tagging, the repository owner must run the audit:

```powershell
./scripts/Configure-GitHubSecurity.ps1 -Repository Makmatoe/SessionDock
```

Treat every failure as a release blocker. A violation must exit nonzero. No form
of manual upload, alternate workflow, or local packaging may
replace that result. The announcement job performs a GET-only preflight before
staging and sends only after the public asset checks pass. Re-run failed jobs;
do not hand-edit, replace, or duplicate an announcement.

GitHub Actions can fall back across environment, repository, and organization
scopes. If a same-named repository or organization value can be selected, the
workflow cannot determine which scope supplied it. The audit therefore rejects
same-named broader-scope release or Discord values. This repository is
personally owned. If it is ever transferred, an organization-admin account
must complete independent audit work before tagging.

## Release admission

Before creating a release tag:

1. Require a clean protected branch and annotated `vX.Y.Z` tag at the exact
   reviewed commit.
2. Restore with the pinned SDK and locked dependency graph. Run the complete
   repository, unit, formatting, security, accessibility, localization,
   documentation, runtime-smoke, and manual hardware gates.
3. Confirm HandleScope's pinned upstream inventory, compatibility catalog, API
   contracts, notices, and SBOM inputs.
4. Confirm ExactWheel provenance. ExactWheel provenance pins 14
   implementation/lock files at commit
   `a290cdb9fb5d0c5047103a9985016cb573ea954f`, the separately pinned current
   build definition, and the root MIT license. The verifier must reproduce
   every path, Git blob, byte count, SHA-256 value, canonical inventory hash,
   build-definition identity, and license identity. Any drift blocks release.
5. Complete the template/macro safety checklist on real Windows hardware:
   bounded recording, the global recording-stop keybind, explicit Play, every
   supported speed, continuous looping until Stop, focus/physical-input pause
   and recovery, injected-input cleanup, destination review, and 4K/1080p
   adaptation.
6. Update the application version and all current localized release notes
   together. The English release notes are the canonical GitHub/Discord source;
   a tag without valid current-version notes is blocked.
7. Review the current user, security, privacy, update, accessibility,
   localization, component-provenance, and announcement documentation as one
   release surface. Do not remove the distribution hold before the later draft
   and laptop gates actually pass.

Documentation, a passing local build, or a tag proposal is not release
approval.

## Canonical asset contract

The public release contains these current assets:

- `SessionDock-win-x64-Portable.zip` — the only beginner/manual application
  download;
- `SessionDockApp-<version>-win-x64-sessiondock-full.nupkg` and exact Velopack
  feed metadata — consumed only by compatible existing installed copies;
- the signed SessionDock update descriptor;
- the version-bound HandleScope compatibility catalog;
- `SHA256SUMS.txt` covering every other release asset;
- an SPDX SBOM; complete first- and third-party notices remain inside the
  verified application inventories;
- GitHub artifact attestations; and
- canonical release notes in the GitHub release body.

Optional reviewed Discord images belong to the separate immutable announcement
artifact. They are not GitHub release assets and must not be added to the exact
release-file inventory.

Do not publish a setup executable, bare application executable, component-only
archive, PowerShell installation script, standalone HandleScope/ExactWheel
payload, or alternative mirror. Bota's Discord announcement links to the
canonical portable ZIP on GitHub; it never attaches a binary.

The portable ZIP and full NUPKG must contain byte-identical application files.
That shared transparent self-contained inventory includes the app host,
application DLLs, pinned runtime DLLs, JSON metadata, licenses, and notices as
separate files. It must not use .NET single-file bundling, embedded-assembly
compression, or native self-extraction.

Exactly six application PEs are intentionally unsigned:

1. `SessionDock.exe`
2. `SessionDock.dll`
3. `SessionDock.HandleScope.dll`
4. `SessionDock.ExactWheel.dll`
5. `SessionDock.ReleaseTrust.dll`
6. `Velopack.dll`

The update-only full NUPKG additionally contains exactly two unsigned Velopack
1.2.0 package helpers: `SessionDock_ExecutionStub.exe` and `Squirrel.exe`.
Neither helper is portable ZIP content or a file users download or open
manually. Verification pins `Squirrel.exe` exactly and pins the generated
execution stub's Velopack vendor code sections and version; unreviewed helper
drift blocks release.

Every expected Microsoft runtime PE outside the six application PEs and two
recognized NUPKG-only helpers must retain a valid Microsoft signature. The
inventory verifier rejects any missing, duplicate, renamed, extra, invalidly
signed, path-like, linked, or reparse-point entry. It also verifies exact file
hashes, versions, dependency locks, notices, component provenance, and SBOM
coverage.

## Build and pre-stage verification

Use the SDK pinned in `global.json` and the runtime pinned in the project:

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

Inspect the publish and prove:

- the app host has no single-file overlay;
- the file and PE counts match the generated allowlist;
- the portable ZIP has only the exact six unsigned application PEs above;
- the NUPKG adds only the two recognized unsigned Velopack 1.2.0 helpers,
  with `Squirrel.exe` pinned exactly and the execution stub's vendor code
  sections and version pinned;
- the Microsoft runtime complement has valid Microsoft signatures;
- the portable ZIP and NUPKG carry byte-identical application bytes; and
- the descriptor, compatibility catalog, feed, checksums, SBOM, notices, and
  attestations bind the same tag, commit, version, filenames, sizes, and hashes.

Run Microsoft Defender against the transparent publish directory without
changing protection or allowing remediation:

```powershell
& "$env:ProgramFiles\Windows Defender\MpCmdRun.exe" `
    -Scan -ScanType 3 -File .\artifacts\release-validation `
    -DisableRemediation
```

This scan must report no threats. The commands above intentionally do not create
a portable ZIP: local production packaging is disabled, and the canonical ZIP
is created only by the protected staging workflow. Scan that exact downloaded
draft ZIP during the separate laptop gate below. The workflow does not replace
either manual Defender check; the publication-environment approver must inspect
the recorded results before approval.

Defender TrustCheck may report the app host as not known-good because this
distribution is unsigned; record that result but do not treat it as malware
clearance. Any named or heuristic malware finding blocks staging and follows
[Defender detection response](DEFENDER_DETECTION_RESPONSE.md). Never add an
exclusion, restore a detected file, disable cloud or real-time protection, or
strip Internet-zone metadata.

## Protected staging and publication order

The protected workflow must perform this order without mutable asset reuse:

1. Validate the tag, source commit, clean tree, locked dependencies, repository
   policy, tests, and component provenance.
2. Build the transparent self-contained publish once. Generate the portable
   ZIP and full NUPKG/feed from those exact bytes.
3. Generate the signed update descriptor and compatibility catalog, complete
   checksums, SPDX SBOM, notices, release notes, and GitHub attestations.
4. Verify every asset and package allowlist locally, including byte equality
   between the portable ZIP and NUPKG application inventories.
5. Create an immutable draft release and upload the complete asset set.
6. Re-download every draft asset into a new empty directory. Recompute every
   size and SHA-256 hash, rerun package/content verification, and compare every
   byte with the pre-upload candidate.
7. On a separate Windows x64 laptop, download the draft portable ZIP through
   GitHub, verify its hash, retain its Internet provenance, run a normal
   Defender scan, extract it into a new folder, and complete a standard-user
   launch/tutorial/HandleScope/macro smoke test. Record the ZIP hash, Defender
   versions/result, Windows version, source URL, and smoke-test result without
   publishing private device or account data.
   This laptop gate must pass before publication and before the
   `release-publication` environment is approved.
8. Require the independent publication reviewer to inspect the source commit,
   exact asset list, checksums, attestations, SBOM, no-remediation scan logs,
   laptop evidence, and draft re-download comparison.
9. Publish the already-verified draft without changing any asset or release
   text.
10. Re-download every asset from its anonymous public GitHub URL into another
    new empty directory. Recompute and compare all filenames and hashes. Exact
    byte equality binds this public copy to the already verified draft
    inventory. If anything differs, stop announcements, withdraw the release,
    create a zero-asset security-hold record, and begin incident response. A
    later named malware report triggers the same response even when hashes
    match.
11. Only after the public re-download gate passes may Bota post or reconcile
    the deterministic Discord announcement. The announcement must label and
    link the **Windows x64 portable ZIP** on GitHub and contain no binary.

Do not rebuild, rezip, rename, or edit an asset after its first accepted hash.
Do not replace an asset in place. A code or byte change requires a new version,
new tag, and complete gate.

## Update compatibility

Portable copies update manually by downloading and extracting the next portable
ZIP into a new folder. They do not invoke Velopack update installation.

Compatible existing installed copies may continue to use SessionDock's in-app
update control and the full NUPKG/feed. Verify upgrade from the latest supported
installed version, cancellation, rollback/failure behavior, preservation of
`%LOCALAPPDATA%\SessionDock`, and byte equality with the portable inventory.
The NUPKG is not a manual beginner download.

The custom update-descriptor signature authorizes only the exact feed metadata
and package identity accepted by SessionDock. It is not Authenticode, Windows
publisher identity, or permission to ignore antivirus.

## Defender incident gate

A named Defender detection is a release-blocking security event even when:

- the SHA-256 matches the release manifest;
- GitHub attestation succeeds;
- another device reports clean;
- the source is understood; or
- the only expected publisher status is **Unknown publisher**.

Preserve the exact bytes and logs without executing the file, submit the sample
to Microsoft as a software developer, compare clean-machine and
Internet-origin behavior, identify the responsible structure or code, and
produce a new version only after review. Do not publicly call a detection a
false positive before Microsoft returns its determination.

## Discord and recovery

The protected announcement path is documented in
[`discord-release-bot/README.md`](../discord-release-bot/README.md). Its artifact
schema requires `portableUrl` for
`SessionDock-win-x64-Portable.zip` and rejects legacy installer identities. A
same-tag marker from different immutable inputs is a conflict, not permission
to post again.

### Optional reviewed Discord images

An announcement has no image unless the current version's reviewed
`docs/images/sessiondock-vX.Y.Z/discord.json` selects it. The selection must sit
beside that version's `manifest.json`, refer only to files in that same reviewed
directory, and pass the bounded type, count, byte-size, and manifest checks in
the protected announcement generator. Never reuse artwork from an older
version.

The selected files travel in the immutable announcement input and are attached
only after the GitHub release is public and reverified. They are not copied into
the SessionDock portable ZIP, NUPKG, or GitHub release asset set.

If publication succeeds but public re-download, Defender, or Discord
verification fails, preserve receipts and evidence. Never replace the public
bytes in place and never work around Discord idempotency. Resolve the release
state first; then rerun only the bounded verification/delivery job for the same
immutable release when safe.
