# Session templates and ExactWheel

This document describes the current source-tree design for SessionDock window
layouts, template metadata, and ExactWheel macro files. It is both a user safety
reference and a maintainer test contract; it does not announce a published
release.

## Home workflow

Home presents four large everyday actions: **Launch accounts**, **Run
template**, **Macros**, and **Templates**. Two smaller setup shortcuts sit
directly below them: **Destinations** and **Manage accounts**. The intended flow
is still setup, run, then automate, but the interface uses task names instead of
making users discover those features inside an abstract workspace:

1. Add/identify accounts, then create named destinations and assign them.
2. Launch an ordinary account batch or restore a complete template.
3. Open **Macros** to record/manage bounded macros, or **Templates** to save and
   edit a verified launch, layout, destination, and macro configuration.

Named destinations live in application settings, while a template stores the
resolved destination for each stable account slot. An account has at most one
named assignment. Reassigning it moves the assignment; directly entering a
custom destination detaches it. Deleting a named entry retains the account's
current value as a backward-compatible custom destination.

### Highlighted-button tours

On first launch, **Get Started** walks through the highlighted account,
destination, launch, recording, template, and Settings controls. The destination
steps identify its unique name and Roblox target, the account checkboxes, and
the save action so assignments are visible before a batch is launched.

Settings keeps two explicit actions: replay **Get Started**, or launch the
optional **Advanced** tour. Advanced separately highlights layout and DPI
scaling, intrinsic macro types and recording safety, template assignments,
portable export/import, current-batch focus assignment, explicit controller
**Play** plus safe client-stop/whole-session-pause behavior, the supported
`0.25x`-to-`2x` range, and the rarely needed Advanced workspace. Completing one
tour never starts the other. Replaying either tour changes no account,
destination, template, macro, or playback state.

## What a template contains

A schema-v3 catalog contains version-1 template records with:

- a stable template ID, name, update time, and launch delay;
- ordered account slots with stable slot IDs and optional destinations;
- either a dynamic clickable cascade or saved monitor-relative positions;
- macro mode: none, per-client, shared client-relative, or whole-layout; and
- references to versioned macro metadata.

The catalog also stores global preferences: whether normal batch launches
auto-arrange, target/minimum window dimensions, reveal spacing, an optional
preferred monitor, and the last selected macro playback speed. Speed is global,
not copied into each template. The global recording-stop keybind is stored here
too and defaults to F8 when a v1/v2 catalog is migrated.

Existing batch presets remain backward-compatible launch choices. Missing
accounts and missing macro references are preserved as repairable references;
SessionDock does not silently point a template at a different account or macro.

## Clickable cascade

The cascade is intended for human focus selection before macro actions:

1. Wait for one stable, visible, non-fullscreen main window for each verified
   Roblox process. Do not choose between equally viable windows.
2. Resolve the selected monitor work area and 96-DPI logical preferences.
3. Clamp target width/height to the configured minimum and available work area.
   If Roblox realizes a different size, keep the stable realized bounds rather
   than assuming the request was exact.
4. Place the first window near the work area's top-left margin.
5. Move each later window right by the configured horizontal reveal and down by
   the vertical reveal.
6. Continue on other monitors when possible. If every monitor is full at the
   minimum size, create another deterministic group and report it.
7. Preserve z-order so exposed client-area patches follow the intended focus
   order.
8. After each move, wait for the outer and client rectangles to remain stable.
   Reapply the requested position once if Roblox moves it during startup; fail
   visibly if the position still does not settle.

Reveal spacing is configurable; it is not a fixed five-percent offset. Choose a
strip wide and tall enough for a person to click reliably at the target DPI.

Roblox clients remain normal non-topmost windows. Before applying the relative
staircase order, SessionDock demotes a stale always-on-top state created by an
older build. It reuses the clients' existing z-order slots without activating
or raising the group over unrelated applications. Focusing a client is a
separate explicit action, such as clicking its exposed patch.

## Saved positions

Each saved rectangle stores `left`, `top`, `width`, and `height` as fractions of
one monitor's work area, plus monitor identity/index fallback. Values are
bounded to `0..1`, and the rectangle is clamped so its right and bottom edges
remain inside the work area.

On restore, SessionDock resolves the destination monitor, converts the
fractions to its current work area, applies the configured minimum, waits
for the realized rectangle to settle, and reports any placement it cannot
safely realize. It does not store raw 4K screen coordinates as the portable
source of truth.

