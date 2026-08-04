# Updates for regular users

SessionDock uses a manual, one-click updater built with Velopack and backed by
the canonical project's GitHub Releases. Updates are not silently installed.

> The integrated HandleScope/ExactWheel/template source is not a published
> release merely because it is documented here. A published build contains that
> workflow only when its own release notes say so.

> **Distribution hold — 2026-08-04:** there is no approved production
> installer or updater target. Do not download or run existing release assets.
> The immutable v3.0.0 binaries are unsigned and use the retired packed layout;
> the separately shared detected development build remains blocked pending
> Microsoft's determination.

## First-time installation

Wait until a reviewed commit and release explicitly remove the distribution
hold above. Then open the canonical
[SessionDock release page](https://github.com/Makmatoe/SessionDock/releases),
select the release identified by the current documentation, and download its
`SessionDock-win-x64-Setup.exe`. Do not use a Discord attachment or a bare
executable copied from another machine.

Confirm the download came from `github.com/Makmatoe/SessionDock`. A current
public SessionDock release must have a valid Authenticode publisher signature
and timestamp; **Unknown publisher** is a verification failure, not an expected
installation step. Before opening Setup, follow
[Verify a manual installer download](#verify-a-manual-installer-download).

When a published build lists the integrated workflow, its reviewed HandleScope
component and ExactWheel engine are part of SessionDock. Do not download a
HandleScope ZIP, install TinyClicks/ExactWheel separately, run a PowerShell
installer, approve UAC, or configure a scheduled task/autostart entry for normal
SessionDock use.

If the browser reports **Virus scan failed**, delete the incomplete download,
check Windows Security protection history, download again from the canonical
release, and verify SHA-256. The message means the download scan did not finish;
it is not a positive malware finding or proof of safety. Do not disable security
software or policy. A managed-laptop administrator should review the canonical
URL and hash.

If Defender reports a named threat such as `Trojan:Win32/Wacatac.B!ml`, do not
run or restore the file. Leave it quarantined and follow the
[Defender detection response](DEFENDER_DETECTION_RESPONSE.md). This is a
positive detection and blocks the artifact; do not call it a false positive
without Microsoft's determination.

If an old HandleScope script reports **running scripts is disabled**, Windows
PowerShell blocked the script under execution policy before it ran. Do not add
`-ExecutionPolicy Bypass`: the integrated build does not use that script at all.

## Normal update flow

1. Select the top-right update button in SessionDock.
2. The app contacts only the canonical SessionDock
   GitHub release feed.
3. If a newer stable version exists, review its version and signed release
   notes.
4. Confirm installation. Cancelling leaves the current version unchanged.
5. SessionDock downloads the authorized package, checks its signed SHA-256,
   exact contents, and version, closes, and asks Velopack to replace and reopen
   the application.

If a verified package was downloaded during an earlier attempt, the update
button asks whether to restart and install that pending version.

The installer-based production build is the recommended updateable edition.
Source, debug, and raw `dotnet publish` builds do not become trusted production
installations and cannot use the production self-update path.

## Moving to a build with integrated components

Use the same SessionDock update button or the same direct
`SessionDock-win-x64-Setup.exe` filename. The portable filename remains
`SessionDock-win-x64-Portable.zip`, and the full Velopack package remains
`SessionDockApp-<version>-win-x64-sessiondock-full.nupkg`. There is no companion
HandleScope or ExactWheel download.

When such a build is approved and published, the verified SessionDock package
replaces the complete transparent application inventory, including its reviewed
`SessionDock.HandleScope.dll` and `SessionDock.ExactWheel.dll` components. Existing
`%LOCALAPPDATA%\SessionDock\handlescope.json` opt-ins remain compatible. A fresh
setup selects **Included with SessionDock (recommended)**. On upgrade, an old
Keep installed/Exact preference or an enabled Automatic configuration with a
currently verified standalone API is migrated to **Standalone HandleScope
(advanced)**; otherwise included is selected. This migration records a source
choice only and never changes the external installation. Users can still choose
Automatic, Keep installed, or an exact signed-catalog-reviewed compatible
standalone version independently from Automatic, `v2`, or `v1` API negotiation.
A stale exact pin remains visible and can be changed in the panel; doing so never
downloads, installs, updates, or downgrades the standalone application.
The standalone-only **Refresh reviewed versions** action explicitly fetches and
verifies the latest signed compatibility catalog while preserving the current
selection. It retrieves no package; opening the panel performs no network check.

An independently installed standalone HandleScope is left exactly as it is.
SessionDock does not remove, update, downgrade, start, stop, reconfigure, or
uninstall its files, process, scheduled task, or preferences. Choose
**Standalone HandleScope (advanced)** only when you intentionally continue to
operate that external application.

## Moving from Roblox One or SessionDock 2.3.0 and earlier

Do not run the 2.1.5 or 2.3.0 Setup over an existing Roblox One installation as
an upgrade or repair. Those installers retain the historic Velopack package ID
`RobloxOne`, which can cause Setup to replace `%LOCALAPPDATA%\RobloxOne`. Older
Roblox One versions also stored account settings and browser profiles in that
same directory, so Setup can remove the data before the newer application gets
a chance to migrate it.

Roblox One 2.1.4 and earlier also cannot use their update button after the
repository rename. GitHub redirects `Makmatoe/RobloxOne` to
`Makmatoe/SessionDock`, and the older fail-closed updater reports that the
release manifest was redirected to an untrusted address. Retrying does not
repair that binary.

SessionDock 2.3.1 uses the corrective package ID `SessionDockApp`. Its Setup is
installed side-by-side and does not use either `%LOCALAPPDATA%\RobloxOne` or
`%LOCALAPPDATA%\SessionDock` as its application directory. Use this corrective
path for every installed version through 2.3.0:

1. Close every Roblox One and SessionDock window. Do not uninstall either app
   and do not delete either local-data directory.
2. After the distribution hold is lifted, use the canonical release page and
   download Setup plus `SHA256SUMS.txt` from the same approved release.
3. Verify the Setup with the checksum procedure below.
4. Run the verified Setup as the same standard Windows user. On first launch,
   SessionDock copies only recognized settings, browser profiles, sounds, and
   local integration configuration into `%LOCALAPPDATA%\SessionDock`. It does
   not move or delete the old `%LOCALAPPDATA%\RobloxOne` tree, and it never
   copies installer files such as `current`, `packages`, or `Update.exe`.
5. Confirm every expected account slot and sign-in. Automatic orphan-profile
   cleanup remains paused after legacy profiles are copied so validation cannot
   erase an unrecognized recovered session.
6. Keep the old installation and its data until that validation is complete
   **and SessionDock shows no unfinished-migration or conflicting-data warning**.
   Seeing accounts while such a warning remains can mean SessionDock is still
   using the preserved legacy copy. Only after both checks pass may the old app
   be removed.

After every expected account and sign-in has been confirmed, close SessionDock
and remove only `%LOCALAPPDATA%\SessionDock\profile-cleanup-paused.txt` to
re-enable automatic orphan-profile cleanup. Do not remove that marker while an
account is missing, and do not delete `settings.json`, `settings.backup.json`,
or the `Profiles` directory as part of this step.

If an account or sign-in is missing, stop using the app, keep
`%LOCALAPPDATA%\RobloxOne` and `%LOCALAPPDATA%\SessionDock` unchanged, and do
not add or remove accounts while recovery is being assessed. Also preserve any
sibling directory named `RobloxOne.<random characters>`; it may be a Velopack
rollback copy. SessionDock does not automatically trust or merge such siblings,
but one can be valuable during supervised recovery.

## Verify a manual installer download

Download both `SessionDock-win-x64-Setup.exe` and `SHA256SUMS.txt` from
the same entry on the canonical
[GitHub Releases page](https://github.com/Makmatoe/SessionDock/releases). For the
latest release, the checksum file is also available through its stable
[direct download link](https://github.com/Makmatoe/SessionDock/releases/latest/download/SHA256SUMS.txt).
Do not combine an installer from one release with a checksum file from another.

Open a normal PowerShell in the directory containing both files, then run:

```powershell
$asset = 'SessionDock-win-x64-Setup.exe'
$checksumFile = '.\SHA256SUMS.txt'
$pattern = '^(?<hash>[0-9a-fA-F]{64})  ' + [Regex]::Escape($asset) + '$'
$matchingLines = @(Get-Content -LiteralPath $checksumFile |
    Where-Object { $_ -match $pattern })

if ($matchingLines.Count -ne 1) {
    throw "Expected exactly one checksum entry for $asset."
}

$expected = $matchingLines[0].Substring(0, 64).ToLowerInvariant()
$actual = (Get-FileHash -LiteralPath ".\$asset" -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -cne $expected) {
    throw "SHA-256 mismatch for $asset. Delete the download and do not run it."
}

Write-Host "SHA-256 verified: $asset"
```

Then verify Windows publisher identity without opening the installer:

```powershell
$signature = Get-AuthenticodeSignature -LiteralPath .\SessionDock-win-x64-Setup.exe
$signature | Select-Object Status, StatusMessage,
  @{Name='Publisher';Expression={$_.SignerCertificate.Subject}},
  @{Name='TimestampAuthority';Expression={$_.TimeStamperCertificate.Subject}}

if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
    $null -eq $signature.SignerCertificate -or
    $null -eq $signature.TimeStamperCertificate) {
    throw 'SessionDock Setup does not have a valid publisher signature and timestamp.'
}
```

Run the installer only after the checksum command prints `SHA-256 verified`,
the Authenticode status is `Valid`, and the displayed publisher matches the
publisher documented by that release. A missing, duplicate, malformed, or
mismatched checksum, signature, timestamp, or publisher is a verification
failure. Do not add an antivirus exclusion or override a named threat detection.

## What is verified

The updater requires a signed release descriptor whose public key is pinned in
the application. That descriptor authorizes the exact target version, package
filename, size, SHA-256 digest, channel, and release notes. Velopack also checks
the downloaded package against the authorized package metadata.

The protected release workflow Authenticode-signs the exact first-party
application PEs before Velopack packaging, then signs the final Setup. Both use
SHA-256 and an RFC 3161 SHA-256 timestamp under one externally provisioned,
publicly trusted publisher profile. The workflow verifies the exact publisher,
code-signing/timestamp EKUs, and trusted certificate chains before staging and
again after draft, approval, and public re-downloads. An unsigned file or an
**Unknown publisher** result cannot be a public release.

The signed update descriptor separately protects the in-app release decision
and exact package hash. GitHub attestations and checksums provide additional,
manually verifiable provenance; none is a bypass for failed Authenticode or a
positive Defender detection.

Immediately before scheduling installation, SessionDock rechecks the downloaded
full package against the signed size and SHA-256, validates its exact application
inventory in a locked temporary directory, rejects missing, duplicate, path-like,
oversized, or unexpected archive entries, validates the package identity/version
metadata, and checks every expected executable structurally. The protected
release flow also requires the installed and portable application inventories to
match the approved publish bytes. This preserves package integrity in addition
to the required Windows publisher identity.

For an integrated candidate, HandleScope and ExactWheel must be the reviewed
`SessionDock.HandleScope.dll` and `SessionDock.ExactWheel.dll` files in the
approved package inventory. Validation checks HandleScope's pinned upstream
identity/hashes and ExactWheel's source manifest, format version, inventory,
provenance, notices, and release-block fields. A component executable, installer,
PowerShell script, source directory, or additional component payload in the
SessionDock package is rejected. The current ExactWheel manifest blocks release
pending explicit TinyClicks ownership/licensing and immutable provenance; this
document does not clear that block.

Release notes are displayed as bounded, inert text. Web content from a release
is not executed in the application.

The protected workflow for an approved release also produces an SPDX SBOM,
reviewed dependency notices, SHA-256 checksums, and GitHub attestations. These
aid independent inspection; the in-app trust decision relies on the signed
descriptor, exact package hash, and package allowlist. This is a required
workflow, not a claim that the integrated candidate has been published.

## User data

Application files and local user data are separate. SessionDock's package ID is
`SessionDockApp`; neither current nor legacy user data is stored in that
installation directory. An update replaces the application, not the data under
`%LOCALAPPDATA%\SessionDock`, so account slots,
isolated WebView2 profiles, favorites, recent history, labels, colors, and sound
preferences normally remain in place. Versioned templates, their catalog
backup, local ExactWheel macro payloads, layout preferences, and onboarding
state are also under `%LOCALAPPDATA%\SessionDock` and normally remain in place.

Updates never contain another user's account profiles or settings. Removing
SessionDock does not imply that Roblox or WebView2 data was removed; use the
application's account removal controls first when profile deletion is desired.
Removing or updating an integrated SessionDock build removes/replaces its
included HandleScope and ExactWheel code. It does not delete or alter an
independently installed standalone HandleScope copy. Uninstall does not
automatically delete local templates or macro files.

## If an update fails

- Keep the existing version open if the check, signature verification, or
  download fails.
- Retry from a stable network, then check the canonical
  [release page](https://github.com/Makmatoe/SessionDock/releases) for notices.
- Do not download a replacement executable from chat, email, file-sharing, or
  a repository fork.
- Treat **Unknown publisher**, invalid/missing Authenticode, a publisher
  mismatch, or a named Defender detection as a hard stop. Keep a detected file
  quarantined and use the [Defender response guide](DEFENDER_DETECTION_RESPONSE.md).
  A matching checksum or attestation never overrides Windows security.
