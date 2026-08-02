# Optional local launch integrations

This directory contains SessionDock's two optional post-launch integrations:

- `LocalApiLaunchHook` sends a bounded event to a user-managed HTTPS loopback
  listener.
- `HandleScopeLaunchHook` uses the separately installed HandleScope API through
  a stricter discovery, runtime, catalog, token, and policy boundary.

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

HandleScope integration is disabled by default. SessionDock does not bundle,
elevate, or silently run HandleScope. It uses a built-in compatibility
bootstrap offline and retrieves the signed public catalog only when the user
selects **Check versions**.

### Set up through SessionDock

Follow these actions in order:

1. Open **Integrations > HandleScope integration**. Opening the panel and
   selecting **Refresh** inspect local files only.
2. Select **Check versions**. SessionDock retrieves only
   `https://github.com/Makmatoe/SessionDock/releases/latest/download/sessiondock-handlescope-compatibility.json`
   through bounded canonical GitHub release-asset redirects.
3. Choose a release mode and an API contract, then save the preference. This
   changes only local selection data; it does not install, replace, start,
   stop, upgrade, or downgrade anything.
4. Select **Install selected HandleScope release**. Review the exact selected
   version and trust disclosure, then confirm.
5. Keep the window open until installation finishes. The installer can start
   HandleScope and enable its limited per-user interactive-logon autostart, but
   it does not enable SessionDock's integration.
6. Select **Refresh**. SessionDock accepts an installed API only when the
   standard per-user non-reparse path, size, and SHA-256 match the selected
   trusted catalog entry.
7. Select **Enable** to write the fixed SessionDock opt-in.
8. Select **Test connection**. The test checks only the verified running API's
   authenticated loopback health endpoint. It never enumerates or closes a
   handle.

If configuration is invalid or nonminimal, SessionDock preserves it and offers
an explicit **Repair and enable** or **Repair and disable** action. Repair
replaces only the SessionDock opt-in with the fixed minimal policy; it never
installs, starts, stops, or changes HandleScope autostart.

### Understand the selectors

| Release choice | Exact behavior |
| --- | --- |
| **Automatic** | Selects the compatible catalog recommendation. It never installs automatically. |
| **Keep installed** | Selects the locally verified installed release only when that exact release remains compatible. |
| **Exact version** | Pins one reviewed, compatible three-part release from the trusted catalog. |

| API choice | Exact behavior |
| --- | --- |
| **Automatic** | Uses the runtime's compatible preferred contract, otherwise the highest compatible compiled contract. |
| `v1` | Requires the selected and verified runtime to support the compiled legacy operation contract. |
| `v2` | Requires the selected and verified runtime to support the compiled v2 operation contract. |

An unavailable exact release or API contract fails closed. Every downgrade is
refused. A supported older installation can be replaced only after the separate
install confirmation discloses that replacement.

### What **Check versions** verifies

The catalog is authorization data, not executable policy. SessionDock verifies:

- the P-256 signature and exact product, repository, and key identity;
- schema, bounded size, generation time, expiry, and monotonic
  sequence/generation floor;
- the exact SessionDock version binding and each release's compatible
  SessionDock range;
- stable version/tag, supported or revoked state, and recommendation;
- only API contracts and required capabilities already compiled into
  SessionDock; and
- immutable package, checksum, optional release-manifest, API executable, and
  versioned integration-guide identities.

A failed network request, invalid signature, expired catalog, rollback, or
incompatible catalog leaves the last trusted local catalog active. Remote
metadata cannot add executable code, a command, a path, an argument, a setup
field, an endpoint, or an endpoint template.

### What installation verifies

Before any installer runs, SessionDock verifies the canonical release redirect,
catalog-authorized filenames, exact byte sizes and SHA-256 hashes, checksum
contents, any immutable release manifest, safe bounded ZIP layout, executable
identities, and every file in the internal inventory. Downloads are staged in a
random non-reparse temporary directory and removed after completion or a
pre-install failure.

There are exactly two locally compiled setup routes:

| Authorized release | Setup route |
| --- | --- |
| HandleScope 0.1.4 or 0.2.2 | Fixed `api/Install-HandleScopeApi.ps1` through Windows PowerShell with process-scoped `RemoteSigned` |
| Any other installable release | Must declare exactly `handlescope.setup.native.v1`; uses fixed `api/HandleScope.Setup.exe` |

