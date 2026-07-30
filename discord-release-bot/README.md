# SessionDock Discord tools

## Official release announcements

Bota publishes official SessionDock release announcements automatically. The
tag-only workflow first builds an immutable announcement and runs a GET-only
`release-announcement` preflight that validates Bota's identity, the target
channel and role, effective least-privilege access, and complete release-marker
history. Only then can it stage, sign, attest, re-download, and publish the
GitHub release. After publication, a second job repeats every check, pings the
configured SessionDock role, and reads the exact resulting message back from
Discord.

There is no announcement form, preview confirmation button, or
assistant-operated publish step in this path. The workflow also creates a
deterministic JSON/Markdown audit artifact, but that artifact is additive: it
never replaces automatic delivery.

The delivery module uses a deterministic message marker and Discord's enforced
nonce. A rerun scans and verifies Bota's existing message before it can post;
conflicting or inconclusive history fails closed instead of risking a duplicate.
All tests use mocked HTTP responses, and repository validation never sends a
Discord message.

Maintainers configure the protected environment described in
[`docs/RELEASING.md`](../docs/RELEASING.md). The official job needs no Gateway
connection or `discord.js` runtime.

Tagging is blocked unless the repository-owner audit confirms that
`release-announcement` is restricted to `v*` tags, has no reviewer gate, and
contains exactly the three pinned IDs plus Bota's environment-scoped token.
GitHub does not reveal or copy an existing token; re-enter it from its approved
secure source or rotate it. The audit also rejects broader-scope fallbacks and
legacy Discord values left on the signing environment.

## Optional community announcement bot

The preserved admin-only `/release` command is an optional community tool. It
is not used by the release workflow and must not be used to publish official
SessionDock releases. It lets a server administrator:

- choose the role to ping;
- choose a text or announcement channel;
- write a title, multiline Markdown release notes, and an optional link in a form;
- upload up to four JPG, PNG, WebP, or GIF images in that same form;
- publish one clean Discord message with a controlled role mention.

The bot intentionally allows only the selected role to ping. Mentions typed inside the title or notes are displayed as text/links but cannot notify other users, roles, `@here`, or `@everyone`.

## Set up Discord

Install [Node.js 22.12 or newer](https://nodejs.org/) first. Node.js 24 LTS works well; alternatively, use the Docker instructions below.

1. Open the [Discord Developer Portal](https://discord.com/developers/applications) and create an application.
2. On **General Information**, copy the **Application ID**.
3. On **Bot**, create/reset the token and copy it. No privileged gateway intents are required.
4. In this folder, create your private environment file:

   ```powershell
   Copy-Item .env.example .env
   ```

5. Put the Application ID in `DISCORD_CLIENT_ID` and the token in `DISCORD_TOKEN`. While testing, also put your server ID in `DISCORD_GUILD_ID`.
6. Install dependencies and print the bot invitation link:

   ```powershell
   npm ci
   npm run community:invite
   ```

7. Make the intended notification role mentionable, then open the printed link and add the bot to your server. The invite requests only View Channel, Read Message History, Send Messages, Embed Links, and Attach Files. It deliberately does not request Administrator, Manage Server, Manage Messages, or Mention Everyone.
8. Register the command and start the bot:

   ```powershell
   npm run community:deploy
   npm run community:start
   ```

Keep the terminal open while using the bot. For permanent hosting, use the Docker option below.

## Publish an optional community update

In Discord, enter `/release` and fill in:

- `role`: the role that should receive the notification;
- `channel`: optional; if omitted, the current channel is used;

Discord then opens one form for the release title, notes, optional release/download link, and up to four screenshots or artwork files. The release is published when you submit the form. Only members with **Manage Server** can use the command.

This form is intentionally outside the canonical release pipeline. Official
release content always comes from `SessionDock/ReleaseNotes/<version>.en-US.md`
and is delivered only by the guarded post-publication job.

## Test server versus global command

With `DISCORD_GUILD_ID` set, `npm run community:deploy` updates only `/release`
in that server and changes appear quickly. When you are happy with it:

1. leave `DISCORD_GUILD_ID` set so the deployer knows which test-server override to remove;
2. run `npm run community:deploy -- --global`;
3. after that command succeeds, remove `DISCORD_GUILD_ID` from `.env` unless you plan to test another revision.

That registers `/release` globally for every server where the bot is installed,
then removes the test-server copy so it cannot shadow the global command. The
deployer updates and deletes `/release` through its individual command endpoint;
it does not bulk-overwrite unrelated application commands. If you deploy
globally without a test-server ID, the deployer cannot locate an older guild
override and prints a warning instead.

## Keep it running with Docker

Starting the long-running container does not invite the bot or register its
command. If you already completed the host-based setup above, start it with:

```powershell
docker compose up -d --build
```

On a Docker-only host, fill in `.env`, then perform the invite and registration
steps inside one-off containers before starting the bot:

```powershell
docker compose build
docker compose run --rm release-bot npm run community:invite
# Open the printed URL and add the bot to the server.
docker compose run --rm release-bot npm run community:deploy
docker compose up -d
```

For global promotion, keep the test `DISCORD_GUILD_ID` in `.env` for the cleanup
step and use `docker compose run --rm release-bot npm run community:deploy -- --global`.

Supply-chain note: the checked-in Dockerfile currently uses the mutable
`node:24-alpine` tag because this repository does not record a reviewed and
tested immutable digest for the required platforms. A rebuild can therefore
select a newer base image. Production operators should resolve and test that
tag for their target platforms, then pin the verified `sha256` digest in the
`FROM` line.

Useful commands:

```powershell
docker compose logs -f
docker compose restart
docker compose down
```

## Configuration

| Variable | Required | Purpose |
| --- | --- | --- |
| `DISCORD_CLIENT_ID` | Yes | Application ID from Discord |
| `DISCORD_TOKEN` | Yes | Secret bot token |
| `DISCORD_GUILD_ID` | No | Test server used for fast registration and `--global` stale-override cleanup |
| `RELEASE_EMBED_COLOR` | No | Six-digit hex color; defaults to Discord blurple |
| `RELEASE_FOOTER` | No | Text shown below every release |
| `MAX_TOTAL_IMAGE_MB` | No | Combined image limit; defaults to 20 MiB and cannot exceed 20 MiB |

## Local checks

```powershell
npm ci
npm test
npm run check
npm audit --omit=dev --audit-level=moderate
```

Never commit `.env` or paste the token into Discord. If a token is exposed, reset it immediately in the Developer Portal.