Monitor fallback is deliberately asymmetric:

1. For a new placement, match the recorded stable monitor ID.
2. If that stable monitor is disconnected, or Windows has reused its logical
   device name for different hardware, use the current primary monitor. Do not
   reinterpret the saved rectangle against the unrelated monitor.
3. For a legacy placement with no stable ID only, try the saved device name,
   then the saved monitor index, and finally the current primary monitor.
4. If no primary is marked, use the first usable monitor in deterministic
   order; if none exists, fail the placement.

Saving rejects minimized windows and rectangles that do not intersect a usable
monitor work area. Restore clamps right and bottom edges into the selected work
area instead of silently leaving a client off-screen.

## Macro modes

| Mode | Behavior |
| --- | --- |
| **None** | Launch and arrange; do not play a macro. |
| **Per client** | On Play, each account slot can run a different client macro or no macro. The complete assignment set repeats in full cycles until Stop. The same macro can be assigned to several clients. |
| **Shared** | On Play, transform one client-relative macro for each selected client and repeat the complete selected-client cycle until Stop. |
| **Whole layout** | On Play, repeat one recording whose pointer coordinates refer to the complete desktop topology until Stop. |

Use a client-relative macro whenever the action stays inside one Roblox client.
It has the narrowest target and the best chance of scaling safely.

Every assigned macro mode repeats in full cycles until **Stop**. Older template
catalogs can still contain the former whole-layout `RepeatWholeLayoutMacro`
preference for schema and import compatibility, but current playback ignores
that bit and the editor no longer shows a repeat checkbox. Repetition does not
weaken foreground, timing, injection, or emergency-stop guards. Physical input
or focus loss enters a non-injecting pause until the safe verified conditions
recover; it is not permission to inject into the background.

## Explicit Play and the current batch

Template launch and macro playback are separate actions:

1. **Run template** verifies accounts, launches clients, discovers stable
   windows, and restores saved positions or builds the cascade.
2. SessionDock resolves valid assignments against the exact verified process
   identities and window handles launched in that batch.
3. If assignments remain, the compact height-to-content controller opens with
   **Play** and a speed selector. It remains resizable for Windows text scaling.
4. SessionDock waits. There is no template-playback countdown and no autostart.
5. The user inspects the clients, chooses a speed, and selects **Play**.

After **Play**, every valid assignment repeats in full cycles until **Stop**.
While idle the controller shows **Play** and speed; while running, **Play**
becomes **Stop** and cancels the loop safely. Closing the controller also stops
an active loop before hiding the window; **Controller** on Home or in Settings
reopens it. Physical mouse/keyboard input, held input, or loss of the verified
foreground condition pauses every macro mode without injecting and shifts the
timeline. Playback resumes only when the safe input state and exact leased
Roblox foreground target recover. The batch-launch Cancel button does not stop
macro playback. The recording dialog's short countdown is independent and does
not change these playback rules.

The speed selector offers `0.25x`, `0.5x`, `0.75x`, `1x`, `1.25x`, `1.5x`,
and `2x`. `2x` is the supported controller maximum; older stored values above
it normalize to `2x` instead of selecting a rate that can overrun dense mouse
input streams. A selection applies to the next loop and is written to the catalog-wide
preference, so it survives later
batches and app restarts. Changing it does not edit a template or its recorded
coordinates.

The controller may stay above the batch so its two controls remain reachable;
that does not make the Roblox clients themselves topmost.

**Assign macros** provides a runtime shortcut for individual-client macros:

1. Choose one saved client macro.
2. Focus exactly one verified Roblox window listed for the current batch.
3. SessionDock revalidates the process and handle, then assigns the macro to
   that account in this runtime context.
4. Repeat to reuse or vary macros across clients, or remove an assignment.
5. Select **Play** in the controller when ready.

These focus assignments do not modify the template catalog. A successfully
launched new batch or an app restart replaces them. Use the template editor for
persistent assignments.

## Recording boundary

ExactWheel recording:

- requires a nonzero, verified foreground target for client recording;
- captures mouse movement/buttons/wheel and keyboard up/down events;
- ignores ExactWheel's own marked injected input;
- uses bounded event and duration limits; and
- fails rather than silently dropping overflowed events.

