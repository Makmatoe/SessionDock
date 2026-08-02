# Optional local launch integrations

This directory contains SessionDock's two optional post-launch integrations:

- `LocalApiLaunchHook` sends a bounded event to a user-managed HTTPS loopback
  listener.
- `HandleScopeLaunchHook` uses the HandleScope 0.3.0 engine included in
  SessionDock 3.0, or an independently managed standalone API when the user
  explicitly selects the advanced source.

Both hooks run only after Roblox Player starts successfully. They use short,
bounded timeouts and cannot turn a successful Roblox launch into a failed
launch. SessionDock waits for each configured attempt before marking the
integration step finished; the activity panel distinguishes **configured** from
**skipped**.

## Install SessionDock first

[![Install Latest SessionDock release](../../docs/assets/install-latest-sessiondock.svg)](https://github.com/Makmatoe/SessionDock/releases/latest/download/SessionDock-win-x64-Setup.exe)

1. Select **Install Latest SessionDock release** on a Windows x64 PC.
2. Confirm that the download came from the canonical
   `Makmatoe/SessionDock` GitHub release.
3. Verify the published checksum or GitHub attestation. SessionDock is not
   currently Authenticode-signed, so Windows may show **Unknown publisher**.
4. Open Setup as a standard Windows user, then configure only the integration
   you need.

Use the [root installation guide](../../README.md#install-and-launch) for the
complete install, update, and uninstall workflow.

## Generic local API hook

Use this hook only when you operate your own local HTTPS listener. HandleScope
does not use these environment variables.

### 1. Prepare the listener

The configured endpoint must meet every requirement:

- an `https://` URL;
- a numeric loopback address such as `127.0.0.1` or `::1`, not `localhost`;
- no URL user information, query, or fragment;
- a certificate Windows trusts and that is valid for the configured IP address;
- a bearer token from 1 to 4,096 characters, with no surrounding whitespace or
  control characters.

SessionDock does not bypass TLS certificate validation. Redirects, cookies,
credentials, preauthentication, and system proxies are disabled. Connection and
request work is bounded by a five-second timeout.

### 2. Save the current-user configuration

Run this in PowerShell, replace both example values, then restart SessionDock:

```powershell
[Environment]::SetEnvironmentVariable(
    "SESSIONDOCK_LAUNCH_HOOK_URL",
    "https://127.0.0.1:3443/roblox-launch",
    "User")
[Environment]::SetEnvironmentVariable(
    "SESSIONDOCK_LAUNCH_HOOK_BEARER_TOKEN",
    "replace-with-your-token",
    "User")
```

At startup, SessionDock captures one coherent pair. If either current variable
exists, it uses only the current pair; a partial current pair fails closed and
does not borrow a value from the legacy pair. The legacy
`ROBLOX_ONE_LAUNCH_HOOK_URL` and
`ROBLOX_ONE_LAUNCH_HOOK_BEARER_TOKEN` pair is considered only when neither
current variable exists.

After capture, all four current and legacy values are removed from
SessionDock's process environment before WebView2 or integration child
processes start. The captured pair stays active in memory until SessionDock
restarts.

### 3. Test the hook

1. Start the HTTPS listener.
2. Restart SessionDock so it captures the saved pair.
3. Launch Roblox through SessionDock.
4. Confirm the listener received one authenticated JSON `POST`.

The payload contains the event type, event ID and UTC time, launched process ID,
place ID, optional experience name, public/private classification, and the
selected local account's user ID, username, and optional label. It deliberately
excludes launch destinations, server codes, cookies, passwords, authentication
tickets, and WebView2 data.

Plain HTTP, a hostname, an incomplete pair, an invalid token, or a certificate
failure makes the hook unconfigured or causes the bounded attempt to fail.
SessionDock still treats the Roblox launch as successful.

### 4. Disable the hook

Close SessionDock, clear both current and legacy pairs, then start it again:

```powershell
$variableNames = @(
    "SESSIONDOCK_LAUNCH_HOOK_URL",
    "SESSIONDOCK_LAUNCH_HOOK_BEARER_TOKEN",
    "ROBLOX_ONE_LAUNCH_HOOK_URL",
    "ROBLOX_ONE_LAUNCH_HOOK_BEARER_TOKEN")

foreach ($variableName in $variableNames) {
    [Environment]::SetEnvironmentVariable($variableName, $null, "User")
}
```

## HandleScope connector

HandleScope integration is disabled by default. SessionDock 3.0 includes the
reviewed HandleScope 0.3.0 engine inside `SessionDock.exe`; normal users install
only SessionDock. The included engine is not a second application and has no
separate download, installer, PowerShell command, UAC prompt, scheduled task,
service, sign-in autostart, updater, or uninstall entry.

### Set up through SessionDock

1. Install or update SessionDock with `SessionDock-win-x64-Setup.exe`.
2. Open **Integrations > HandleScope integration**.
3. Keep **Included with SessionDock (recommended)** selected.
4. Choose **Automatic**, `v2`, or `v1` under **API version**.
5. Select **Enable**.
6. Wait for **Ready**. Enabling automatically verifies the parent/child identity,
   authenticated metadata, negotiated API, policy, and health endpoint. The
   readiness check does not enumerate or close a handle. Use **Retry** only when
   the bounded check reports a problem.

That is the complete normal setup. Do not download HandleScope, run an
`Install-HandleScopeApi.ps1` file, change PowerShell execution policy, approve
elevation, or create an autostart task for this mode. A device that previously
reported **running scripts is disabled** or **Virus scan failed** can use the
included engine because SessionDock does not invoke or download those standalone
installation files.

### Choose the source, standalone version, and API

The three selectors are independent:

| Runtime source | Exact behavior |
| --- | --- |
| **Included with SessionDock (recommended)** | Uses the reviewed HandleScope 0.3.0 engine compiled into this SessionDock release. Its version changes only with a verified SessionDock update. |
| **Standalone HandleScope (advanced)** | Connects to a compatible API that the user installed and started independently. SessionDock never downloads, installs, starts, stops, updates, downgrades, reconfigures, or uninstalls it. |

| Standalone runtime version | Exact behavior |
| --- | --- |
| **Automatic** | Accepts any currently installed runtime authorized by the signed catalog and compatible with this SessionDock version. |
| **Keep the installed version** | Keeps the 2.x preference explicit while accepting the installed reviewed compatible runtime; it performs no lifecycle or file action. |
| Exact reviewed version | Requires the already running standalone runtime to match that catalog-authorized version exactly. It does not fetch or install that version. |

**Refresh reviewed versions** is visible only for the standalone source. It
best-effort fetches the latest catalog from the canonical GitHub release URL,
then accepts it only after the existing signature, identity, expiry, compatibility,
and rollback checks pass. It preserves the current version/API selections and
never downloads or changes an executable. Opening the panel does not perform
this network request.

| API choice | Exact behavior |
| --- | --- |
| **Automatic** | Uses the runtime's compatible preferred contract, otherwise the highest compatible compiled contract. |
| `v2` | Requires the selected runtime to support SessionDock's compiled v2 operation contract. |
| `v1` | Requires the selected runtime to support the compiled legacy operation contract. |

The included component-version display remains fixed to the code shipped in the
current `SessionDock.exe`. An unavailable source, standalone version, or API
contract fails
closed and never falls back to a different source without changing the user's
saved selection.

### Included-engine lifecycle and token boundary

Enabling, retrying, or restarting included mode starts a non-elevated HandleScope child owned
by the current SessionDock process. SessionDock passes the bootstrap material
through an inherited anonymous pipe. The rotating 256-bit bearer token remains
only in SessionDock and child-process memory; it is never written to a
connection file, command line, environment variable, preference, log, or UI.

The child:

- accepts only the exact current-user, current-session parent that created it;
- binds Kestrel only to an ephemeral numeric IPv4 loopback address;
- rejects elevation, service accounts, session 0, browser-originated requests,
  unauthenticated requests, and every policy other than
  `roblox-singleton-event-v1`;
- exposes only the compiled metadata, health, dry-run/execute, and authenticated
  shutdown routes; and
- exits when SessionDock disables it or closes, including when the parent
  lifetime ends unexpectedly.

SessionDock owns only this child. It never adopts, terminates, or changes a
standalone HandleScope process. An application update replaces the embedded
engine atomically as part of `SessionDock.exe`; there is no independent
HandleScope update race or file lock.

### Standalone compatibility

Use **Standalone HandleScope (advanced)** only when you intentionally operate
the separately released product. Install, verify, start, update, and remove it
with the instructions in the canonical
[HandleScope repository](https://github.com/Makmatoe/HandleScope). Start its API
before enabling/retrying the advanced source or launching through SessionDock.

In this mode SessionDock reads the existing protected
`%LOCALAPPDATA%\HandleScope\connection.json`, validates its strict schema and
same-user/same-session process identity, authenticates `/v1/metadata`, and
negotiates only an API adapter already compiled into SessionDock. The discovery
token is used for that validated numeric-loopback process only and is never
copied into SessionDock settings, logs, diagnostics, exports, or UI.

Existing standalone HandleScope files, its scheduled-task preference, its
compatibility preference, and its lifecycle remain owned by HandleScope. Merely
selecting or deselecting the advanced source changes none of them.

## HandleScope status and troubleshooting

| Status | Meaning and next action |
| --- | --- |
| **Off** | Integration is disabled. Select a source, standalone version preference when relevant, and API version, then enable it if wanted. |
| **Starting** | SessionDock is starting and authenticating its included child. Wait for the bounded check to finish. |
| **Ready** | Source identity, token bootstrap/discovery, metadata, API negotiation, fixed policy, and health checks passed. |
| **Standalone runtime unavailable** | The advanced source is selected but no compatible running standalone API matched the saved choices. Select **Automatic**, **Keep the installed version**, or a matching reviewed exact version; otherwise repair that application independently or switch to included. |
| **HandleScope needs attention** | The chosen API is unavailable or the runtime failed a security or health check. Return to **Automatic**, select **Retry**, and verify the current SessionDock package. |
| **Settings need repair** | An existing opt-in or source preference is malformed or nonminimal. Review it, then use **Repair settings**. |

If included mode fails, close SessionDock, verify the current
`SessionDock-win-x64-Setup.exe` against the matching `SHA256SUMS.txt`, and run
that verified Setup as the same standard user. Do not fetch a HandleScope ZIP as
a repair and do not weaken PowerShell policy, Group Policy, SmartScreen,
antivirus, or application control. A managed device may still block the
unsigned SessionDock executable; ask the device administrator to review the
canonical checksum and GitHub attestation.

Opening the panel or changing a selector does not close a handle. **Disable**
stops future post-launch work and the included child. In standalone mode it
disconnects SessionDock only and leaves the external application running.

## Maintained files and schemas

| File or resource | Owner and purpose |
| --- | --- |
| `SessionDock.HandleScope/Upstream/` | Allowlisted HandleScope 0.3.0 source snapshot compiled into `SessionDock.exe`. |
| `SessionDock.HandleScope/handlescope-upstream.json` | Reviewed upstream repository, version, tag, commit, and synchronized-file hashes. |
| `scripts/Sync-BundledHandleScope.ps1` | Maintainer-only deterministic synchronization and provenance verifier; never runs on an end user's computer. |
| `%LOCALAPPDATA%\SessionDock\handlescope-runtime.json` | Strict source selection: included or standalone. It never contains a token or executable path. |
| `%LOCALAPPDATA%\SessionDock\handlescope-preferences.json` | Backwards-compatible standalone runtime-version and API preferences. It never installs software or contains a token. |
| `%LOCALAPPDATA%\SessionDock\handlescope.json` | Backwards-compatible integration opt-in and optional fixed policy. |
| `%LOCALAPPDATA%\HandleScope\connection.json` | Standalone HandleScope-owned discovery document. Included mode does not create or read it. |

`handlescope-runtime.json` is strict UTF-8, at most 1 KiB, and contains exactly:

```json
{
  "schemaVersion": 1,
  "runtimeSource": "bundled"
}
```

`runtimeSource` is `bundled` for **Included with SessionDock (recommended)** or
`standalone` for **Standalone HandleScope (advanced)**. On a fresh setup, a
missing file selects included. During a 2.x upgrade, Keep installed/Exact maps to
standalone; an enabled Automatic setup also stays standalone when its existing
API passes the migration probe. Otherwise the missing choice becomes included.
The result is then saved explicitly without changing any external process or
file. An invalid source file fails closed and is never used to construct a path
or command. SessionDock 3.0 keeps the standalone version and API choices from
`handlescope-preferences.json`. Automatic, Keep installed, and exact reviewed
version are compatibility requirements only; they cannot download, install,
start, replace, update, or downgrade software. A stale exact pin remains visible
in the panel and can be changed to Automatic, Keep installed, or another
catalog-reviewed version without touching the external application.

### Backwards-compatible SessionDock opt-in

The minimal enabled object written by SessionDock is:

```json
{"enabled":true}
```

The minimal disabled file is `{"enabled":false}`. Release and API fields must
not be added to this file.

Existing full configurations remain supported without migration:

```json
{
  "enabled": true,
  "processName": "RobloxPlayerBeta",
  "handleName": "\\Sessions\\{SESSION_ID}\\BaseNamedObjects\\ROBLOX_singletonEvent",
  "handleType": "Event",
  "access": "0x001F0003",
  "match": "exact",
  "closeAll": false,
  "allProcesses": true,
  "retryTimeoutSeconds": 10,
  "retryIntervalMilliseconds": 500
}
```

SessionDock accepts only this fixed Roblox policy. Any supplied selector that
differs from it disables the integration instead of broadening it. Timeout is
clamped to 1–30 seconds; retry interval is clamped to 100–2,000 milliseconds
and cannot exceed the timeout. Existing 2.x opt-ins continue to work, subject to
the one-time source migration above.

Use the SessionDock **Enable**, **Disable**, and repair controls to write this
opt-in. For advanced standalone use, HandleScope's own installed opt-in helper
may write the same file, but it does not install or start either application and
is not needed for included mode.

### Standalone discovery document

The advanced source continues to accept the standalone API's exact five-field
object:

```json
{
  "apiVersion": "v1",
  "baseUrl": "http://127.0.0.1:43123/",
  "token": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
  "processId": 1234,
  "startedAtUtc": "2026-08-02T12:34:56Z"
}
```

The token above is illustrative. A real token is an exact 43-character
base64url value generated and rotated by HandleScope. `baseUrl` must be numeric
HTTP loopback at `127.0.0.1`, with a nondefault explicit port, `/` path, and no
user information, query, or fragment. `apiVersion` remains exactly `"v1"`; it
identifies the discovery schema, not the negotiated operation endpoint.

SessionDock requires the file to be a bounded regular non-reparse file at its
known safe local path. Its process ID and bounded UTC start time must identify a
live, non-elevated, same-user, same-Windows-session `HandleScope.Api` process at
the standard standalone path. A stale file or reused PID is rejected. Included
mode does not use this document; its token and endpoint arrive through the
inherited pipe and remain in memory.

## Runtime negotiation and launch sequence

Both sources send authenticated `GET /v1/metadata`. The response must match the
strict seven-field schema `schemaVersion`, `productVersion`,
`discoveryApiVersion`, `supportedApiVersions`, `preferredApiVersion`,
`policies`, and `capabilities`. Its version, API/capability sets, and sole fixed
Roblox policy must match the selected source and SessionDock's compiled
contract.

SessionDock intersects the authenticated metadata with its compiled adapters
and the user's API preference. The only possible operation endpoints are the
compiled `/v1/handles/close` and `/v2/handles/close` routes.

A missing or malformed metadata response, identity/capability disagreement,
unknown API version, or unavailable explicit API preference fails closed.

For each successful Roblox launch:

1. SessionDock replaces `{SESSION_ID}` with the Windows session ID of the exact
   Roblox PID that just launched.
2. It sends a dry run for that PID using the fixed Roblox selector.
3. It requires the canonical single-use plan ID returned by that dry run.
4. It sends that ID only with the immediately corresponding execution request,
   which closes only the matching handle.
5. If `allProcesses` is enabled, SessionDock requests a separate plan ID for one
   independently dry-run-checked sweep after the launched PID succeeds.

A missing or malformed plan ID stops the operation. Any parent, pipe,
discovery, token, policy, selector, metadata, process, runtime, or protocol
failure skips the optional hook while leaving the Roblox launch successful.

## Maintainer synchronization policy

The canonical `Makmatoe/HandleScope` repository remains the upstream source and
standalone release channel. SessionDock carries a reviewed source snapshot so
users receive one application without making the two repositories drift:

1. Start from an immutable upstream HandleScope tag and its exact commit.
2. Review that tag's Core/API policy, security documents, license, and tests.
3. Verify the current snapshot with
   `.\scripts\Sync-BundledHandleScope.ps1`. To replace it from the pinned tag and
   commit in a local HandleScope checkout, run
   `.\scripts\Sync-BundledHandleScope.ps1 -UpstreamPath C:\path\to\HandleScope -Sync`.
   The script performs no network operation. Never hand-copy a partial source
   tree.
4. Review every change under `SessionDock.HandleScope/Upstream/` and the complete
   regenerated `SessionDock.HandleScope/handlescope-upstream.json`.
5. Confirm the provenance version, tag, commit, allowlisted paths, and hashes
   agree with the immutable upstream source. Do not edit synchronized files
   directly; land a fix upstream and synchronize it back.
6. Update the displayed included-engine version, both repositories' current
   integration/security/privacy documentation, the root MIT license reference,
   third-party notices, and SPDX SBOM inputs in the same SessionDock change.
7. Run the complete gate:

   ```powershell
   .\scripts\Build.ps1 -Configuration Release -Runtime win-x64 -CI
   ```

8. Verify that the release package still contains only the approved SessionDock
   inventory: HandleScope code must be inside `SessionDock.exe`, never an
   untracked executable or install script. Publish the standalone HandleScope
   release independently when its users need the same upstream change.

The sync script and CI must fail on an unknown/missing file, hash difference,
provenance mismatch, uncommitted generated output, version mismatch, or missing
license attribution. Never add remote-defined commands, arbitrary policy, elevation,
PowerShell-policy bypass, silent standalone installation, or standalone
lifecycle mutation to this boundary.
