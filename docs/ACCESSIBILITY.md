# Accessibility verification

SessionDock uses native WPF controls and UI Automation semantics wherever an
interactive choice exists. Segmented choices are radio-button groups, status
changes use live regions with duplicate suppression, and errors use assertive
announcements. Visible text, icons, and state labels accompany color.

Automated tests cover resource contrast, keyboard selector semantics,
accessible names, localization-key parity, live-region metadata and duplicate
suppression, and a real off-screen WPF automation peer. Automation cannot prove
the quality of Narrator speech, focus order, high-contrast rendering, text
scaling, or physical multi-monitor behavior, so complete the matrix below for a
release candidate.

## Manual release matrix

Record the Windows version, display scale, theme, SessionDock language, build
commit, result, and any issue link for each run.

| Area | Setup and action | Expected result |
| --- | --- | --- |
| Keyboard-only navigation | Disconnect the mouse. Traverse the main window and every dialog with Tab and Shift+Tab; activate with Space or Enter; dismiss cancellable dialogs with Escape. | Focus is always visible, follows a logical order, never becomes trapped, and returns to the initiating control when a dialog closes. |
| Segmented choices | Focus Launch/Recent, Experience/User, and All/Public/Private. Use each arrow key. | Each group exposes one selected radio item, wraps within its own group, updates the visible panel, and never changes another group. |
| Search | Use Ctrl+F in Launch and Recent, enter a query, clear it, and return to the account list. | Focus moves to the relevant search field; result counts and empty states are understandable without color; account reordering resumes after clearing account search. |
| Narrator names and roles | With Narrator running, inspect sidebar icon buttons, account actions, segmented choices, destination input, Recent actions, and dialog buttons. | Every control has a useful name, standard role/state, and concise help text where the consequence is not obvious. No selector is announced as an unrelated push button. |
| Live status announcements | Trigger a successful and failing main launch check, invalid destination, metadata export/import validation, Windows-link registration refresh/failure, batch validation, sound validation, HandleScope refresh/failure, diagnostics copy/export, and running-client refresh/close failure. | Each meaningful change is announced once. Informational progress is polite; warnings and errors interrupt assertively. Re-rendering identical state does not repeat it. Visible text matches the announced outcome. |
| Private-link privacy | Open an official private-server link through the optional Windows handler while Narrator is active; switch language before confirming. | The destination is described as private, but the private code is never displayed or spoken. The flow still requires account selection and a separate confirmation. |
| High contrast | Enable Windows High Contrast before startup, then repeat main navigation, validation, confirmation, and disabled-control checks. | System high-contrast colors remain legible; focus and selection are visible; no meaning depends only on the SessionDock dark/light palette. |
| Dark and light themes | Check main and all dialogs in both themes, including hover, pressed, disabled, selected, success, warning, and error states. | Text and icons remain legible and state distinctions remain clear. |
| Text scaling | Test Windows text size at 100%, 150%, and 200%; resize the main window and scroll every dialog. | Text is not clipped or overlapped, controls remain operable, horizontal scrolling appears only for exact code/JSON previews, and primary actions stay reachable. |
| DPI and multiple monitors | Test 100% and mixed-DPI displays. Move/resize/maximize the main window on a secondary display, close, relaunch, disconnect that display, and relaunch again. | Size/state restore on the saved monitor when available; otherwise the window is clamped visibly onto the primary work area. Dialogs remain on the active monitor. A minimized window never relaunches minimized. |
| Language switching | Switch live among System default, English, and Dutch on the open main window. After each switch, open every dialog and exercise its status and confirmation paths. | The main window updates without restart; each newly opened workflow uses the selected language for static labels, accessible names, and tooltips. Some runtime confirmations and detailed technical content, such as exact JSON and structured import plans, may use the complete English fallback. Controls do not resize into unusable layouts. |
| Reduced input precision | At 150% scale, use touch or a coarse pointer for sidebar and account Add/Edit/Remove actions. | Primary icon targets are comfortably hittable, do not overlap, and expose the same accessible names as keyboard navigation. |

## Reporting

Accessibility issues are regular product defects. Include the control or flow,
input method or assistive technology, Windows version, display scale/theme,
language, expected behavior, actual behavior, and a minimal reproduction. Do not
include Roblox cookies, tickets, private-server codes, account-slot keys, local
paths, or unredacted settings in screenshots or reports.
