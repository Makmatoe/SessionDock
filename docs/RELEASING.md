# Maintainer release guide

SessionDock releases are tag-triggered, environment-approved,
descriptor-signed, checksummed, attested, re-downloaded, and separately approved
before publication. A GET-only Discord readiness gate must pass before staging;
after publication succeeds, the guarded workflow prepares an audit artifact and
asks Bota to post the release announcement automatically.
The Windows executables and Setup are currently unsigned because the project
does not have a paid Authenticode certificate. Windows may therefore show
**Unknown publisher** or a SmartScreen warning.

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

### Discord announcement environment

The automatic announcement uses a separate `release-announcement` environment
with no GitHub repository permissions. Configure these values only on that
environment:

| Kind | Name | Purpose |
| --- | --- | --- |
| Secret | `DISCORD_RELEASE_BOT_TOKEN` | Bota's private bot token |
| Variable | `DISCORD_RELEASE_BOT_ID` | Bota's pinned bot user/Application ID |
| Variable | `DISCORD_RELEASE_CHANNEL_ID` | Dedicated release channel |
| Variable | `DISCORD_RELEASE_ROLE_ID` | Mentionable SessionDock notification role |

Before tagging, the repository owner must run the audit below and treat every
failure as a release blocker. It verifies that `release-announcement` is restricted
to tags matching `v*`, has no reviewer gate, declares exactly the expected
environment-scoped secret and variable names, has no broader-scope fallbacks, and
leaves no legacy Discord values on `release`. GitHub's API does not expose secret
values, and this audit does not validate variable values. The GET-only release
preflight validates the effective IDs and Bota identity before any draft is staged.

GitHub does not reveal an existing secret value. When recreating the environment
or rotating Bota's credential, retrieve the token from its approved secure source
or rotate it rather than attempting to read or copy it from GitHub. Pin the bot
user/Application ID shown in the Discord Developer Portal, remove any legacy
Discord values from `release`, and rerun the audit. Do not intentionally configure
these names at repository or organization scope.

GitHub Actions expression lookup is an important provenance limitation: if an
environment-scoped secret or variable is absent, a same-named repository or
organization value can be selected instead. The workflow can validate the
effective IDs and credentials, but it cannot determine which scope supplied
them. A missing effective value fails before any Discord request; a missing
*environment-scoped* value is not by itself proof that the expression will be
empty. The repository-owner audit is therefore mandatory before enabling live
posting:

```powershell
./scripts/Configure-GitHubSecurity.ps1 -WhatIf
```

The audit reads the environment details, its deployment policies, and the
names of its environment secrets and variables. It requires exactly one
custom deployment policy (`tag` / `v*`), no required-reviewer rule, exactly
the environment-scoped `DISCORD_RELEASE_BOT_TOKEN` secret name, and exactly
the three environment-scoped ID variable names. It also checks repository-scoped
names, and, for an organization-owned repository, the organization names that
GitHub reports as shared with this repository. It also rejects legacy Discord
values on `release`. Any violation makes the audit exit nonzero; warnings are
release blockers. The audit never prints a secret or variable value.

GitHub's API does not expose the stored token, prove that a re-entered token
came from the approved source, or let the workflow cryptographically attest
value provenance. Organization owners must confirm the organization access
policy with an organization-admin account as well: a repository-admin token
may lack the organization visibility needed for a complete independent audit.
The `tag` / `v*` environment policy controls deployment eligibility; the
repository's separate `v*` tag ruleset supplies tag protection.

Use the protected-tag restriction without a required-reviewer gate on this
environment. The separate `release-publication` approval is the human release
gate; adding another approval here would turn the announcement into a manual
confirmation step instead of the required automatic post-publication action.

Bota needs View Channel, Read Message History, Send Messages, and Embed Links
in the configured channel, plus Attach Files when reviewed images are selected.
Keep the configured role mentionable. The preflight computes effective access
from Bota's guild roles and channel overwrites and fails if any required access
is missing or Administrator, Manage Server, Manage Messages, or Mention
Everyone is effective.

The official path has no form, preview confirmation button, or manual publish
step. Before signing or drafting, a GET-only preflight verifies the immutable
bundle, pinned bot identity, channel, role, effective permissions, and complete
bounded marker history; an exact announcement already present at that point is
treated as early disclosure. The sender repeats those checks after guarded
GitHub publication, uses the canonical
`SessionDock/ReleaseNotes/<version>.en-US.md`, optionally includes images
reviewed for that same version, and pings exactly the configured SessionDock
role. The optional interactive `/release` command is a separate community tool
and is never a source or publishing path for official releases.

