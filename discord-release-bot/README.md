# SessionDock Discord tools

This directory contains two separate Discord paths. Choose the path that matches
your job; they are deliberately not interchangeable.

> **Scope:** these are maintainer/community announcement tools. SessionDock
> users do not install or run them, and they have no role in HandleScope,
> ExactWheel, macros, templates, or the laptop execution-policy issue. A Discord
> message cannot turn an untested or provenance-blocked source tree into a
> release.

| Path | Audience | What it does | Official release authority |
| --- | --- | --- | --- |
| Protected release announcement | SessionDock maintainers | Posts the canonical release notes automatically after the guarded GitHub release becomes public | Yes |
| Optional `/release` community bot | Discord server administrators | Publishes an administrator-written community update through a Discord form | No |

The optional community bot is **not used by the release workflow** and **must
not be used to publish official SessionDock releases**. Submitting its form
creates only a Discord message: it does not build, sign, attest, upload, or
publish a GitHub release.

Before configuring an announcement for the integrated workflow, confirm the
complete build/manual-test gate and every component provenance/licensing block
is cleared. This README does not claim that the current source has been
published.

## Official release announcements

The tag-triggered GitHub Actions workflow owns the official path. It generates
the announcement from
`SessionDock/ReleaseNotes/<version>.en-US.md`; there is no announcement form,
preview confirmation, or assistant-operated posting step.

### Configure the protected path