Press the configured global stop-recording keybind (F8 by default) to finish
without activating SessionDock or taking focus from Roblox. Configure F6–F11,
optionally with Ctrl, Alt, and/or Shift, from the macro library settings.
SessionDock claims the stop atomically and removes the complete
terminal hotkey chord from the saved event stream. The visible Stop button
remains an accessibility fallback for whole-layout recording.

An individual-client recording admits events only while its exact verified
Roblox window is foreground. Moving away pauses admission and returning resumes
it; the click used to refocus is suppressed as one complete gesture. Admitted
key/button transitions remain balanced. If authorization is lost while an
admitted input is held, or recording stops with unbalanced held input,
SessionDock rejects the result instead of writing a dangerous macro.

The file can still contain sensitive keystrokes. Never record credentials,
codes, chat, payments, account settings, or personal information. Give every
macro a purpose-specific name and keep recordings short.

### Manage the macro library

Open **Macros** on Home for routine maintenance. The same library is available
from Settings:

- select a macro and choose **Rename** to change only its display name; its
  stable content ID, hash, type, attribution, timing, and recorded payload stay
  unchanged;
- choose **Remove** only for an unreferenced macro. SessionDock blocks removal
  and names the templates to edit when any per-client, shared, or whole-layout
  assignment still references it; and
- confirm removal explicitly, then choose **Save settings**. SessionDock first
  commits the catalog change, then verifies and deletes the content-addressed
  recording only when no remaining macro definition uses that file. Shared or
  changed payloads are retained, and a cleanup failure never rolls back the
  saved catalog.

## Playback safety

Default playback is fail-closed:

- it refuses to start while unrelated physical input is held;
- new physical mouse or keyboard activity, held input, or verified focus loss
  pauses every macro mode without injecting; playback resumes on a shifted
  timeline only after the safe input state and exact leased Roblox foreground
  target recover;
- each run retains the original verified Roblox process lifetime and exact
  process/window mapping, and its pre-dispatch guard stops if that identity or
  foreground target is lost or reused;
- stale input is not burst after a long scheduling stall;
- invalid timelines, timer failure, and partial input injection stop playback;
  and
- cleanup attempts to release only the keys/buttons that SessionDock
  successfully injected.

Cleanup reports whether Windows accepted every release. A cleanup failure is a
real failure state, not a successful completion. Keep the run supervised and
use physical mouse/keyboard input as the intervention signal.

## Coordinate scaling

### Client-relative

For every recorded pointer event inside the source Roblox client rectangle,
ExactWheel maps each axis proportionally into the destination client rectangle.
The mapping preserves endpoints and uses integer rounding. A recorded event
outside the source client is rejected; it is never clamped into an unrelated
control.

This supports a 4K-to-1080p move when the corresponding Roblox control remains
at the same relative location. It cannot compensate for a different Roblox UI
layout, language, aspect ratio, UI scale, modal, menu state, or experience
revision. Test the actual destination device.

### Whole desktop

Virtual-desktop scaling maps coordinates across the full source and destination
virtual rectangles only when explicitly selected by the calling workflow.
Monitor-normalized scaling requires the same monitor count and maps within the
corresponding monitor. An event in a virtual-desktop gap is rejected.

Prefer a saved window layout plus client-relative macros over a whole-layout
macro when moving between a 4K desktop and a 1080p laptop.

## Portable package transfer

**Export or import data** creates a versioned `.sessiondock` ZIP from an
explicit selection rather than copying `catalog.json`. Eligible content is:

- selected templates, including their normalized placements and account
  references;
- every macro dependency of those templates plus any separately selected
  macros;
- matching eligible named-destination dependencies plus any separately selected
  public place destinations; and
- selected launch presets.

Selecting a template closes over its referenced macros and shows those
dependencies in the export review. When an eligible named destination has the
same resolved public value as a selected template slot, that dependency is
included too. Private-server and tracked-server template destinations are
omitted and counted. Macro payloads are stored as exact, content-addressed bytes
with a SHA-256 in the manifest; export/import never rescales or rewrites an
`.ewmacro` file. Account references use only the Roblox user ID for matching on
the destination device. Local account keys and paths, source placement monitor
IDs/device names, sign-ins, browser profiles, cookies, tokens, authentication
tickets, usernames, private-server links/codes, server JobIds, integrations,
and logs are excluded.

A macro containing keyboard events is not automatically safe to share: those
events can encode typed secrets. Export lists every selected keyboard-bearing
macro and requires a separate opt-in. Import repeats the warning and requires a
separate acknowledgement before applying the reviewed plan.

