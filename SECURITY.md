# Security policy

## Supported versions

Only the latest production release published from the canonical
[SessionDock repository](https://github.com/Makmatoe/SessionDock/releases) is
supported. Development builds, portable test artifacts, and older releases may
not receive security fixes.

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
- HandleScope uses a separate verified loopback process, discovery-file, and
  rotating-token boundary. Its API is optional, explicitly enabled, and never
  bundled or elevated by SessionDock. The explicit install action is pinned to
  the immutable HandleScope v0.1.4 Windows x64 release. It requires the exact
  package and checksum sizes and SHA-256 hashes, a matching same-release
  checksum, safe bounded ZIP layout, and complete internal manifest before
  running the standard-user installer. SessionDock uses `RemoteSigned` only for
  that verified child PowerShell process so the Windows default `Restricted`
  policy does not reject it; it never uses `Bypass` or `Unrestricted`, changes
  saved policy, or overrides Group Policy. It supplies no downgrade switch,
  elevation request, or automatic integration opt-in. The installer starts the
  API and enables HandleScope's limited
  per-user autostart only after the user confirms the action, including notice
  that an older supported per-user installation may be replaced. Before
  inspecting or trusting the local API, SessionDock also requires the exact published
  v0.1.4 executable size and SHA-256 hash at the expected non-reparse per-user
  path, then retains the process-path, session, owner, non-elevated token, PID,
  discovery-time, strict loopback, rotating-token, and health-policy checks.
- Account/history settings under `%LOCALAPPDATA%\SessionDock` are private local
  data, not portable release content.
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
portable ZIP. SessionDock does not currently have an Authenticode certificate,
so Windows reports Unknown publisher for Setup. The signed update descriptor,
package hash, checksums, and GitHub attestation reduce substitution risk but do
not provide Windows publisher identity and do not make an unsigned executable
equivalent to an Authenticode-signed one.

Roblox executable verification requests whole-chain Windows revocation checking
with online retrieval and root exclusion only. Revoked, offline, unknown,
malformed, expired-without-valid-timestamp, untrusted, or incorrectly purposed
signatures fail closed. Successful results may be cached briefly only against a
canonical path, length, last-write timestamp, and SHA-256; launches and process
termination revalidate immediately.