1. Create or select the dedicated Bota application in the
   [Discord Developer Portal](https://discord.com/developers/applications).
2. Give Bota only **View Channel**, **Read Message History**, **Send Messages**,
   and **Embed Links** in the dedicated release channel. Add **Attach Files**
   only when that release can include reviewed images. Keep the notification
   role mentionable.
3. In GitHub, configure the `release-announcement` environment for protected
   `v*` tags with no required reviewer. Put only these values on that
   environment:

   | Kind | Name | Value |
   | --- | --- | --- |
   | Secret | `DISCORD_RELEASE_BOT_TOKEN` | Bota's token |
   | Variable | `DISCORD_RELEASE_BOT_ID` | Pinned Bota user/Application ID |
   | Variable | `DISCORD_RELEASE_CHANNEL_ID` | Dedicated release-channel ID |
   | Variable | `DISCORD_RELEASE_ROLE_ID` | Mentionable SessionDock role ID |

4. From the repository root, sign in to GitHub CLI as a repository
   administrator and run the read/audit pass:

   ```powershell
   ./scripts/Configure-GitHubSecurity.ps1 -WhatIf
   ```

5. Treat every warning or error as a release blocker. The audit requires the
   exact environment-scoped names above, rejects broader-scope fallbacks and
   legacy Discord values on `release`, and confirms that `release-announcement`
   has the reviewed tag policy and no reviewer gate.
6. Prepare the canonical release notes and, when wanted, the current version's
   reviewed `docs/images/sessiondock-vX.Y.Z/discord.json`. Never select artwork
   from an older version. See [the maintainer release guide](../docs/RELEASING.md#optional-reviewed-discord-images)
   for the exact schema and limits.
7. Follow the validation and annotated-tag procedure in
   [the maintainer release guide](../docs/RELEASING.md#prepare-and-validate).
   Approve the separate `release` and `release-publication` environments only
   after reviewing their evidence.
8. Let the post-publication job deliver and read back the announcement. If it
   reports ambiguous delivery, inspect the configured channel and use **Re-run
   failed jobs** for that same workflow run; do not create a new tag or post a
   manual replacement.

### What this path verifies

Before any draft release exists, the GET-only preflight verifies the immutable
announcement bundle, pinned Bota identity, target channel and role, effective
least-privilege permissions, and complete bounded release-marker history. After
GitHub publication, the sender repeats those checks, uses a deterministic
marker and Discord nonce, posts at most once, and reads the exact message and
reviewed attachments back. Conflicting or inconclusive history fails closed.

The generated JSON/Markdown audit artifact records evidence; it does not
replace Discord delivery. The official job needs no Gateway connection and no
`discord.js` runtime. Repository tests use mocked HTTP responses and do not
send Discord messages.

### Trust boundary

- The repository-owner audit can verify secret and variable **names**, scopes,
  environment rules, and visible access policy. GitHub does not reveal stored
  secret values, so the audit cannot prove where a token originally came from
  or print/copy it.
- The workflow validates the effective IDs and Bota identity at runtime. An
  organization owner must separately confirm any organization-level value
  visibility that a repository administrator cannot inspect.
- The Discord proof covers the announcement's identity, content, attachments,
  permissions, and delivery. It is not Windows publisher identity and does not
  replace the release assets' independent hashes, signed descriptor, or GitHub
  attestations.
- Never add Administrator, Manage Server, Manage Messages, or Mention Everyone
  to Bota. The protected preflight rejects those effective permissions.

The complete environment, release, and recovery contract is maintained in
[`docs/RELEASING.md`](../docs/RELEASING.md).

## Optional community announcement bot

Use this path only when a Discord server administrator wants to publish a
noncanonical community update. The `/release` command lets a member with
**Manage Server**:

- choose one role to notify;
- choose a text or announcement channel, or use the current channel;
- enter a title, multiline Markdown notes, and an optional link; and
- attach up to four JPG, PNG, WebP, or GIF images.

Only the selected role can notify users. Mentions typed into the title or notes
are rendered without notifying other users, roles, `@here`, or `@everyone`.

### Install and test in one server

Prerequisites: [Node.js 22.12 or newer](https://nodejs.org/) and a Discord
application. Node.js 24 LTS is the closest local match to the workflow's pinned
Node.js 24 runtime.

1. In the Discord Developer Portal, open **General Information** and copy the
   **Application ID**.
2. Open **Bot**, create or reset the token, and copy it to a secure temporary
   location. No privileged Gateway intents are required.
3. From the repository root, enter this directory, create the private local
   configuration, and install the locked dependencies:

   ```powershell
   Set-Location ./discord-release-bot
   Copy-Item .env.example .env
   npm ci
   ```

4. Edit `.env`. Set `DISCORD_CLIENT_ID`, `DISCORD_TOKEN`, and the test server's
   ID in `DISCORD_GUILD_ID`. Keep `RELEASE_EMBED_COLOR` quoted because its value
   starts with `#`.
5. Print the least-privilege invitation URL:

   ```powershell
   npm run community:invite
   ```

6. Make the intended notification role mentionable, open the printed URL, and
   add the bot to the test server. The invitation requests only View Channel,
   Read Message History, Send Messages, Embed Links, and Attach Files; it does
   not request Administrator or Mention Everyone.
7. Register `/release` only in the configured test server:

   ```powershell
   npm run community:deploy
   ```

8. Start the bot and leave the terminal open:

   ```powershell
   npm run community:start
   ```

9. In Discord, enter `/release`, choose the role and optional channel, complete
   the form, and submit it. Form submission publishes the community message.

### Promote `/release` globally

After testing succeeds:

1. Keep `DISCORD_GUILD_ID` in `.env` so the deployer can locate and remove the
   test-server override.
2. Run:

   ```powershell
   npm run community:deploy -- --global
   ```

3. Confirm that the command reports a global registration and stale guild
   override cleanup.
4. Remove `DISCORD_GUILD_ID` from `.env` unless another test-server deployment
   is planned.

The deployer updates only the individual `/release` command; it does not
bulk-overwrite unrelated application commands. If `DISCORD_GUILD_ID` is absent,
global registration still works, but the deployer cannot find an old guild
override and prints a warning.

## Run the community bot with Docker

Run these commands from `discord-release-bot`. If the host-based steps above
already invited the bot and registered the command, build and start it with:

```powershell
docker compose up -d --build
```

For a Docker-only setup:

1. Copy `.env.example` to `.env` and fill in the required values.
2. Build the image and print the invitation:

   ```powershell
   docker compose build
   docker compose run --rm release-bot npm run community:invite
   ```

3. Open the printed URL and add the bot to the server.
4. Register the test-server command, then start the long-running service:

   ```powershell
   docker compose run --rm release-bot npm run community:deploy
   docker compose up -d
   ```

For global promotion, keep the test `DISCORD_GUILD_ID` through cleanup and run:

```powershell
docker compose run --rm release-bot npm run community:deploy -- --global
```

Operational commands:

```powershell
docker compose logs -f
docker compose restart
docker compose down
```

The checked-in Dockerfile uses the mutable `node:24-alpine` tag because the
repository does not contain a reviewed, cross-platform image digest. A later
build may therefore use different base-image bytes. Production operators
should resolve, test, and pin the appropriate `sha256` digest for every target
platform before treating a container build as reproducible.

## Community-bot configuration

| Variable | Required | Purpose |
| --- | --- | --- |
| `DISCORD_CLIENT_ID` | Yes | Application ID from the Developer Portal |
| `DISCORD_TOKEN` | Yes, except when only printing the invitation | Private bot token |
| `DISCORD_GUILD_ID` | No | Fast test-server registration and `--global` stale-override cleanup |
| `RELEASE_EMBED_COLOR` | No | Quoted six-digit hex color; defaults to Discord blurple |
| `RELEASE_FOOTER` | No | Footer shown below each community update |
| `MAX_TOTAL_IMAGE_MB` | No | Combined image limit; defaults to 20 MiB and cannot exceed 20 MiB |

Each image also has a hard 10 MiB limit. Discord's entire message request is
limited to 25 MiB, so raising the configured aggregate limit above 20 MiB is
intentionally rejected.

## Local checks

Run these commands from `discord-release-bot` before changing or deploying the
community bot:

```powershell
npm ci
npm test
npm run check
npm audit --omit=dev --audit-level=moderate
```

`npm test` uses mocks and does not contact Discord. `npm audit` contacts the npm
registry and reports moderate-or-higher production dependency findings.

Never commit `.env`, paste the token into Discord, or reuse the protected
official-announcement credential for the optional community bot. If a token is
exposed, reset it immediately in the Developer Portal and update only the
intended secret store.
