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
  metadata export/import, integrations, HandleScope, Windows link handling,
  updates, sounds, and Auto-Join.
- Exercise every confirmation and file picker. Verify titles, filters, status
  text, singular/plural counts, and safe error guidance use the selected
  language.
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
and Roblox-provided experience or release-feed content remain unchanged. Their
surrounding labels and explanations are localized. Internal trace messages and
exception text that never reaches the interface are developer diagnostics and
are not translated.
