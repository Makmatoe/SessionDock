# Security policy

## Supported versions

There is currently no supported production binary. Distribution is on hold as
of 2026-08-04: the immutable v3.0.0 assets are unsigned and use the retired
compressed single-file layout. A separately shared development build with that
layout received `Trojan:Win32/Wacatac.B!ml`; the reported bytes remain blocked
pending Microsoft's determination. Do not download, restore, allow, or run any
existing release asset. Development builds and older releases are unsupported.

Support resumes only when a later release from the canonical
[SessionDock repository](https://github.com/Makmatoe/SessionDock/releases)
passes every source-provenance, transparent-inventory, trusted Authenticode,
Defender-response, staging, and public re-download gate documented below.

The integrated HandleScope/ExactWheel/template source documented in this tree
is not a production release unless it appears in the canonical release feed and
passes every provenance gate. ExactWheel is currently release-blocked pending
explicit TinyClicks ownership/licensing and immutable provenance.

## Reporting a vulnerability

Do not open a public issue for a suspected vulnerability or include secrets,
tokens, cookies, private-server codes, personal data, or exploit details in an
issue or pull request.

Use GitHub's **Report a vulnerability** private-reporting feature on the
repository's Security page. Include:

- the affected version and Windows version;
- a concise description of the impact and security boundary crossed;
- reproducible steps or a minimal proof of concept;
- whether Roblox account, local-file, process, update, or code-execution data is
  involved; and
- any suggested mitigation.

If private vulnerability reporting is temporarily unavailable, open a minimal
public issue that asks the maintainer to establish a private contact channel.
Do not disclose the vulnerability in that issue.

No bug bounty, payment, or response deadline is promised. Good-faith reports
will be reviewed as capacity permits. Do not access other people's accounts or
data, degrade Roblox or GitHub services, run denial-of-service testing, or use
social engineering while researching SessionDock.

## Security boundaries

SessionDock is designed around these boundaries:

- Roblox credentials and cookies belong to isolated WebView2 profiles and are
  not application configuration data.
- Launch tickets are short-lived values used for process launch and must not be
  logged or persisted.
- Only trusted Roblox installation paths and Roblox-signed Player executables
  may be launched or closed.
- Application updates come only from this repository and require a valid
  descriptor signed by the release key pinned in the app, an exact package
  hash, bounded metadata, and an exact package-content allowlist.
- The optional generic launch hook requires a Windows-trusted HTTPS certificate
  for a numeric loopback address and a valid bearer token. Plain HTTP generic
  hooks are rejected.
- The integrated source carries the reviewed HandleScope engine as the explicit
  `SessionDock.HandleScope.dll` component in the same SessionDock package, but
  keeps it optional and disabled until the user enables it. The source snapshot,
  upstream tag/commit, allowlisted files, and hashes are
  pinned in `SessionDock.HandleScope/handlescope-upstream.json` and checked by repository,
  build, release-inventory, license, and SBOM gates.
- Included mode creates one non-elevated, current-user/current-session child
  owned by SessionDock. Bootstrap data travels through an inherited anonymous
  pipe. Its rotating 256-bit bearer token stays in parent/child memory and is
  never placed in a file, command line, environment variable, setting, log, or
  UI. The child binds only to ephemeral numeric IPv4 loopback, verifies its
  parent, rejects browser-originated requests, and exits when disabled or when
  the parent lifetime ends. It creates no service, scheduled task, autostart
  entry, or separately mutable executable.
- Normal SessionDock use never downloads, installs, starts through PowerShell,
  elevates, or separately updates HandleScope. Therefore script execution
  policy, UAC, a download-time antivirus verdict on a HandleScope ZIP, and a
  standalone autostart task are not part of the included trust boundary.
  SessionDock package verification covers the component assembly with the rest
  of the exact transparent self-contained inventory.
  PowerShell's **running scripts is disabled** message is an execution-policy
  block before script execution. A browser's **Virus scan failed** message says
  scanning did not complete; it is not a positive detection or a safety
  verdict. Neither should be bypassed by weakening device policy. A named
  Defender result such as `Trojan:Win32/Wacatac.B!ml` is different: leave the
  file quarantined and follow the
  [Defender detection response](docs/DEFENDER_DETECTION_RESPONSE.md). A positive
  detection blocks use and release until the exact bytes have been investigated
  and Microsoft has returned a clean determination; a matching hash alone does
  not make the file safe.
- ExactWheel is compiled as a SessionDock component, not installed as a
  separate TinyClicks toolbar/application. Client recording requires a verified
  foreground target and bounded capture. Default playback refuses unrelated
  held physical input and stops on physical intervention, target loss,
  dangerous lateness, invalid timing, timer/injection failure, or cancellation.
  Cleanup attempts to release only successfully injected held inputs and must
  report partial cleanup as failure.
- Client-relative macro scaling rejects any recorded pointer event outside the
  source client rectangle instead of clamping it. Monitor-normalized playback
  requires the same monitor count and rejects virtual-desktop gaps. These are
  fail-closed geometry checks, not image recognition or proof that the Roblox UI
  is unchanged.
- Versioned template/catalog storage is bounded, strict UTF-8, atomic, and
  reparse-safe. A valid backup may recover a corrupt primary. Syntactically
  valid stale account/macro references remain visible for repair, and macro
  payloads are never auto-deleted. Macro files can contain typed key events and
  must be treated as private local data.
- **Standalone HandleScope (advanced)** preserves the old separate-process
  compatibility boundary. SessionDock connects only after the user selects that
  source and the already running API passes strict discovery-file,
  reparse-point, owner, non-elevated token, current-session, PID, start-time,
  numeric-loopback, executable-identity, and authenticated metadata checks.
  SessionDock never downloads, installs, starts, stops, updates, downgrades,
  reconfigures, or uninstalls the standalone application.
- The legacy `%LOCALAPPDATA%\SessionDock\handlescope.json` opt-in remains
  compatible. Source selection is stored separately in strict
  `handlescope-runtime.json`; a fresh missing selection defaults to included,
  while a one-time 2.x migration preserves a verified enabled standalone or an
  old Keep installed/Exact choice. An invalid selection fails closed. Included
  mode does not use the standalone
  `%LOCALAPPDATA%\HandleScope\connection.json` document. Both sources require
  authenticated `/v1/metadata` and can select only SessionDock's compiled
  `/v1/handles/close` or `/v2/handles/close` adapter and the single fixed
  `roblox-singleton-event-v1` policy.
- Account/history settings under `%LOCALAPPDATA%\SessionDock` are private local
  data, not portable release content. This includes isolated profiles,
  templates, macro payloads, catalog backups, and onboarding state.
- Safe metadata transfer uses a small versioned allowlist and bounded strict
  JSON parser. Export shows the exact file first and excludes authentication,
  local account keys/paths, destinations, private-server material, JobIds, and
  integration data. Import requires a reviewed confirmation, matches only
  existing accounts by Roblox user ID, and commits as one rollback-protected
  settings mutation; a transfer file can never create a signed-in profile.
- The optional Windows link handler is disabled until the user explicitly
  enables its per-user `HKCU\Software\Classes` registration. SessionDock owns
  reserved ProgID/protocol keys and will neither overwrite nor remove a foreign
  registration. It does not become the default HTTPS or Roblox handler. Link
  input is bounded and allowlisted to official Roblox destinations, rejects
  authentication tickets, cookies, tokens, JobIds, duplicate/unknown
  parameters, and is validated both before and after bounded same-user,
  same-session IPC. Receipt activates a preview/account chooser and never
  auto-launches; a second confirmation is required. Private link codes received
  this way are not persisted.

Please report any path that bypasses these boundaries, including unsafe URI
handling, navigation outside official Roblox domains, profile-crossing session
data, untrusted process execution/termination, update verification bypasses,
secret leakage, or unsafe local-API behavior.

## Authentic releases

Use only assets attached to releases in
`https://github.com/Makmatoe/SessionDock`. A production release is expected to
include a signed release descriptor, Velopack package metadata, an SPDX SBOM,
complete dependency notices, checksums covering every other asset, and a GitHub
artifact attestation. The release verifier rejects unexpected package files and
requires exact byte equality for the application files carried by the NUPKG and
portable ZIP. Integrated HandleScope and ExactWheel code must be the reviewed
`SessionDock.HandleScope.dll` and `SessionDock.ExactWheel.dll` files in that
same package; an unexpected component executable, installer, script, source
directory, toolbar, or additional component payload is a release-verification
failure. The verifier also rejects reparse points and unexpected executables or
scripts, validates the pinned dependency/runtime manifest, and requires a valid
Microsoft signature on every expected runtime PE outside the exact six-file
SessionDock publisher-signing set. It must check both component
manifests, immutable provenance, reviewed licensing/notices, SBOM entries, macro
format, and every explicit release-block field. The current ExactWheel block
must be cleared through reviewed evidence before publication; this policy does
not claim that it is cleared.

Public releases require Authenticode on the exact first-party application PE
set and final Setup. The reviewer-gated workflow uses GitHub OIDC with an
externally provisioned Azure Artifact Signing Public Trust profile; no signing
private key, PFX, self-signed fallback, or certificate password belongs in this
repository. It signs application files before Velopack and Setup after packaging
with SHA-256 and an RFC 3161 SHA-256 timestamp. The release fails unless every
configured file reports `Valid`, the exact configured publisher subject,
code-signing EKU, a timestamping certificate, and trusted chains. The same
checks run after draft, approval, and public re-downloads. Missing signing
configuration, an unsigned asset, a publisher mismatch, any signature/chain
failure, or any positive malware detection blocks publication; there is no
bypass or unsigned public-release path. Checksums, the signed update descriptor,
SBOM, and attestations remain independent complementary controls.

Roblox executable verification requests whole-chain Windows revocation checking
with online retrieval and root exclusion only. Revoked, offline, unknown,
malformed, expired-without-valid-timestamp, untrusted, or incorrectly purposed
signatures fail closed. Successful results may be cached briefly only against a
canonical path, length, last-write timestamp, and SHA-256; launches and process
termination revalidate immediately.
