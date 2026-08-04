# Downloads and updates for regular users

SessionDock's Windows distribution is permanently unsigned and portable-only.
New users download one transparent ZIP from the canonical GitHub release. There
is no SessionDock setup executable and no separate HandleScope or ExactWheel
download.

> **Distribution hold — 2026-08-04:** the current latest release is a
> zero-asset security-hold record. No SessionDock download is approved while
> this hold is active. The flow below applies only after a reviewed replacement
> explicitly lifts the hold and passes separate laptop validation before
> publication.

## First download

After a reviewed release explicitly lifts the hold:

1. Open the canonical
   [SessionDock Releases page](https://github.com/Makmatoe/SessionDock/releases).
2. Open the release you intend to use and read its notes. Confirm it lists the
   transparent portable layout.
3. Download `SessionDock-win-x64-Portable.zip`. Never download a SessionDock
   binary attached to Discord, email, chat, or a file-sharing mirror. A Discord
   announcement may link to GitHub; it must not carry the binary.
4. Download `SHA256SUMS.txt` from that same release and verify the ZIP before
   extracting it. The verification steps below do not run a script or require
   a PowerShell execution-policy change.
5. In File Explorer, right-click the ZIP, select **Extract All**, and choose a
   new folder. Do not start the application from inside the ZIP and do not mix
   files from different versions.
6. Open the new folder and run `SessionDock.exe` as your normal Windows user,
   not as administrator.
7. Complete the highlighted first-launch **Get Started** tutorial. It begins
   with **Manage accounts** and **Destinations**, then covers launching,
   macros, templates, and the controller. The Advanced tutorial is available
   afterward from Settings.

Keep the complete extracted folder together. `SessionDock.exe` loads the
separately inspectable application, HandleScope, ExactWheel, update-trust, and
runtime DLLs beside it.

## Verify the portable ZIP

The release page and both downloaded files must be from
`github.com/Makmatoe/SessionDock`. In Command Prompt, run:

```text
certutil -hashfile SessionDock-win-x64-Portable.zip SHA256
```

Compare the complete 64-character result with the line for
`SessionDock-win-x64-Portable.zip` in `SHA256SUMS.txt`. The filename and hash
must both match exactly. This command is built into Windows and does not change
PowerShell execution policy.

If GitHub CLI is already installed, the release's GitHub artifact attestation
can be checked independently:

```text
gh attestation verify SessionDock-win-x64-Portable.zip --repo Makmatoe/SessionDock
```

A checksum proves only that the bytes match the release manifest. An
attestation proves the recorded GitHub build identity. Neither proves that a
file is harmless, and neither overrides a named malware detection.

SessionDock has no Authenticode publisher signature. Windows may therefore show
**Unknown publisher** or an unrecognized-app SmartScreen prompt. That reputation
warning is different from a named antivirus finding. After the canonical source
and hash have been verified, and only when Windows Security reports no named
threat, a user may continue through the normal Windows prompt. Never disable
Defender, add an exclusion, restore a quarantined file, remove Internet-zone
metadata, or weaken a managed-device policy to force SessionDock to run.

If Microsoft Defender names a threat such as
`Trojan:Win32/Wacatac.B!ml`, stop. Leave the file quarantined and follow the
[Defender detection response](DEFENDER_DETECTION_RESPONSE.md). A matching hash,
clean result on another PC, GitHub attestation, or **Unknown publisher** prompt
does not cancel that verdict.

## How updates work

Update behavior depends on how the existing copy was obtained:

- **Existing installed copies:** older SessionDock installations that already
  have the Velopack update machinery may continue to consume the verified full
  NUPKG and release feed through SessionDock's in-app update control. Review the
  release notes and confirm the update only after SessionDock verifies the
  descriptor, package hash, version, and package inventory.
- **Portable copies:** portable SessionDock does not update itself. Close it,
  download the new portable ZIP from GitHub Releases, verify it, extract it to
  a new folder, and run the new folder's `SessionDock.exe`. After testing the
  new copy, the old application folder can be deleted.

Do not manually download or open the NUPKG. It exists for already-installed
copies and the update feed, not as a beginner installation format. Do not copy
new binaries over an old portable directory: using a new folder prevents stale
or mixed-version files.

SessionDock user data lives under `%LOCALAPPDATA%\SessionDock`, outside the
portable application folder. A normal update or replacement of the portable
folder does not erase saved accounts, isolated browser profiles, destinations,
templates, layouts, macros, sounds, or tutorial state.

## Moving from Roblox One or SessionDock 2.3.0 and earlier

Do not run an old Roblox One or SessionDock Setup as an upgrade or repair. Those
historic installers can share the `RobloxOne` package identity with the same
directory that older builds used for account settings and browser profiles.
Preserve the old data until the current portable copy proves migration is
complete.

After a reviewed release explicitly lifts the distribution hold:

1. Close every Roblox One and SessionDock window. Do not uninstall either app
   and do not delete `%LOCALAPPDATA%\RobloxOne`,
   `%LOCALAPPDATA%\SessionDock`, or a sibling `RobloxOne.<random>` rollback
   directory.
2. Download and verify the new `SessionDock-win-x64-Portable.zip`, then extract
   it into a new folder. Do not overwrite or launch through an old installed
   application directory.
3. Run the new folder's `SessionDock.exe` as the same standard Windows user.
   SessionDock copies only recognized settings, browser profiles, sounds, and
   local integration configuration into `%LOCALAPPDATA%\SessionDock`. It leaves
   the source tree unchanged and does not copy installer/package files.
4. Confirm every expected account, local label, destination, and sign-in. Keep
   both old data trees while SessionDock reports an unfinished migration,
   conflicting data, or a paused-cleanup warning.
5. Only after every expected account and sign-in is present and no migration
   warning remains may you close SessionDock and remove
   `%LOCALAPPDATA%\SessionDock\profile-cleanup-paused.txt` to re-enable bounded
   orphan-profile cleanup. Do not remove `settings.json`,
   `settings.backup.json`, or `Profiles` as part of this step.
6. Keep the old application and data long enough to validate another launch.
   Remove them only when you deliberately no longer need recovery.

If an account or sign-in is missing, stop. Do not add/remove accounts or change
either data tree while recovery is assessed. Preserve any
`RobloxOne.<random>` sibling because it may contain a useful Velopack rollback
copy. SessionDock does not automatically trust or merge those siblings.

## What a release contains and verifies

The public release contract includes:

- `SessionDock-win-x64-Portable.zip` for new and portable users;
- the full Velopack NUPKG and feed metadata for existing installed copies;
- a signed SessionDock update descriptor;
- `SHA256SUMS.txt` covering every published asset except itself;
- an SPDX SBOM and complete dependency notices; and
- GitHub artifact attestations.

The portable ZIP and full NUPKG must carry byte-identical application files.
The application inventory is transparent rather than a compressed
self-extracting executable. Six application PEs are intentionally unsigned:
`SessionDock.exe`, `SessionDock.dll`, `SessionDock.HandleScope.dll`,
`SessionDock.ExactWheel.dll`, `SessionDock.ReleaseTrust.dll`, and
`Velopack.dll`. The expected Microsoft runtime complement must retain valid
Microsoft signatures. Unexpected executables, scripts, source directories,
duplicate paths, reparse points, or extra component payloads are rejected.

ExactWheel provenance pins 14 implementation/lock files at commit
`e1f77bd77cf9c3db708c587f17f6ea58d9d961ca`, the separately pinned current
build definition, and the root MIT license. HandleScope has its own pinned
upstream inventory and compatibility checks. These gates establish source
identity; they do not bypass Windows malware detection.

## If a download or update fails

- **Running scripts is disabled:** a standalone PowerShell script was blocked
  before it ran. Normal SessionDock download and launch uses no script. Do not
  change execution policy and do not install HandleScope separately.
- **Virus scan failed:** the browser or device policy did not finish scanning.
  Delete the incomplete file, check **Windows Security > Virus & threat
  protection > Protection history**, and retry only from the canonical GitHub
  release. On a managed laptop, ask its administrator to review the GitHub URL
  and expected hash.
- **Named malware detection:** do not retry, restore, allow, or run the file.
  Follow the Defender response guide.
- **Portable update does not appear in-app:** this is expected. Portable copies
  update manually with a newly downloaded and extracted ZIP.
- **An existing installed update fails verification:** leave the current
  version closed or unchanged, keep its data, and report the exact error. Do
  not substitute an asset from Discord or manually unpack the NUPKG.

To remove a portable copy, close SessionDock and delete only its extracted
application folder. Remove `%LOCALAPPDATA%\SessionDock` separately only when
you intentionally want to erase all SessionDock data for that Windows user.
