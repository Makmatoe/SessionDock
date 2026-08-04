# SessionDock desktop project

This directory contains the Windows WPF application. Start with the
[root README](../README.md) for the future portable download flow and the
[first-run guide](../docs/GETTING_STARTED.md) for the integrated user workflow.

> HandleScope and ExactWheel are source-tree components of one SessionDock
> application. Normal users must not use a separate package or PowerShell
> execution-policy bypass. The current integrated source is not a release
> announcement.

> **Distribution hold — 2026-08-04:** the current latest release is a
> zero-asset security-hold record. Download nothing while this hold is active.
> A future reviewed release must explicitly state that it lifts the hold and
> has passed separate laptop validation before any public download is approved.

## Run from source

Requirements:

- Windows x64 in a normal, non-elevated interactive session;
- the exact .NET SDK pinned by [`global.json`](../global.json); and
- the locked NuGet dependency graph.

From the repository root:

```powershell
dotnet --info
dotnet restore .\SessionDock.slnx --locked-mode
dotnet run --project .\SessionDock\SessionDock.csproj
```

`dotnet run` is a development build. It is not installed, self-updating, or a
published production package.

## First development run

1. Start SessionDock as a standard user.
2. Complete or skip the Home tutorial, then replay it from Settings to verify
   onboarding persistence.
3. Verify Home's four large cards (**Launch accounts**, **Run template**,
   **Macros**, and **Templates**) and the two smaller setup shortcuts directly
   beneath them (**Destinations** and **Manage accounts**). Add test accounts,
   create a harmless named destination, and assign it before launch.
4. Select **Launch accounts** and verify the clickable cascade without raising
   Roblox above an unrelated application or leaving any client topmost.
5. Record only a short, reversible ExactWheel sequence. Stop with F8 (or the
   configured global keybind), then verify that chord is absent during replay.
6. Confirm an individual recording pauses when its exact Roblox target loses
   foreground, excludes the refocus click, and resumes with balanced input after
   the target returns. Focus loss with an accepted key/button still held must
   reject the recording safely.
7. Open **Templates**, select **Save current session**, assign layout/macro
   behavior, review or edit every slot's resolved destination, and save.
8. Restart SessionDock and select **Run template**.
9. Confirm launch and stable window placement finish without starting a macro.
10. In the compact controller, test `0.25x`, `1x`, and `2x`, then select
    **Play** explicitly. Confirm every assignment repeats in full cycles until
    **Stop**. Reopen it and confirm the speed survives an app restart.
11. Confirm per-client and whole-layout playback both pause without injection
    on physical input or verified focus loss and resume only after their safe
    input and exact leased Roblox foreground conditions recover.
12. Use **Assign macros** to select a client macro and focus one current-batch
    Roblox window. Confirm a new batch discards that runtime-only assignment.
13. Rename a macro, verify its content ID/hash stay stable, then confirm removal
    is blocked while referenced. After removing its last reference and saving
    settings, confirm the exact unshared payload is deleted while a changed or
    shared payload is retained.
14. Open **Templates** on Home and edit the saved template. Save the editor and
    settings, then confirm stable template/slot identifiers survive and a
    per-slot destination can change without recapturing its window position.
15. Verify 4K/1080p scaling, disconnected-monitor fallback, all-mode physical-
    intervention pause/resume, and controller Stop/close cancellation on real
    devices.

The detailed expectations are in
[`docs/TEMPLATES_AND_MACROS.md`](../docs/TEMPLATES_AND_MACROS.md).

## Complete validation gate

Run from the repository root:

```powershell
.\scripts\Build.ps1 -Configuration Release -Runtime win-x64 -CI
```

The gate must:

1. validate repository and release policy;
2. restore every project in locked mode;
3. audit dependencies in CI mode;
4. build with warnings treated as errors;
5. run every test project;
6. verify HandleScope provenance and synchronized source;
7. build and test the ExactWheel component, macro format, scaling, timing,
   recording, playback, cleanup, and physical-intervention paths;
8. create a self-contained Windows x64 publish; and
9. verify the publish inventory.

Do not stop at unit tests. Manually test the first-run tutorial, batch cascade,
template recovery and editing, v1/v2-to-v3 catalog migration, per-client/shared/
whole-layout macro modes, explicit Play with no playback countdown or
autostart, all-mode full-cycle looping until Stop, the `0.25x`-to-`2x` speed
range, custom recording-stop hotkey, normal non-topmost Roblox z-order,
all-mode pause/resume, named destination assignment, macro rename/removal,
current-batch focused-client assignment, controller Stop/close cancellation,
monitor fallback, and 4K-to-1080p behavior.

