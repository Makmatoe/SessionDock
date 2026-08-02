# SessionDock desktop project

This directory contains the Windows WPF application. Repository-level build,
security, privacy, and release instructions are in the [root README](../README.md).

## Install the production app

[![Install Latest SessionDock release](../docs/assets/install-latest-sessiondock.svg)](https://github.com/Makmatoe/SessionDock/releases/latest/download/SessionDock-win-x64-Setup.exe)

The button downloads the official Windows x64 Setup asset from the latest
stable canonical release. Open `SessionDock-win-x64-Setup.exe` to install the
updateable production app; no release asset selection is required. Release
details and manual checksum verification remain available in the
[root installation guide](../README.md#install-sessiondock).

## Development run

From the repository root:

```powershell
dotnet run --project .\SessionDock\SessionDock.csproj
```

Development and raw `dotnet publish` builds are intentionally not self-updating.
Only a Velopack Setup installation from the canonical latest-release button
enables the production update path. The project currently publishes unsigned
Windows executables, so Windows may show Unknown publisher. Production updates
still require the independently signed update descriptor and exact package
integrity checks.

## Local data

SessionDock keeps account-slot metadata, optional account groups, batch preset
account keys and delays, launch history, theme/language preferences, imported
sounds, and isolated WebView2 profiles under `%LOCALAPPDATA%\SessionDock`.
Batch presets do not copy destinations or server identifiers. No account data, cookies,
passwords, tokens, or private-server codes are compiled into the application.

The display-language preference supports `system`, `en-US`, `nl-NL`, `de-DE`,
`fr-FR`, and `es-ES`. Language switching is live and affects static resources,
runtime messages, dialogs, diagnostics, and accessibility text. System default
uses a supported Windows language and otherwise falls back to English.

The sidebar's safe metadata transfer panel can write a user-reviewed JSON file
containing Roblox user IDs, account labels/groups/colors/order, and pinned
public place IDs/names. It never exports local account keys, usernames,
destinations, timestamps, private-server details, server JobIds, browser data,
settings internals, or integrations. Import validates the complete bounded
schema, previews all applicable/skipped counts, requires confirmation, and only
merges into accounts already present on this computer.

The app is single-instance for each Windows login session. Each saved account
has its own WebView2 profile. Roblox credentials are entered only on official
Roblox pages and are not read or stored by SessionDock.

## Main components

- `Services/DestinationParser.cs` validates supported Roblox destinations.
- `Services/JoinUserDestination.cs` validates explicit user destinations so a
  numeric user ID cannot be confused with a Place ID.
- `Services/AutoJoinWatchContext.cs`, `AutoJoinLaunchGate.cs`, and
  `JoinUserWatchPolicy.cs` pin the selected context, enforce one-shot handoff,
  and bound the explicit session-only watch, backoff, and four-hour expiry.
- `Services/RobloxWebSessionService.cs` manages isolated browser sessions.
- `Services/RobloxClientService.cs` discovers, verifies, launches, and closes
  Roblox Player processes.
- `Services/SupportDiagnosticsService.cs` creates the bounded allowlisted text
  shown, copied, and exported by the About and diagnostics panel.
- `Services/MetadataTransferService.cs` owns the versioned allowlist, bounded
  validation, preview plan, and safe metadata merge.
- `Services/SessionDockUpdateService.cs` coordinates the manual Velopack updater and
  requires a descriptor authorized by the pinned release key.
- `SystemProcesses/` contains optional, loopback-only post-launch connectors.
- `MainWindow.*.cs` splits UI coordination by launcher feature.

## Optional integrations

SessionDock can notify a user-configured loopback endpoint after a successful
launch. It can also use the optional HandleScope local API when the user
explicitly enables the fixed Roblox policy. SessionDock never bundles or
elevates HandleScope. The panel loads a bundled compatibility bootstrap and
makes a network request only when the user explicitly selects **Check
versions** or confirms an installation. A signed, rollback-resistant catalog
authorizes compatible releases, exact package/executable identities, and only
the `v1`/`v2` adapters already compiled into SessionDock. Users can select
Automatic, Keep installed, or an exact reviewed release and API contract;
checking or changing that preference never installs, replaces, or downgrades
software.

After a separate confirmation, SessionDock verifies the selected package,
checksum, any cataloged release manifest, executable identities, ZIP layout,
and internal inventory, then uses one locally compiled setup adapter. Native
releases require a v2 manifest and run only the fixed locked
`api/HandleScope.Setup.exe` directly after its identity matches the inventory.
Reviewed 0.1.4 and 0.2.2
releases retain their fixed process-scoped `RemoteSigned` adapter. SessionDock
refuses downgrades and never uses `Bypass` or `Unrestricted`, changes saved
policy, overrides Group Policy, elevates, or enables the integration
automatically. The existing minimal `handlescope.json` opt-in and exact
five-field `connection.json` discovery contract remain compatible. See
[SystemProcesses/README.md](SystemProcesses/README.md).

## Updates

The top-right update button checks the canonical stable GitHub release feed.
The app verifies the signed release descriptor before it displays notes or
downloads a package. Velopack then verifies and stages the authorized package;
installation happens only after the user confirms and SessionDock exits.

Release engineering details are in [docs/RELEASING.md](../docs/RELEASING.md).