Import validates the supported package/manifest version, ZIP entry paths and
duplicates, bounded entry/count/size limits, manifest dependencies, and every
macro SHA-256 before preparing a mutation. The plan identifies name conflicts,
identical macros already present, unmatched Roblox user IDs, templates skipped
for missing accounts or conflicts, and assignments removed for compatibility.
Only applicable reviewed items are committed, and a persistence failure keeps
no partial catalog update.

Portable placements keep normalized monitor-work-area rectangles and rebuild
local monitor identity on the importing device. Client-relative macro bytes
remain exact; their source client rectangle is mapped to the exact verified
destination client only at playback. Whole-layout macros preserve their
recorded display geometry. Before retaining a whole-layout assignment, import
compares monitor count, virtual-desktop aspect ratio, and normalized monitor
arrangement with the current topology. An incompatible assignment is left
unassigned for repair rather than transforming the payload or guessing a map.

The previous bounded metadata JSON action remains available for account
appearance, matched account order, and pinned public favorites. It is a legacy
compatibility path, not a template or macro package.

## Playback performance behavior

One controller run owns one ExactWheel playback session. The high-resolution
timer, cancellation and intervention wait handles, verified process leases,
process basenames, and immutable window classes are reused while each focus and
dispatch is still live-validated. The blocking timing loop stays on its
dedicated worker, and SessionDock runs the surrounding playback orchestration
off the WPF UI thread. Progress sent back to the interface is coalesced to at
most four updates per second without repeated UI Automation announcements.

One low-level physical-input intervention hook is retained for the complete
macro run instead of being recreated for every client or complete loop. The
hook takes one physical-input baseline when the run starts and then tracks key
and button transitions. This removes hook-thread and full key-state setup churn
even for a very short one-client macro. ExactWheel checks the monitor before
each serial playback and fails closed if its thread exits; Windows does not
provide a direct liveness query for silent hook removal.

Canonical recordings are validated once and then reused without another sort
or copy. Input injection reuses its fixed one- and two-event native batches.
Recording capture grows through small pooled segments instead of reserving the
500,000-event safety ceiling at Start. The final immutable macro array is still
created once when recording stops, and the pool is cleared before return.

Controller readiness resolves catalog and assignment metadata only. Template
launch preflight transfers its already verified source cache into the first
**Play**, so preflight and the first cycle do not deserialize the same artifact
twice. A source introduced after launch is deserialized once when its playback
run first needs it. Exact Roblox target validation still happens at playback.
SessionDock never creates a full transformed event array per destination:
small immutable coordinate plans map only the mouse events that the scheduler
actually dispatches. Up to 1,000,000 source events remain in compact managed
arrays. Additional unique sources are copied once into immutable,
delete-on-close memory maps and read through reusable 512-event pages; later
loops neither deserialize the artifact again nor rebuild a full event array.
The run admits at most 128 client sources plus one whole-layout source, and
also enforces a 256 MiB aggregate mapped-byte ceiling. Managed event memory is
therefore hard-bounded, with small O(n) page and geometry state instead of
O(events × clients) transformed copies. Hot source and coordinate-plan cache
hits allocate no managed memory.

An unavailable client does not pay its complete focus and trust cost on every
short cycle: the base retry backoff grows exponentially from 250 milliseconds
to five seconds and resets after success. Large failed groups retry in waves of
at most eight clients separated by 50 milliseconds, so wave placement can add
at most 850 milliseconds to the base delay for 128 clients plus the
whole-layout target. If every target is temporarily unavailable, the loop
sleeps until the earliest retry instead of stopping or rescanning at the
10-millisecond floor. If healthy targets still
complete, playback continues and deferred targets add only lightweight O(n)
deadline checks per completed cycle. A sticky failed lease is disposed and
reacquired when its retry becomes due; whole-layout lease and focus failures
use the same transient policy. Terminal identity and artifact failures stay
suppressed. The 10-millisecond floor remains a minimum duration for a healthy
complete cycle, not a fixed delay after successful playback.

Within one discovery, layout, or playback operation, clients using the same
canonical Roblox path share the first successful forced Authenticode result.
The file's SHA-256, WinVerifyTrust decision, and signer certificate are all read
from the same non-write-shared open handle; path metadata alone is never a trust
proof. A changed file therefore cannot inherit the cached result. Exact
path-and-process-token proof still refreshes on a five-second per-target
schedule when that target is used, while retained-process liveness, exact HWND
ownership, foreground, pointer-target checks, and final event authorization
remain adjacent to dispatch. Deterministic scalability tests cover 1, 8, 32,
100, and 128-client cache/trust/retry paths.