Local production packaging is intentionally unavailable. Follow
[`docs/RELEASING.md`](../docs/RELEASING.md) only after every automated and
manual gate passes. ExactWheel provenance pins 14 implementation/lock files at
commit `f32799820fb4a31089523beb184314542f4fe521`, the separately pinned current
build definition, and the root MIT license. The release verifier must
cryptographically confirm that complete identity before publication.

## Architecture map

### Application and accounts

- `Program.cs` handles Velopack lifecycle admission and standard-user startup.
- `Services/RobloxWebSessionService.cs` owns isolated WebView2 sessions.
- `Services/RobloxClientService.cs` verifies Roblox Player binaries/processes,
  launches clients, and closes verified clients.
- `Services/RunningRobloxClient.cs` keeps a launched process attributed to
  the account that created it.
- `Services/DestinationParser.cs` and related planners validate destinations.
- `Services/NamedDestinationPolicy.cs` normalizes reusable names, enforces one
  named assignment per account, mirrors its value to that account, and retains
  a custom per-account value when a named entry is deleted.

### Windows and templates

- `Services/RobloxWindowService.cs` discovers verified top-level Roblox
  windows, waits for stable handle and geometry selection, and verifies that
  bounded size/position changes settle.
- `Services/RobloxPlaybackTargetLease.cs` retains each original Roblox process
  during ExactWheel playback and fails closed on PID/HWND ownership, lifetime,
  trust, or usability changes. Every playback mode treats physical input or an
  unavailable verified foreground target as a non-injecting pause and resumes
  only when the safe input state and exact leased Roblox HWND return. A
  click-driven cross-client focus change receives an unscaled settle boundary;
  a target that stays unavailable yields with backoff so other clients and the
  other macro mode continue looping. A failed intervention monitor is rebuilt
  with a fresh playback session. If no assignment remains runnable, or global
  injected-input cleanup cannot be confirmed, the controller stays active in a
  zero-input safety pause until the user selects Stop.
- `Services/RobloxWindowLayoutPolicy.cs` computes the top-left clickable
  cascade, reveal spacing, normalized saved geometry, and monitor fallback.
- `Services/RobloxSessionLayoutCoordinator.cs` captures and restores
  monitor-relative placements using 96-DPI logical preferences. Z-order work
  demotes stale topmost Roblox windows and reorders only existing Roblox slots,
  so unrelated applications are not raised behind the group.
- `Models/SessionTemplate.cs` defines the versioned catalog, templates, client
  slots, normalized placements, preferences, and macro metadata.
- `Services/SessionTemplatePolicy.cs` bounds and normalizes the schema while
  preserving repairable stale references.
- `Services/SessionTemplateStore.cs` provides strict, bounded, atomic catalog
  persistence and recovery without auto-deleting macro files.
- `Services/SessionMacroLibraryPolicy.cs` validates display-name edits and
  blocks catalog removal while any per-client/shared/whole-layout template
  reference remains. After the outer catalog save, exact unreferenced payload
  cleanup deletes only bytes that still match the removed definition; shared,
  changed, or unverifiable files are retained.
- `TemplateEditorDialog.*` creates or edits a valid template while preserving
  stable identifiers; `SessionAutomationSettingsDialog.*` commits edits;
  `RunTemplateDialog.*` selects templates or backward-compatible batch presets.
- `Services/SessionMacroLaunchContext.cs` pins macro assignments to the exact
  verified clients in one launched batch. `ClientMacroAssignmentDialog.*`
  assigns a selected client macro by foreground focus for that batch only.
- `SessionMacroControllerWindow.*` is deliberately limited to explicit Play
  and the supported `0.25x`-to-`2x` playback range at idle. During playback,
  Play becomes Stop. Its height follows its content and it remains resizable
  for Windows text scaling. It has no countdown or autostart; speed is a
  catalog-wide persisted preference rather than per-template data.
- `Services/MacroRecordingHotkey.cs` registers the configurable global
  recording-stop keybind (F8 by default) without activating SessionDock and
  removes its complete terminal chord from the saved recording.
- `Services/OnboardingStateStore.cs` keeps tutorial versioning separate from
  application settings and template data.

### ExactWheel

- `../SessionDock.ExactWheel/` is the managed ExactWheel component compiled
  with SessionDock; it is not a separately installed toolbar/application.
- `ExactWheelSession` serializes recording/playback state and exposes emergency
  stop.
- `ExactWheelMacroSerializer` validates the bounded versioned binary format.
- `ExactWheelCoordinateTransforms` implements strict client-relative,
  virtual-desktop, and monitor-normalized scaling.
- `Windows/LowLevelInputCapture` records bounded physical input and monitors
  physical intervention.
- `Windows/ExactWheelPlaybackEngine` schedules playback, verifies guards,
  stops fail-closed, and reports injected-input cleanup.

### HandleScope and other integrations

