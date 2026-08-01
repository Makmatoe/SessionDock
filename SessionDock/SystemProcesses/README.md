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
bundle, or elevate HandleScope. Its explicit install action is pinned to the
immutable HandleScope v0.1.4 Windows x64 release. It uses `RemoteSigned` only
for the verified child process so Windows' default `Restricted` policy does not
reject the installer; saved policy and Group Policy remain unchanged.

1. Select **Integrations** in the SessionDock sidebar to open the HandleScope
   panel. Opening the panel and selecting **Refresh** inspect local files only.
2. Select **Install HandleScope v0.1.4** and review the confirmation. SessionDock
   downloads only the canonical package and checksum assets. It requires the
   exact published sizes and SHA-256 hashes, matching same-release checksum,
   safe bounded ZIP layout, and complete internal manifest before it invokes
   HandleScope's own `-VerifyOnly` check and standard-user installer. It supplies
   only the process-scoped `-ExecutionPolicy RemoteSigned`; it never supplies
   `Bypass`, `Unrestricted`, `-AllowDowngrade`, `-EnableSessionDock`, or any
   elevation option. The confirmation discloses that this may replace an older
   supported per-user installation.
3. The installer starts the API and enables HandleScope's limited per-user
   interactive-logon autostart. This does not enable SessionDock's integration.
   The panel's **Open official v0.1.4 setup guide** action remains available for
   release review or manual installation.
4. Refreshing the panel accepts only
   `%LOCALAPPDATA%\Programs\HandleScope\Api\HandleScope.Api.exe` from v0.1.4:
   exactly 50,275,061 bytes with SHA-256
   `9925d032819750809d66f5e6f267606cb1d6ff419acadffc15d7bdbcb1402e95`.
5. Select **Enable** to write the fixed, minimal per-user SessionDock opt-in.
   This does not install or start HandleScope and does not change its autostart.
6. Select **Test connection** to check only an already-running API's loopback
   health endpoint after the connection file and same-session process identity
   pass local checks. This test never enumerates or closes a handle.

The pinned download identities are:

- `HandleScope-0.1.4-win-x64.zip`: 100,841,616 bytes, SHA-256
  `b06bfe850b8334b6be86d9037ea43e7210845420e7473cf7c17d030277c06622`.
- `SHA256SUMS.txt`: 198 bytes, SHA-256
  `860bcd77e7cd83693a87b15a1f464908e6dbe43195b0ed0572684e009b1e6ccf`.

HandleScope is not Authenticode-signed, so this trust comes from the reviewed
immutable canonical release and pinned hashes rather than a certificate-backed
publisher identity. Downloads are staged under a random non-reparse temporary
directory and removed after completion or a pre-install failure. Once the
verified installer begins its atomic file replacement, SessionDock lets it
finish and keeps the integration window open instead of interrupting the swap.

The exact executable hash is only one part of the trust boundary. SessionDock
also checks the standard per-user path and reparse points, process path,
current Windows session, owner, non-elevated token, PID, discovery start time,
strict numeric loopback URL, rotating token, API version, and fixed policy. It
does not persist or display the token. A different HandleScope build fails
closed until a future immutable release and the cross-repository contract are
reviewed and the pin is deliberately updated.

The panel reports **Not installed**, **Installed - connection not tested**,
**Integration disabled**, **Ready**, **Update required**, or a configuration
warning. The disabled state is based only on the pinned local install and opt-in;
SessionDock does not inspect the process, discovery file, or API until the user
enables the integration and selects **Test connection**. An invalid or
nonminimal existing configuration is
preserved. Only after displaying that warning does the panel offer an explicit
**Repair integration** action, which replaces the SessionDock opt-in with the
fixed minimal policy. **Disable** prevents future SessionDock post-launch
operations but does not stop HandleScope.

Opening or refreshing the integration panel never changes the HandleScope
installation, process, autostart task, or SessionDock opt-in. Only the explicit
confirmed install action invokes HandleScope's installer. Any legacy public
verification metadata under `%LOCALAPPDATA%\SessionDock\HandleScopeAuthorization`
is left unchanged and ignored; it is not required for the pinned v0.1.4 API.

Developers working from a SessionDock source checkout may instead run
`./scripts/Enable-HandleScope.ps1` from the repository root. HandleScope's
installed `Enable-SessionDockIntegration.ps1` helper provides the matching
command-line route. Both helpers and the UI use the same per-user opt-in; the
source helper does not install or start HandleScope.

The complete required configuration can be only:

```json
{"enabled": true}
```

SessionDock supplies the fixed v1 Roblox selector internally. Existing full
configuration files remain supported, but any explicitly supplied selector
that differs from the fixed policy disables the integration rather than
broadening it.

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

Each operation reloads `%LOCALAPPDATA%\HandleScope\connection.json`. Only an
exact v1 discovery document for `http://127.0.0.1:<port>/` and a live,
same-session `HandleScope.Api` process at the exact expected executable path are
accepted. The process start time must also match the bounded discovery time so a
stale file or reused PID is rejected. The rotating token is used directly from
the HandleScope connection file and is not copied into SessionDock settings or
logs. If the file, API, token, policy, or selector is unavailable, the hook is
skipped.
