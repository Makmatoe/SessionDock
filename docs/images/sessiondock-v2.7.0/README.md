# SessionDock 2.7.0 visual evidence

This is an archived, version-specific asset set for SessionDock 2.7.0. It
documents the installed 2.7.0 interface; it is not a claim about the current
release and must not be relabeled as newer-version evidence.

- [Current SessionDock release](https://github.com/Makmatoe/SessionDock/releases/latest)
- [Archived v2.7.0 release and verification assets](https://github.com/Makmatoe/SessionDock/releases/tag/v2.7.0)
- [Install SessionDock v2.7.0](https://github.com/Makmatoe/SessionDock/releases/download/v2.7.0/SessionDock-win-x64-Setup.exe)

The reviewed build identity is
`2.7.0+e30ad6acf8165befe11e00d9f1f5d1de1f7e90de`. The direct installer link is
intentionally pinned to that historical release for provenance; use the current
release link when you want the newest SessionDock version.

## Verify before using an image

1. Start PowerShell from the repository root.
2. Run the pinned verifier:

   ```powershell
   ./scripts/Verify-SessionDockDocumentationImages.ps1
   ```

3. Require exit code `0` and JSON output identifying the exact build above,
   manifest SHA-256
   `1B2BCD38597BE4336DDA7863289E146038CDE59E342A118487094F4EB822709E`,
   `VerifiedAssets` equal to `10`, and `PrivacyMasksVerified` equal to `8`.
4. If verification fails, do not publish, copy, or present any file from this
   directory as reviewed evidence. Restore it from a trusted clone and rerun
   the verifier.

For marketing or announcement attachments, verify and use the byte-identical
mirror under [`marketing/trusted/v2.7.0`](../../../marketing/trusted/v2.7.0/README.md)
instead of copying directly from this documentation directory.

## Provenance

The source windows were captured from the real installed WPF application. The
main window and dialogs were opened through their real UI Automation controls.
No application controls, labels, screens, or in-window state were generated or
reconstructed. The only in-window edits are deterministic, fully opaque mosaic
redactions over the personal values listed in `manifest.json`; headings and
callouts added outside a captured window are explanatory layout.

Raw captures were processed in a task-specific temporary directory and were
not retained. [`manifest.json`](manifest.json) records their SHA-256 hashes,
dimensions, capture method, redaction rectangles, and every reviewed output's
hash and dimensions. A raw hash preserves identity metadata; because the raw
bytes were intentionally deleted, it does not make the private capture
recoverable or independently viewable.

## Sanitized source captures

| Asset | Size | Source window | Privacy treatment |
| --- | ---: | --- | --- |
| `sessiondock-v2.7.0-full-window.png` | 1048 x 720 | Main window | Account identities, destination, and active account replaced by opaque mosaics |
| `sessiondock-v2.7.0-batch-dialog.png` | 666 x 713 | Batch launch dialog | Visible account identities and destinations replaced by opaque mosaics |
| `sessiondock-v2.7.0-diagnostics-dialog.png` | 706 x 673 | About and diagnostics dialog | No mask required; the allowlisted preview contains no account details, destinations, paths, tokens, or browser data |

## Focused documentation views

| Asset | Size | Content shown from the reviewed 2.7.0 UI |
| --- | ---: | --- |
| `sessiondock-v2.7.0-accounts-focused.png` | 1200 x 260 | Account strip, search, add, edit, remove, and drag-to-reorder hint |
| `sessiondock-v2.7.0-destinations-focused.png` | 1200 x 560 | Launch/Recent tabs, Experience/User modes, destination input, Set for all, Batch, and Launch |
| `sessiondock-v2.7.0-batch-focused.png` | 1100 x 840 | Batch launch account/group selection, presets, and remembered launch delay |
| `sessiondock-v2.7.0-diagnostics-focused.png` | 1100 x 820 | About and diagnostics review, copy, and export controls |

## Reusable reviewed layouts

| Asset | Size | Intended placement |
| --- | ---: | --- |
| `sessiondock-v2.7.0-readme-overview.png` | 1200 x 900 | Documentation and project pages |
| `sessiondock-v2.7.0-social-wide.png` | 1600 x 900 | Wide store or social post |
| `sessiondock-v2.7.0-social-square.png` | 1200 x 1200 | Square social post |

The external callouts are limited to behavior visible in these windows or
confirmed in the 2.7.0 source: isolated local browser profiles; supported
experience, user, private-link, and tracked-server destinations; account search
and management; batch selection, groups, presets, and delay; and privacy-safe
diagnostics.

## What verification proves

The verifier checks the reviewed manifest hash, exact asset inventory, every
PNG's SHA-256 and dimensions, required PNG structure, absence of PNG text/EXIF
chunks and trailing data, and every declared redaction pixel's membership in
the deterministic opaque-mosaic palette.

It does **not** prove that:

- SessionDock 2.7.0 is the current or recommended release;
- a separately downloaded installer is safe or byte-identical to a GitHub
  release asset;
- the deleted raw captures contained no private information outside the
  reviewed mask plan; or
- every product behavior is demonstrated by a still image.

Use the v2.7.0 release's own checksums, signed update descriptor, and GitHub
attestations when verifying downloadable software. Those release controls are
separate from this image verifier.

## Recapture rules

Do not run a fixed-coordinate recapture against a different build, DPI,
window state, account layout, or display scale. A moved value could escape an
old mask.

For a future version:

1. create a new `docs/images/sessiondock-vX.Y.Z` directory;
2. capture into a new task-specific temporary directory at native resolution;
3. perform a fresh privacy and UI-fidelity review;
4. create a new manifest with that version's hashes, dimensions, and masks;
5. add or update a verifier contract for the new immutable evidence set; and
6. delete the unredacted temporary inputs after review.

Never overwrite this archive, reuse its redaction rectangles, retain its raw
private inputs, or claim that these 2.7.0 pixels document another release.
