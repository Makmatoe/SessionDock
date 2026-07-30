# Privacy and local data

SessionDock is local-first. It has no project-operated account service, cloud
database, advertising system, or telemetry collector.

## Data stored on the computer

SessionDock stores application settings and isolated browser profiles under
`%LOCALAPPDATA%\SessionDock`. Depending on features used, this can include:

- local account-slot identifiers, Roblox user ID/username after Roblox reports
  them, custom labels, optional groups, and accent colors;
- a separate WebView2 profile per account, including Roblox cookies and browser
  storage controlled by Roblox;
- each account's selected destination;
- named batch presets containing only stable local account-slot keys and a
  launch delay, plus the last selected batch delay; presets do not duplicate
  destinations, private-server codes, server JobIds, cookies, or launch tickets;
- shared Recent/Favorite metadata, timestamps, experience names, public/private
  classification, and a server JobId when a best-effort local match succeeds;
- private-server codes only when the user explicitly saves or launches such a
  destination through the normal launcher. A private-server link received by
  the optional Windows link handler is used in memory for that confirmed launch
  and is not written to account defaults, Recent, or Favorites;
- sound preferences, generated built-in sound files, and a local copy under the
  `Sounds` folder of any startup sound the user explicitly imports;
- the selected display-language preference (`system`, `en-US`, `nl-NL`,
  `de-DE`, `fr-FR`, or `es-ES`);
  changing it only swaps bundled text resources and culture-aware display
  formatting, without contacting a translation service or changing the stable
  JSON representation of stored dates and numbers;
- the last main-window display identifier, monitor-relative position, size, and
  maximized state, used only to restore a visible window on the next launch;
- the current settings, the prior successful settings backup, and timestamped
  preserved copies of settings files that could not be read;
- small recovery markers that keep automatic browser-profile cleanup paused
  when settings are uncertain or record an account profile whose requested
  deletion has not completed yet; and
- optional local integration configuration or connection metadata created by
  those separately installed integrations.

SessionDock does not intentionally store Roblox passwords, launch tickets, raw
Roblox Player logs, server IP addresses, HandleScope bearer tokens, or raw
handle values. Never send the `%LOCALAPPDATA%\SessionDock` directory in a public
bug report because its WebView2 profiles may contain authenticated cookies.

## Privacy-safe support diagnostics

The **About and diagnostics** panel builds a small, read-only support summary
from an explicit allowlist. It can include the SessionDock, Windows, .NET, and
WebView2 versions; architecture; whether a Windows-verified Roblox Player was
found; install/update capability; bounded aggregate counts; theme; and the
interface-sound setting. Roblox Player discovery is reduced immediately to an
availability/trust state, so its path is never retained in the report model.

The preview is exactly what the Copy and Export actions use. Export writes only
that preview to a user-chosen text file. The panel never reads or attaches
settings files, logs, browser profiles, or integration configuration, and it
omits user and computer names, local paths, account names/labels/IDs/keys,
destinations, place/server/private-server details, cookies, tokens, URLs, and
exception details. SessionDock does not send the summary automatically. Users
should still review the complete preview before sharing it.

## Safe metadata transfer

The **Safe metadata transfer** sidebar panel creates a separate, versioned JSON
document from a fixed allowlist. Its exact export preview contains Roblox user
IDs (needed to match existing accounts), account labels, optional groups,
approved accent colors, account order, and pinned public place IDs with their
display and custom names. The file is written only after the user selects
**Export reviewed JSON** and chooses a destination; SessionDock does not send
it. Roblox user IDs and user-written labels are still personal metadata, so the
complete preview should be reviewed before the file is stored or shared.

The transfer file never contains local account-slot keys, usernames, active
account state, selected account destinations, Recent timestamps, private-server
links or codes, server JobIds, session/profile folder names, WebView2 data,
cookies, passwords, authentication/launch tickets, sound files or preferences,
pending-deletion state, settings/backup files, logs, local paths, or integration
configuration and secrets. A public Favorite is eligible only when its stored
destination parses to the same plain public place ID, has no private-server
material, and has no server JobId.

