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
bundle, or elevate HandleScope. Regular users can install and opt in without
PowerShell:

1. Select **Integrations** in the SessionDock sidebar to open the HandleScope
   panel. Opening the panel and selecting Refresh inspect local files only.
2. Select **Install Latest HandleScope release** and review the confirmation.
   This is the only panel action that contacts the canonical
   `Makmatoe/HandleScope` GitHub repository. SessionDock requires a stable,
   immutable release with the exact versioned Windows x64 package and checksum
   assets. SessionDock verifies GitHub's asset digests, the matching checksum,
   safe bounded archive extraction, and every internal file before it runs the included standard-user
   installer with `StartNow` and limited per-user `EnableAutostart`. The scoped
   child PowerShell execution-policy argument does not change saved policy or
   override Group Policy. SessionDock never supplies integration, downgrade, or
   elevation switches.
3. Select **Enable** to write the fixed, minimal per-user opt-in.
4. The installer starts the API immediately and its limited task starts it at
   future Windows sign-ins. Select **Start API** only if a manual restart is
   later needed; SessionDock still never starts it merely because the app or
   integration panel opens.
5. Select **Test connection** to check only its loopback health endpoint after
   the connection file and same-session process identity pass local checks.
   This test never enumerates or closes a handle.

HandleScope is not Authenticode-signed and its current release has no separate
signed descriptor. The install therefore trusts the canonical immutable GitHub
release, its published SHA-256 digests, and its same-release checksum; these
checks do not prove a certificate-backed publisher. If a future release adds a
signed descriptor, SessionDock also requires its pinned HandleScope key. After
installation, SessionDock stores the verified manifest and a local GitHub
release receipt under its own data, hashes `HandleScope.Api.exe` against that
inventory before start or trust, checks the exact standard path and reparse points, and retains the
process-path, session, owner, non-elevated token, PID, start-time, and loopback
discovery checks. Replacing the installed executable makes the integration fail
closed unless the same-user receipt and manifest are also replaced.

Installation starts HandleScope immediately and enables its limited per-user
interactive-logon task, but it does not change SessionDock's opt-in. Updating
an already running API may stop it briefly while its files are atomically
replaced; the install then starts the updated API. A cancellation before the
installer starts removes the staged download. Once HandleScope's installer
begins its atomic replacement, SessionDock lets it finish rather than
deliberately interrupting the file swap. HandleScope refuses automatic
downgrades and preserves the prior installation if its staged replacement
fails.

The panel reports **Not installed**, **Installed - connection not tested**,
**API start requested**, **API running - connection not tested**,
**API running - integration disabled**, **Ready**, **Update required**, or a
configuration warning. A bounded start-pending state prevents rapid or
concurrent requests from spawning another API before discovery is published.
The interval uses a monotonic clock. After that window, SessionDock verifies
the process ID returned by the explicit start request before it will allow
another API process to be launched. It also checks for an already-running,
fully verified API at the exact install path when no valid discovery file is
available.
An invalid or nonminimal existing configuration is preserved. Only after
displaying that warning does the panel offer an explicit **Repair integration**
action, which replaces the SessionDock opt-in with the fixed minimal policy.
**Disable** prevents future SessionDock launch operations but does not stop the
HandleScope API.

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