The pre-send JSON/Markdown artifact is deterministic and auditable, but it does
not replace delivery. Reruns must find and verify an existing matching Discord
message from the protected, pinned Bota ID before reporting success; the token's
`/users/@me` response must match that exact bot identity. Ambiguous history or
delivery fails closed and leaves a token-free receipt for review. The sender
reserves that receipt path before any network request, reconciles unreadable or
malformed POST responses through message history, and honors Discord's full
`Retry-After` within its overall deadline. It never replaces a delivery error
with a receipt write error.

## Required update-descriptor key

The protected signing job consumes exactly one secret:

```text
UPDATE_SIGNING_PRIVATE_KEY_PKCS8_BASE64
```

Discord values must not remain on `release`; the fail-closed configuration audit
rejects the legacy Discord secret and variable names from that signing
environment.

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

## Bundled HandleScope provenance and compatibility catalog

SessionDock 3.0 includes the reviewed HandleScope 0.3.0 engine in
`SessionDock.exe`. The canonical `Makmatoe/HandleScope` repository remains the
upstream source and standalone release channel. `SessionDock.HandleScope/`
contains only the allowlisted Core/API source needed by SessionDock, plus
`handlescope-upstream.json`, which binds the exact upstream repository, version, immutable
tag, commit, synchronized paths, and SHA-256 hashes.

Before changing the included engine:

1. Prepare and review the change in the HandleScope repository.
2. Select an immutable upstream tag and confirm its exact commit.
3. Run `.\scripts\Sync-BundledHandleScope.ps1` to verify the current snapshot.
   To synchronize from a local checkout whose pinned tag resolves to the
   required commit, run
   `.\scripts\Sync-BundledHandleScope.ps1 -UpstreamPath C:\path\to\HandleScope -Sync`.
   The script performs no network operation. Do not hand-copy or directly edit
   synchronized files.
4. Review every changed source file and the complete regenerated
   `SessionDock.HandleScope/handlescope-upstream.json`.
5. Verify the pinned version/tag/commit, allowlisted inventory, hashes, API
   contracts, single Roblox policy, parent/pipe lifecycle, and MIT license.
6. Update the SessionDock displayed component version, both repositories'
   current integration/security/privacy documents, all five localization
   dictionaries and current release notes, notices, and SBOM inputs together.

The repository and release gates must reject an unknown/missing synchronized
file, hash mismatch, provenance mismatch, version mismatch, direct snapshot
edit, or missing license attribution. The final publish must contain HandleScope
only inside `SessionDock.exe`; a `HandleScope*.exe`, installer, PowerShell
script, component directory, or other publish sidecar is forbidden. The Setup,
portable ZIP, and NUPKG must carry byte-identical approved `SessionDock.exe`
content.

SessionDock 3.0 does not download, install, start, stop, update, downgrade,
reconfigure, or uninstall standalone HandleScope. The source selector exposes
**Included with SessionDock (recommended)** and **Standalone HandleScope
(advanced)**; the standalone version selector exposes Automatic, Keep installed,
and exact signed-catalog-reviewed compatible versions; the API selector exposes
Automatic/`v2`/`v1`. Version selection is authorization only and must perform no
download, install, update, downgrade, or lifecycle action. Included mode must
use an inherited anonymous pipe for bootstrap, keep the rotating token in
memory, bind only to numeric IPv4 loopback, and tie the child lifetime to its
SessionDock parent. Preserve backwards-compatible `handlescope.json` opt-ins
and fail closed on invalid source/version/API preferences. A retained 2.x exact
pin must remain visible and recoverable through Automatic or Keep installed.
The standalone-only **Refresh reviewed versions** action may retrieve only the
canonical signed compatibility catalog, must preserve the current selection,
and must reuse signature, expiry, compatibility, and rollback enforcement. It
must never retrieve a package or mutate either runtime's lifecycle. Opening the
panel remains local-only.

The signed `sessiondock-handlescope-compatibility.json` release asset remains
for older SessionDock clients and reviewed advanced-standalone runtime
identities. It is authorization data, not executable policy: retain its exact
product/repository/key identity, monotonic sequence, bounded validity, immutable
asset and executable hashes, API/capability allowlist, and rollback resistance.
It must never define a command, script, local path, argument, endpoint, setup
behavior, or capability not compiled into the relevant legacy client. The 3.0
included flow must not fetch or execute anything from this catalog.

When a catalog update is required for older clients, review the immutable
HandleScope release and its existing versioned integration contract, update the
sorted bootstrap, advance `sequence`, leave the bootstrap signature empty, and
preserve still-supported or explicitly revoked historical identities. The
reviewer-gated release job signs and verifies the canonical catalog with the
protected P-256 release key. Never restore SessionDock's removed in-app
HandleScope downloader/installer or use the catalog to mutate a standalone
installation.

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