Import reads at most 256 KiB from a regular file, rejects unsupported versions,
unknown or duplicate JSON fields, duplicate account/favorite entries, invalid
types, out-of-range counts, unsupported colors, and unbounded/control text. It
then shows a human-readable plan and skipped-item counts. The import button
remains disabled until the user selects a confirmation checkbox. Import matches
by Roblox user ID only to account slots already present on this computer; it
does not create accounts or browser profiles. Unmatched accounts and their
favorites are skipped. The settings change is saved as one mutation and is
fully rolled back if persistence fails.

When upgrading from the historic Roblox One package identity, SessionDock may
copy recognized settings, browser profiles, sounds, and local integration
configuration from `%LOCALAPPDATA%\RobloxOne` into
`%LOCALAPPDATA%\SessionDock`. It rejects reparse points and conflicting files,
copies settings last, records a recovery receipt, and leaves the entire source
tree unchanged. Installer files and unknown entries are not copied. Automatic
orphan-profile cleanup remains paused after profile recovery until the user has
confirmed the expected accounts and sign-ins.

## Network connections

SessionDock makes its direct Roblox requests to official Roblox HTTPS endpoints
when the user signs in, verifies an account, resolves supported destinations,
looks up experience metadata, or requests a launch ticket. The embedded Roblox
pages may also load subresources selected by Roblox. Roblox receives data
according to its own privacy policy and account settings.

The optional **Watch and auto-join** action is off until the user explicitly
starts it. While armed, SessionDock resolves and pins the requested user's
stable numeric ID, retrying temporary identity failures with bounded backoff,
then sends bounded repeated presence requests through the selected account's
isolated Roblox session. The watch is memory-only, expires after four hours,
stops after one launch attempt, and is never restored after restart. Failed
checks, observed locations, presence timelines, and watch state are not added
to diagnostics or sent as telemetry. A successful launch may create the same
ordinary Recent entry as a manual Join User launch.

When the user explicitly checks for an application update, SessionDock connects
to GitHub Releases for `Makmatoe/SessionDock`. GitHub receives ordinary request
metadata such as the source IP address and user agent under GitHub's policies.

An optional generic post-launch hook is used only after the user configures an
HTTPS URL for a numeric loopback address and a bearer token. Windows must trust
the endpoint certificate and it must match the configured IP address;
SessionDock does not bypass TLS certificate validation. Plain HTTP, missing or
invalid tokens, and non-loopback destinations make the hook unconfigured, so
the event payload is not created or sent. A certificate-validation failure
prevents the HTTP request from being transmitted. Redirects, cookies, and
system proxies are disabled. The bounded payload contains the Roblox process
ID, place and experience, public/private classification, and selected account
ID, username, and label. It excludes passwords, cookies, launch tickets, raw
destinations, private-server codes, and server job IDs.

The optional HandleScope integration inspects only the expected local install
and SessionDock opt-in files when its panel opens or the user selects Refresh.
SessionDock contacts GitHub for `Makmatoe/HandleScope` only when the user
selects **Install Latest HandleScope release**, to resolve the latest stable
immutable release and download its checksum and Windows package. A future
release may also supply an independently signed descriptor. Those requests contain
ordinary request metadata such as the source IP address and user agent; they do
not include a HandleScope token, configuration, local path, or Roblox account
data. The verified package is staged in a random temporary directory and
removed after the install attempt when cleanup succeeds.

