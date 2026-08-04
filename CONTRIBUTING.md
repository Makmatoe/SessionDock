# Contributing

Thank you for helping improve SessionDock. Bug reports, usability feedback, and
focused feature proposals are welcome.

## Before submitting code

This repository is available under the MIT License. Open an issue before doing
substantial implementation work so effort can be coordinated; focused pull
requests are welcome.

Never submit Roblox credentials, cookies, launch tickets, private-server codes,
local account data, signing keys, access tokens, HandleScope connection files,
or production certificates.

## Development

SessionDock targets Windows x64, self-contained .NET 10.0.10, and SDK 10.0.302.
Use the exact SDK selected by `global.json` and work from a short-lived branch
based on `main`.

```powershell
dotnet restore --locked-mode
./scripts/Build.ps1 -Configuration Release -Runtime win-x64 -CI
```

Changes under `discord-release-bot` must also pass its dependency, test, and
syntax checks. These checks use mocked HTTP and must never contact Discord:

```powershell
Push-Location ./discord-release-bot
npm ci
npm test
npm run check
npm audit --omit=dev --audit-level=moderate
Pop-Location
```

Keep changes narrowly scoped. Preserve the project's local-first behavior and
do not add network destinations, telemetry, automatic updates, elevated helper
processes, or third-party packages without an explicit security and maintenance
case. Prefer .NET and Windows platform APIs. Roblox network calls must use
official Roblox endpoints.

HandleScope engine changes require a coordinated upstream sync. Do not edit
files under `SessionDock.HandleScope/Upstream/` by hand. Land or identify the
change in the canonical HandleScope repository, pin an immutable tag and commit,
then run `scripts/Sync-BundledHandleScope.ps1`. Review the regenerated
`SessionDock.HandleScope/handlescope-upstream.json`, root MIT license reference, notices, SBOM
inputs, both repositories' current integration documents, and the focused
parent/pipe/lifecycle tests in the same pull request.

## Pull requests

An approved pull request should:

- explain the user-visible behavior and security impact;
- include tests for parsers, trust decisions, update metadata, or persistence
  logic when those areas change;
- keep account and browser-profile data out of fixtures and screenshots;
- update user and maintainer documentation when behavior changes;
- preserve the included HandleScope source/provenance allowlist and prove that
  the transparent release carries it only as the reviewed
  `SessionDock.HandleScope.dll` component beside `SessionDock.exe`;
- pass formatting, build, test, dependency, and secret-scanning checks; and
- avoid generated build output, local settings, or release secrets.

Changes to the official Discord sender or release workflow must preserve the
GET-only pre-publication readiness gate, automatic post-publication delivery,
canonical versioned inputs, exact role-only mention, idempotency, and fail-closed
verification. Do not add a form, preview confirmation, or manual publishing
path for official announcements.

Do not edit a release tag after publication. Release preparation and descriptor signing
follow [docs/RELEASING.md](docs/RELEASING.md).

## Reporting security issues

Follow [SECURITY.md](SECURITY.md). Security issues must not be discussed in a
public issue or pull request until the maintainer confirms disclosure is safe.

## Conduct

Participation in this repository is subject to
[CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
