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
bundle, download, install, update, uninstall, elevate, or start HandleScope. It
never invokes the HandleScope installer or lifecycle scripts and never uses an
execution-policy override. Installation, startup, and optional per-user
autostart remain separate explicit HandleScope actions.

1. Select **Integrations** in the SessionDock sidebar to open the HandleScope
   panel. Opening the panel and selecting **Refresh** inspect local files only.
2. Select **Open official v0.1.3 setup guide**. This opens the immutable
   [HandleScope v0.1.3 installation instructions](https://github.com/Makmatoe/HandleScope/blob/v0.1.3/docs/INSTALL.md)
   in the user's browser. Follow those instructions from a normal,
   non-administrator PowerShell window. Verify the release before installation,
   do not use `-ExecutionPolicy Bypass`, and start the API separately or choose
   HandleScope's optional limited per-user autostart.
3. Return to SessionDock and select **Refresh**. SessionDock accepts only
   `%LOCALAPPDATA%\Programs\HandleScope\Api\HandleScope.Api.exe` from v0.1.3:
   exactly 50,275,056 bytes with SHA-256
   `ca273df4b3822e358658c43fd764c70661f9279b37d883d11a470cd363ad7852`.
4. Select **Enable** to write the fixed, minimal per-user SessionDock opt-in.
   This does not install or start HandleScope and does not change its autostart.
5. Select **Test connection** to check only an already-running API's loopback
   health endpoint after the connection file and same-session process identity
   pass local checks. This test never enumerates or closes a handle.

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

Upgrading from SessionDock v2.7.0 does not replace the HandleScope installation,
stop its process, change its optional autostart task, or alter the existing
SessionDock opt-in. Any legacy public verification metadata under
`%LOCALAPPDATA%\SessionDock\HandleScopeAuthorization` is left unchanged and
ignored by v2.7.1; it is not required for a separately installed v0.1.3 API.

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