It contacts the loopback health endpoint only when the user selects **Test
connection** and only after local connection-file and same-session process
checks. The user can explicitly ask SessionDock to start the separately
installed API at its expected per-user path. The explicit install action also
starts it and enables its limited per-user sign-in task; opening SessionDock or
the integration panel never starts it by itself. SessionDock verifies the
immutable GitHub asset digest, same-release checksum, and internal inventory,
then saves a local receipt and rehashes the installed API against that inventory
before it starts or trusts the process. This is repository-based verification,
not certificate-backed publisher identity.
SessionDock never bundles or elevates HandleScope. When testing or using the
enabled integration, it reads the rotating bearer token from HandleScope's
checked local connection file and sends it only to the validated loopback API;
it does not send the token off-machine, log it, or copy it into SessionDock's
persistent configuration. Installation starts the API and enables HandleScope's
limited per-user autostart task for future Windows sign-ins, but does not change
SessionDock's integration setting. The connection test does not enumerate or
close handles.

The optional **Open with SessionDock** feature is off by default. Enabling it
writes only SessionDock-owned per-user URL-handler keys under
`HKCU\Software\Classes`, a private `sessiondock-roblox:` protocol, and a named
Open With hint for `roblox:` links. It does not register for all HTTPS links,
replace Roblox's default handler, elevate, or make a network request. Disable
removes only registrations carrying SessionDock's ownership marker; unknown or
conflicting keys are preserved.

An incoming link is bounded, forwarded only within the same Windows user and
interactive session, and validated before sending and again on receipt.
SessionDock accepts only official Roblox HTTPS destinations or restricted
`roblox:` forms that normalize through its normal destination parser. It
rejects arbitrary protocols, credentials, authentication tickets, cookies,
tokens, server JobIds, duplicate or unknown parameters, fragments, and
ambiguous authority or port syntax. The UI hides private codes, requires an
account choice, and asks for final confirmation before requesting a fresh
ticket. Incoming links are never logged. Public launches may appear in Recent;
private links received through this handler are not persisted.

Windows necessarily places the incoming link in the newly invoked SessionDock
process's OS command line. Windows retains that command line for the lifetime
of the invoked process, including when it becomes the new primary instance.
SessionDock clears its own argument and startup-field references promptly, then
holds the link in memory only as needed to forward, review, and confirm it.
That payload can include a private-server code even though the preview hides
the code and SessionDock does not write it to settings, history, diagnostics,
or logs. A different process already running as the same Windows user may be
able to inspect process command lines or memory; the handler does not claim to
protect against a compromised same-user desktop.

## Browser permissions

Account pages run in Microsoft WebView2 profiles. SessionDock limits top-level
navigation to official Roblox HTTPS domains and blocks downloads, external app
protocols, password autofill integration, and camera, microphone, location, and
notification permissions. Browser extensions are not loaded; standard
clipboard paste and the context menu remain available for credentials copied
from a password manager. Microsoft services may install or update the WebView2
Runtime independently of SessionDock.

## Deleting local data

Removing an account in SessionDock is intended to delete that account slot's
complete local WebView2 profile, including cookies, local storage, cache,
history, service workers, and autofill data. Clear Recent/Public/Private history
with the corresponding in-app controls. The account filter also scopes a clear
operation when one account is selected. Clearing history does not remove pinned
Favorites unless the user removes those entries separately, and removing an
account does not silently erase its shared Recent/Favorite records.

An interrupted account removal leaves a bounded local deletion marker alongside
any profile data that could not yet be deleted, so SessionDock can retry that
cleanup on a later launch. Preserved corrupt settings copies can contain the
same account, destination, and history metadata as the settings files they came
from and remain until the user deletes them. Unused imported-sound copies are
removed on a best-effort basis; deleting all SessionDock data removes any copies
that remain.

To remove all SessionDock data, first remove accounts in the app, close
SessionDock, and then delete `%LOCALAPPDATA%\SessionDock`. This action signs those local
profiles out by removing their data; it does not revoke Roblox sessions on
other devices. Use Roblox account security controls when global session
revocation is needed.

Application updates replace application files and normally preserve this local
data. Published release artifacts contain the application, release metadata,
licenses/notices, checksums, and an SBOM; they never include a developer's or
another user's local data. Repository validation rejects tracked machine-user
paths and common credential formats before packaging.
