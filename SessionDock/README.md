# SessionDock desktop project

This directory contains the Windows WPF application for SessionDock 3.0.
Start with the [root README](../README.md) for the user-facing feature,
security, privacy, and release overview.

## Install the production app

[![Install Latest SessionDock release](../docs/assets/install-latest-sessiondock.svg)](https://github.com/Makmatoe/SessionDock/releases/latest/download/SessionDock-win-x64-Setup.exe)

1. Select **Install Latest SessionDock release** on a Windows x64 PC.
2. Confirm the download is
   `github.com/Makmatoe/SessionDock/.../SessionDock-win-x64-Setup.exe`.
3. Verify the Setup checksum using the
   [regular-user instructions](../docs/UPDATES.md#verify-a-manual-installer-download).
4. Open Setup as a standard Windows user, not as administrator.
5. Do not install HandleScope separately for normal use. SessionDock 3.0 already
   contains the reviewed HandleScope 0.3.0 engine.
6. Start SessionDock and add each Roblox account through its own official
   Roblox sign-in page.

The Setup edition is the recommended, updateable production build. Windows may
show **Unknown publisher** because the project does not currently have an
Authenticode certificate. Checksums, GitHub attestations, and the signed update
descriptor verify release integrity but do not create Windows publisher
identity.

The portable ZIP does not self-update. Source, Debug, and raw `dotnet publish`
outputs are development builds and cannot use the production update path.

## Update or uninstall

To update an installed production build:

1. Select the update button at the top right of SessionDock.
2. Review the verified version and signed release notes.
3. Confirm the install. SessionDock verifies the authorized full package before
   it exits and asks Velopack to replace and reopen the app.

To uninstall:

1. Disable **Open with SessionDock** first if you want its owned per-user link
   registration removed.
2. Disable HandleScope integration if you do not want future post-launch
   operations. The included child stops with SessionDock. An independently
   installed standalone HandleScope is never removed or changed.
3. Remove any account inside SessionDock first if you want that account's local
   WebView2 profile deleted.
4. Close SessionDock, then use **Windows Settings > Apps > Installed apps >
   SessionDock > Uninstall**.

Application removal does not imply removal of `%LOCALAPPDATA%\SessionDock`.
Keep that directory for a later reinstall, or remove it only when you
intentionally want to erase every SessionDock setting and profile for the
current Windows user. Follow the root
[uninstall guide](../README.md#uninstall-safely) and the special
[Roblox One/2.3.0 migration guide](../docs/UPDATES.md#moving-from-roblox-one-or-sessiondock-230-and-earlier)
before deleting any legacy data.

## Run from source

Requirements:

- Windows x64 in a normal, non-elevated interactive user session.
- The exact SDK in [`global.json`](../global.json), currently .NET SDK 10.0.302.
- Restored packages from the locked dependency graph.

From the repository root:

```powershell
dotnet --info
dotnet restore .\SessionDock.slnx --locked-mode
dotnet run --project .\SessionDock\SessionDock.csproj
```

`dotnet run` is useful for interactive development. It does not create an
installed, trusted, or self-updating production copy.

## Run the complete validation gate

From the repository root:

```powershell
.\scripts\Build.ps1 -Configuration Release -Runtime win-x64 -CI
```

That command:

1. validates repository and release policy;
2. restores every project in locked mode;
3. audits NuGet dependencies in CI mode;
4. builds the desktop app, tests, and release signer with warnings treated as
   errors;
5. runs every test project;
6. verifies the synchronized HandleScope 0.3.0 source and provenance;
7. creates a self-contained Windows x64 publish with that engine embedded in
   `SessionDock.exe`; and
8. verifies the publish inventory.

The default output is `artifacts/publish`. Use `-SkipPublish` only when a
publish check is intentionally unnecessary, or use `-OutputDirectory` with a
path under the repository's safe artifact boundary.

Production packaging cannot be performed locally. `scripts/Publish.ps1`
intentionally stops with an explanation. Maintainers validate a release with
`scripts/Verify-Release.ps1`, then use the protected, tag-triggered GitHub
workflow described in [docs/RELEASING.md](../docs/RELEASING.md).

## Local data and process boundary

SessionDock runs only as a non-elevated interactive Windows user and is
single-instance within that Windows login session. It stores user data under
`%LOCALAPPDATA%\SessionDock`, separate from the Velopack application directory.

The local-data tree can contain:

- account-slot metadata and one isolated WebView2 profile per account;
- account labels, colors, order, and optional groups;
- favorites, recent launches, and batch presets;
- theme and language preferences plus user-imported sounds; and
- local integration preferences.

Batch presets contain selected account keys and delay only; they do not copy
destinations or server identifiers. No account data, cookies, passwords,
tokens, or private-server codes are compiled into the application.

The safe metadata export contains only reviewed account appearance/order data,
Roblox user IDs, and pinned public favorite names/IDs. It excludes local account
keys, usernames, sign-ins, destinations, timestamps, private-server details,
JobIds, browser data, settings internals, and integrations. Import validates the
complete bounded schema, previews applied and skipped changes, requires
confirmation, and merges only into accounts already present on the computer.

Display-language values are `system`, `en-US`, `nl-NL`, `de-DE`, `fr-FR`, and
`es-ES`. Switching is live for resources, runtime messages, dialogs,
diagnostics, dates, and accessibility text. `system` follows a supported
Windows culture and otherwise uses English.

## Code map

- `Program.cs` initializes Velopack lifecycle handling, admits only the expected
  standard-user security context, and starts the WPF app.
- `Services/DestinationParser.cs` validates supported Roblox destinations.
- `Services/JoinUserDestination.cs` keeps user IDs distinct from place IDs.
- `Services/AutoJoinWatchContext.cs`, `AutoJoinLaunchGate.cs`, and
  `JoinUserWatchPolicy.cs` pin watch context, enforce one-shot handoff, and bound
  backoff and four-hour expiry.
- `Services/RobloxWebSessionService.cs` owns isolated browser sessions.
- `Services/RobloxClientService.cs` discovers, verifies, launches, and closes
  Roblox Player processes.
- `Services/SupportDiagnosticsService.cs` creates the bounded allowlisted text
  shown by **About and diagnostics** and used unchanged by Copy and Export.
- `Services/MetadataTransferService.cs` validates the safe-transfer schema,
  creates the exact preview, and applies the allowed merge.
- `Services/SessionDockUpdateService.cs` coordinates the manual Velopack updater
  and requires a release descriptor authorized by the embedded public key.
- `../SessionDock.HandleScope/` contains the reviewed HandleScope engine source
  compiled into `SessionDock.exe`; `handlescope-upstream.json` pins its upstream
  version, tag, commit, and synchronized inventory.
- `SystemProcesses/` contains the optional, bounded post-launch integrations.
- `MainWindow.*.cs` separates UI coordination by launcher feature.

## Optional integration workflow

### Generic local launch hook

The generic hook is off until both supported environment variables are set. It
accepts only a trusted HTTPS numeric-loopback URL and a valid bearer token,
disables redirects, cookies, and proxies, and times out quickly. It sends a
bounded launch event only after Roblox Player starts. See the
[SystemProcesses maintenance guide](SystemProcesses/README.md#generic-local-api-hook).

### HandleScope

SessionDock 3.0 includes HandleScope engine 0.3.0. Use the UI in this order:

1. Open **Integrations**.
2. Select **Included with SessionDock (recommended)** or, only when you already
   operate it, **Standalone HandleScope (advanced)**.
3. For standalone mode, choose **Automatic**, **Keep the installed version**, or
   an exact reviewed version. This only constrains compatibility and never
   changes the external installation.
4. Choose **Automatic**, `v2`, or `v1` API negotiation.
5. Select **Enable**. SessionDock starts/checks the runtime automatically; wait
   for **Ready**, or use **Retry** if the bounded check fails.

The included engine runs only when enabled, as a non-elevated, parent-owned
child with numeric IPv4 loopback as its sole network surface. SessionDock passes
its rotating token through an inherited anonymous pipe; the token remains in
memory and never enters a file, command line, environment variable, setting,
log, or UI. Closing SessionDock closes the child. There is no HandleScope
download, separate installation, PowerShell invocation, UAC prompt, scheduled
task, service, autostart entry, or separate update path.

The advanced standalone source preserves compatibility with an independently
installed, already running HandleScope. SessionDock validates and connects to
that runtime but never changes its files, version, configuration, process, or
lifecycle. Both sources expose only the compiled `v1`/`v2` adapters and fixed
Roblox singleton-event policy.

The existing minimal `%LOCALAPPDATA%\SessionDock\handlescope.json` opt-in remains
compatible. Source, standalone runtime-version, and API preferences stay
independent in
`handlescope-preferences.json`. Included mode does not use HandleScope's
disk-based `connection.json`; standalone mode retains that legacy discovery
contract without modifying it. Protocol negotiation begins with authenticated
`/v1/metadata` and can select only the compiled `v1` or `v2` adapter.
An upgraded exact-version pin that no longer matches can be cleared from the
same panel by selecting **Automatic** or **Keep the installed version**; no
download, install, upgrade, or downgrade occurs.
**Refresh reviewed versions** is an explicit, best-effort signed-catalog check
for advanced standalone mode. It preserves the current preference and downloads
no HandleScope package. Merely opening the panel remains local-only.

For schemas, paths, status meanings, manual configuration, and trust checks,
use [SystemProcesses/README.md](SystemProcesses/README.md).

## Before contributing

Read [CONTRIBUTING.md](../CONTRIBUTING.md), [SECURITY.md](../SECURITY.md), and
the [accessibility verification matrix](../docs/ACCESSIBILITY.md). Changes to
security, update, localization, data, or integration contracts require focused
tests and corresponding documentation updates.