Confirm `SessionDock.HandleScope/handlescope-upstream.json` pins HandleScope 0.3.0 to the
reviewed upstream tag and commit and that the synchronization check is clean.
Inspect the publish, NUPKG, portable ZIP, notices, and SBOM: the engine must be
inside `SessionDock.exe`, the root MIT license and HandleScope notice must be
present, and no separate HandleScope binary/script/component directory may
appear.

The direct-upgrade asset names do not change for 3.0:
`SessionDock-win-x64-Setup.exe`, `SessionDock-win-x64-Portable.zip`, and the
existing `SessionDockApp-<version>-win-x64-sessiondock-full.nupkg` convention
remain stable. Test an upgrade from the latest 2.x Setup edition and verify the
included engine is available without a second install, while an existing
standalone HandleScope installation and its autostart/lifecycle settings remain
byte-for-byte untouched.

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

### Optional reviewed Discord images

An announcement has no image unless the current version's reviewed image
directory contains `discord.json`. Never reuse an older version's artwork. Put
the selection beside that version's `manifest.json` at
`docs/images/sessiondock-vX.Y.Z/discord.json`:

```json
{
  "images": [
    "sessiondock-vX.Y.Z-social-wide.png"
  ],
  "product": "SessionDock",
  "schemaVersion": 1,
  "version": "X.Y.Z"
}
```

Select one to four PNG, JPEG, WebP, or GIF files already covered by that
version's reviewed manifest, with at most 8 MiB per file and 20 MiB total. The
generator verifies the selected bytes and copies them into the immutable
announcement bundle. After posting, the sender binds every displayed embed
image to its reviewed attachment URL and verifies the downloaded bytes again;
its reviewed alt text and non-spoiler attachment presentation metadata must
also match, and unexpected display-bearing message or embed fields fail closed.
Omitting `discord.json` intentionally produces a text-only announcement.

## Protected workflow order

After an annotated `vX.Y.Z` tag is pushed from the protected `main` tip, the
workflow enters one repository-wide, non-cancelling FIFO release queue. This
prevents queued tag runs from replacing each other and keeps versions from
publishing or announcing concurrently. It then:

1. validates release metadata, locked restore, NuGet audit, tests, production
   publish, and the separate smoke build;
2. enters `release-announcement` and performs a GET-only readiness check; a
   failure stops the run before any draft or public release exists;
3. enters the reviewer-gated `release` environment;
4. packages the verified but unsigned production application;
5. prepares the canonical update descriptor and signs its digest with the
   protected P-256 descriptor key;
6. verifies the descriptor, exact package hash and package/portable contents;
7. generates the SBOM and complete SHA-256 checksums;
8. creates a fresh draft, uploads, re-downloads, byte-compares, and attests all
   assets;
9. waits for `release-publication` approval, then re-downloads and verifies the
   exact inventory, checksums, attestations, release body, prerelease state,
   source tag, and commit; it preserves an attempt-specific guarded publication
   intent before making the release public;
10. re-enters `release-announcement`, publishes the deterministic audit artifact,
   and automatically asks Bota to find or create and then verify one Discord
   announcement from the immutable canonical inputs.

Never mutate an executable, package, descriptor, Setup, SBOM, or checksum after
the stage that binds it. Investigate and explicitly remove only a failed
unpublished draft before retrying. Never reuse a published tag or asset.

### Recovery after publication or announcement failure

Use **Re-run failed jobs** for the same workflow run. Do not start a full new
release run and do not edit the tag, release, announcement artifact, or assets.
If publication succeeded server-side but its response was lost, a later run
attempt re-downloads and re-verifies the already-public release before allowing
the automatic announcement job to continue. Recovery also requires an immutable
publication-intent artifact created by an earlier attempt after that attempt
finished verification and reached the guarded publication boundary. A bare
rerun count is never sufficient, so the workflow rejects an unexpectedly public
release when no matching earlier intent exists.

For an announcement failure, inspect the token-free receipt artifact. A status
of `ambiguous` means the message may exist; inspect the configured channel, then
re-run the failed announcement job. Bota's marker, enforced nonce, complete
history scan, exact message-ID binding, and read-back verification make that
rerun a verified no-op when the correct message already exists. A `reserved`
receipt means the attempt began but final receipt replacement failed; preserve
it as evidence and investigate the job log before rerunning. Never compensate
with a manual official post.

## User verification

Before tagging a release, confirm the in-app updater accepts the signed
descriptor and the manual checksum and GitHub attestation commands in
`docs/UPDATES.md` succeed. Tell users plainly that Windows will show Unknown
publisher and that checksums or attestations should be verified before they
continue through that warning. Bota sends the reviewed canonical notes only
after publication succeeds.
