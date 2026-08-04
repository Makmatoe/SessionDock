# SessionDock

[![CI](https://github.com/Makmatoe/SessionDock/actions/workflows/ci.yml/badge.svg)](https://github.com/Makmatoe/SessionDock/actions/workflows/ci.yml)

SessionDock is a Windows x64 launcher for Roblox. It keeps each website sign-in
in a separate local browser profile and can launch several attributed Roblox
clients, arrange their windows, and restore a saved session template.

> SessionDock is independent from Roblox Corporation. Roblox and the Roblox
> logo are trademarks of Roblox Corporation.

> **Development status:** the integrated HandleScope, ExactWheel, clickable
> cascade, and template workflow described here is the current source-tree
> target. It is not a release announcement. Use a published release only when
> its own notes explicitly include that behavior.

> **Distribution hold — 2026-08-04:** the current latest release is a
> zero-asset security-hold record. Download nothing while this hold is active.
> A future reviewed release must explicitly state that it lifts the hold and
> has passed separate laptop validation before any public download is approved.

## Download and launch after the hold is lifted

SessionDock is permanently distributed as a transparent, unsigned portable
ZIP. There is no Setup executable. After a reviewed release explicitly lifts
the hold:

1. Install Roblox Player on a Windows x64 PC.
2. Open only the canonical
   [`Makmatoe/SessionDock` GitHub Releases page](https://github.com/Makmatoe/SessionDock/releases).
3. Download `SessionDock-win-x64-Portable.zip` and the matching
   `SHA256SUMS.txt` from that same release. A Discord message may link to the
   release page, but never download a SessionDock binary from Discord.
4. [Verify the ZIP's SHA-256](docs/UPDATES.md#verify-the-portable-zip) without
   changing PowerShell execution policy.
5. In File Explorer, select **Extract All** and choose a new folder. Keep the
   complete extracted folder together.
6. Run `SessionDock.exe` as your normal Windows user, not as administrator.
7. Complete the first-launch tutorial, then add each Roblox account through its
   own official Roblox sign-in page.

Because SessionDock is unsigned, Windows may show **Unknown publisher** or a
reputation warning. That is expected of an unsigned app, but it is not a safety
verdict: proceed through a normal warning only after the hold is explicitly
lifted, the GitHub source and hash match, and Windows reports no named threat.
A named detection such as `Trojan:Win32/Wacatac.B!ml` is always a hard stop.
Checksums and attestations identify bytes; they never override a malware
detection. Never disable Defender, restore a detected file, add an exclusion,
remove its download-zone metadata, or weaken device policy to make it run.

The Windows package is intentionally a transparent self-contained folder, not
a compressed self-extracting single executable. `SessionDock.exe` loads the
separately inspectable `SessionDock.dll`, `SessionDock.HandleScope.dll`,
`SessionDock.ExactWheel.dll`, and pinned runtime files beside it. Keep the
complete extracted folder together; never run a bare `SessionDock.exe` or
component DLL. This makes every component individually scannable. A positive
result still blocks use and release pending investigation. If Windows reports
a named malware detection such as
`Trojan:Win32/Wacatac.B!ml`, do not run or restore the file; follow the
[Defender detection response](docs/DEFENDER_DETECTION_RESPONSE.md).

Read the full [first-run guide](docs/GETTING_STARTED.md) before testing the new
workflow.

## The laptop error: execution policy, not a HandleScope requirement

The reported PowerShell message:

```text
Install-HandleScopeApi.ps1 cannot be loaded because running scripts is disabled on this system
```

means Windows PowerShell blocked the script under that laptop's execution
policy before the script could run. It is not proof that the script ran, and it
is not by itself an antivirus detection.

`-ExecutionPolicy` is a startup option for a PowerShell process. It is not a
standalone command and is not an argument that can make an already-blocked
script execute. That is why these forms did not solve the error:

```powershell
& $installer -ExecutionPolicy Bypass
-ExecutionPolicy Bypass
```

Normal SessionDock users should not work around the policy at all. The
integrated workflow does not run `Install-HandleScopeApi.ps1`, create a
scheduled task, or require a PowerShell execution-policy change.

A browser message such as **Virus scan failed** usually means that the browser,
Windows Attachment Manager, antivirus, or a managed-device policy could not
finish scanning the download. It does **not** mean "malware confirmed." A file
working once or on another PC makes a device-specific download or policy issue
more likely, but it is not proof that any file is safe.

A named Defender malware detection is different from **Virus scan failed**.
Leave a named detection quarantined and use the
[Defender detection response](docs/DEFENDER_DETECTION_RESPONSE.md); do not use
the download-retry steps below for that detected file.

Use this recovery order:

1. Delete the incomplete download; do not keep retrying an old standalone ZIP
   or script.
2. Check the canonical release notes. Download nothing while the distribution
   hold is active.
3. After an approved release explicitly lifts the hold, download only its
   portable ZIP from GitHub Releases and verify its SHA-256.
4. Open **Windows Security > Virus & threat protection > Protection history**
   and read the exact event, if one exists.
5. On a managed laptop, ask the administrator to review the canonical URL,
   hash, and policy. Do not disable antivirus, SmartScreen, execution policy,
   or application control.
6. If no reviewed release has lifted the hold, do not invent an alternate
   download path or substitute a separate HandleScope package.

## First run

Home keeps the common workflow in one place. Its four large action cards are
**Launch accounts**, **Run template**, **Macros**, and **Templates**. Directly
under them are the two smaller setup shortcuts used most often:
**Destinations** and **Manage accounts**.

1. Start with **Manage accounts** and **Destinations**. Add and verify accounts,
   give them clear labels/colors, then create named destinations and assign each
   one to the intended accounts.
2. Use **Launch accounts** for an ordinary batch or **Run template** for a saved
   complete session.
3. Open **Macros** to record or maintain ExactWheel recordings. Open
   **Templates** to save the current session or edit reusable sessions.
4. Open the settings icon for less-frequent options such as target/minimum
   Roblox window size, clickable reveal area, preferred monitor, and the global
   recording-stop keybind.
5. Follow the highlighted Get Started tutorial when Home first opens. It walks
   through the actual controls; the optional Advanced tutorial can be launched
   separately from Settings.

Account attribution is what keeps a captured window, destination, or
per-client macro tied to the correct account. Start with test accounts and a
harmless destination. Do not begin by recording purchases, chat, login,
moderation, or irreversible actions.

The tutorial can be replayed from Settings. It is onboarding state only; it
does not change accounts, windows, macros, or templates.

## Everyday integrated workflow

### 0. Set up accounts and named destinations

1. On Home, open **Manage accounts** and add or review the accounts you will use.
2. Open **Destinations**, choose **New destination**, enter a unique human name
   and a valid Roblox destination, then check the accounts that should use it.
3. Select **Save destination**. Each account can belong to at most one named
   destination; assigning it to another moves that assignment and updates the
   account's launch destination.
4. Reopen the destination and confirm the account checkboxes before launching.

A named destination is a reusable label and account assignment, not a secret
store. Deleting its named entry leaves each assigned account's current value as
a backward-compatible custom destination. Editing a destination directly on an
account detaches that account from the named entry. Existing per-account
destinations, batch presets, and older templates remain usable.

### 1. Launch accounts into a clickable cascade

1. Select **Launch accounts** on Home.
2. Choose the accounts and destination, then review the launch delay.
3. Start the batch.
4. SessionDock attributes each verified Roblox process to its account and waits
   for one stable, visible main window. A minimized, fullscreen, or ambiguous
   window is not guessed.
5. SessionDock applies the configured target and minimum sizes, then builds the
   cascade from the top-left of the preferred monitor's work area.
6. Each following window moves down and right by the configured reveal. Click
   the exposed client-area patch to focus it without aiming at a one-pixel
   border.

SessionDock waits for the realized bounds to settle and can reapply a position
once if Roblox moves the window during startup. It never requests a size below
the configured minimum; if Roblox realizes a different size, SessionDock uses
and reports the stable realized bounds. A long cascade continues on other
monitors; if it must reuse monitor space as another group, SessionDock reports
that result. If the preferred monitor is disconnected, a dynamic cascade uses
the first usable monitor in the current deterministic monitor order.

Roblox windows remain normal, non-topmost windows. Layout applies their
relative staircase order without raising the group over unrelated applications,
and it removes an accidental always-on-top state left by an older build. An
explicit click on an exposed patch focuses only that selected client.

### 2. Record an ExactWheel macro

1. Put the Roblox client in the exact state you want to automate.
2. On Home, open **Macros**, then select **Record macro**.
3. Choose one verified, focused client for a client-relative macro, or choose a
   whole-layout recording only when the action genuinely spans the desktop.
4. Start recording, perform a short harmless sequence, then press the global
   stop-recording keybind (F8 by default) so Roblox remains focused. Change it
   from the macro library's settings; supported base keys are F6 through F11,
   optionally combined with Ctrl, Alt, and/or Shift. The complete stop chord is
   removed from the saved macro.
5. Give the recording a clear name, observe at least one complete playback
   cycle, then select **Stop** before assigning it to a template.

Client recording starts only for the verified foreground target. ExactWheel
records bounded mouse and keyboard events; it does not need a separate macro
application or ExactWheel installation. Never record a password, authentication code,
payment, private chat, or other secret. A macro file contains your input timing
and may contain typed keys, so treat it as private data.
For an individual-client recording, input is captured only while its exact
verified Roblox window is foreground. Moving to another application pauses
capture and refocusing resumes it; the click used to refocus is excluded. Switch
only after releasing held keys and mouse buttons. If focus is lost while an
accepted input is still held, SessionDock rejects the recording instead of
saving an unbalanced macro. Whole-layout recording remains global.

To maintain saved recordings, open **Macros** on Home. The same library is also
available from Settings.
**Rename** changes only the display name; the stable content ID, hash, type,
assignments, and payload stay unchanged. **Remove** is blocked while any
template references the macro. Removing an unreferenced entry requires
confirmation and first changes only the catalog draft. After **Save settings**
commits that removal, SessionDock verifies and deletes the exact
content-addressed payload only when no remaining macro definition uses it. A
shared, changed, locked, or unverifiable payload is retained and cleanup failure
does not undo the saved catalog.

### 3. Save the current session as a template

1. Restore, arrange, and focus-test every running client. Minimized or wholly
   off-screen windows cannot be captured as saved positions.
2. On Home, open **Templates**, then select **Save current session**.
3. Enter a name and review the launch delay.
4. Review the resolved destination shown for every account slot. A template
   stores these per-slot values so it can reproduce the intended launches even
   if the named-destination library later changes. Edit a slot before saving
   when this session should launch somewhere else.
5. Choose **Saved positions** to keep each monitor-relative window position, or
   **Clickable cascade** to rebuild the staircase dynamically when run.
6. Choose a macro mode:

   - **No macro**: launch and arrange only.
   - **Per client**: assign a different macro to each client; leave any client
     on **No macro** to skip it.
   - **Shared across clients**: transform one client-relative macro for every
     selected client.
   - **Whole layout**: run one macro against the complete desktop layout.

   Every assigned macro mode loops continuously after **Play** until you select
   **Stop** or close the controller. Physical input, held input, or loss of the
   verified foreground target pauses injection until the safe focus and input
   conditions recover. Replacing the batch or closing SessionDock cancels the
   loop.

   Client switches include a short unscaled settle gap before the next input.
   A temporarily unavailable client yields its turn and is retried with
   backoff, so it cannot freeze the other clients. An invalid assignment is
   isolated to that assignment or macro mode; it does not end another healthy
   mode. Normal playback therefore ends only through **Stop**, controller/app
   close, or batch replacement. SessionDock still fails closed if Windows
   cannot release injected input or the physical-input safety monitor fails.

7. Select **Save template**. Existing batch presets remain available as legacy
   launch-only choices; they are not silently rewritten.

### 4. Run a template

1. Select **Run template**.
2. Choose the template and review every account's saved destination plus the
   layout, macro, and delay summary.
3. Resolve any missing account before continuing.
4. Start the run. SessionDock verifies accounts, launches them in order,
   then restores saved positions or creates the cascade.
5. If the batch has a valid macro assignment, the compact height-to-content
   controller opens with **Play** and speed. Nothing plays automatically: there
   is no playback countdown or autostart. While a run is active, **Play** becomes
   **Stop** so playback can be canceled without closing the controller.
6. Choose a supported speed from `0.25x` through `2x`, check the focused
   clients, and select **Play**. The chosen
   speed is saved as the global controller preference and is reused later; it
   is not stored separately in each template.
7. Watch at least one complete loop. Stop immediately if the Roblox UI, display
   topology, destination, or account state differs from the recording.

The controller stays available after you stop playback, so **Play** can start
the current assignments again. Closing the controller stops an active loop
before hiding it; use **Controller** on Home or in Settings to reopen it. It is
deliberately small and remains resizable so Windows text scaling does not crop
the controls.

### 5. Change assignments or edit a template

To try an individual-client macro without changing the saved template:

1. Launch the batch, then select **Assign macros**.
2. Choose an individual-client macro.
3. Click or focus exactly one Roblox window from the current batch.
4. Repeat for other clients, close the assignment window, and select **Play**.

These focused-client assignments are attached only to the exact verified
processes and windows in the current launched batch. Starting another
successful batch or closing SessionDock replaces them. To make a change
permanent, open **Templates** on Home, select one template, choose **Edit**, then
save the editor and **Save settings**. Editing preserves stable template,
slot, account, and valid macro identifiers. The editor can change each slot's
destination without recapturing its saved window position.

Legacy batch presets remain labeled launch-only choices. Catalog versions 1
and 2 are read conservatively and upgraded to version 3 on the next explicit
catalog save; malformed or unknown versions are not guessed.

See [Templates and ExactWheel](docs/TEMPLATES_AND_MACROS.md) for file formats,
scaling rules, recovery behavior, and the safety checklist.

## 4K-to-1080p scaling

Saved window rectangles use fractions of the selected monitor work area, not
raw 4K pixels. Target/minimum dimensions and reveal spacing use 96-DPI logical
pixels and are clamped to the destination work area.

New saved positions first match the recorded stable monitor ID. If that monitor
is disconnected, or Windows reuses its logical device name for different
hardware, they move to the current primary monitor. Only legacy placements
without a stable ID try their recorded device name and then monitor index before
falling back to primary.

For a client-relative macro, each mouse coordinate is mapped from the recorded
Roblox client rectangle into the destination client rectangle. A macro recorded
in a 4K-sized window can therefore target the corresponding relative point in a
1080p-sized window. It is not image recognition: a different aspect ratio,
Roblox UI scale, menu state, language, or responsive layout can still move the
control. Test both displays before relying on the template.

Whole-layout scaling is stricter. Virtual-desktop scaling is explicit, and
monitor-normalized playback requires the same monitor count. Events in monitor
gaps or outside the recorded client are rejected instead of being guessed or
silently clamped.

## Scaling to many clients

SessionDock keeps one verified source event timeline per macro and a tiny
coordinate plan per destination window. It maps coordinates immediately before
dispatch, so adding clients adds small O(n) geometry and lease state instead of
copying the complete recording n times. The first 1,000,000 unique-source events
stay in compact managed arrays. Further assigned sources use run-owned,
delete-on-close memory maps with reusable 512-event pages, so a large unique
working set never causes deserialize-and-allocate churn on every loop. A run is
explicitly capped at 128 client sources plus one whole-layout source and a
256 MiB aggregate mapped-byte ceiling. Template preflight transfers this cache
into the first playback cycle instead of loading the same artifacts twice.
There is no eight-client cache cliff.

One playback worker, one physical-input monitor, shared executable-trust proofs,
deadline-driven retry waves, and bounded UI progress are reused for the whole
run. Deterministic performance contracts exercise 1, 8, 32, 100, and 128-client
paths. Retry backoff has a 250-millisecond-to-five-second base; eight-client
waves can add at most 850 milliseconds for 128 clients plus the whole-layout
target. While healthy targets continue, deferred targets require only
lightweight O(n) deadline checks per completed cycle, and a due retry replaces
any sticky failed lease before reacquiring the exact target. Within one batch operation, executable checks for
clients using the same canonical Roblox path share one forced Authenticode
proof. That proof hashes and verifies the same open file handle, so the reduced
work does not rely on path metadata alone.

Optional joined-server attribution is shared by the complete batch too. One
incremental snapshot covers as many as 128 recent Roblox logs under a fixed
4 MiB aggregate read budget; unchanged logs read zero payload bytes, and
lookups use the latest indexed `(user, place)` observation instead of rescanning
one log tail per client.

Actual macro work still grows with the number of events that must be sent to
each selected client, and each Roblox process has its own CPU/GPU/memory cost.
SessionDock bounds its supporting caches and fails closed rather than removing
identity, focus, or input-authorization checks to claim a higher client count.

## Export or import selected data

Open **Export or import data** to choose templates, macros, public destinations,
and launch presets for a versioned `.sessiondock` ZIP. Template selections bring
their required macro dependencies and any matching eligible named-destination
dependencies into the review automatically. Private-server and tracked-server
values are omitted and counted instead of being exported. Macro files are
copied byte for byte and checked by SHA-256; an import validates archive version,
paths, hashes, dependencies, and bounded entry/count/size limits before it
offers a confirmation.

The package uses Roblox user IDs only to match accounts already present on the
destination device. It excludes sign-ins, cookies, tokens, authentication
tickets, usernames, local account keys and paths, private-server material,
server JobIds, integrations, and logs. A macro can contain recorded keyboard
input, so keyboard-bearing macros are listed and require explicit acknowledgement
for both export and import. Review them as potentially sensitive data.

Window placements use normalized monitor-work-area rectangles across devices.
Client-relative macro bytes remain unchanged and coordinates adapt only at
playback against the verified Roblox client. An incompatible whole-layout
assignment is left unassigned when monitor count, aspect ratio, or normalized
arrangement differs. The import review also reports conflicts and missing
account matches. The earlier reviewed metadata JSON transfer remains available
for account appearance, matched order, and pinned public favorites. See
[Privacy](docs/PRIVACY.md#portable-selected-data-transfer) and
[Templates and ExactWheel](docs/TEMPLATES_AND_MACROS.md#portable-package-transfer).

## Macro stop and physical intervention

Keep a hand on the mouse or keyboard during every test. The ExactWheel engine's
default safety behavior:

- refuses to begin while unrelated physical keys or mouse buttons are held;
- treats new physical mouse or keyboard input, held input, and verified focus
  loss as a pause condition for every macro mode; injection resumes only after
  the safe input state and exact leased Roblox foreground target recover;
- yields and retries verified-target loss, late scheduling, injection failure,
  timer failure, and held physical input with bounded n-client backoff;
- rebuilds a failed intervention-monitor session, while permanently invalid
  assignments or an unconfirmed global-input cleanup enter a zero-input safety
  pause that remains active until the user selects Stop;
- honors cancellation when the current batch is replaced or SessionDock exits;
  and
- attempts to release only held inputs that SessionDock successfully injected
  and reports cleanup failure if Windows does not accept every release.

The compact controller deliberately has one playback button and one speed
selector. **Play** begins a continuous loop and becomes **Stop** while running;
closing the controller also stops the active loop before hiding it. Every macro
mode pauses without injecting while physical input is active or its verified
foreground condition is unavailable, and resumes only after the safe input and
focus conditions recover. A failed replacement-batch preflight leaves the
existing loop running. The batch-launch Cancel button does not control macro
playback. A run that cannot safely resume stays active without injecting until
you select **Stop**, close the controller, close SessionDock, or lock the
Windows session. Do not leave
macro playback unattended, and do not use it for actions whose failure could
cost money, disclose data, or damage an account.

## Local storage and privacy

SessionDock has no SessionDock cloud backend or telemetry. Its active data stays
under `%LOCALAPPDATA%\SessionDock` for the current Windows user:

| Path | Contents |
| --- | --- |
| `settings.json` and profile directories | Account metadata, named destinations/account assignments, preferences, and isolated WebView2 browser data |
| `Templates\catalog.json` | Catalog schema v3: templates, macro metadata, layout preferences, persisted controller speed and recording-stop keybind, and stale references kept for repair |
| `Templates\catalog.backup.json` | Last known-good catalog used for bounded recovery |
| `Macros\` | Bounded ExactWheel macro files; these can contain recorded key input |
| `onboarding-state.json` | Completed tutorial version only |
| `handlescope*.json` | HandleScope opt-in, source, and compatibility preferences; never the bundled runtime token |

Catalog writes are bounded and atomic. Macro files are not automatically
deleted merely because a template stops referencing them. Review them before
manual deletion. Deleting the portable application folder does not
automatically erase this directory; remove it only when you intentionally want
to delete all local SessionDock data for that Windows user.

## HandleScope: included by default, standalone only by choice

Normal use needs no separate HandleScope download or installation:

1. Open **Integrations > HandleScope integration**.
2. Keep **Included with SessionDock (recommended)** selected.
3. Choose **Automatic**, `v2`, or `v1` for the API contract.
4. Select **Enable** and wait for **Ready**.

The included engine is a non-elevated, parent-owned child bound to numeric IPv4
loopback. Its rotating token is passed through an inherited anonymous pipe and
remains in process memory. Closing SessionDock closes the child. It creates no
service, scheduled task, sign-in autostart, separate updater, or separate
uninstall entry.

**Standalone HandleScope (advanced)** remains for users who already manage a
separate compatible runtime. Its source/version/API selectors are compatibility
requirements only; SessionDock does not download, install, start, stop, update,
downgrade, or uninstall that copy. Read the
[technical connector guide](SessionDock/SystemProcesses/README.md#handlescope-connector).

## Update and remove

Portable copies update manually: download the new portable ZIP from the
canonical GitHub release, verify it, and extract it into a new folder. Do not
overwrite a running folder. Existing copies installed by an older supported
release may continue to consume the verified full NUPKG and update feed through
SessionDock's in-app update control; a NUPKG is not a file users open manually.

To remove a portable copy safely:

1. Disable **Open with SessionDock** if you want its owned link-handler entries
   removed.
2. Disable HandleScope integration. The included child also stops when
   SessionDock exits; an advanced standalone copy is left untouched.
3. Delete an account inside SessionDock first only when you want its browser
   profile removed.
4. Close SessionDock, then delete the extracted application folder.
5. Keep or deliberately remove `%LOCALAPPDATA%\SessionDock` as described above.

Users moving from Roblox One or SessionDock 2.3.0 and earlier must follow the
[side-by-side migration guide](docs/UPDATES.md#moving-from-roblox-one-or-sessiondock-230-and-earlier)
before deleting legacy data.

## Build and test before any release

Development requires Windows and the exact SDK pinned in `global.json`.

```powershell
dotnet --info
dotnet restore .\SessionDock.slnx --locked-mode
.\scripts\Build.ps1 -Configuration Release -Runtime win-x64 -CI
```

Before any release decision, require the complete gate and manually test:

1. clean first run and tutorial replay;
2. normal and canceled batch launch;
3. stable minimum-size cascade with clearly clickable reveal patches;
4. saved layouts with a disconnected stable monitor ID, a reused logical
   device name, and legacy device-name/index fallback;
5. explicit Play with no playback countdown or autostart;
6. compact-controller sizing and every selectable speed from `0.25x` to `2x`;
7. F8/custom recording stop with no terminal hotkey events in playback;
8. balanced per-client recording pause/refocus and all-mode continuous
   playback pause/refocus/resume until controller Stop/close;
9. normal Roblox z-order with an unrelated application kept above the group;
10. named-destination creation, reassignment, deletion fallback, and restart;
11. current-batch focused-client assignment and app-close cancellation;
12. template save/edit, v1/v2-to-v3 migration, restart, recovery backup, and
   stale-reference repair;
13. macro rename plus referenced/unreferenced removal behavior;
14. 4K recording replayed on a 1080p laptop and the reverse;
15. included HandleScope with scripts disabled on the test device; and
16. a clean machine that has never installed standalone HandleScope or
    ExactWheel.

Development output is not a published release. ExactWheel provenance pins 14
implementation/lock files at commit
`40023f516fe89977a35d94cc5580e790e48d54a1`, the separately pinned current
build definition, and the root MIT license. Follow
[Releasing](docs/RELEASING.md) only after code, security, provenance,
accessibility, documentation, and end-to-end tests all pass; none of those
gates overrides the live distribution hold.

## More documentation

- [First-run guide](docs/GETTING_STARTED.md)
- [Templates, ExactWheel, scaling, and safety](docs/TEMPLATES_AND_MACROS.md)
- [Privacy](docs/PRIVACY.md)
- [Updates and checksum verification](docs/UPDATES.md)
- [Accessibility verification](docs/ACCESSIBILITY.md)
- [Security policy](SECURITY.md)
- [Desktop-project maintainer guide](SessionDock/README.md)

The [SessionDock 2.7.0 visual evidence](docs/images/sessiondock-v2.7.0/README.md)
is an immutable historical snapshot. It does not show the current source-tree
Home, cascade, macro, or template workflow.

The repository contains [`LICENSE.md`](LICENSE.md), component provenance, and
third-party notices for maintainers to review. That evidence does not by itself
approve or announce an unpublished integrated release.
