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

The app is single-instance for each Windows login session. Each saved account
has its own WebView2 profile. Roblox credentials are entered only on official
Roblox pages and are not read or stored by SessionDock.

## Main components

- `Services/DestinationParser.cs` validates supported Roblox destinations.
- `Services/JoinUserDestination.cs` validates explicit user destinations so a
  numeric user ID cannot be confused with a Place ID.
- `Services/RobloxWebSessionService.cs` manages isolated browser sessions.
- `Services/RobloxClientService.cs` discovers, verifies, launches, and closes
  Roblox Player processes.
- `Services/SupportDiagnosticsService.cs` creates the bounded allowlisted text
  shown, copied, and exported by the About and diagnostics panel.
- `Services/SessionDockUpdateService.cs` coordinates the manual Velopack updater and
  requires a descriptor authorized by the pinned release key.
- `SystemProcesses/` contains optional, loopback-only post-launch connectors.
- `MainWindow.*.cs` splits UI coordination by launcher feature.

## Optional integrations

SessionDock can notify a user-configured loopback endpoint after a successful
launch. It can also use the optional HandleScope local API when the user
explicitly enables the fixed Roblox policy. SessionDock never bundles or
elevates HandleScope. Its integration panel downloads and installs the latest
stable canonical GitHub release only after the user explicitly selects
**Install Latest HandleScope release** and accepts the confirmation. Install is
per-user, starts the API immediately, and enables HandleScope's limited
interactive-logon autostart task. It does not change SessionDock's integration
setting. Before any installer runs, SessionDock verifies the stable immutable
GitHub release, exact asset digests, matching checksum, safe archive, and full
internal manifest. It saves that verified inventory and rechecks the installed
API hash before every start or trust decision. A future signed descriptor is
also enforced when present. The panel can start only the API at the expected
per-user installation path after those checks if a manual restart is later needed. See
[SystemProcesses/README.md](SystemProcesses/README.md).

## Updates

The top-right update button checks the canonical stable GitHub release feed.
The app verifies the signed release descriptor before it displays notes or
downloads a package. Velopack then verifies and stages the authorized package;
installation happens only after the user confirms and SessionDock exits.

Release engineering details are in [docs/RELEASING.md](../docs/RELEASING.md).
