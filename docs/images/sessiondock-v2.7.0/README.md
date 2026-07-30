# SessionDock 2.7.0 visual assets

[Install Latest SessionDock release](https://github.com/Makmatoe/SessionDock/releases/latest/download/SessionDock-win-x64-Setup.exe)

This directory contains documentation and marketing images derived from the
real installed SessionDock 2.7.0 interface. The source build is
`2.7.0+e30ad6acf8165befe11e00d9f1f5d1de1f7e90de`.

No application controls, text, screens, or application-state details were
created with generative image editing. The only changes inside captured
windows are deterministic opaque-mosaic redactions over personal account and
destination values. Titles, explanatory text, and callouts are placed outside
the application window.

## Sanitized source captures

| Asset | Size | Source window | Privacy treatment |
| --- | ---: | --- | --- |
| `sessiondock-v2.7.0-full-window.png` | 1048 x 720 | Main window | Account identities, destination, and active account pixelated |
| `sessiondock-v2.7.0-batch-dialog.png` | 666 x 713 | Batch launch dialog | Visible account identities and destinations pixelated |
| `sessiondock-v2.7.0-diagnostics-dialog.png` | 706 x 673 | About and diagnostics dialog | None required; the dialog's allowlisted preview contains no account details, destinations, paths, tokens, or browser data |

The unredacted captures are not retained. Their SHA-256 hashes, exact
dimensions, and redaction rectangles are recorded in [`manifest.json`](manifest.json)
so future changes can be reviewed against the precise capture inputs without
keeping private pixels.

## Focused documentation views

| Asset | Size | Verified content |
| --- | ---: | --- |
| `sessiondock-v2.7.0-accounts-focused.png` | 1200 x 260 | Account strip, search, add, edit, remove, and drag-to-reorder hint |
| `sessiondock-v2.7.0-destinations-focused.png` | 1200 x 560 | Launch/Recent tabs, Experience/User modes, destination input, Set for all, Batch, and Launch |
| `sessiondock-v2.7.0-batch-focused.png` | 1100 x 840 | Real Batch launch dialog with account/group selection, presets, and remembered launch delay |
| `sessiondock-v2.7.0-diagnostics-focused.png` | 1100 x 820 | Real About and diagnostics dialog with review, copy, and export controls |

## Reusable sizes

| Asset | Size | Intended use |
| --- | ---: | --- |
| `sessiondock-v2.7.0-readme-overview.png` | 1200 x 900 | README, documentation, and project pages |
| `sessiondock-v2.7.0-social-wide.png` | 1600 x 900 | Wide store or social post |
| `sessiondock-v2.7.0-social-square.png` | 1200 x 1200 | Square social post |

The external callouts describe only behavior visible in these windows or
confirmed in the 2.7.0 source: isolated local browser profiles, supported
experience/user/private-link/tracked-server destinations, account search and
management, batch selection/groups/presets/delay, and privacy-safe diagnostics.

## Verification and future recapture

Verify the checked-in, reviewed assets without opening SessionDock or reading
private application data:

```powershell
./scripts/Verify-SessionDockDocumentationImages.ps1
```

The verifier pins the reviewed manifest and every output hash and dimension. It
also rejects text/EXIF metadata or trailing PNG data and checks every declared
privacy rectangle for an entirely opaque deterministic mosaic.

Automated recapture and fixed-coordinate rebuilding are intentionally not
distributed. A changed build, DPI, window state, or account layout could move
private pixels outside an old mask. Future assets must therefore use a new
task-specific capture directory, a new manifest, and a fresh native-resolution
privacy and UI-fidelity review. Never reuse this version's redaction rectangles
for another capture or retain its unredacted private inputs.
