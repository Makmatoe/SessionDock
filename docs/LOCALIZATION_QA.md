# SessionDock localization QA

Use this checklist for `en-US`, `nl-NL`, `de-DE`, `fr-FR`, and `es-ES` before
shipping 2.7.1 or a later release. Record the build, Windows version, display
language, scale, theme, assistive technology, result, and issue link.

## Workflow matrix

- Switch languages while the main window is open. Verify the current status,
  account and Recent cards, destination validation, tooltips, accessible names,
  selector states, and Auto-Join action update without a restart.
- Check the main window and every dialog in normal, empty, loading, success,
  failure, cancellation, and retry states. Include account edit/removal,
  Batch and Retry failed, Running clients, release notes, About/diagnostics,
  portable-package and legacy-metadata export/import, integrations, HandleScope, Windows link handling,
  updates, sounds, Auto-Join, both guided tutorials, all four Home action cards,
  the Destinations and Manage accounts shortcuts/pages, Launch Accounts
  cascade, Macros > Record macro, Templates > Save current session, Run
  Template, and template recovery.
- In the template editor, verify per-client named/custom destinations, Saved
  positions/Clickable cascade, all four macro modes, per-client assignments,
  and the absence of a separate repeat checkbox. Help text explains that every
  assigned macro repeats in full cycles after Play until Stop, and that physical
  input or verified focus loss pauses injection until safe conditions recover.
- In HandleScope, verify **Included with SessionDock (recommended)**,
  **Standalone HandleScope (advanced)**, standalone Automatic/Keep installed/
  exact reviewed/unavailable saved version choices, the standalone-only Refresh
  reviewed versions action and its success/failure states, Automatic/`v2`/`v1`
  API, the included version shown by the tested build, parent-owned lifecycle
  explanation, and Off/Starting/Ready/
  HandleScope needs attention/Standalone runtime unavailable/Settings need
  repair statuses. No
  translation may suggest that normal use downloads or installs HandleScope.
- Exercise every confirmation and file picker. Verify titles, filters, status
  text, singular/plural counts, and safe error guidance use the selected
  language.
- In **Export or import data**, exercise both tabs, empty and populated category
  lists, Select eligible/Clear, automatic template macro dependencies, public-
  only exclusions, manifest review, cancel/save/read/invalid states, conflicts,
  missing account matches, import results, and the legacy action. Verify the
  keyboard-input warning and its separate acknowledgement on export and import;
  no translation may describe a keyboard-bearing macro as non-sensitive.
  Trigger the incompatible whole-layout warning and verify it clearly says the
  assignment remains unassigned instead of implying that the macro was scaled
  during import.
- Enable **Open with SessionDock** in each language and inspect its Windows
  shell/Open With description. Disable it before switching languages so the
  newly written per-user registration uses the current localized description.
- With Narrator, verify control names, help text, item status, live-region
  announcements, keyboard order, and focus restoration. Informational updates
  must be polite; warnings and errors must be assertive and must not be
  announced twice merely because a language changed.
- Test dark, light, and Windows high-contrast themes at 100%, 125%, 150%, and
  200% scaling. Pay particular attention to longer German and French text.
  Buttons remain reachable, text wraps or trims intentionally, and meaning is
  never conveyed by color alone.
- Open the current and previous bundled release notes in every language. The
  current release must be translated. An older note without a translation must
  display the localized English-fallback label before the English content.

## Intentionally invariant content

Product and vendor names, usernames, display names, user and place IDs, PIDs,
server JobIds, URLs, filenames, paths shown for a recovery action, SHA-256
values, JSON property names and payloads, protocol values, keyboard shortcuts,
HandleScope version/tag/commit provenance, and Roblox-provided experience or
release-feed content remain unchanged. Their
surrounding labels and explanations are localized. Internal trace messages and
exception text that never reaches the interface are developer diagnostics and
are not translated.

Before release, parse all five resource dictionaries, reject duplicate keys,
and compare the complete key set with `en-US`. `Portable.*` and
`Tutorial.AdvancedTransferTitle`/`Tutorial.AdvancedTransferBody` require exact
key parity and matching format placeholders such as `{0}` and `{1}`. Product
tokens including `.sessiondock`, SHA-256, Roblox, SessionDock, and JobId remain
invariant; their surrounding sentences require genuine locale translations.
