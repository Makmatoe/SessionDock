# Responding to a Microsoft Defender malware detection

A named Microsoft Defender Antivirus result such as
`Trojan:Win32/Wacatac.B!ml` is a security verdict, not an **Unknown publisher**
or SmartScreen reputation warning. It blocks use and release of those exact
bytes. A matching checksum, GitHub attestation, clean scan on another PC, or
known source tree does not override it.

SessionDock's Windows distribution is permanently unsigned. **Unknown
publisher** may therefore appear for a clean canonical build. That status does
not prove safety and does not authorize bypassing a named Defender detection.

> **Distribution hold — 2026-08-04:** the current latest release is a
> zero-asset security-hold record. No SessionDock download is approved while
> this hold is active. A future reviewed release must explicitly lift the hold
> after completing the transparent-build, Defender, and separate laptop gates.

## For users

1. Do not run the file. Leave it quarantined.
2. Do not select **Allow on device**, restore it, add a file/folder/process
   exclusion, disable real-time or cloud protection, remove Internet-zone
   metadata, or weaken SmartScreen or managed-device policy.
3. Open **Windows Security > Virus & threat protection > Protection history**.
   Record the exact threat name, affected path, detection time, action, and
   security-intelligence version. Take a screenshot that does not expose
   private paths or account data.
4. Confirm where the file came from. SessionDock binaries are distributed only
   through `github.com/Makmatoe/SessionDock/releases`. Discord may contain a
   link to that page but never a binary attachment.
5. If the file ran before detection, disconnect the device from the network and
   run **Microsoft Defender Offline scan**. Change important credentials from a
   different trusted device if account or browser data may have been exposed.
6. Report the release version, exact filename, hash if still available, Windows
   version, and Defender details privately to the maintainer.

Hashing a file does not execute it. In Command Prompt, use:

```text
certutil -hashfile SessionDock-win-x64-Portable.zip SHA256
```

If Defender has removed or quarantined the file, do not restore it merely to
calculate a hash. The Protection History resource and original download record
are sufficient starting evidence.

**Virus scan failed** in a browser is different: it says the download scan did
not complete. Delete the incomplete file, inspect Protection History, and retry
only from the canonical GitHub release if no named detection exists. Never
disable protection to make the browser finish.

## For maintainers

1. Stop distribution and announcements for the affected bytes. If they are
   public, withdraw the affected release assets and publish a zero-asset
   security-hold record. Preserve the release, workflow, Defender, and Discord
   evidence without executing the sample.
2. Record SHA-256, size, PE inventory, Authenticode state, Internet-zone state,
   Defender product/engine/signature versions, detection source, and exact
   resource path.
3. Compare the reported hash with the canonical draft/public asset. A mismatch
   is a possible transport or provenance incident. A match rules out byte
   substitution but does not make the detection safe.
4. Reproduce with a no-remediation custom scan on clean Windows devices and on
   a downloaded copy that retains Internet provenance. Do not change Defender
   settings:

   ```powershell
   & "$env:ProgramFiles\Windows Defender\MpCmdRun.exe" `
       -Scan -ScanType 3 -File <path> -DisableRemediation
   ```

5. Submit the exact sample through the
   [Microsoft Security Intelligence file-submission portal](https://www.microsoft.com/wdsi/filesubmission)
   as a software developer and retain the submission ID and final
   determination.
6. Audit packaging, PE structure, dependencies, network destinations, process
   and input APIs, update behavior, and source provenance. Treat a compressed
   or opaque layout as a design problem even if source review finds no malicious
   behavior.
7. Correct the responsible design or code, build a new version from a clean
   reviewed commit, and repeat every release gate. Never replace an existing
   public asset in place.

## 2026-08-04 incident record

A development r3 executable shared through Discord was reported on a laptop as
`Trojan:Win32/Wacatac.B!ml`.

- Reported executable SHA-256:
  `8041C3268B698A2F964654B7B0C5F67BC2B2B1035E44AB767D9074F498612B28`
- The Discord stream matched the local r3 executable byte-for-byte, ruling out
  Discord transport modification for that sample.
- The executable was unsigned and used a compressed .NET self-contained
  single-file layout. Its high-entropy bundle contained process-handle,
  low-level input-hook, input-injection, child-process, window-control, and
  updater behavior.
- Defender on the build PC reported the file as not known-good but found no
  threat with that PC's then-current definitions. The laptop result remained a
  named detection and was not bypassed.

That layout was retired. Current candidates use a transparent multi-file
publish with no compressed application overlay. The portable ZIP has exactly
six intentionally unsigned application PEs; expected Microsoft runtime files
retain Microsoft signatures. The update-only NUPKG additionally carries the
two recognized unsigned Velopack 1.2.0 package helpers, never as beginner
portable content. A clean no-remediation scan is required, but it is only one
release gate and never overrides a later named detection.

ExactWheel provenance pins 14 implementation/lock files at commit
`a290cdb9fb5d0c5047103a9985016cb573ea954f`, the separately pinned current
build definition, and the root MIT license. This provenance evidence explains
the source identity; it does not grant an antivirus exception.
