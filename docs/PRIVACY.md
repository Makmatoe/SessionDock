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
- `Templates\catalog.json` and its last-known-good backup, containing versioned
  account-slot references, resolved per-slot destinations, launch delays,
  layout preferences, normalized window rectangles, and macro metadata. A local
  template destination can contain private-server material when the user saved
  that launch; portable export excludes it;
- ExactWheel payloads under `Macros\`. A recording can contain mouse/keyboard
  timing and typed key events, so it must be treated as private even though it
  contains no browser cookie or Roblox launch ticket by design;
- `onboarding-state.json`, containing only the independent completed-version
  markers for the Get Started and Advanced tutorials;
- small recovery markers that keep automatic browser-profile cleanup paused
  when settings are uncertain or record an account profile whose requested
  deletion has not completed yet; and
- optional local integration configuration, including the selected HandleScope
  source, standalone runtime-version requirement, and API contract. Included
  mode stores no HandleScope token or endpoint;
  advanced standalone mode reads its external discovery document only when
  needed.

SessionDock does not intentionally store Roblox passwords, launch tickets, raw
Roblox Player logs, server IP addresses, HandleScope bearer tokens, or raw
handle values. A user can nevertheless type a secret while recording a macro;
SessionDock cannot make that input non-sensitive afterward. Never send the
`%LOCALAPPDATA%\SessionDock` directory in a public bug report because its
WebView2 profiles may contain authenticated cookies and its macro files may
contain recorded keys.

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

## Portable selected-data transfer

The **Export or import data** panel creates a versioned `.sessiondock` ZIP from
only the items selected for review: templates, their required macro
dependencies, separately selected macros, public destinations, and launch
presets. Selecting a template automatically includes every macro it references
and any matching eligible named destination in the reviewed package. Dependency
checkboxes do not necessarily mirror that closure, especially for a macro that
contains keyboard input; the package review shows the effective dependencies
and export remains blocked until the required keyboard acknowledgement. The
pre-review screen labels its non-public named-destination count as library-wide,
while the package review reports the exact selected-template omissions.
Private-server and tracked-server destination values are omitted and counted
rather than serialized. The archive contains a bounded manifest and
content-addressed macro entries. Macro payloads are copied byte for byte,
recorded in the manifest by SHA-256, and never rewritten during export or
import.

Packages use Roblox numeric user IDs only to match template and preset slots to
accounts that already exist on the importing device. They do not create an
account or browser profile. Source monitor identifiers, local account-slot keys,
and source paths are stripped or rebuilt locally. Only plain public place
destinations are eligible.

A portable package never contains sign-ins, WebView/browser profiles, cookies,
passwords, tokens, authentication or launch tickets, usernames, local account
keys or paths, private-server links or codes, server JobIds, integration
configuration or secrets, or logs. Names and numeric Roblox IDs that the user
selects can still be personal data and should be reviewed before sharing.
Macro files require special care: a recording can contain keyboard input,
including text that was sensitive when typed. SessionDock lists those macros
and requires an explicit acknowledgement before exporting or importing them;
that warning does not make the payload non-sensitive.

Before showing an import plan, SessionDock validates the archive version,
manifest shape and dependencies, SHA-256 for every macro, duplicate entries,
paths, entry/count/size limits, and supported values without extracting an
unbounded ZIP. The plan reports naming conflicts, already-present macro hashes,
missing account matches, skipped templates, and incompatible macro assignments.
Nothing changes until the user reviews the plan and confirms the import; a
persistence failure keeps no partial catalog change.

Saved window rectangles are normalized to the monitor work area so they can be
realized on a different resolution. Imported client-relative macro bytes remain
exact; their recorded coordinates are adapted to the verified destination
client only at playback. Whole-layout assignments fail closed: if monitor
count, virtual aspect ratio, or normalized monitor arrangement is incompatible,
the macro remains imported but that assignment is left unassigned for repair.
SessionDock does not guess a desktop mapping.

### Legacy JSON metadata compatibility

The existing reviewed metadata JSON remains available for account appearance,
matched account order, and pinned public favorites. Its preview contains Roblox
user IDs, user-written labels, optional groups, approved accent colors, and
eligible public place IDs/names. It excludes the same authentication, private
destination, local-path, and integration data described above.

Legacy import reads at most 256 KiB from a regular file and rejects unsupported
versions, unknown or duplicate fields, duplicate entries, invalid types,
out-of-range counts, unsupported colors, and unbounded/control text. It matches
only by Roblox user ID to existing local account slots, shows the complete plan
and skipped-item counts, and requires confirmation before one atomic settings
mutation.

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

The optional HandleScope panel and the included HandleScope component require
no HandleScope network download. Opening the panel, choosing the included or
standalone source, choosing a standalone version requirement or
Automatic/`v2`/`v1` API, enabling, disabling, or
retrying does not contact GitHub or any other internet service. The included
engine is part of the complete SessionDock build and can change only with the
complete SessionDock package.

The standalone-only **Refresh reviewed versions** button is the exception: when
you explicitly select it, SessionDock requests the latest compatibility-catalog
JSON from the canonical SessionDock release URL on GitHub. It verifies the
catalog's signature, identity, validity window, compatibility, and rollback
floor before saving it. The request downloads no HandleScope executable or
installer, preserves the selected source/version/API preferences, and does not
start, stop, install, update, or downgrade either runtime.

The source selection is stored at
`%LOCALAPPDATA%\SessionDock\handlescope-runtime.json`; standalone version and
API preferences remain separate from the backwards-compatible
`%LOCALAPPDATA%\SessionDock\handlescope.json` opt-in. These files contain no
bearer token, endpoint, process ID, Roblox account data, or executable path.
Legacy 2.x Automatic, Keep installed, and exact-version data remains available
as a compatibility requirement. A stale exact pin can be changed in the panel,
but no choice can cause SessionDock to download, install, start, replace,
update, downgrade, or uninstall a standalone HandleScope copy.

In **Included with SessionDock (recommended)** mode, SessionDock creates a
non-elevated, parent-owned child and delivers its bootstrap data through an
inherited anonymous pipe. The rotating bearer token and ephemeral numeric IPv4
loopback endpoint remain in parent/child memory. They are not written to a
connection file, command line, environment variable, setting, log,
diagnostics/export, or UI. Loopback requests do not leave the computer, and the
child exits when disabled or when SessionDock exits.

In **Standalone HandleScope (advanced)** mode, SessionDock reads the external
application's existing protected
`%LOCALAPPDATA%\HandleScope\connection.json`. That file remains the standalone
product's exact five-field object: `apiVersion`, `baseUrl`, `token`,
`processId`, and `startedAtUtc`. SessionDock validates the same-user,
same-session process and sends the token only to its numeric-loopback endpoint
for authenticated metadata, health, and an explicitly enabled post-launch
operation. It never sends the token off-machine, persists it, or changes the
standalone application, files, process, version, configuration, autostart, or
uninstall state.

Both modes negotiate only SessionDock's compiled `v1` or `v2` adapter. Automatic
readiness and **Retry** checks do not enumerate or close handles. A post-launch
operation may ask the selected engine to dry-run and close only matching Roblox
singleton handles: first for the newly launched PID and, if configured, in one
separately planned all-process sweep. Each execution uses only its matching
short-lived, single-use plan ID.

The signed HandleScope compatibility catalog is used only by clients for which
it was explicitly published and by reviewed advanced-standalone identities;
the included flow does not download or execute anything from it. Existing
compatibility caches and legacy verification metadata contain public package
facts, not HandleScope tokens, Roblox credentials, or account data. SessionDock
preserves them during upgrade rather than treating them as current
configuration.

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

Removing a template does not automatically delete its ExactWheel payload. This
preserves the macro for other templates and later reuse. To remove an
unreferenced recording, remove its macro in the Macro library and save the
settings. After the catalog commit, SessionDock verifies and deletes the local
payload only if no remaining macro definition shares that content-addressed
file; a changed, unverifiable, locked, or still-shared file is retained safely.

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
data. A release candidate must never contain a developer's or another user's
local data. Repository validation rejects tracked machine-user paths and common
credential formats before packaging. This page does not assert that an
unpublished candidate or unresolved component license has been approved.
