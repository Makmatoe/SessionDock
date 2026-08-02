# Updates for regular users

SessionDock uses a manual, one-click updater built with Velopack and backed by
the canonical project's GitHub Releases. Updates are not silently installed.

## First-time installation

Use the repository's **Install Latest SessionDock release** button or this
[direct latest Setup link](https://github.com/Makmatoe/SessionDock/releases/latest/download/SessionDock-win-x64-Setup.exe).
It always selects `SessionDock-win-x64-Setup.exe` from the latest stable
canonical release, so a first-time user does not have to identify the correct
file in GitHub's asset list. Open the download to start Setup.

Confirm the download came from `github.com/Makmatoe/SessionDock`. SessionDock
does not currently have a paid Authenticode certificate, so Windows may show
**Unknown publisher** or a SmartScreen warning. Before opening Setup or
continuing through that warning, follow
[Verify a manual installer download](#verify-a-manual-installer-download).

SessionDock 3.0 already contains HandleScope engine 0.3.0 inside
`SessionDock.exe`. Do not download a HandleScope ZIP, run a PowerShell installer,
approve UAC, or configure a scheduled task/autostart entry for normal
SessionDock use.

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

## Upgrading to SessionDock 3.0

Use the same SessionDock update button or the same direct
`SessionDock-win-x64-Setup.exe` filename. The portable filename remains
`SessionDock-win-x64-Portable.zip`, and the full Velopack package remains
`SessionDockApp-<version>-win-x64-sessiondock-full.nupkg`. There is no companion
HandleScope download.

The verified SessionDock package atomically replaces `SessionDock.exe`, including
the reviewed HandleScope 0.3.0 engine embedded in it. Existing
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
2. Use the [direct latest Setup link](https://github.com/Makmatoe/SessionDock/releases/latest/download/SessionDock-win-x64-Setup.exe),
   then download `SHA256SUMS.txt` from that same latest canonical release.
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

Run the installer only after the command prints `SHA-256 verified`. A missing,
duplicate, malformed, or mismatched entry is a verification failure.

## What is verified

The updater requires a signed release descriptor whose public key is pinned in
the application. That descriptor authorizes the exact target version, package
filename, size, SHA-256 digest, channel, and release notes. Velopack also checks
the downloaded package against the authorized package metadata.

The project executable and final Setup are currently unsigned. The signed
update descriptor protects the in-app release decision and exact package hash.
GitHub artifact attestations and published checksums provide additional,
manually verifiable provenance, but Windows cannot display a verified publisher.

Immediately before scheduling installation, SessionDock rechecks the downloaded
full package against the signed size and SHA-256, extracts only its application
executables into a locked temporary directory, rejects missing, duplicate,
path-like, oversized, or unexpected archive entries, validates the package
identity/version metadata, and checks that each expected executable is
structurally a Windows PE file and requires the embedded project executable's
exact hash to match the verified release input. This preserves signed-package
integrity but does not add certificate-backed Windows publisher identity.

HandleScope is part of the approved `SessionDock.exe` bytes. Release validation
also checks its pinned upstream version/tag/commit and source hashes, bundled
MIT license, notices, and SBOM identity. A separate HandleScope executable,
installer, PowerShell script, or component directory in the SessionDock package
is rejected.

Release notes are displayed as bounded, inert text. Web content from a release
is not executed in the application.

Each release also publishes an SPDX SBOM, complete bundled dependency notices,
SHA-256 checksums for every other asset, and GitHub attestations. These aid
independent inspection; the in-app trust decision relies on the signed
descriptor, exact package hash, and package allowlist.

## User data

Application files and local user data are separate. SessionDock's package ID is
`SessionDockApp`; neither current nor legacy user data is stored in that
installation directory. An update replaces the application, not the data under
`%LOCALAPPDATA%\SessionDock`, so account slots,
isolated WebView2 profiles, favorites, recent history, labels, colors, and sound
preferences normally remain in place.

Updates never contain another user's account profiles or settings. Removing
SessionDock does not imply that Roblox or WebView2 data was removed; use the
application's account removal controls first when profile deletion is desired.
Removing or updating SessionDock removes/replaces only its included HandleScope
engine. It does not delete or alter an independently installed standalone copy.

## If an update fails

- Keep the existing version open if the check, signature verification, or
  download fails.
- Retry from a stable network, then check the canonical
  [release page](https://github.com/Makmatoe/SessionDock/releases) for notices.
- Do not download a replacement executable from chat, email, file-sharing, or
  a repository fork.
- Expect Windows to report **Unknown publisher** while SessionDock has no
  Authenticode certificate. Continue only for a download from the canonical
  release after its checksum or GitHub attestation verifies. A matching checksum
  still does not create Windows publisher identity.
