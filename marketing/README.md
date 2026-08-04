# SessionDock marketing assets

This directory is the publishing entry point for reviewed SessionDock artwork.
At present it contains one archived evidence set: SessionDock 2.7.0.

> **Artwork-maintainer scope:** this page does not install SessionDock and does
> not document the current HandleScope/ExactWheel/template workflow. The only
> files here are historical 2.7.0 images; never use them to imply that the
> integrated source has shipped.

- [SessionDock release archive (distribution hold)](https://github.com/Makmatoe/SessionDock/releases)
- [Archived v2.7.0 release](https://github.com/Makmatoe/SessionDock/releases/tag/v2.7.0)
- [Trusted v2.7.0 marketing assets](trusted/v2.7.0/README.md)

The version number in the asset path describes the UI that was captured. Do
not use the v2.7.0 images to represent a newer release or imply that they show
the current interface. This archive is not an installation path; existing
release binaries are under a 2026-08-04 distribution hold.

## Publish a reviewed asset

1. Start PowerShell from the repository root.
2. Verify the complete trusted directory:

   ```powershell
   ./scripts/Verify-SessionDockDocumentationImages.ps1 `
       -AssetDirectory ./marketing/trusted/v2.7.0
   ```

3. Require exit code `0`, `VerifiedAssets` equal to `10`, and
   `PrivacyMasksVerified` equal to `8`.
4. Choose the required size from
   [`trusted/v2.7.0`](trusted/v2.7.0/README.md).
5. Publish the selected file byte-for-byte. Keep the `v2.7.0` label or nearby
   context so viewers can tell which application version it depicts.

If the verifier fails, stop. Do not publish any file from that directory as a
reviewed SessionDock image.

## Trust boundary

The trusted files are byte-identical mirrors of the reviewed documentation
assets derived from installed SessionDock 2.7.0 windows. Their manifest records
capture provenance, source and output hashes, dimensions, and privacy-mask
coordinates. The verifier checks those fixed bytes, PNG structure, and declared
opaque masks.

Verification does not make an edited copy reviewed. Cropping, resizing,
recompressing, annotating, or otherwise changing an image creates a derivative:
store it outside `trusted`, label it as a derivative, and complete a new privacy
and fidelity review before publication. The image verifier also does not verify
the SessionDock installer or prove current product behavior.

## Do not use

Earlier synthetic or generatively altered images may exist only in the local,
Git-ignored `archive/synthetic-not-for-use` directory. That directory is
intentionally absent from clean clones and is retained locally only for
provenance. Never commit, publish, attach, or present those files as SessionDock
screenshots.

Do not add raw captures, private account data, unreviewed derivatives, or files
from another SessionDock version to `trusted/v2.7.0`.
