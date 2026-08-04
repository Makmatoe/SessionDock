# Trusted SessionDock 2.7.0 marketing assets

This is an archived, immutable marketing mirror for the installed SessionDock
2.7.0 interface. Its PNG files and `manifest.json` are byte-identical to the
reviewed evidence under
[`docs/images/sessiondock-v2.7.0`](../../../docs/images/sessiondock-v2.7.0/README.md).
Only files from this trusted directory may be used as reviewed v2.7.0 marketing
or announcement attachments.

> **Immutable historical evidence:** this directory predates integrated
> HandleScope, ExactWheel, clickable cascade, and session templates. It is not a
> current user guide or evidence that those source-tree features were released.

- [SessionDock release archive (distribution hold)](https://github.com/Makmatoe/SessionDock/releases)
- [Archived v2.7.0 release and verification assets](https://github.com/Makmatoe/SessionDock/releases/tag/v2.7.0)

The archived release is provenance evidence, not an installation path. These
images must not be used to imply that 2.7.0 is current or that its UI is
unchanged in a later release. Existing binaries are under a 2026-08-04
distribution hold.

## Verify before publication

1. Start PowerShell from the repository root.
2. Run:

   ```powershell
   ./scripts/Verify-SessionDockDocumentationImages.ps1 `
       -AssetDirectory ./marketing/trusted/v2.7.0
   ```

3. Require exit code `0` and JSON output containing:

   - build `2.7.0+e30ad6acf8165befe11e00d9f1f5d1de1f7e90de`;
   - manifest SHA-256
     `1B2BCD38597BE4336DDA7863289E146038CDE59E342A118487094F4EB822709E`;
   - `VerifiedAssets` equal to `10`; and
   - `PrivacyMasksVerified` equal to `8`.

4. If verification succeeds, select the needed original file using the
   [documented inventory](../../../docs/images/sessiondock-v2.7.0/README.md#sanitized-source-captures).
5. Publish it byte-for-byte with visible v2.7.0 context. If verification fails,
   do not publish anything from this directory as reviewed evidence.

## What is trusted

The verifier pins the exact manifest, file inventory, image hashes, dimensions,
PNG envelope, metadata restrictions, and declared opaque privacy masks. The
manifest says the windows came from the real installed WPF application and
records the hashes of temporary raw captures that were deleted after review.

That trust does not extend to:

- a cropped, resized, recompressed, annotated, or otherwise modified copy;
- current or later SessionDock UI and behavior;
- installer authenticity, malware scanning, or Windows publisher identity; or
- undeclared files placed beside the reviewed assets.

Verify downloadable software separately with the checksums, signed update
descriptor, and GitHub attestations attached to the archived v2.7.0 release.

## Directory rules

- Do not edit, rename, replace, or add files in this directory.
- Do not add raw captures, account data, synthetic images, generatively altered
  images, or derivatives.
- Do not copy files from another SessionDock version into this archive.
- Create a new versioned, reviewed directory and manifest for new captures.

Unredacted captures are intentionally not retained. The archive preserves the
reviewed outputs and provenance metadata without preserving private source
pixels.