- `../SessionDock.HandleScope/` contains the reviewed HandleScope component
  compiled into the application.
- `SystemProcesses/` owns the optional post-launch hooks and HandleScope runtime
  negotiation. See its [technical README](SystemProcesses/README.md).
- Included HandleScope uses a parent-owned child and inherited anonymous-pipe
  token. It does not use an install script, execution-policy bypass, service,
  or scheduled task.
- Standalone HandleScope remains an explicit advanced compatibility source;
  SessionDock never manages that external installation's lifecycle.

## Local data

SessionDock runs single-instance per interactive Windows session and stores
active data below `%LOCALAPPDATA%\SessionDock`, separate from application files.

| Path | Purpose |
| --- | --- |
| `settings.json` and profile directories | Account metadata, named destination assignments, application preferences, and isolated WebView2 browser data |
| `Templates\catalog.json` | Catalog schema v3: templates with resolved per-slot destinations, macro definitions, layout preferences, and the persisted controller speed and recording-stop keybind |
| `Templates\catalog.backup.json` | Last known-good template catalog |
| `Macros\` | ExactWheel macro payloads; may contain typed key input |
| `onboarding-state.json` | Completed tutorial version only |
| `handlescope*.json` | Opt-in/source/API compatibility preferences, never the included runtime token |

Template files reject unsafe reparse paths. The catalog is bounded strict UTF-8
and uses atomic replacement. A stale template reference is retained for repair;
an unreferenced macro file is never silently deleted.

Catalog schemas v1 and v2 remain readable. Loading infers an omitted macro kind only
when it can do so conservatively, supplies the 1x speed and F8 recording-stop
defaults, and normalizes in memory to v3. The next explicit catalog save writes
v3. Unknown or malformed
schemas fail closed; they are not rewritten as if migration succeeded.

Do not add sign-ins, tokens, passwords, private-server codes, or personal macro
payloads to source control, diagnostics, fixtures, screenshots, or release
artifacts.

## Portable selected-data packages

The user-facing transfer flow writes a versioned `.sessiondock` ZIP, not a copy
of the local catalog. Its bounded manifest contains only reviewed templates,
public destinations, launch presets, and selected macro references. Selecting a
template closes over its macro dependencies and any matching eligible named
destination. Private-server and tracked-server values are omitted and counted.
Macro entries are content-addressed, copied as exact bytes, and verified by
SHA-256 on import; archive paths, duplicates, versions, dependency graphs, and
entry/count/size limits must fail closed before any catalog mutation.

Portable account references use Roblox user IDs only. Never serialize sign-ins,
cookies, tokens, tickets, usernames, local account keys or paths, private-server
links/codes, server JobIds, integrations, or logs. Keyboard-bearing macros are
potentially sensitive and require a separate acknowledgement on export and
import. Source monitor identifiers are stripped. Placements remain normalized;
client-relative coordinates adapt only at playback while macro bytes stay
unchanged. Incompatible whole-layout topology leaves the assignment unassigned
for repair. The reviewed import plan must expose conflicts, missing accounts,
and skips before one coordinated apply whose individually atomic writes are
rolled back if the settings commit fails.

The legacy bounded JSON transfer remains supported for account appearance,
matched account order, and pinned public favorites. See
[`docs/PRIVACY.md`](../docs/PRIVACY.md#portable-selected-data-transfer) and
[`docs/TEMPLATES_AND_MACROS.md`](../docs/TEMPLATES_AND_MACROS.md#portable-package-transfer).

## Integrated distribution boundary

After a reviewed release explicitly lifts the hold, users download only the
transparent `SessionDock-win-x64-Portable.zip` from the canonical GitHub
release and extract it into a new folder. HandleScope and ExactWheel must remain
inside the reviewed SessionDock inventory. Do not add:

- a separate HandleScope or ExactWheel package;
- a PowerShell install or execution-policy bypass step;
- a service, scheduled task, or sign-in autostart entry;
- runtime code downloaded from a compatibility catalog; or
- an independent updater for a bundled component.

The advanced standalone HandleScope selector may connect to an already managed
compatible runtime. The version and API selectors are compatibility constraints
only and cannot download, install, update, downgrade, start, or stop that copy.

## Documentation obligations

Changes to security, storage, macros, scaling, HandleScope negotiation,
accessibility, onboarding, or update behavior require:

1. focused automated tests;
2. an end-to-end manual test on real Windows hardware;
3. updates to the root/user guides and technical contract; and
4. provenance and release-policy review.

Read [Contributing](../CONTRIBUTING.md), [Security](../SECURITY.md),
[Privacy](../docs/PRIVACY.md), and the
[accessibility verification matrix](../docs/ACCESSIBILITY.md) before changing
those boundaries.
