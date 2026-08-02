# Optional local launch integrations

The integrations in this directory run only after Roblox Player starts
successfully. They are optional, loopback-only, bounded by short timeouts, and
cannot change a successful launch into a failed launch.

## Install SessionDock first

[![Install Latest SessionDock release](../../docs/assets/install-latest-sessiondock.svg)](https://github.com/Makmatoe/SessionDock/releases/latest/download/SessionDock-win-x64-Setup.exe)

This button downloads the correct Windows x64 Setup asset from the latest
stable canonical release without requiring users to navigate the release asset
list. SessionDock does not currently have an Authenticode certificate, so
Windows may show Unknown publisher; verify the published checksum or GitHub
attestation before continuing. Open the downloaded Setup before configuring the
optional integrations described below.

SessionDock waits for each bounded integration attempt before marking that step
finished. The activity panel distinguishes a configured attempt from a skipped
step, but it never reports an optional integration as the reason Roblox itself
did or did not launch.

## Generic local API hook

`LocalApiLaunchHook` sends one JSON `POST` when
`SESSIONDOCK_LAUNCH_HOOK_URL` is an HTTPS URL for a numeric loopback address
and `SESSIONDOCK_LAUNCH_HOOK_BEARER_TOKEN` contains a valid bearer token.
Windows must trust the endpoint certificate, and the certificate must be valid
for the configured IP address. SessionDock does not bypass normal TLS
certificate validation. Redirects, cookies, and system proxies are disabled.

Configure it for the current Windows user, then restart SessionDock:

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

SessionDock captures one coherent current-variable pair at startup. It uses the
legacy pair only when neither current variable exists; a partial current pair
fails closed instead of borrowing its missing value from the legacy pair. The
four current and legacy variables are then removed from SessionDock's process
environment before WebView2 or launch-integration child processes start. The
captured configuration remains in effect until SessionDock restarts.

Plain HTTP, hostnames such as `localhost`, and missing or invalid bearer tokens
make the generic hook unconfigured, so SessionDock does not create or send its
launch payload. An untrusted or mismatched certificate fails the bounded HTTPS
attempt before the HTTP request is transmitted and cannot turn a successful
Roblox launch into a failed launch. Existing HTTP hook users must add a locally
trusted HTTPS certificate to their listener or clear both environment
variables.

The payload contains an event ID and time, the launched PID, place ID,
experience name, public/private classification, and local account identity.
It deliberately excludes destinations, server codes, cookies, passwords,
authentication tickets, and WebView2 data.

This boundary applies only to the generic hook. HandleScope uses its separate,
locally verified discovery-file, process-identity, and rotating-token flow and
continues to use the exact HTTP loopback endpoint described below.

## HandleScope connector

HandleScope support is disabled by default. SessionDock does not include,
bundle, or elevate HandleScope. Compatibility comes from the embedded
`Resources/handlescope-compatibility-bootstrap.json` and, after an explicit
network check, a signed catalog. Catalog entries authorize only immutable
release identities, SessionDock version ranges, capabilities, and the `v1`/`v2`
adapters already compiled into SessionDock. Remote data never defines code,
commands, local paths, or API endpoints.

1. Select **Integrations** in the SessionDock sidebar to open the HandleScope
   panel. Opening the panel and selecting **Refresh** inspect local files only.
2. Select **Check versions** to make the explicit catalog network request. It
   retrieves only
   `https://github.com/Makmatoe/SessionDock/releases/latest/download/sessiondock-handlescope-compatibility.json`,
   follows only bounded canonical GitHub release-asset redirects, and verifies
   the P-256 signature, product/repository/key identity, schema, validity window,
   monotonic sequence/generation floor, SessionDock range, compiled contracts,
   required capabilities, and release asset identities. Failure leaves the last
   trusted local catalog active. No software is installed or replaced.
3. Choose **Automatic**, **Keep installed**, or an exact reviewed HandleScope
   release. The API selector can use the compatible runtime's preferred/highest
   compiled contract automatically or require exact `v1` or `v2`. Saving either
   preference is local-only and performs no lifecycle action.
4. Select **Install** and review the separate confirmation for the selected
   version. SessionDock downloads only the canonical catalog-authorized package,
   checksum, and any cataloged immutable release manifest. It requires exact
   sizes and SHA-256 hashes, matching checksum contents, executable identities,
   safe bounded ZIP layout, and complete internal inventory before it invokes one
   locally compiled setup adapter. The exact signed
   `handlescope.setup.native.v1` capability requires release-manifest schema v2
   and fixed `api/HandleScope.Setup.exe` identity. SessionDock matches that
   identity to the locked extracted inventory and directly supplies only
   `verify`, then `install --start-now --enable-autostart`. Reviewed 0.1.4 and
   0.2.2 releases retain only their fixed PowerShell script with process-scoped
   `-ExecutionPolicy RemoteSigned`. Neither adapter supplies `Bypass`,
   `Unrestricted`, `-AllowDowngrade`, `-EnableSessionDock`, or an elevation
   option. A supported older installation may be replaced only after the
   disclosed confirmation; every downgrade is refused.
5. The installer may start the API and enable HandleScope's limited per-user
   interactive-logon autostart. This does not enable SessionDock's integration.
   The selected release's tag-pinned official guide remains available for
   release review or manual installation.
6. Refreshing accepts an API executable only when its expected non-reparse
   per-user path, size, and SHA-256 exactly match the selected trusted catalog
   entry.
7. Select **Enable** to write the fixed, minimal per-user SessionDock opt-in.
   This does not install or start HandleScope and does not change its autostart.
8. Select **Test connection** to check only an already-running API's loopback
   health endpoint after the connection file and same-session process identity
   pass local checks. This test never enumerates or closes a handle.

HandleScope is not Authenticode-signed, so this trust comes from the reviewed
immutable canonical releases and catalog-authorized hashes rather than a
certificate-backed publisher identity. The embedded bootstrap is trusted through
the signed SessionDock package. A refreshed remote catalog must be signed by the
SessionDock release key and cannot roll the last trusted sequence/generation
back. The verified public catalog cache is
`%LOCALAPPDATA%\SessionDock\HandleScopeCompatibility\sessiondock-handlescope-compatibility.json`.
Downloads are staged under a random non-reparse temporary directory and removed
after completion or a pre-install failure. Once the verified installer begins
its atomic file replacement, SessionDock lets it finish and keeps the integration
window open instead of interrupting the swap.

Catalog data never supplies the setup path or arguments. Releases 0.1.4 and
0.2.2 are the only reviewed legacy adapters. Every other installable release
must declare exactly `handlescope.setup.native.v1`; an unknown, missing, or
contradictory `handlescope.setup.*` capability fails before any asset is
downloaded or process is launched. The native setup executable, the archive,
every inventory file, and the catalog lease stay locked and are revalidated
before and after both child-process phases.

The exact executable hash is only one part of the trust boundary. SessionDock
also checks the standard per-user path and reparse points, process path,
current Windows session, owner, non-elevated token, PID, discovery start time,
strict numeric loopback URL, rotating token, exact legacy discovery schema,
authenticated metadata, catalog identity, negotiated API contract, and fixed
policy. It does not persist or display the token. A build that is absent,
revoked, outside the SessionDock range, or different from its selected catalog
identity fails closed.

The panel reports **Not installed**, **Installed - connection not tested**,
**Integration disabled**, **Ready**, **Update required**, or a configuration
warning. The disabled state is based only on the selected catalog-compatible
local install and opt-in; SessionDock does not inspect the process, discovery file, or API until
the user enables the integration and selects **Test
connection**. An invalid or nonminimal existing configuration is preserved.
Only after displaying that warning does the panel offer an explicit
**Repair integration** action, which replaces the SessionDock opt-in with the
fixed minimal policy. **Disable** prevents future SessionDock post-launch
operations but does not stop HandleScope.

Opening or refreshing the integration panel never changes the HandleScope
installation, process, autostart task, or SessionDock opt-in. Only the explicit
**Check versions** action refreshes remote catalog metadata, and only the
separately confirmed install action invokes HandleScope's installer. Saving a
version/API preference never installs or replaces software. Any legacy public
verification metadata under `%LOCALAPPDATA%\SessionDock\HandleScopeAuthorization`
is left unchanged and ignored.

Version/API preference is separate from the legacy opt-in and is stored at
`%LOCALAPPDATA%\SessionDock\handlescope-preferences.json`. The strict four-field
schema is:

```json
{
  "schemaVersion": 1,
  "versionMode": "automatic",
  "exactVersion": null,
  "apiContract": "automatic"
}
```

`versionMode` is `automatic`, `keep-installed`, or `exact`. Exact mode requires
one stable three-part `exactVersion`; the other modes require `null`.
`apiContract` is `automatic`, `v1`, or `v2`. An invalid preference is ignored
and does not alter `handlescope.json` or select an unapproved runtime.

Developers working from a SessionDock source checkout may instead run
`./scripts/Enable-HandleScope.ps1` from the repository root. HandleScope's
installed `Enable-SessionDockIntegration.ps1` helper provides the matching
command-line route. Both helpers and the UI use the same per-user opt-in; the
source helper does not install or start HandleScope.

The backwards-compatible opt-in remains
`%LOCALAPPDATA%\SessionDock\handlescope.json`. SessionDock does not add release
or API fields to this file. The complete enabled configuration can still be
only:

```json
{"enabled": true}
```

`{"enabled":false}` remains the minimal disabled form. SessionDock supplies the
fixed Roblox selector internally, independent of whether the negotiated
operation adapter is `v1` or `v2`. Existing full configuration files remain
supported without migration, but any explicitly supplied selector that differs
from the fixed policy disables the integration rather than broadening it.

Full compatibility example:

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

`{SESSION_ID}` is replaced with the Windows session ID of the exact Roblox PID
that was just launched. SessionDock first performs a dry run against that PID,
requires the canonical single-use plan ID returned by that dry run, and forwards
that ID only to the immediately corresponding execution request. It then closes
only the matching handle. Missing or malformed plan IDs stop the operation. If
`allProcesses` is enabled, SessionDock obtains a separate plan ID for one
independently dry-run-checked sweep after the launched PID succeeds.

Each operation reloads `%LOCALAPPDATA%\HandleScope\connection.json`. Its legacy
wire format remains exact: one object with exactly these five unique fields and
no additions:

```json
{
  "apiVersion": "v1",
  "baseUrl": "http://127.0.0.1:43123/",
  "token": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
  "processId": 1234,
  "startedAtUtc": "2026-08-02T12:34:56Z"
}
```

The token shown is illustrative. A real token is an exactly 43-character
base64url value generated by HandleScope. `baseUrl` must be strict numeric HTTP
loopback with a nondefault explicit port and `/` path. `apiVersion` remains
exactly `"v1"`; it identifies the discovery schema, not the operation endpoint.
The file's safe local path, process ID, and bounded UTC start time must match a
live, non-elevated, same-user, same-session `HandleScope.Api` process at the
catalog-authorized executable path. A stale file or reused PID is rejected. The
rotating token is used directly and is never copied into SessionDock settings
or logs.

After those local checks, SessionDock sends an authenticated `GET
/v1/metadata`. A nonlegacy response must be one strict seven-field schema:
`schemaVersion`, `productVersion`, `discoveryApiVersion`,
`supportedApiVersions`, `preferredApiVersion`, `policies`, and `capabilities`.
The product version, exact API/capability sets, discovery version, and sole
fixed Roblox policy must equal the selected signed-catalog runtime identity.
SessionDock intersects that data with its compiled adapters and the user's API
preference. Automatic mode uses the runtime's compatible preferred contract or
the highest compatible compiled contract; exact mode fails closed if the
requested contract is unavailable. The only resulting operation endpoints are
compiled `/v1/handles/close` and `/v2/handles/close`.

HandleScope v0.1.4 predates metadata. Only when the selected, executable-verified
runtime is the catalog-authorized v0.1.4 identity and authenticated
`/v1/metadata` returns HTTP 404 may SessionDock use the legacy compiled `v1`
adapter. A 404 from any other runtime, a malformed response, any identity or
capability disagreement, an unknown API version, or unavailable explicit API
preference fails closed. If the file, API, token, policy, selector, metadata, or
negotiation is unavailable, the hook is skipped.