Joined-server attribution uses one 500-millisecond snapshot for all concurrent
batch callers. The scanner follows up to 128 recent Roblox logs incrementally,
keeps partial-line and join state between captures, and divides one fixed 4 MiB
read budget fairly across the selected files. A capture reads no payload bytes
from an unchanged log. Its latest `(user, place)` index makes each caller lookup
constant-time without discarding a valid target merely because later user IDs
appeared in the same log.

When no browser work is active, macro playback asks the hidden WebView2 to
suspend. Playback waits at most one second for that request; on a slower laptop
it then starts while the request continues in the background. A late success
stays suspended until Stop, and every cancellation, failure, browser resume,
or shutdown race releases the suspension gate exactly once. WebView2 disposal
is not used as a shortcut because it would invalidate the account-session
token and require a complete asynchronous sign-in/browser reconstruction.

## Versioning and compatibility

- Template catalog schema: version 3; versions 1 and 2 are supported legacy
  inputs and migrate the recording-stop keybind to F8.
- Individual template schema: version 1.
- ExactWheel macro format: version 1 in the current component manifest.
- HandleScope API selection: Automatic, v2, or v1; this is independent from
  template/macro versions.
- Existing per-account custom destinations and legacy batch presets remain
  valid. Named destinations add reusable assignments without requiring either
  older launch mechanism to be converted.

When a valid catalog v1 or v2 is read, SessionDock conservatively infers any
omitted v1 macro kind from a kind-specific content ID or unambiguous template
use, adds the default `1x` speed and F8 recording-stop keybind where absent,
and normalizes the in-memory catalog to v3. Ambiguous or
unused byte-only definitions retain the historical client kind and therefore
fail a later whole-layout kind check instead of being guessed. The next
explicit catalog save writes v3; merely reading the file does not rewrite it.

Unknown or malformed schemas fail closed. Normalization bounds counts, strings,
dimensions, event metadata, duration, hashes, and monitor data. It preserves
syntactically valid stale references for repair and never deletes a macro file
simply because no current template references it.

To edit a template, open **Templates** on Home, select one template, and choose
**Edit**. The
editor preserves its stable template ID,
existing slot IDs, account keys, unchanged destinations, saved placements,
legacy preset marker, and valid macro references. A per-slot destination may be
changed in the editor without changing the named-destination library or
recapturing that slot's saved position. The edit remains isolated until both
**Save template** and the focused **Save settings** are selected. Use **Save
current session** for a new live-window capture.
Unavailable or wrong-kind macro references remain visible for repair and must
be replaced or removed before the edit can be saved.

Legacy batch presets remain separately stored, labeled launch-only choices.
SessionDock adapts one in memory to a cascade/no-macro run; it does not silently
insert or rewrite it as a session template.

## Local file layout

All paths are below `%LOCALAPPDATA%\SessionDock` for the current Windows user:

