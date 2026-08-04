# Getting started with integrated SessionDock

This guide covers the current source-tree workflow that combines SessionDock,
the included HandleScope component, ExactWheel macros, window layouts, and
session templates.

> A published release has these features only when its own notes say so. Source
> and test builds are not release announcements.

> **Distribution hold — 2026-08-04:** the current latest release is a
> zero-asset security-hold record. Download nothing while this hold is active.
> The future flow below applies only after a reviewed release explicitly states
> that it lifts the hold and has passed separate laptop validation.

## Before you start

You need:

- Windows x64 in a normal, non-administrator desktop session;
- Roblox Player installed for the same Windows user;
- one approved SessionDock portable folder or validated source build; and
- a harmless Roblox destination for the first end-to-end test.

You do **not** need:

- a separate HandleScope download or `Install-HandleScopeApi.ps1`;
- a PowerShell execution-policy bypass;
- a separate macro application or ExactWheel installation;
- a scheduled task, service, or sign-in autostart entry; or
- an administrator PowerShell window.

## 1. Get and verify SessionDock

SessionDock is permanently distributed as a transparent, unsigned portable
ZIP. There is no Setup executable. After a reviewed release explicitly lifts
the hold:

1. Open only the canonical
   [`Makmatoe/SessionDock` GitHub Releases page](https://github.com/Makmatoe/SessionDock/releases).
2. Download `SessionDock-win-x64-Portable.zip` and `SHA256SUMS.txt` from that
   same release. A Discord message may link to GitHub, but never download a
   SessionDock binary from Discord.
3. Confirm the browser address is `github.com/Makmatoe/SessionDock` and follow
   the [portable ZIP verification steps](UPDATES.md#verify-the-portable-zip).
4. In File Explorer, select **Extract All** and choose a new folder. Keep all
   extracted files together.
5. Run `SessionDock.exe` as your normal Windows user, not as administrator.
6. Complete the first-launch Get Started tutorial.

Unsigned SessionDock may show **Unknown publisher** or a reputation warning.
Proceed through a normal warning only after the hold is explicitly lifted, the
GitHub source and hash match, and Windows reports no named threat. A named
malware detection is always a hard stop. A matching checksum or attestation
does not make detected bytes safe. Never disable Defender, restore or allow a
detected file, add an exclusion, remove download-zone metadata, change
PowerShell execution policy, or weaken device policy to make SessionDock run.

## 2. If the laptop says scripts are disabled or virus scan failed

`Install-HandleScopeApi.ps1 cannot be loaded because running scripts is
disabled` is a PowerShell execution-policy block. PowerShell rejected the file
before its parameters were processed. Appending `-ExecutionPolicy Bypass` to
the script call cannot change that, and typing `-ExecutionPolicy Bypass` by
itself is not a command.

The integrated SessionDock path avoids the script entirely. Do not weaken the
laptop's execution policy.

**Virus scan failed** is a browser/download-pipeline status. It may come from
the browser, Windows Attachment Manager, antivirus, or managed-device policy.
It is not a positive malware result by itself. The same file working on another
PC, or once on the laptop, suggests a device-specific condition but does not
prove that the file is trustworthy.

Recover safely:

1. Delete the incomplete download.
2. Check the canonical release notes. Download nothing while the distribution
   hold is active.
3. After a reviewed release explicitly lifts the hold, download only its
   portable ZIP from GitHub Releases and verify SHA-256 before extracting it.
4. Check **Windows Security > Virus & threat protection > Protection history**.
5. If the laptop is managed, give its administrator the canonical URL and
   checksum.
6. Do not disable antivirus, SmartScreen, execution policy, or application
   control, and do not fall back to a standalone HandleScope script.

## 3. Complete the first-launch Get Started tour

1. Open SessionDock. On a fresh profile, the **Get Started** tour begins once
   Home is ready.
2. Follow each highlighted button. The tour first opens account management,
   highlights **Add**, and explains the isolated browser sign-in. It then opens
   named destinations and highlights the unique name, Roblox destination,
   account checkboxes, and **Save destination** controls.
3. Continue through Home's visible controls. The four large action cards are
   **Launch accounts**, **Run template**, **Macros**, and **Templates**. The two
   smaller setup shortcuts directly below them are **Destinations** and
   **Manage accounts**. This keeps setup close without making it compete with
   the actions used every session.

4. Finish or skip **Get Started**. Open **Settings** whenever you want to launch
   it again with **Start Get Started tour**.
5. For the optional technical walkthrough, select **Start Advanced tour** in
   Settings. Its highlighted buttons cover **Client window layout**, **Macro
   library and recording**, **Templates and batch setup**, **Export or import
   data**, **Current batch macros**, the compact macro controller, and the
   rarely needed Advanced workspace. It is separate from Get Started and never
   begins automatically merely because Get Started finished.
6. Under **Client window layout**, select **Open automation settings**, then
   review:

   - target Roblox window width and height;
   - minimum width and height;
   - horizontal and vertical reveal spacing for the cascade;
   - preferred monitor; and
   - whether normal batch launches are arranged automatically.

Get Started and Advanced completion are stored separately from templates and
account settings. Replaying either highlighted-button tour does not reset
anything or start a launch, recording, or playback action.

## 4. Set up accounts and named destinations

On Home, open **Manage accounts**. For each account:

1. Select **Add account**.
2. Sign in only on the official Roblox page in that account's isolated browser.
3. Verify the shown Roblox identity.
4. Add a clear label and color.
5. After verification or canceling the browser, SessionDock returns to
   **Manage accounts** and keeps the relevant account selected. Return to Home
   when the account list is ready.

SessionDock attributes a launched process to the account that created it. That
attribution is required before a running window can be saved into a template or
used as a per-client macro target.

Create a reusable destination next:

1. Return to Home, open **Destinations**, and select **New destination**.
2. Enter a unique, recognizable name and a valid Place ID, official Roblox
   link, private-server link/code, tracked Job ID, or supported user target.
3. Check each account that should use it, then select **Save destination**.
4. Reopen the entry and verify its value and account checkboxes.

An account can be assigned to only one named destination. Assigning it to a new
one moves the named assignment and updates that account's launch value. A
direct per-account destination edit detaches it from the named entry. Deleting
the named entry does not blank existing assigned accounts: their current value
remains as a backward-compatible custom destination.

## 5. Launch accounts and inspect the cascade

1. Select **Launch accounts** on Home.
2. Choose two or more test accounts.
3. Review the destination and delay.
4. Start the batch and leave the mouse alone while clients open.
5. Wait for SessionDock to find one stable, visible main window for each
   verified client. Restore minimized clients and leave fullscreen first; an
   ambiguous main window is not guessed.
6. Inspect the staircase:

   - the first window starts at the top-left of the selected work area;
   - every later window moves down and right;
   - each window keeps a human-sized exposed strip; and
   - the size remains inside Roblox and monitor constraints.

7. Click each exposed strip. Confirm it focuses the intended client without
   accidentally clicking inside another Roblox client.
8. If the layout is too tight, increase reveal spacing or reduce the target
   window size in Settings and repeat.
9. Put an unrelated application in front, run the layout again, and verify it
   stays above the Roblox group. SessionDock must not make Roblox always-on-top.

SessionDock waits for each moved window to remain stable and retries its
position once if Roblox moves it during startup. It can continue a long
staircase on other monitors without requesting a size below the configured
minimum. If Roblox realizes a different size, SessionDock uses and reports the
stable realized bounds. If another layout group must reuse monitor space, the
result is reported. When the preferred staircase monitor is unavailable,
SessionDock uses the first usable monitor in its current deterministic order.
The layout demotes any stale Roblox topmost state left by an older build and
changes only the relative order of verified Roblox windows without activating
or raising the group over unrelated applications.

## 6. Record a safe test macro

1. Use a test account and a reversible in-game action.
2. Put the intended Roblox client in the foreground.
3. On Home, open **Macros**, then select **Record macro**.
4. Choose a client-relative recording unless the action truly spans the whole
   desktop.
5. Start recording, perform one short action, then press the global
   stop-recording keybind (F8 by default). It stops capture without focusing
   SessionDock, and the complete stop-key chord is removed from the saved macro.
   Change it in the macro library's settings; supported base keys are F6-F11,
   optionally combined with Ctrl, Alt, and/or Shift.
6. Name the macro for its purpose and target state.
7. Replay at least one complete cycle under supervision, then select **Stop**,
   before assigning it to a template.

Do not record passwords, authentication codes, payment actions, private chat,
account changes, moderation actions, or anything irreversible. ExactWheel
macro files can contain typed key events and timing; treat them as private.
For an individual-client recording, SessionDock records only while the exact
verified Roblox client is foreground. Switching away pauses capture and
refocusing that client resumes it; the focus click is excluded. Release every
key and mouse button before switching. If focus is lost while an accepted input
is still held, SessionDock rejects the recording at Stop rather than saving an
unbalanced event stream. Whole-layout recording remains global.

To manage saved macros later, open **Macros** on Home. The same library is also
available from Settings.
Select a macro and choose **Rename** to change only its display name; its
content ID, hash, type, assignments, and recorded payload remain stable. Choose
**Remove** only for unreferenced catalog metadata. Removal is blocked and names
the templates to edit while any per-client/shared/whole-layout reference
remains. An allowed removal defaults its confirmation to No and changes only
the draft until you choose **Save settings**. After that catalog commit,
SessionDock deletes the exact content-addressed payload only when no other
macro definition uses it. A changed, shared, locked, or unverifiable payload is
retained and cleanup failure does not undo the catalog save.

## 7. Save the session as a template

1. Leave every client open, restored, and attributed.
2. Put every window where you want it and confirm that it overlaps a current
   monitor work area.
3. On Home, open **Templates**, then select **Save current session**.
4. Enter a name and confirm the delay.
5. Review every account slot's resolved destination. The template stores those
   values independently of the named-destination library; edit a slot now when
   the saved session should launch somewhere else.
6. Choose a layout:

   - **Saved positions** stores each monitor-relative rectangle.
   - **Clickable cascade** recalculates the staircase on every run.

7. Choose a macro mode:

   - **No macro**;
   - **Per client**, with different macros or **No macro** per client;
   - **Shared across clients**, using one transformed client macro on the
     clients you select; or
   - **Whole layout**, using one desktop-layout macro.

8. Every assigned Per client, Shared across clients, or Whole layout macro
   repeats in full cycles after **Play** until **Stop**. There is no separate
   repeat option. Keep the loop supervised: physical input, held input, or loss
   of verified focus pauses injection until safe conditions recover; replacing
   the batch or closing SessionDock cancels it.
9. Select **Save template**.

If SessionDock cannot capture every requested saved position, fix the missing
window or choose **Clickable cascade**. Minimized and wholly off-screen windows
are rejected; SessionDock does not invent coordinates.

## 8. Run and verify the template

1. Close the test Roblox clients.
2. Select **Run template**.
3. Choose the new template.
4. Review every account's saved destination plus the account count, layout,
   macro mode, and delay.
5. Resolve every missing account before continuing.
6. Start the template.
7. Wait for account launch and stable window placement to finish.
8. If there are valid assignments, the compact height-to-content macro window
   opens with only **Play** and speed. It remains resizable for Windows text
   scaling. No macro starts automatically, and template playback has no
   countdown.
9. Choose a supported speed from `0.25x`, `0.5x`, `0.75x`, `1x`, `1.25x`,
   `1.5x`, or `2x`, then inspect every client. The speed applies to the next
   **Play**, is saved globally, and is reused for later batches and app starts.
10. Select **Play** only when the batch is ready. Every valid assignment now
    repeats in full cycles until you select **Stop**.
11. Intervene immediately if the target, Roblox UI, display topology, or
    account state differs from the recording.

The controller remains available after you stop playback, so you can select
**Play** again. Closing the controller stops an active loop before hiding it;
use **Controller** on Home or under Settings to reopen it. The three-second
countdown used when *recording* a macro is separate; there is deliberately no
countdown before controller playback. The small controller can remain above the
clients for access; the Roblox clients themselves remain normal non-topmost
windows.

To change per-client assignments for this launched batch:

1. Select **Assign macros** on Home.
2. Choose one saved individual-client macro.
3. Click or otherwise focus exactly one listed Roblox client. SessionDock
   assigns the selected macro only after re-verifying that focused window.
4. Repeat for each client. You may reuse the same macro on several clients or
   remove an assignment.
5. Close the assignment window, reopen **Controller** if needed, and select
   **Play**.

These changes belong only to the exact verified processes and windows in the
current launched batch. A new successful batch or app restart replaces them;
the saved template is unchanged.

Existing batch presets can appear as legacy launch choices. They keep their
original account keys and delay; they are not silently converted into a macro
template.

## 9. Edit, migrate, or repair a template

For a permanent template change:

1. Open **Templates** on Home.
2. Select exactly one saved template and choose **Edit**.
3. Change its name, launch delay, per-slot destinations, layout mode, or macro
   assignments.
4. Select **Save template** in the editor.
5. Back in the template library, select **Save settings**. Closing that focused
   dialog without saving discards the edit.

Editing preserves the stable template ID and existing client slot IDs, account
keys, unchanged destinations, saved placements, and valid macro references. A
destination edit changes only that slot's saved launch value; it does not edit
the named-destination library or recapture live windows. Use **Save current
session** in the template library to create a fresh capture when you need new
positions. An unavailable or wrong-kind macro stays visible for repair, and the
editor requires a valid replacement or a no-macro choice before it can save.
Deleting a template never deletes its macro files.

Catalog schemas v1 and v2 remain readable. SessionDock infers omitted legacy macro
kinds conservatively, adds 1x playback-speed and F8 recording-stop defaults,
and uses schema v3 in memory. The next explicit template/settings/speed save
writes v3. Unknown or
malformed schemas fail closed. If the primary catalog is corrupt but the backup
is valid, SessionDock reports recovery and requires an explicit save/repair
path before replacing the primary.

## 10. Export or import selected data

To move only intended automation data to another SessionDock device:

1. Open **Export or import data** and choose **Export selected data**.
2. Select templates, macros, public destinations, and launch presets. Selecting
   a template automatically includes its required macros and any matching
   eligible named destination in the reviewed package. A dependency checkbox
   may remain unchanged, especially for a keyboard-bearing macro, so treat the
   package review and manifest as the effective contents rather than assuming
   the template is metadata-only.
3. Review the manifest and exclusions. Private-server destinations are never
   eligible. Before package review, the named-destination exclusion count is
   explicitly library-wide; the package review then reports the exact omitted
   template-slot and named-destination values for this export.
   If any selected macro contains recorded keyboard input, inspect it as
   potentially sensitive and select the separate acknowledgement only when you
   intend to include it.
4. Choose **Export reviewed package**, then save the versioned `.sessiondock`
   ZIP. SessionDock copies macro files byte for byte and records their SHA-256.

To import, choose **Import selected data**, open the package, and wait for local
validation. Review every applicable item, name conflict, already-present macro,
missing Roblox user-ID match, skipped template, and whole-layout assignment
left unassigned. Confirm the keyboard warning when applicable, then explicitly
confirm the complete plan. Archive version, paths, hashes, dependencies, and
bounded entry/count/size limits are checked before anything changes.

The package never carries sign-ins, cookies, tokens, tickets, usernames, local
account keys or paths, private-server links/codes, server JobIds, integrations,
or logs. Accounts match only by Roblox user ID and must already exist locally.
Saved placements use normalized monitor-work-area rectangles. Client-relative
macro coordinates adapt only at playback; the imported macro bytes stay exact.
If a whole-layout macro's monitor count, aspect ratio, or normalized arrangement
is incompatible, its assignment remains unassigned instead of being guessed.

Choose **Legacy metadata** when you specifically need the earlier reviewed JSON
format for account appearance, matched order, and pinned public favorites.

## 11. Test 4K-to-1080p behavior

1. On the 4K PC, save window positions and record a short client-relative
   pointer macro.
2. Copy only the intended template/macro data through an approved test process;
   never share browser-profile or account data.
3. On the 1080p laptop, use the same aspect ratio and Roblox UI scale for the
   first test.
4. Run the template while supervised.
5. Confirm window rectangles stay in the monitor work area and the pointer lands
   on the equivalent control.
6. Repeat with the UI scale or aspect ratio you actually intend to use.

Window positions are monitor-work-area fractions. Client macro coordinates are
mapped between recorded and destination client rectangles. This is proportional
geometry, not image recognition; a responsive Roblox UI can still move a
control. Whole-layout macros have stricter monitor-topology requirements.

Monitor selection is also deterministic:

- a new saved placement first uses its recorded stable monitor ID;
- if that stable monitor is disconnected, or Windows reuses its logical device
  name for different hardware, the placement uses the current primary monitor;
- a legacy placement with no stable ID tries its recorded device name, then its
  saved monitor index, and finally the current primary monitor; and
- every restored rectangle is clamped into the destination work area and to
  the configured/Roblox minimum size.

## 12. Know the stop behavior

Before every playback:

1. Release all mouse buttons and keyboard keys.
2. Keep one hand ready for physical intervention.
3. If playback is wrong, move the mouse or press a key. New physical input
   pauses every macro mode without injecting; select **Stop** to end the loop.
4. If necessary, close SessionDock or lock Windows.

ExactWheel also stops on target loss, dangerous lateness, invalid timing, or
input-injection failure. Cleanup attempts to release only inputs that
SessionDock successfully injected and reports a cleanup failure if Windows does
not accept every release.

The compact controller contains **Play** and speed while idle. During playback,
**Play** becomes **Stop**, which safely cancels the continuous loop. Closing the
controller also cancels an active loop before hiding it. The batch-launch Cancel
button does not stop macro playback. Every macro mode pauses before its next
dispatch if physical input is active or its exact verified foreground target is
unavailable. It resumes on a shifted timeline only after the safe input and
focus conditions recover. If it cannot safely regain that target, select
**Stop**, close the controller, close SessionDock, or lock Windows.

## 13. Run the focused regression checklist

Use test accounts and reversible actions throughout:

1. Record a one-client macro and stop with F8. Replay it and confirm the final
   F8 press is absent. Change the keybind, repeat, and confirm the complete new
   chord is absent too.
2. Start another client recording with all inputs released, switch to an
   unrelated application, and confirm capture pauses without recording that
   application's input. Refocus the exact client, verify the focus click is not
   captured, and finish with the global keybind. Repeat while holding an
   accepted key and confirm the recording is rejected safely.
3. Keep an unrelated application in front while arranging two or more Roblox
   clients. Confirm it stays above them, no Roblox window is always-on-top, and
   clicking one staircase patch focuses only that client.
4. Run a short client macro at `0.25x`, `1x`, and `2x`. At each speed, observe
   more than one complete cycle, confirm looping does not crash or dispatch a
   late input burst, then select **Stop**. Check the compact controller at the
   Windows text scales you use and resize it if necessary; neither control may
   be vertically cropped.
5. During per-client playback, focus an unrelated application. Confirm the loop
   pauses before another event is injected, then refocus the exact leased client
   and confirm it resumes without a burst.
6. During whole-layout playback, focus an unrelated application. Confirm input
   pauses, then focus one exact client from that launched batch and confirm the
   loop resumes without replaying the paused interval as a burst.
7. Create a named destination, assign two accounts, move one account to a
   second named destination, and restart. Delete one named entry and confirm
   its account keeps the same value as a custom destination.
8. Rename a macro and confirm its existing template assignment still works.
   Try to remove it while referenced and confirm SessionDock names the blocking
   template. After removing the reference, decline removal once, accept it the
   second time, select **Save settings**, and confirm the exact unreferenced
   recording file was deleted. Repeat with a deliberately changed disposable
   payload and confirm the changed file is retained while the catalog save
   succeeds.
9. Open a backed-up catalog from schema v1 or v2 in a development test. Confirm
   it loads conservatively, defaults speed to `1x` and the stop keybind to F8,
   and writes schema v3 only after an explicit save.
10. Export one template with a macro dependency and one keyboard-bearing macro.
    Confirm the dependency and acknowledgement appear, then import on a device
    with different client size and review every conflict or missing account.
    Verify client mapping happens only at playback and an incompatible
    whole-layout assignment stays unassigned. In a disposable copy, alter one
    macro byte and confirm SHA-256 validation rejects the package unchanged.

Do not treat this checklist or a passing source build as public-release
approval or as lifting the distribution hold.

## 14. Find local data

SessionDock stores active user data under `%LOCALAPPDATA%\SessionDock`:

- `Templates\catalog.json` and `catalog.backup.json` hold the versioned template
  catalog, resolved per-slot destinations, and recovery copy;
- `Macros\` holds ExactWheel recordings;
- `settings.json` holds account metadata, named destinations and their account
  assignments, and application preferences;
- `onboarding-state.json` holds only the tutorial version; and
- account profile directories hold isolated WebView2 sign-in data.

Uninstall does not automatically delete this directory. Macro files are not
automatically removed when a reference becomes stale. Review and back up what
you need before deleting anything manually.

Continue with [Templates and ExactWheel](TEMPLATES_AND_MACROS.md) for the data,
scaling, recovery, and test contracts.