The native route also requires a catalog-pinned external release-manifest
schema v2. Its one exact `setupExecutable` identity must be
`api/HandleScope.Setup.exe`, and its size and SHA-256 must match both the signed
catalog chain and the locked extracted `CONTENTS.sha256` inventory. SessionDock
directly invokes that fixed executable for only these two phases:

```text
verify
install --start-now --enable-autostart
```

The catalog cannot supply either path or arguments. An unknown, missing, or
contradictory `handlescope.setup.*` capability fails before an asset is
downloaded or a process is launched. The native Setup, legacy script, archive,
inventory files, and catalog lease remain locked and are revalidated before and
after both child-process phases.

Neither route uses `Bypass` or `Unrestricted`, changes saved PowerShell policy,
overrides Group Policy, elevates, passes `-AllowDowngrade`, passes
`-EnableSessionDock`, silently installs, silently updates, silently downgrades,
or enables the SessionDock opt-in. HandleScope is not Authenticode-signed; trust
comes from reviewed immutable canonical releases and catalog-authorized hashes,
not a certificate-backed publisher identity.

## HandleScope status and troubleshooting

| Status | Meaning and next action |
| --- | --- |
| **Not installed** | No selected catalog-compatible API executable was verified. Check versions, choose a release, then install it. |
| **Installed - connection not tested** | The selected runtime is present, but no API connection test has run. Enable the integration, start HandleScope if needed, then test. |
| **Integration disabled** | A supported install is present and the opt-in is off. SessionDock does not inspect the process, discovery file, or API until the user enables the integration and selects **Test connection**. |
| **Ready** | The enabled opt-in, installed runtime, discovery file, process identity, metadata, API negotiation, and health test passed. |
| **Update required** | The installed runtime is not compatible with the trusted catalog selection. Update SessionDock or choose/install a compatible non-downgrade release. |
| Configuration warning | The existing opt-in was preserved because it is invalid or differs from the fixed policy. Review it, then use an explicit Repair action if intended. |

Opening the panel, selecting **Refresh**, or saving a selector never changes the
HandleScope installation, process, autostart, or opt-in. **Disable** prevents
future SessionDock post-launch operations but does not stop HandleScope.

If an install reports that the verified installer was refused or could not
finish, refresh the panel before retrying because the atomic replacement may
already have committed files. Then use the selected tag-pinned official setup
guide or ask the device administrator whether local security policy blocks the
verified standard-user program. Do not weaken execution policy, Group Policy,
SmartScreen, or antivirus to force installation.

## Maintained files and schemas

| File or resource | Owner and purpose |
| --- | --- |
| `SessionDock/Resources/handlescope-compatibility-bootstrap.json` | Repository-owned unsigned offline bootstrap; its bytes are covered by the trusted SessionDock package. |
| `%LOCALAPPDATA%\SessionDock\HandleScopeCompatibility\sessiondock-handlescope-compatibility.json` | Last verified signed public compatibility catalog cache. |
| `%LOCALAPPDATA%\SessionDock\handlescope-preferences.json` | Separate release/API selection; never enables integration. |
| `%LOCALAPPDATA%\SessionDock\handlescope.json` | Backwards-compatible SessionDock opt-in and optional fixed policy. |
| `%LOCALAPPDATA%\HandleScope\connection.json` | HandleScope-owned rotating connection discovery document; reloaded for every operation. |

Legacy public verification metadata under
`%LOCALAPPDATA%\SessionDock\HandleScopeAuthorization` is preserved but ignored.

### Release and API preference

`handlescope-preferences.json` is strict UTF-8, at most 4 KiB, and contains
exactly these four case-sensitive fields:

```json
{
  "schemaVersion": 1,
  "versionMode": "automatic",
  "exactVersion": null,
  "apiContract": "automatic"
}
```

- `versionMode` is `automatic`, `keep-installed`, or `exact`.
- Exact mode requires one stable three-part `exactVersion`; the other modes
  require `null`.
- `apiContract` is `automatic`, `v1`, or `v2`.

An invalid preference is ignored. It cannot alter `handlescope.json` or select
an unapproved runtime.

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
clamped to 1-30 seconds; retry interval is clamped to 100-2,000 milliseconds
and cannot exceed the timeout.