```text
SessionDock\
|-- Templates\
|   |-- catalog.json
|   `-- catalog.backup.json
|-- Macros\
|   `-- <safe local macro files>
`-- onboarding-state.json
```

`catalog.json` is strict, bounded UTF-8. Writes use a temporary file and atomic
replacement; the prior valid catalog becomes `catalog.backup.json`. If the
primary is corrupt, SessionDock can read the valid backup and marks the catalog
as recovered. Replacing a corrupt primary requires an explicit repair path.

Template storage rejects reparse-point directories/files. Safe macro filenames
cannot contain traversal or reserved Windows device names. The catalog stores a
SHA-256 for each macro definition so a caller can verify content before use.

Macro payloads are intentionally separate from catalog metadata. Removing a
template does not auto-delete them. This prevents silent data loss but means the
user must review old macro files deliberately.

## Test before release or regular use

At minimum, validate this matrix on real Windows hardware:

For an ordered device pass, follow the
[focused regression checklist](GETTING_STARTED.md#13-run-the-focused-regression-checklist).

| Scenario | Expected result |
| --- | --- |
| First run | Get Started highlights the first-launch workflow once and can be skipped or replayed; Advanced remains a separately launched optional tour in Settings. |
| Two clients, one monitor | Stable windows keep minimum size and configured client-area reveal patches remain clickable. |
| Another application is above the cascade | Roblox clients remain normal non-topmost windows and do not force themselves over it. |
| Work area too small | Layout clamps or reports failure; no window is silently lost off-screen. |
| Roblox moves a startup window | Position is reapplied once and accepted only after geometry settles. |
| Saved monitor disconnected or logical name reused | A stable-ID placement uses the current primary; only a legacy placement may fall back through device name and saved index. |
| Saved 4K layout on 1080p | Rectangles scale into the destination work area. |
| Client macro 4K to 1080p | Pointer reaches equivalent relative control with matching UI state. |
| Different aspect/UI scale | Mismatch is detected during supervised test; do not assume success. |
| Template run with assignments | Controller opens after layout; no macro starts until Play, then every assignment repeats in full cycles until Stop. No playback countdown appears. |
| Windows text scale increased | The compact controller sizes to its content, remains resizable, and does not crop vertically. |
| Each supported speed from 0.25x through 2x | Dense input plays without crashing or dispatching a stale burst. |
| Speed changed and app restarted | The safe global controller speed is restored; template data is unchanged. |
| Recording stopped with the configured keybind | Recording ends without focusing SessionDock and the complete terminal hotkey chord is absent from the saved macro. |
| Focus assignment | Only the selected current-batch client changes; the catalog and next batch stay unchanged. |
| Named destination reassigned or removed | An account has at most one named assignment; direct edits detach it, and deleting the name retains the resolved value as a custom destination. |
| Macro renamed | Only its display name changes; templates keep their stable macro reference. |
| Referenced macro removal attempted | Removal is blocked and the referencing templates are named. After an unreferenced removal is confirmed and Save settings succeeds, the exact unshared payload is deleted; a changed, shared, or unverifiable payload is retained. |
| Edit existing template | Stable template/slot IDs remain; outer Save settings is required. |
| Catalog v1/v2 opened and saved | It reads safely, defaults speed to 1x and the stop keybind to F8, and the explicit save writes v3. |
| New physical input during any macro | Playback pauses without injection and resumes on a shifted timeline after safe input and verified focus recover. |
| Whole-layout target loses foreground | Playback pauses without injection; refocusing an exact leased Roblox window resumes on a shifted timeline. |
| Per-client target loses foreground | Playback pauses before the next dispatch and resumes only after the exact leased client is safely foreground again. |
| Scheduler stall | Playback stops rather than sending a stale burst. |
| Corrupt catalog | Valid backup is recovered; corrupt data is not silently overwritten. |
| Missing macro/account | Reference remains visible for repair; no different item is substituted. |
| Scripts disabled | Included SessionDock flow still runs without a HandleScope script. |

Run the complete repository gate as well:

```powershell
dotnet restore .\SessionDock.slnx --locked-mode
.\scripts\Build.ps1 -Configuration Release -Runtime win-x64 -CI
```

## Release and provenance boundary

`SessionDock.ExactWheel/exactwheel-provenance.json` records ExactWheel as a
repository-native component under the root MIT license. It is intentionally
tagless and records the full source commit,
`1e3b6bfbbc5a2335af6c863cdcc32e2b70c7ffc1`, an exact 14-file implementation
and dependency-lock count, 216,308 canonical source bytes, and canonical
inventory SHA-256
`83ef063c36a990a0322e867d67e9a0db151c88424af17b5bed1eada1901b6232`.
The renamed current build definition is pinned separately as Git blob
`07fe8f9ec14088750f6d2a0d835c86b678a0f76e` and SHA-256
`76e3be05eea91e5526965d05da043219da67afdc52a423b07707b63fdfaa1841`.

The manifest and `scripts/Verify-ExactWheelReleaseProvenance.ps1` together pin
every inventory path, Git blob, byte count, and SHA-256 identity. The verifier
rejects a missing or extra source file, worktree/blob drift, an
inventory-summary mismatch, a different license, an invented source tag, or a
nonmatching commit tree. A shallow CI checkout can still verify the exact
checked-out Git blobs and their independent SHA-256 hashes; when the pinned
commit object is available, the verifier also checks every entry directly
against that commit and proves it is an ancestor.
`releaseBlockedPendingLicense: false` means only that this source/license gate
has complete evidence. It does not bypass update-descriptor verification,
malware response, packaging, manual hardware, or reviewer approval gates.

This document describes behavior under development. It does not declare that a
public build has shipped or that a release has been approved.
