# SessionDock

[![CI](https://github.com/Makmatoe/SessionDock/actions/workflows/ci.yml/badge.svg)](https://github.com/Makmatoe/SessionDock/actions/workflows/ci.yml)

SessionDock is a Windows launcher that keeps Roblox website sessions separate,
so you can choose the account and destination before opening Roblox Player.

## Install SessionDock

[![Install Latest SessionDock release](docs/assets/install-latest-sessiondock.svg)](https://github.com/Makmatoe/SessionDock/releases/latest/download/SessionDock-win-x64-Setup.exe)

Select the button on a Windows x64 PC, then open the downloaded
`SessionDock-win-x64-Setup.exe`. The fixed link always selects the Setup asset
from the latest stable release in the canonical `Makmatoe/SessionDock`
repository, so there is no release page or asset list to navigate.

[View release details, checksums, and the portable ZIP](https://github.com/Makmatoe/SessionDock/releases/latest)

Release installers are published only on the canonical GitHub Releases page.
If that page has no release yet, a production installer is not currently
available. SessionDock does not currently have a paid Authenticode certificate,
so Windows identifies the installer as an unknown publisher. The protected
workflow instead binds every release to a signed update descriptor, exact
SHA-256 checksums, GitHub attestations, and a separately approved immutable
draft. These controls do not make Windows display a verified publisher.

> SessionDock is an independent project. It is not affiliated with, endorsed by,
> or sponsored by Roblox Corporation. Roblox and the Roblox logo are trademarks
> of Roblox Corporation.

## Quick start

1. Install Roblox Player. WebView2 is already included with Windows 11 and
   nearly all Windows 10 installations. If it is missing or damaged,
   SessionDock stays open and offers the
   [official Microsoft WebView2 repair and download page](https://developer.microsoft.com/en-us/microsoft-edge/webview2/consumer/).
2. Select **Install Latest SessionDock release** above and open the downloaded
   Setup. You do not need to choose a release asset manually.
3. Confirm the browser download came from `github.com/Makmatoe/SessionDock`.
   Windows may show **Unknown publisher** because the project does not currently
   buy an Authenticode certificate. Before continuing through that warning,
   verify the checksum or GitHub attestation using the
   [regular-user verification steps](docs/UPDATES.md#verify-a-manual-installer-download).
4. Add an account, then sign in on the official Roblox page shown in its
   isolated browser session.
5. Choose a destination and select **Launch Roblox**.

The installer is the recommended edition and supports in-app updates. A
portable ZIP is also published for temporary use, but it does not update
itself.

If Roblox One or SessionDock 2.3.0 or earlier is already installed, do not use
an older Setup as an upgrade or repair. Follow the
[side-by-side corrective upgrade](docs/UPDATES.md#moving-from-roblox-one-or-sessiondock-230-and-earlier)
so the historic `%LOCALAPPDATA%\RobloxOne` account data is preserved.

## What it does

- Keeps any number of Roblox sign-ins in separate local WebView2 profiles and
  lets you drag saved accounts into the order used by the account strip and
  batch launch.
- Gives accounts custom labels, colors, and optional groups, and remembers a
  destination per account. Groups provide one-click selection in Batch launch.
- Searches saved accounts by label, group, username, user ID, or destination,
  and searches Recent and Favorites by name, place, account, destination, or
  tracked server. Press
  **Ctrl+F** to focus the search for the current workspace and **Escape** to
  clear it.
- Opens public places, official private-server links or codes, and supported
  server IDs recovered from recent launches.
- Joins an online Roblox user by exact username, user ID, or official profile
  URL when that user's privacy settings and current experience allow the
  selected account to follow them. Roblox rechecks the user when Player starts;
  user destinations currently use single launch rather than batch launch.
- An explicit **Watch and auto-join** option checks that user with the selected
  account immediately and then at a bounded, backoff-aware rate. When Roblox
  reports a usable current server it starts Player at most once; Player makes
  the final access check. The watch remains cancelable, expires after four
  hours, and is never saved or resumed automatically.
- Shares Recent and Favorites across accounts while preserving the account and
  public/private context of each launch.
- Launches one account at a time or runs a best-effort pipelined batch with a
  remembered configurable delay. Named presets save only the selected account
  keys and delay, and failed accounts can be reviewed and retried without
  reselecting successful ones. Batch mode verifies selected sign-ins before
  closing any running clients, prepares the next isolated session while the
  current client settles, requests each launch ticket only when it is ready to
  be used, can be cancelled, and restores the previously selected account.
  Starting a batch or retry closes currently running verified Roblox Player
  clients; Roblox still decides whether multiple Players may run.
- Closes all visible and background Roblox Player processes on request.
- Provides optional interface sounds and a user-selected startup sound.
- Provides live **System default**, **English (United States)**,
  **Nederlands (Nederland)**, **Deutsch (Deutschland)**,
  **Français (France)**, and **Español (España)** display-language choices
  from the sidebar. System default follows supported Dutch, German, French,
  and Spanish Windows cultures and otherwise uses the complete English
  fallback. The choice is remembered locally and does not require a restart.
  Static labels, runtime messages, confirmations, validation, diagnostics, and
  accessibility text use the selected language. Display dates and times follow
  the selected culture, while stored data keeps its stable machine-readable
  format. Older release notes without a translation are clearly labeled when
  shown in English. Before SessionDock can safely open local settings, very
  early command-line, security-context, and single-instance errors use the
  supported Windows display language; the saved app preference takes effect
  as soon as normal startup begins.
- Provides an **About and diagnostics** panel with an exact, reviewable support
  summary that can be copied or exported as text. The summary contains bounded
  counts and system/component states, never settings files, logs, paths,
  account details, destinations, browser data, cookies, or tokens.
- Exports and imports a separate, exact-preview **safe metadata** JSON file for
  account appearance/order and pinned public favorites. Import requires a
  validated preview and confirmation, matches Roblox user IDs only to accounts
  that already exist locally, and never moves sign-ins, account-slot keys,
  usernames, destinations, private-server details, JobIds, or local paths.
- Provides an optional **Open with SessionDock** Windows link handler. Enabling
  it is an explicit per-user action under **Integrations** and does not replace
  Roblox's default handler. Every accepted link opens a parsed preview and
  account chooser followed by a separate confirmation; it never launches an
  account merely because a link arrived. Authentication-bearing or ambiguous
  launch URLs are rejected, and private-server links received this way are not
  saved to account defaults, Recent, or Favorites.

## Local by design

SessionDock has no cloud backend, advertising, or telemetry. It does not ask
for, read, or store Roblox passwords. Account browser profiles, settings,
favorites, and recent-launch metadata remain under `%LOCALAPPDATA%\SessionDock`.
The optional safe metadata export is written only to the file the user chooses;
SessionDock never sends it. It contains Roblox user IDs so account appearance
can be matched, and should be reviewed before it is stored or shared.

SessionDock's direct Roblox API requests and top-level sign-in navigation are
limited to official Roblox HTTPS endpoints. Embedded Roblox pages may still
load subresources chosen by Roblox. The Player executable is location-checked
and Windows-signature-checked before launch.
Optional post-launch integrations accept loopback addresses only and are off
until the user configures them. HandleScope is optional, off by default, and
never bundled or elevated by SessionDock. When the user explicitly selects
**Install Latest HandleScope release**, SessionDock resolves the latest stable,
immutable release from the canonical `Makmatoe/HandleScope` GitHub repository,
downloads the Windows x64 ZIP and checksum file, then verifies GitHub's exact
asset digests, the same-release checksum, safe archive layout, and every file in
the bundle manifest before running the standard-user installer. It records the
verified inventory and rechecks the installed API hash before starting or
trusting it. HandleScope is unsigned, so this trust comes from the canonical
immutable GitHub release rather than a certificate-backed publisher.

To use the optional connector, select **Integrations** in the SessionDock
sidebar. That panel also manages the separate **Open with SessionDock** link
handler. The link handler is off by default and writes only owned per-user
entries under `HKCU\Software\Classes`: a SessionDock URL ProgID, the private
`sessiondock-roblox:` forwarding protocol, and an Open With hint for safe
`roblox:` links. It does not claim all HTTPS links, change the default Roblox
handler, or elevate. Received links are forwarded over bounded same-user,
same-Windows-session IPC to the existing SessionDock process and are validated
again before any UI is shown. Windows leaves the link in the invoked process's
OS command line for that process's lifetime, including when it becomes the new
primary instance. SessionDock clears its own startup references promptly and
keeps the link in memory only as needed to forward, review, and confirm it;
private codes are hidden from the preview and not persisted. This is not a
boundary against another process already running as the same Windows user.

HandleScope installation starts the API immediately and enables HandleScope's
limited per-user task so it starts automatically at future Windows sign-ins;
it does not change SessionDock's integration opt-in. The HandleScope panel can
separately enable the fixed per-user Roblox policy, explicitly start the API at
its expected local path if needed, and test its loopback health endpoint.
Testing never enumerates or closes a handle. SessionDock stores only the
minimal enabled/disabled opt-in; its
narrow Roblox handle policy remains compiled into the app. Command-line setup
remains documented for source developers under
[SystemProcesses](SessionDock/SystemProcesses/README.md).

The embedded sign-in view intentionally does not load extensions or password
manager integrations. It supports normal clipboard paste and its context menu,
while keeping each Roblox account in its own isolated local browser profile.

Read [Privacy](docs/PRIVACY.md) for the complete data/network summary and
[Security](SECURITY.md) before reporting a security issue. Maintainers and
accessibility testers can use the manual
[accessibility verification matrix](docs/ACCESSIBILITY.md).

For troubleshooting, open **About and diagnostics** from the top of the main
window. The preview is the exact text used by both Copy and Export. SessionDock
does not send it automatically, and the export is a small text-only summary;
it does not gather a support archive from local application data.

## Updates

The top-right update button checks this repository's stable GitHub Releases
feed. SessionDock shows the version and cryptographically signed release notes
before it downloads anything, and installs only after confirmation. Bundled
notes use the selected language when available and visibly identify an English
fallback for older releases.

Production updates require a release descriptor authorized by the public key
pinned in the app. The descriptor binds the version, notes, exact package name,
size, and SHA-256; the app then enforces an exact package-content allowlist.
The Windows executables are currently unsigned, but the protected release
pipeline still requires the signed update descriptor, exact hashes, package
allowlists, SBOM, GitHub attestations, and separate publication approval.
Source, debug, raw publish, and portable builds cannot replace themselves. See
[Updates](docs/UPDATES.md) for the regular-user flow.

## Build and verify

The repository pins .NET SDK 10.0.302, self-contained runtime 10.0.10, and its
packaging tool from
`global.json` and the local tool manifest, so these commands stay the same when
the pins change. From the repository root:

```powershell
dotnet restore --locked-mode
./scripts/Build.ps1 -Configuration Release -Runtime win-x64 -CI
```

To run the desktop project during development:

```powershell
dotnet run --project ./SessionDock/SessionDock.csproj
```

Local builds are development artifacts, not official SessionDock releases.
Release packages include the MIT license, pinned upstream licenses and notices,
an SPDX SBOM, checksums, and GitHub artifact attestations.
Maintainer setup and the tag-driven release checklist are in
[Releasing](docs/RELEASING.md). Optional local hook configuration is documented
under [SystemProcesses](SessionDock/SystemProcesses/README.md).

## License and contributions

SessionDock is open source under the [MIT License](LICENSE.md). Read
[CONTRIBUTING.md](CONTRIBUTING.md) before proposing code changes.