To create the minimal enabled opt-in from a source checkout, run from the
repository root:

```powershell
.\scripts\Enable-HandleScope.ps1
```

The helper refuses to overwrite an existing configuration. After reviewing the
existing file, `-Force` explicitly replaces it with the minimal enabled form.
HandleScope's installed `Enable-SessionDockIntegration.ps1` helper writes the
same opt-in. Neither helper installs or starts HandleScope.

### HandleScope discovery document

`connection.json` remains one object with exactly five unique,
case-sensitive fields:

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

SessionDock requires the file to be a bounded regular non-reparse file at the
safe local path. Its process ID and bounded UTC start time must identify a live,
non-elevated, same-user, same-Windows-session `HandleScope.Api` process at the
catalog-authorized executable path:
`%LOCALAPPDATA%\Programs\HandleScope\Api\HandleScope.Api.exe`. A stale file or
reused PID is rejected. The token is used directly and is never copied into
SessionDock settings, logs, or UI.

## Runtime negotiation and launch sequence

After local discovery and process checks, SessionDock sends authenticated `GET
/v1/metadata`. A nonlegacy response must be exactly the strict seven-field
schema `schemaVersion`, `productVersion`, `discoveryApiVersion`,
`supportedApiVersions`, `preferredApiVersion`, `policies`, and `capabilities`.
The product version, discovery version, API/capability sets, and sole fixed
Roblox policy must match the selected signed-catalog runtime identity.

SessionDock intersects the authenticated metadata with its compiled adapters
and the user's API preference. The only possible operation endpoints are the
compiled `/v1/handles/close` and `/v2/handles/close` routes.

HandleScope 0.1.4 predates metadata. Only when the selected executable is the
exact catalog-authorized 0.1.4 identity and authenticated `/v1/metadata`
returns HTTP 404 may SessionDock use the legacy compiled `v1` adapter. A 404
from any other runtime, malformed metadata, identity/capability disagreement,
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

A missing or malformed plan ID stops the operation. If any file, API, token,
policy, selector, metadata, process identity, runtime identity, or protocol
check fails, the optional hook is skipped and the Roblox launch remains
successful.

## Maintainer compatibility-catalog checklist

Before adding, replacing, recommending, or revoking a HandleScope release:

1. Review the immutable HandleScope tag and commit, canonical public assets,
   checksums, optional release manifest, standard-user installer, versioned
   SessionDock contract, discovery schema, authenticated metadata, operation
   behavior, and capabilities together.
2. Edit
   `SessionDock/Resources/handlescope-compatibility-bootstrap.json`. Keep entries
   sorted, use stable three-part versions/tags, and bind exact asset names, byte
   sizes, SHA-256 values, SessionDock ranges, API contracts, capabilities, and
   tag-pinned contract URLs.
3. Advance the monotonic `sequence`; never reuse a sequence for different
   content. Set `sessionDockVersion` to the project version, set one compatible
   recommendation, refresh generation/expiry within the 400-day limit, and
   leave the bootstrap `signature` empty.
4. Preserve every still-required backwards-compatibility entry. Mark a known
   unsafe identity `revoked` instead of treating a higher version as trusted by
   default.
5. For 0.1.4 and 0.2.2, preserve the exact historical no-setup-capability
   identities and fixed `RemoteSigned` adapter. Every other installable release
   must declare exactly `handlescope.setup.native.v1` and bind the native Setup
   through external release-manifest schema v2; never add a path, command,
   argument, or setup executable to catalog schema v1.
6. Update all five localization dictionaries, the current five localized
   release notes, this guide, privacy/security documentation when affected, and
   focused catalog, installer, runtime, negotiation, and backwards-
   compatibility tests.
7. Run the complete repository gate from the root:

   ```powershell
   .\scripts\Build.ps1 -Configuration Release -Runtime win-x64 -CI
   ```

8. Follow [docs/RELEASING.md](../../docs/RELEASING.md). Only the protected
   release workflow may convert the unsigned embedded bootstrap into the signed
   public catalog, sign it with the protected P-256 key, verify the final asset,
   and publish it after approval.

Never introduce catalog-defined executable behavior, policy bypass, elevation,
silent installation, silent update, or downgrade behavior.
