import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
  copyFileSync,
  mkdtempSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  rmSync,
  statSync,
  symlinkSync,
  writeFileSync,
} from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import {
  createDeliveryReceipt,
  diagnoseExistingAnnouncement,
  deliverAnnouncement,
  generateAnnouncement,
  migrateExistingAnnouncement,
  preflightAnnouncement,
  readAnnouncementBundle,
  ReleaseAutomationError,
  sha256,
} from "../src/release-automation.js";

const COMMIT = "0123456789abcdef0123456789abcdef01234567";
const BOT_ID = "123456789012345678";
const GUILD_ID = "223456789012345678";
const CHANNEL_ID = "323456789012345678";
const ROLE_ID = "423456789012345678";
const MESSAGE_ID = "523456789012345678";
const OTHER_MESSAGE_ID = "623456789012345678";
const BOT_ROLE_A_ID = "723456789012345678";
const BOT_ROLE_B_ID = "823456789012345678";
const TOKEN = "test-token-that-is-never-sent-to-discord";
const APPROVED_VERSION = "3.1.2";
const APPROVED_TAG = `v${APPROVED_VERSION}`;
const APPROVED_COMMIT = "0e257dd53b1c729bbf109185a10d470a1dd6483d";
const APPROVED_SOURCE_ANNOUNCEMENT = "2022db1656d2125f5ea79ce69e1b91e57292423488713278321f2eaeeef2ec83";
const APPROVED_SOURCE_ARTIFACT = "ccbcceb02c21a8d38bd14cd9f9369d34648016412f6e5006c3cbd1f141abfae4";
const APPROVED_TARGET_ANNOUNCEMENT = "969011eb65d290bcf8d410da7d5050c46e5d226ab5c1b626e095b6653de28e93";
const APPROVED_TARGET_ARTIFACT = "ee32312ca14ed64bc9e5a7bc2a54beba3b17595c2b0776a5c327d3300b8c6db5";
const BOT_CHANNEL_PERMISSIONS = String(1024 + 2048 + 16384 + 32768 + 65536);
const TEST_PERMISSION_ADMINISTRATOR = 1n << 3n;
const TEST_PERMISSION_VIEW_CHANNEL = 1n << 10n;
const JSON_RESPONSE_LIMIT = 1024 * 1024;
const AUTOMATION_SCRIPT = fileURLToPath(new URL("../src/release-automation.js", import.meta.url));
const PNG = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=",
  "base64",
);

function jsonResponse(value, status = 200, headers) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "content-type": "application/json", ...headers },
  });
}

function streamedResponse(
  chunks,
  { status = 200, headers = {}, onRead = () => {}, onCancel = () => {}, readError } = {},
) {
  let index = 0;
  return {
    body: {
      getReader() {
        return {
          async cancel() {
            onCancel();
          },
          async read() {
            onRead();
            if (readError) {
              throw readError;
            }
            if (index === chunks.length) {
              return { done: true, value: undefined };
            }
            const value = chunks[index];
            index += 1;
            return { done: false, value };
          },
          releaseLock() {},
        };
      },
    },
    headers: new Headers(headers),
    ok: status >= 200 && status < 300,
    status,
  };
}

function fixtureNotes(version = "2.7.2") {
  return [
    `SessionDock ${version}`,
    "",
    "Automatic guarded announcement",
    "",
    "- Notes can contain @everyone, @here, <@123456789012345678>, and <@&999999999999999999> without pinging them.",
    "",
  ].join("\n");
}

function createFixture(
  t,
  {
    withImage = false,
    notes = fixtureNotes(),
    imageFileName = "sessiondock-v2.7.2-social-wide.png",
    imageFileNames = [imageFileName],
    schemaVersion = 3,
  } = {},
) {
  const root = mkdtempSync(path.join(os.tmpdir(), "sessiondock-release-automation-"));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const notesDirectory = path.join(root, "SessionDock", "ReleaseNotes");
  mkdirSync(notesDirectory, { recursive: true });
  writeFileSync(path.join(notesDirectory, "2.7.2.en-US.md"), notes);

  let imagesPath;
  if (withImage) {
    imagesPath = "docs/images/sessiondock-v2.7.2";
    const imageDirectory = path.join(root, ...imagesPath.split("/"));
    mkdirSync(imageDirectory, { recursive: true });
    for (const fileName of imageFileNames) {
      writeFileSync(path.join(imageDirectory, fileName), PNG);
    }
    writeFileSync(
      path.join(imageDirectory, "discord.json"),
      `${JSON.stringify(
        {
          images: imageFileNames,
          product: "SessionDock",
          schemaVersion: 1,
          version: "2.7.2",
        },
        null,
        2,
      )}\n`,
    );
    writeFileSync(
      path.join(imageDirectory, "manifest.json"),
      `${JSON.stringify(
        {
          product: "SessionDock",
          version: "2.7.2",
          outputs: imageFileNames.map((file) => ({ file, sha256: sha256(PNG), width: 1, height: 1 })),
        },
        null,
        2,
      )}\n`,
    );
  }

  generateAnnouncement({
    root,
    version: "2.7.2",
    sourceCommit: COMMIT,
    notesPath: "SessionDock/ReleaseNotes/2.7.2.en-US.md",
    imagesPath,
    outputPath: "artifacts/announcement",
    schemaVersion,
  });
  const artifactDirectory = path.join(root, "artifacts", "announcement");
  const bundle = readAnnouncementBundle({
    artifactDirectory,
    expectedTag: "v2.7.2",
    expectedRef: "refs/tags/v2.7.2",
    expectedCommit: COMMIT,
  });
  return { artifactDirectory, bundle, root };
}

function createApprovedMigrationFixture(t) {
  const root = mkdtempSync(path.join(os.tmpdir(), "sessiondock-approved-discord-migration-"));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const notesDirectory = path.join(root, "SessionDock", "ReleaseNotes");
  mkdirSync(notesDirectory, { recursive: true });
  const canonicalNotes = readFileSync(
    fileURLToPath(new URL(`../../SessionDock/ReleaseNotes/${APPROVED_VERSION}.en-US.md`, import.meta.url)),
  );
  writeFileSync(path.join(notesDirectory, `${APPROVED_VERSION}.en-US.md`), canonicalNotes);
  const notesPath = `SessionDock/ReleaseNotes/${APPROVED_VERSION}.en-US.md`;
  generateAnnouncement({
    root,
    version: APPROVED_VERSION,
    sourceCommit: APPROVED_COMMIT,
    notesPath,
    outputPath: "artifacts/source-announcement",
    schemaVersion: 2,
  });
  generateAnnouncement({
    root,
    version: APPROVED_VERSION,
    sourceCommit: APPROVED_COMMIT,
    notesPath,
    outputPath: "artifacts/target-announcement",
    schemaVersion: 3,
  });
  const readBundle = (directory) =>
    readAnnouncementBundle({
      artifactDirectory: path.join(root, "artifacts", directory),
      expectedTag: APPROVED_TAG,
      expectedRef: `refs/tags/${APPROVED_TAG}`,
      expectedCommit: APPROVED_COMMIT,
    });
  const sourceBundle = readBundle("source-announcement");
  const targetBundle = readBundle("target-announcement");
  assert.equal(sourceBundle.artifact.announcement.id, APPROVED_SOURCE_ANNOUNCEMENT);
  assert.equal(sourceBundle.artifactDigest, APPROVED_SOURCE_ARTIFACT);
  assert.equal(targetBundle.artifact.announcement.id, APPROVED_TARGET_ANNOUNCEMENT);
  assert.equal(targetBundle.artifactDigest, APPROVED_TARGET_ARTIFACT);
  return { root, sourceBundle, targetBundle };
}

function deliveryEnv(overrides = {}) {
  return {
    DISCORD_RELEASE_BOT_ID: BOT_ID,
    DISCORD_RELEASE_BOT_TOKEN: TOKEN,
    DISCORD_RELEASE_CHANNEL_ID: CHANNEL_ID,
    DISCORD_RELEASE_ROLE_ID: ROLE_ID,
    ...overrides,
  };
}

function discordPayload(bundle) {
  const payload = {
    allowed_mentions: {
      replied_user: false,
      roles: [ROLE_ID],
      users: [],
    },
    content: `<@&${ROLE_ID}>`,
    ...bundle.artifact.announcement.message,
    enforce_nonce: true,
    nonce: bundle.artifact.announcement.nonce,
  };
  if (bundle.images.length) {
    payload.attachments = bundle.artifact.announcement.attachments.map((attachment) => ({
      description: `SessionDock 2.7.2 reviewed release image`,
      filename: attachment.fileName,
      id: attachment.id,
    }));
  }
  return payload;
}

function discordMessage(bundle, overrides = {}) {
  const payload = discordPayload(bundle);
  const attachments = bundle.artifact.announcement.attachments.map((attachment, index) => ({
    description: `SessionDock 2.7.2 reviewed release image`,
    filename: attachment.fileName,
    id: String(index),
    size: attachment.bytes,
    url: `https://cdn.discordapp.com/attachments/${CHANNEL_ID}/${MESSAGE_ID}/${attachment.fileName}`,
  }));
  const embeds = structuredClone(payload.embeds);
  for (let index = 0; index < attachments.length; index += 1) {
    embeds[index].image.url = attachments[index].url;
  }
  return {
    attachments,
    author: { bot: true, id: BOT_ID },
    channel_id: CHANNEL_ID,
    components: payload.components ?? [],
    content: payload.content,
    edited_timestamp: null,
    embeds,
    flags: 0,
    id: MESSAGE_ID,
    mention_everyone: false,
    mention_roles: [ROLE_ID],
    mentions: [],
    nonce: payload.nonce,
    pinned: false,
    tts: false,
    type: 0,
    ...overrides,
  };
}

function migratedDiscordMessage(sourceBundle, targetBundle, flags, overrides = {}) {
  return discordMessage(targetBundle, {
    edited_timestamp: "2026-08-18T12:34:56.862000+00:00",
    flags,
    nonce: sourceBundle.artifact.announcement.nonce,
    ...overrides,
  });
}

function approvedMigrationArguments(bundle, overrides = {}) {
  return {
    bundle,
    expectedTargetAnnouncementId: APPROVED_TARGET_ANNOUNCEMENT,
    expectedTargetArtifactSha256: APPROVED_TARGET_ARTIFACT,
    env: deliveryEnv(),
    sleepImpl: async () => {},
    ...overrides,
  };
}

function createMigrationFetchHarness({
  sourceBundle,
  targetBundle,
  sourceFlags = 4,
  historyMessages,
  rereadSource,
  finalMessage,
  patchMode = "accepted",
}) {
  const source = discordMessage(sourceBundle, { flags: sourceFlags });
  const finalFlags = sourceFlags & ~4;
  const target = finalMessage ?? migratedDiscordMessage(sourceBundle, targetBundle, finalFlags);
  const history = historyMessages ?? [source];
  const requests = [];
  const patchBodies = [];
  let patched = false;
  let historyReads = 0;
  const fetchImpl = async (url, init = {}) => {
    const requestUrl = String(url);
    const method = init.method ?? "GET";
    requests.push({ method, url: requestUrl });
    const response = preflightResponse(requestUrl);
    if (response) return response;
    if (requestUrl.includes(`/channels/${CHANNEL_ID}/messages?limit=100`)) {
      historyReads += 1;
      return jsonResponse(history);
    }
    if (requestUrl.endsWith(`/channels/${CHANNEL_ID}/messages/${MESSAGE_ID}`) && method === "GET") {
      return jsonResponse(patched ? target : rereadSource ?? source);
    }
    if (requestUrl.endsWith(`/channels/${CHANNEL_ID}/messages/${MESSAGE_ID}`) && method === "PATCH") {
      patchBodies.push(JSON.parse(init.body));
      patched = true;
      if (patchMode === "network-ambiguous") {
        throw new Error("The PATCH response was lost after acceptance.");
      }
      if (patchMode === "server-ambiguous") {
        return jsonResponse({ message: "upstream error" }, 503);
      }
      if (patchMode === "timeout-ambiguous") {
        return jsonResponse({ message: "request timeout" }, 408);
      }
      if (patchMode === "rate-limit-ambiguous") {
        return jsonResponse({ retry_after: 30 }, 429);
      }
      if (patchMode === "accepted-unusable-body") {
        return new Response("not-json", { status: 200, headers: { "content-type": "text/plain" } });
      }
      return jsonResponse(target);
    }
    throw new Error(`Unexpected migration request: ${method} ${requestUrl}`);
  };
  return {
    fetchImpl,
    get historyReads() {
      return historyReads;
    },
    patchBodies,
    requests,
    source,
    target,
  };
}

function preflightResponse(url) {
  if (url === "https://discord.com/api/v10/users/@me") {
    return jsonResponse({ bot: true, id: BOT_ID });
  }
  if (url === `https://discord.com/api/v10/channels/${CHANNEL_ID}`) {
    return jsonResponse({
      guild_id: GUILD_ID,
      id: CHANNEL_ID,
      last_message_id: null,
      permission_overwrites: [],
      type: 0,
    });
  }
  if (url === `https://discord.com/api/v10/guilds/${GUILD_ID}/roles`) {
    return jsonResponse([
      { id: GUILD_ID, managed: false, mentionable: false, name: "@everyone", permissions: BOT_CHANNEL_PERMISSIONS },
      { id: ROLE_ID, managed: false, mentionable: true, name: "SessionDock", permissions: "0" },
    ]);
  }
  if (url === `https://discord.com/api/v10/guilds/${GUILD_ID}/members/${BOT_ID}`) {
    return jsonResponse({ roles: [], user: { bot: true, id: BOT_ID } });
  }
  return null;
}

function installChildFetchMock(root) {
  const preload = path.join(root, "mock-discord-fetch.mjs");
  writeFileSync(
    preload,
    `import { appendFileSync, readFileSync } from "node:fs";
import path from "node:path";

const artifact = JSON.parse(readFileSync(path.join(process.cwd(), "artifacts", "announcement", "announcement.json"), "utf8"));
const botId = "${BOT_ID}";
const guildId = "${GUILD_ID}";
const channelId = "${CHANNEL_ID}";
const roleId = "${ROLE_ID}";
const messageId = "${MESSAGE_ID}";
const message = {
  attachments: [],
  author: { bot: true, id: botId },
  channel_id: channelId,
  components: artifact.announcement.message.components ?? [],
  content: "<@&" + roleId + ">",
  edited_timestamp: null,
  embeds: artifact.announcement.message.embeds,
  flags: 0,
  id: messageId,
  mention_everyone: false,
  mention_roles: [roleId],
  mentions: [],
  nonce: artifact.announcement.nonce,
  pinned: false,
  tts: false,
  type: 0,
};
if (process.env.MOCK_MODE === "diagnostic") {
  message.shared_client_theme = { attacker: process.env.ATTACKER_VALUE };
}
let historyReads = 0;
function json(value, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: { "content-type": "application/json" } });
}
globalThis.fetch = async (url, init) => {
  appendFileSync(process.env.FETCH_LOG, init.method + " " + String(url) + "\\n");
  const requestUrl = String(url);
  if (requestUrl.endsWith("/users/@me")) return json({ bot: true, id: botId });
  if (requestUrl.endsWith("/channels/" + channelId)) {
    return json({ guild_id: guildId, id: channelId, last_message_id: null, permission_overwrites: [], type: 0 });
  }
  if (requestUrl.endsWith("/guilds/" + guildId + "/roles")) {
    return json([
      { id: guildId, managed: false, mentionable: false, name: "@everyone", permissions: "${BOT_CHANNEL_PERMISSIONS}" },
      { id: roleId, managed: false, mentionable: true, name: "SessionDock", permissions: "0" },
    ]);
  }
  if (requestUrl.endsWith("/guilds/" + guildId + "/members/" + botId)) {
    return json({ roles: [], user: { bot: true, id: botId } });
  }
  if (requestUrl.includes("/messages?limit=100")) {
    historyReads += 1;
    if (process.env.MOCK_MODE === "diagnostic") return json([message]);
    return json(process.env.MOCK_MODE === "ambiguous" || historyReads === 1 ? [] : [message]);
  }
  if (requestUrl.endsWith("/messages") && init.method === "POST") {
    if (process.env.MOCK_MODE === "ambiguous") throw new Error("accepted response lost");
    return json(message);
  }
  if (requestUrl.endsWith("/messages/" + messageId)) return json(message);
  throw new Error("Unexpected mocked Discord request");
};
`,
  );
  return preload;
}

function stageStandaloneAutomation(root) {
  const stagedDirectory = path.join(root, "release-input");
  mkdirSync(stagedDirectory, { recursive: true });
  const stagedScript = path.join(stagedDirectory, "release-automation.mjs");
  copyFileSync(AUTOMATION_SCRIPT, stagedScript);
  return stagedScript;
}

function runCliChild(fixture, receiptPath, mode = "confirmed", automationScript = AUTOMATION_SCRIPT) {
  const preload = installChildFetchMock(fixture.root);
  const fetchLog = path.join(fixture.root, "fetch.log");
  const result = spawnSync(
    process.execPath,
    [
      automationScript,
      "post",
      "--artifact-dir",
      "artifacts/announcement",
      "--expected-tag",
      "v2.7.2",
      "--expected-ref",
      "refs/tags/v2.7.2",
      "--expected-commit",
      COMMIT,
      "--receipt",
      receiptPath,
    ],
    {
      cwd: fixture.root,
      encoding: "utf8",
      env: {
        ...process.env,
        DISCORD_RELEASE_BOT_ID: BOT_ID,
        DISCORD_RELEASE_BOT_TOKEN: TOKEN,
        DISCORD_RELEASE_CHANNEL_ID: CHANNEL_ID,
        DISCORD_RELEASE_ROLE_ID: ROLE_ID,
        FETCH_LOG: fetchLog,
        MOCK_MODE: mode,
        NODE_OPTIONS: `--import=${pathToFileURL(preload).href}`,
      },
    },
  );
  return { fetchLog, result };
}

function runPreflightCliChild(fixture, automationScript = AUTOMATION_SCRIPT) {
  const preload = installChildFetchMock(fixture.root);
  const fetchLog = path.join(fixture.root, "preflight-fetch.log");
  const result = spawnSync(
    process.execPath,
    [
      automationScript,
      "preflight",
      "--artifact-dir",
      "artifacts/announcement",
      "--expected-tag",
      "v2.7.2",
      "--expected-ref",
      "refs/tags/v2.7.2",
      "--expected-commit",
      COMMIT,
    ],
    {
      cwd: fixture.root,
      encoding: "utf8",
      env: {
        ...process.env,
        DISCORD_RELEASE_BOT_ID: BOT_ID,
        DISCORD_RELEASE_BOT_TOKEN: TOKEN,
        DISCORD_RELEASE_CHANNEL_ID: CHANNEL_ID,
        DISCORD_RELEASE_ROLE_ID: ROLE_ID,
        FETCH_LOG: fetchLog,
        NODE_OPTIONS: `--import=${pathToFileURL(preload).href}`,
      },
    },
  );
  return { fetchLog, result };
}

function runDiagnosticCliChild(fixture, reportPath, automationScript = AUTOMATION_SCRIPT) {
  const preload = installChildFetchMock(fixture.root);
  const fetchLog = path.join(fixture.root, "diagnostic-fetch.log");
  const attackerValue = `message value containing ${TOKEN}`;
  const result = spawnSync(
    process.execPath,
    [
      automationScript,
      "diagnose-existing",
      "--artifact-dir",
      "artifacts/announcement",
      "--expected-tag",
      "v2.7.2",
      "--expected-ref",
      "refs/tags/v2.7.2",
      "--expected-commit",
      COMMIT,
      "--report",
      reportPath,
    ],
    {
      cwd: fixture.root,
      encoding: "utf8",
      env: {
        ...process.env,
        ATTACKER_VALUE: attackerValue,
        DISCORD_RELEASE_BOT_ID: BOT_ID,
        DISCORD_RELEASE_BOT_TOKEN: TOKEN,
        DISCORD_RELEASE_CHANNEL_ID: CHANNEL_ID,
        DISCORD_RELEASE_ROLE_ID: ROLE_ID,
        FETCH_LOG: fetchLog,
        MOCK_MODE: "diagnostic",
        NODE_OPTIONS: `--import=${pathToFileURL(preload).href}`,
      },
    },
  );
  return { attackerValue, fetchLog, result };
}

function installMigrationChildFetchMock(root) {
  const preload = path.join(root, "mock-discord-migration-fetch.mjs");
  writeFileSync(
    preload,
    `import { appendFileSync, readFileSync } from "node:fs";
import path from "node:path";

const source = JSON.parse(readFileSync(path.join(process.cwd(), "artifacts", "source-announcement", "announcement.json"), "utf8"));
const target = JSON.parse(readFileSync(path.join(process.cwd(), "artifacts", "target-announcement", "announcement.json"), "utf8"));
const botId = "${BOT_ID}";
const guildId = "${GUILD_ID}";
const channelId = "${CHANNEL_ID}";
const roleId = "${ROLE_ID}";
const messageId = "${MESSAGE_ID}";
function message(artifact, flags, editedTimestamp) {
  return {
    attachments: [],
    author: { bot: true, id: botId },
    channel_id: channelId,
    components: artifact.announcement.message.components ?? [],
    content: "<@&" + roleId + ">",
    edited_timestamp: editedTimestamp,
    embeds: artifact.announcement.message.embeds,
    flags,
    id: messageId,
    mention_everyone: false,
    mention_roles: [roleId],
    mentions: [],
    nonce: source.announcement.nonce,
    pinned: false,
    tts: false,
    type: 0,
  };
}
const oldMessage = message(source, 4, null);
const newMessage = message(target, 0, "2026-08-18T12:34:56.862000+00:00");
let patched = false;
function json(value, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: { "content-type": "application/json" } });
}
globalThis.fetch = async (url, init = {}) => {
  const method = init.method ?? "GET";
  const requestUrl = String(url);
  appendFileSync(process.env.FETCH_LOG, method + " " + requestUrl + "\\n");
  if (requestUrl.endsWith("/users/@me")) return json({ bot: true, id: botId });
  if (requestUrl.endsWith("/channels/" + channelId)) {
    return json({ guild_id: guildId, id: channelId, last_message_id: null, permission_overwrites: [], type: 0 });
  }
  if (requestUrl.endsWith("/guilds/" + guildId + "/roles")) {
    return json([
      { id: guildId, managed: false, mentionable: false, name: "@everyone", permissions: "${BOT_CHANNEL_PERMISSIONS}" },
      { id: roleId, managed: false, mentionable: true, name: "SessionDock", permissions: "0" },
    ]);
  }
  if (requestUrl.endsWith("/guilds/" + guildId + "/members/" + botId)) {
    return json({ roles: [], user: { bot: true, id: botId } });
  }
  if (requestUrl.includes("/messages?limit=100")) return json([oldMessage]);
  if (requestUrl.endsWith("/messages/" + messageId) && method === "GET") {
    return json(patched ? newMessage : oldMessage);
  }
  if (requestUrl.endsWith("/messages/" + messageId) && method === "PATCH") {
    patched = true;
    return json(newMessage);
  }
  throw new Error("Unexpected mocked migration request");
};
`,
  );
  return preload;
}

function runMigrationCliChild(
  fixture,
  receiptPath,
  {
    expectedTargetAnnouncement = APPROVED_TARGET_ANNOUNCEMENT,
    expectedTargetArtifactSha256 = APPROVED_TARGET_ARTIFACT,
    automationScript = AUTOMATION_SCRIPT,
  } = {},
) {
  const preload = installMigrationChildFetchMock(fixture.root);
  const fetchLog = path.join(fixture.root, "migration-fetch.log");
  const result = spawnSync(
    process.execPath,
    [
      automationScript,
      "migrate-existing",
      "--artifact-dir",
      "artifacts/source-announcement",
      "--expected-tag",
      APPROVED_TAG,
      "--expected-ref",
      `refs/tags/${APPROVED_TAG}`,
      "--expected-commit",
      APPROVED_COMMIT,
      "--expected-target-announcement",
      expectedTargetAnnouncement,
      "--expected-target-artifact-sha256",
      expectedTargetArtifactSha256,
      "--receipt",
      receiptPath,
    ],
    {
      cwd: fixture.root,
      encoding: "utf8",
      env: {
        ...process.env,
        DISCORD_RELEASE_BOT_ID: BOT_ID,
        DISCORD_RELEASE_BOT_TOKEN: TOKEN,
        DISCORD_RELEASE_CHANNEL_ID: CHANNEL_ID,
        DISCORD_RELEASE_ROLE_ID: ROLE_ID,
        FETCH_LOG: fetchLog,
        NODE_OPTIONS: `--import=${pathToFileURL(preload).href}`,
      },
    },
  );
  return { fetchLog, result };
}

test("the staged standalone module executes workflow-shaped generate and verify commands", (t) => {
  const root = mkdtempSync(path.join(os.tmpdir(), "sessiondock-release-cli-"));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const notesDirectory = path.join(root, "SessionDock", "ReleaseNotes");
  const stagedDirectory = path.join(root, "release-input");
  mkdirSync(notesDirectory, { recursive: true });
  mkdirSync(stagedDirectory, { recursive: true });
  writeFileSync(path.join(notesDirectory, "2.7.2.en-US.md"), fixtureNotes());
  const stagedScript = path.join(stagedDirectory, "release-automation.mjs");
  copyFileSync(AUTOMATION_SCRIPT, stagedScript);

  const generate = spawnSync(
    process.execPath,
    [
      stagedScript,
      "generate",
      "--version",
      "2.7.2",
      "--source-commit",
      COMMIT,
      "--notes",
      "SessionDock/ReleaseNotes/2.7.2.en-US.md",
      "--output",
      "artifacts/announcement",
    ],
    { cwd: root, encoding: "utf8" },
  );
  assert.equal(generate.status, 0, generate.stderr);
  assert.deepEqual(readdirSync(path.join(root, "artifacts", "announcement")).sort(), [
    "announcement.json",
    "announcement.sha256",
    "notes.md",
    "summary.md",
  ]);

  const verify = spawnSync(
    process.execPath,
    [
      stagedScript,
      "verify",
      "--artifact-dir",
      "artifacts/announcement",
      "--expected-tag",
      "v2.7.2",
      "--expected-ref",
      "refs/tags/v2.7.2",
      "--expected-commit",
      COMMIT,
    ],
    { cwd: root, encoding: "utf8" },
  );
  assert.equal(verify.status, 0, verify.stderr);
  assert.match(verify.stdout, /Verified Discord announcement/);
});

test("generation is byte-for-byte deterministic and records canonical sources", (t) => {
  const first = createFixture(t);
  const second = createFixture(t);
  for (const file of ["announcement.json", "announcement.sha256", "notes.md", "summary.md"]) {
    assert.deepEqual(
      readFileSync(path.join(first.artifactDirectory, file)),
      readFileSync(path.join(second.artifactDirectory, file)),
    );
  }
  assert.equal(first.bundle.artifact.schemaVersion, 3);
  assert.equal(
    first.bundle.artifact.release.portableUrl,
    "https://github.com/Makmatoe/SessionDock/releases/download/v2.7.2/SessionDock-win-x64-Portable.zip",
  );
  assert.equal(
    first.bundle.artifact.release.latestUrl,
    "https://github.com/Makmatoe/SessionDock/releases/latest",
  );
  assert.equal(Object.hasOwn(first.bundle.artifact.release, "installerUrl"), false);
  assert.equal(
    first.bundle.artifact.announcement.message.components[0].components[0].url,
    "https://github.com/Makmatoe/SessionDock/releases/download/v2.7.2/SessionDock-win-x64-Portable.zip",
  );
  assert.equal(
    first.bundle.artifact.announcement.message.components[0].components[1].url,
    "https://github.com/Makmatoe/SessionDock/releases/latest",
  );
  assert.equal(first.bundle.artifact.announcement.message.embeds[0].title, "SessionDock 2.7.2 is available");
  assert.ok(first.bundle.artifact.announcement.message.embeds[0].description.length < 180);
  assert.equal(
    first.bundle.artifact.announcement.message.embeds[0].url,
    "https://github.com/Makmatoe/SessionDock/releases/tag/v2.7.2",
  );
  assert.match(first.bundle.artifact.announcement.marker, /^sdrel:v2:v2\.7\.2:[0-9a-f]{32}$/);
  assert.match(
    first.bundle.artifact.announcement.message.embeds[0].footer.text,
    /^Official SessionDock release • v2\.7\.2 • ID [0-9a-f]{32}$/,
  );
  assert.equal(first.bundle.artifact.release.sourceCommit, COMMIT);
  assert.equal(first.bundle.artifact.sources.releaseNotes.canonicalPath, "SessionDock/ReleaseNotes/2.7.2.en-US.md");
  assert.match(first.bundle.summaryText, /Bota delivers it automatically only after/);
  assert.match(first.bundle.summaryText, /No form, preview confirmation, or manual publish action/);
});

test("schema 3 renders compact feature sections and prominent official links", (t) => {
  const notes = [
    "SessionDock 2.7.2",
    "",
    "Release approval",
    "",
    "- Internal gate detail that should not dominate the community announcement.",
    "",
    "Sessions from one home screen",
    "",
    "- Accounts and destinations now stay visible from Home. The complete details remain on GitHub.",
    "- Templates keep the selected destinations and scalable layouts.",
    "- A third detail belongs in the full release notes only.",
    "",
    "Continuous macros",
    "",
    "- Playback loops until the user explicitly stops it. Temporary lag pauses safely.",
    "- Client switching includes a bounded settle delay.",
    "",
    "Guidance",
    "",
    "- First launch includes Get Started and Advanced tours.",
    "",
  ].join("\n");
  const { bundle } = createFixture(t, { notes });
  const message = bundle.artifact.announcement.message;
  const primary = message.embeds[0];
  assert.equal(primary.fields.length, 3);
  assert.doesNotMatch(JSON.stringify(primary), /Release approval|Internal gate detail/);
  assert.equal(primary.fields[0].value.split("\n").length, 3);
  assert.ok(primary.fields.every((field) => field.value.split("\n").length <= 3));
  assert.ok(
    primary.fields.reduce((total, field) => total + field.name.length + field.value.length, 0) < 2_000,
  );
  assert.equal(
    primary.footer.text,
    `Official SessionDock release • v2.7.2 • ID ${bundle.artifact.announcement.marker.split(":").at(-1)}`,
  );
  assert.deepEqual(
    message.components[0].components.map(({ label, style, type, url }) => ({ label, style, type, url })),
    [
      {
        label: "Download portable ZIP",
        style: 5,
        type: 2,
        url: "https://github.com/Makmatoe/SessionDock/releases/download/v2.7.2/SessionDock-win-x64-Portable.zip",
      },
      {
        label: "View latest release",
        style: 5,
        type: 2,
        url: "https://github.com/Makmatoe/SessionDock/releases/latest",
      },
    ],
  );
});

test("schema 3 truncates Unicode release highlights without producing invalid surrogate halves", (t) => {
  const notes = [
    "SessionDock 2.7.2",
    "",
    "Unicode-safe highlights",
    "",
    `- ${"😀".repeat(400)}`,
    "",
  ].join("\n");
  const { bundle } = createFixture(t, { notes });
  const value = bundle.artifact.announcement.message.embeds[0].fields[0].value;
  assert.match(value, /…$/u);
  assert.equal(Buffer.from(value, "utf8").toString("utf8"), value);
  assert.doesNotMatch(value, /�/u);
});

test("schema 3 budgets three long highlights within one Discord field", (t) => {
  const longHighlight = "A".repeat(600);
  const notes = [
    "SessionDock 2.7.2",
    "",
    "Performance",
    "",
    `- ${longHighlight}`,
    `- ${longHighlight}`,
    `- ${longHighlight}`,
    "",
  ].join("\n");
  const { bundle } = createFixture(t, { notes });
  const field = bundle.artifact.announcement.message.embeds[0].fields[0];
  assert.equal(field.value.split("\n").length, 3);
  assert.ok(field.value.length <= 1_024);
});

test("bundle validation preserves schema-2 compatibility and rejects installer identities", (t) => {
  const legacy = createFixture(t, { schemaVersion: 2 });
  assert.equal(legacy.bundle.artifact.schemaVersion, 2);
  assert.equal(Object.hasOwn(legacy.bundle.artifact.release, "latestUrl"), false);
  assert.equal(Object.hasOwn(legacy.bundle.artifact.announcement.message, "components"), false);
  assert.match(legacy.bundle.artifact.announcement.message.embeds[0].footer.text, /^sdrel:v1:/);

  for (const schemaVersion of [2, 3]) {
    const fixture = createFixture(t, { schemaVersion });
    const artifactPath = path.join(fixture.artifactDirectory, "announcement.json");
    const portableUrl =
      "https://github.com/Makmatoe/SessionDock/releases/download/v2.7.2/SessionDock-win-x64-Portable.zip";
    const installerUrl =
      "https://github.com/Makmatoe/SessionDock/releases/download/v2.7.2/SessionDock-win-x64-Setup.exe";
    const artifactText = readFileSync(artifactPath, "utf8")
      .replace(`"portableUrl": "${portableUrl}"`, `"installerUrl": "${installerUrl}"`)
      .replace(portableUrl, installerUrl);
    writeFileSync(artifactPath, artifactText);

    assert.throws(
      () => readAnnouncementBundle({ artifactDirectory: fixture.artifactDirectory }),
      (error) =>
        error instanceof ReleaseAutomationError && ["INVALID_ARTIFACT", "INVALID_JSON"].includes(error.code),
    );
  }

  const unsupported = createFixture(t);
  const unsupportedPath = path.join(unsupported.artifactDirectory, "announcement.json");
  writeFileSync(
    unsupportedPath,
    readFileSync(unsupportedPath, "utf8").replace('"schemaVersion": 3', '"schemaVersion": 1'),
  );
  assert.throws(
    () => readAnnouncementBundle({ artifactDirectory: unsupported.artifactDirectory }),
    (error) => error instanceof ReleaseAutomationError && error.code === "INVALID_ARTIFACT",
  );
});

test("generation rejects mismatched notes without leaving a partial output", (t) => {
  const root = mkdtempSync(path.join(os.tmpdir(), "sessiondock-release-automation-invalid-"));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  mkdirSync(path.join(root, "SessionDock", "ReleaseNotes"), { recursive: true });
  writeFileSync(path.join(root, "SessionDock", "ReleaseNotes", "2.7.2.en-US.md"), fixtureNotes("2.7.1"));
  assert.throws(
    () =>
      generateAnnouncement({
        root,
        version: "2.7.2",
        sourceCommit: COMMIT,
        notesPath: "SessionDock/ReleaseNotes/2.7.2.en-US.md",
        outputPath: "artifacts/announcement",
      }),
    (error) => error instanceof ReleaseAutomationError && error.code === "INVALID_NOTES",
  );
  assert.throws(() => readFileSync(path.join(root, "artifacts", "announcement", "announcement.json")));
});

test("generation rejects linked or non-regular canonical release notes", (t) => {
  const root = mkdtempSync(path.join(os.tmpdir(), "sessiondock-release-automation-symlink-"));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const notesDirectory = path.join(root, "SessionDock", "ReleaseNotes");
  mkdirSync(notesDirectory, { recursive: true });
  const target = path.join(root, "untrusted-notes.md");
  const notesPath = path.join(notesDirectory, "2.7.2.en-US.md");
  writeFileSync(target, fixtureNotes());
  try {
    symlinkSync(target, notesPath, "file");
  } catch (error) {
    if (error?.code === "EPERM" || error?.code === "EACCES") {
      mkdirSync(notesPath);
    } else {
      throw error;
    }
  }

  assert.throws(
    () =>
      generateAnnouncement({
        root,
        version: "2.7.2",
        sourceCommit: COMMIT,
        notesPath: "SessionDock/ReleaseNotes/2.7.2.en-US.md",
        outputPath: "artifacts/announcement",
      }),
    (error) => error instanceof ReleaseAutomationError && error.code === "INVALID_FILE",
  );
});

test("canonical release note reads enforce the exact byte ceiling without imposing the legacy embed limit", (t) => {
  const root = mkdtempSync(path.join(os.tmpdir(), "sessiondock-release-automation-bounded-"));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const notesDirectory = path.join(root, "SessionDock", "ReleaseNotes");
  mkdirSync(notesDirectory, { recursive: true });
  const notesPath = path.join(notesDirectory, "2.7.2.en-US.md");
  const header = Buffer.from("SessionDock 2.7.2\n\n", "utf8");
  const exact = Buffer.concat([
    header,
    Buffer.alloc(64 * 1024 - header.length - 1, 0x61),
    Buffer.from("\n", "utf8"),
  ]);
  assert.equal(exact.length, 64 * 1024);
  writeFileSync(notesPath, exact);

  const generate = (schemaVersion, outputPath) =>
    generateAnnouncement({
      root,
      version: "2.7.2",
      sourceCommit: COMMIT,
      notesPath: "SessionDock/ReleaseNotes/2.7.2.en-US.md",
      outputPath,
      schemaVersion,
    });
  assert.doesNotThrow(() => generate(3, "artifacts/schema-3"));
  assert.throws(
    () => generate(2, "artifacts/schema-2"),
    (error) => error instanceof ReleaseAutomationError && error.code === "INVALID_NOTES",
  );

  writeFileSync(notesPath, Buffer.concat([exact, Buffer.from("x")]));
  assert.throws(
    () => generate(3, "artifacts/too-large"),
    (error) => error instanceof ReleaseAutomationError && error.code === "FILE_TOO_LARGE",
  );
});

test("non-regular canonical release notes fail without blocking", (t) => {
  const root = mkdtempSync(path.join(os.tmpdir(), "sessiondock-release-automation-fifo-"));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const notesDirectory = path.join(root, "SessionDock", "ReleaseNotes");
  mkdirSync(notesDirectory, { recursive: true });
  const notesPath = path.join(notesDirectory, "2.7.2.en-US.md");
  if (process.platform === "win32") {
    mkdirSync(notesPath);
  } else {
    const fifo = spawnSync("mkfifo", [notesPath], { encoding: "utf8" });
    assert.equal(fifo.status, 0, fifo.stderr);
  }

  const result = spawnSync(
    process.execPath,
    [
      AUTOMATION_SCRIPT,
      "generate",
      "--version",
      "2.7.2",
      "--source-commit",
      COMMIT,
      "--notes",
      "SessionDock/ReleaseNotes/2.7.2.en-US.md",
      "--output",
      "artifacts/announcement",
    ],
    { cwd: root, encoding: "utf8", timeout: 2_000 },
  );
  assert.notEqual(result.error?.code, "ETIMEDOUT");
  assert.equal(result.status, 1, result.stderr);
  assert.match(result.stderr, /INVALID_FILE/);
});

test("bundle validation fails after any canonical source is altered", (t) => {
  const fixture = createFixture(t);
  writeFileSync(path.join(fixture.artifactDirectory, "notes.md"), fixtureNotes().replace("guarded", "changed"));
  assert.throws(
    () => readAnnouncementBundle({ artifactDirectory: fixture.artifactDirectory }),
    /Bundled release notes do not match/,
  );
});

test("reviewed images require the current version's selection and manifest hashes", (t) => {
  const fixture = createFixture(t, { withImage: true });
  assert.equal(fixture.bundle.images.length, 1);
  assert.equal(fixture.bundle.images[0].sha256, sha256(PNG));
  assert.equal(fixture.bundle.artifact.announcement.attachments[0].sourcePath, "docs/images/sessiondock-v2.7.2/sessiondock-v2.7.2-social-wide.png");
});

test("reviewed image selections cannot opt into Discord spoiler presentation", (t) => {
  assert.throws(
    () => createFixture(t, { withImage: true, imageFileName: "SPOILER_sessiondock-v2.7.2.png" }),
    (error) => error instanceof ReleaseAutomationError && error.code === "INVALID_IMAGE_SELECTION",
  );
});

test("the read-only preflight proves identity, permissions, role, and history without posting", async (t) => {
  const { bundle } = createFixture(t);
  const calls = [];
  const fetchImpl = async (url, init) => {
    calls.push({ method: init.method, url: String(url) });
    const response = preflightResponse(String(url));
    if (response) return response;
    if (String(url).endsWith("/messages?limit=100")) return jsonResponse([]);
    throw new Error(`Unexpected request: ${init.method} ${url}`);
  };

  const result = await preflightAnnouncement({
    bundle,
    env: deliveryEnv(),
    fetchImpl,
    sleepImpl: async () => {},
  });
  assert.equal(result.status, "ready");
  assert.equal(result.verifiedDelivery, false);
  assert.ok(calls.length >= 5);
  assert.ok(calls.every((call) => call.method === "GET"));
});

test("preflight rejects an early matching announcement and never posts", async (t) => {
  const { bundle } = createFixture(t);
  const existing = discordMessage(bundle);
  let posts = 0;
  const fetchImpl = async (url, init) => {
    const response = preflightResponse(String(url));
    if (response) return response;
    if (String(url).endsWith("/messages?limit=100")) return jsonResponse([existing]);
    if (init.method === "POST") posts += 1;
    throw new Error(`Unexpected request: ${init.method} ${url}`);
  };

  await assert.rejects(
    preflightAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} }),
    (error) => error instanceof ReleaseAutomationError && error.code === "DISCORD_EARLY_DISCLOSURE",
  );
  assert.equal(posts, 0);
});

test("preflight proves Read Message History through effective channel permissions", async (t) => {
  const { bundle } = createFixture(t);
  let historyReads = 0;
  const permissionsWithoutHistory = String(Number(BOT_CHANNEL_PERMISSIONS) - 65536);
  const fetchImpl = async (url) => {
    const requestUrl = String(url);
    if (requestUrl === `https://discord.com/api/v10/guilds/${GUILD_ID}/roles`) {
      return jsonResponse([
        { id: GUILD_ID, managed: false, mentionable: false, name: "@everyone", permissions: permissionsWithoutHistory },
        { id: ROLE_ID, managed: false, mentionable: true, name: "SessionDock", permissions: "0" },
      ]);
    }
    const response = preflightResponse(requestUrl);
    if (response) return response;
    if (requestUrl.endsWith("/messages?limit=100")) {
      historyReads += 1;
      return jsonResponse([]);
    }
    throw new Error(`Unexpected request: ${url}`);
  };

  await assert.rejects(
    preflightAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} }),
    (error) => error instanceof ReleaseAutomationError && error.code === "DISCORD_PERMISSIONS",
  );
  assert.equal(historyReads, 0);
});

test("effective channel permission overwrites follow Discord precedence", async (t) => {
  const { bundle } = createFixture(t);
  const basePermissions = BigInt(BOT_CHANNEL_PERMISSIONS);
  const cases = [
    {
      name: "an assigned role allow restores an @everyone denial",
      everyonePermissions: basePermissions,
      memberRoles: [BOT_ROLE_A_ID],
      overwrites: [
        { allow: "0", deny: String(TEST_PERMISSION_VIEW_CHANNEL), id: GUILD_ID, type: 0 },
        { allow: String(TEST_PERMISSION_VIEW_CHANNEL), deny: "0", id: BOT_ROLE_A_ID, type: 0 },
      ],
      passes: true,
    },
    {
      name: "combined role allows win over combined role denials",
      everyonePermissions: basePermissions,
      memberRoles: [BOT_ROLE_A_ID, BOT_ROLE_B_ID],
      overwrites: [
        { allow: String(TEST_PERMISSION_VIEW_CHANNEL), deny: "0", id: BOT_ROLE_B_ID, type: 0 },
        { allow: "0", deny: String(TEST_PERMISSION_VIEW_CHANNEL), id: BOT_ROLE_A_ID, type: 0 },
      ],
      passes: true,
    },
    {
      name: "a member denial wins after an assigned role allowance",
      everyonePermissions: basePermissions,
      memberRoles: [BOT_ROLE_A_ID],
      overwrites: [
        { allow: "0", deny: String(TEST_PERMISSION_VIEW_CHANNEL), id: GUILD_ID, type: 0 },
        { allow: String(TEST_PERMISSION_VIEW_CHANNEL), deny: "0", id: BOT_ROLE_A_ID, type: 0 },
        { allow: "0", deny: String(TEST_PERMISSION_VIEW_CHANNEL), id: BOT_ID, type: 1 },
      ],
      passes: false,
    },
    {
      name: "a member allowance wins after an assigned role denial",
      everyonePermissions: basePermissions,
      memberRoles: [BOT_ROLE_A_ID],
      overwrites: [
        { allow: "0", deny: String(TEST_PERMISSION_VIEW_CHANNEL), id: BOT_ROLE_A_ID, type: 0 },
        { allow: String(TEST_PERMISSION_VIEW_CHANNEL), deny: "0", id: BOT_ID, type: 1 },
      ],
      passes: true,
    },
    {
      name: "Administrator is rejected even when channel overwrites deny it",
      everyonePermissions: basePermissions | TEST_PERMISSION_ADMINISTRATOR,
      memberRoles: [],
      overwrites: [
        { allow: "0", deny: String(TEST_PERMISSION_ADMINISTRATOR), id: GUILD_ID, type: 0 },
        { allow: "0", deny: String(TEST_PERMISSION_ADMINISTRATOR), id: BOT_ID, type: 1 },
      ],
      passes: false,
    },
  ];

  for (const permissionCase of cases) {
    await t.test(permissionCase.name, async () => {
      let historyReads = 0;
      const roles = [
        {
          id: GUILD_ID,
          managed: false,
          mentionable: false,
          name: "@everyone",
          permissions: String(permissionCase.everyonePermissions),
        },
        { id: ROLE_ID, managed: false, mentionable: true, name: "SessionDock", permissions: "0" },
        ...permissionCase.memberRoles.map((roleId) => ({
          id: roleId,
          managed: false,
          mentionable: false,
          name: `Bota test role ${roleId}`,
          permissions: "0",
        })),
      ];
      const fetchImpl = async (url) => {
        const requestUrl = String(url);
        if (requestUrl === "https://discord.com/api/v10/users/@me") {
          return jsonResponse({ bot: true, id: BOT_ID });
        }
        if (requestUrl === `https://discord.com/api/v10/channels/${CHANNEL_ID}`) {
          return jsonResponse({
            guild_id: GUILD_ID,
            id: CHANNEL_ID,
            last_message_id: null,
            permission_overwrites: permissionCase.overwrites,
            type: 0,
          });
        }
        if (requestUrl === `https://discord.com/api/v10/guilds/${GUILD_ID}/roles`) {
          return jsonResponse(roles);
        }
        if (requestUrl === `https://discord.com/api/v10/guilds/${GUILD_ID}/members/${BOT_ID}`) {
          return jsonResponse({ roles: permissionCase.memberRoles, user: { bot: true, id: BOT_ID } });
        }
        if (requestUrl.endsWith("/messages?limit=100")) {
          historyReads += 1;
          return jsonResponse([]);
        }
        throw new Error(`Unexpected request: ${url}`);
      };

      const preflight = preflightAnnouncement({
        bundle,
        env: deliveryEnv(),
        fetchImpl,
        sleepImpl: async () => {},
      });
      if (permissionCase.passes) {
        assert.equal((await preflight).status, "ready");
        assert.equal(historyReads, 1);
      } else {
        await assert.rejects(
          preflight,
          (error) => error instanceof ReleaseAutomationError && error.code === "DISCORD_PERMISSIONS",
        );
        assert.equal(historyReads, 0);
      }
    });
  }
});

test("preflight rejects @everyone in Bota's assigned member roles", async (t) => {
  const { bundle } = createFixture(t);
  let historyReads = 0;
  const fetchImpl = async (url) => {
    const requestUrl = String(url);
    if (requestUrl === `https://discord.com/api/v10/guilds/${GUILD_ID}/members/${BOT_ID}`) {
      return jsonResponse({ roles: [GUILD_ID], user: { bot: true, id: BOT_ID } });
    }
    const response = preflightResponse(requestUrl);
    if (response) return response;
    if (requestUrl.endsWith("/messages?limit=100")) {
      historyReads += 1;
      return jsonResponse([]);
    }
    throw new Error(`Unexpected request: ${url}`);
  };

  await assert.rejects(
    preflightAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} }),
    (error) => error instanceof ReleaseAutomationError && error.code === "DISCORD_PERMISSIONS",
  );
  assert.equal(historyReads, 0);
});

test("the workflow-shaped preflight CLI uses the standalone staged module and makes no POST", (t) => {
  const fixture = createFixture(t);
  const stagedScript = stageStandaloneAutomation(fixture.root);
  const { fetchLog, result } = runPreflightCliChild(fixture, stagedScript);
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /preflight is ready/);
  const requests = readFileSync(fetchLog, "utf8");
  assert.doesNotMatch(requests, /^POST /m);
});

test("the existing-message diagnostic is GET-only and returns only allowlisted mismatch codes", async (t) => {
  const { bundle } = createFixture(t, { schemaVersion: 2 });
  const existing = discordMessage(bundle);
  existing.shared_client_theme = { attacker: `private value containing ${TOKEN}` };
  const calls = [];
  const fetchImpl = async (url, init) => {
    calls.push({ method: init.method, url: String(url) });
    const response = preflightResponse(String(url));
    if (response) return response;
    if (String(url).endsWith("/messages?limit=100")) return jsonResponse([existing]);
    if (String(url).endsWith(`/messages/${MESSAGE_ID}`)) return jsonResponse(existing);
    throw new Error(`Unexpected request: ${init.method} ${url}`);
  };

  const diagnostic = await diagnoseExistingAnnouncement({
    bundle,
    env: deliveryEnv(),
    fetchImpl,
    sleepImpl: async () => {},
  });
  assert.equal(diagnostic.status, "mismatch");
  assert.equal(diagnostic.verified, false);
  assert.deepEqual(diagnostic.mismatchCodes, ["message.optional.shared-client-theme"]);
  const serialized = JSON.stringify(diagnostic);
  assert.doesNotMatch(serialized, new RegExp(TOKEN));
  assert.doesNotMatch(serialized, /private value/);
  assert.ok(calls.length >= 6);
  assert.ok(calls.every((call) => call.method === "GET"));
});

test("the existing-message diagnostic does not classify a transient CDN failure as a presentation mismatch", async (t) => {
  const { bundle } = createFixture(t, { withImage: true });
  const existing = discordMessage(bundle);
  const fetchImpl = async (url) => {
    const requestUrl = String(url);
    const response = preflightResponse(requestUrl);
    if (response) return response;
    if (requestUrl.endsWith("/messages?limit=100")) return jsonResponse([existing]);
    if (requestUrl.endsWith(`/messages/${MESSAGE_ID}`)) return jsonResponse(existing);
    if (requestUrl.startsWith("https://cdn.discordapp.com/attachments/")) {
      throw new Error("temporary CDN failure");
    }
    throw new Error(`Unexpected request: ${requestUrl}`);
  };

  await assert.rejects(
    diagnoseExistingAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} }),
    (error) => error instanceof ReleaseAutomationError && error.code === "DISCORD_ATTACHMENT_READ",
  );
});

test("the existing-message diagnostic reports a missing announcement with one fixed code", async (t) => {
  const { bundle } = createFixture(t);
  const fetchImpl = async (url) => {
    const requestUrl = String(url);
    const response = preflightResponse(requestUrl);
    if (response) return response;
    if (requestUrl.endsWith("/messages?limit=100")) return jsonResponse([]);
    throw new Error(`Unexpected request: ${requestUrl}`);
  };

  const diagnostic = await diagnoseExistingAnnouncement({
    bundle,
    env: deliveryEnv(),
    fetchImpl,
    sleepImpl: async () => {},
  });
  assert.equal(diagnostic.status, "not-found");
  assert.equal(diagnostic.verified, false);
  assert.deepEqual(diagnostic.mismatchCodes, ["message.missing"]);
});

test("the diagnostic CLI reserves a content-free report and never mutates Discord", (t) => {
  const fixture = createFixture(t, { schemaVersion: 2 });
  const reportPath = "diagnostics/report.json";
  const { attackerValue, fetchLog, result } = runDiagnosticCliChild(fixture, reportPath);
  assert.equal(result.status, 0, result.stderr);
  assert.doesNotMatch(`${result.stdout}\n${result.stderr}`, new RegExp(TOKEN));
  assert.doesNotMatch(`${result.stdout}\n${result.stderr}`, new RegExp(attackerValue));
  const reportText = readFileSync(path.join(fixture.root, ...reportPath.split("/")), "utf8");
  const report = JSON.parse(reportText);
  assert.equal(report.kind, "sessiondock.discord-release-diagnostic");
  assert.deepEqual(report.mismatchCodes, ["message.optional.shared-client-theme"]);
  assert.doesNotMatch(reportText, new RegExp(TOKEN));
  assert.doesNotMatch(reportText, new RegExp(attackerValue));
  const requests = readFileSync(fetchLog, "utf8");
  assert.match(requests, /^GET /m);
  assert.doesNotMatch(requests, /^(?:POST|PATCH|PUT|DELETE) /m);
});

test("automatic delivery pings exactly the configured role and verifies the message", async (t) => {
  const { bundle } = createFixture(t);
  const calls = [];
  const expectedMessage = discordMessage(bundle);
  const fetchImpl = async (url, init) => {
    calls.push({ init, url: String(url) });
    const preflight = preflightResponse(String(url));
    if (preflight) return preflight;
    if (String(url) === `https://discord.com/api/v10/channels/${CHANNEL_ID}/messages?limit=100`) {
      return jsonResponse([]);
    }
    if (String(url) === `https://discord.com/api/v10/channels/${CHANNEL_ID}/messages` && init.method === "POST") {
      const body = JSON.parse(init.body);
      assert.deepEqual(body.allowed_mentions, {
        replied_user: false,
        roles: [ROLE_ID],
        users: [],
      });
      assert.equal(body.allowed_mentions.parse, undefined);
      assert.equal(body.content, `<@&${ROLE_ID}>`);
      assert.equal(body.enforce_nonce, true);
      assert.equal(body.nonce, bundle.artifact.announcement.nonce);
      assert.equal(body.flags, undefined);
      assert.ok(!body.content.includes("everyone"));
      return jsonResponse(expectedMessage);
    }
    if (String(url) === `https://discord.com/api/v10/channels/${CHANNEL_ID}/messages/${MESSAGE_ID}`) {
      return jsonResponse(expectedMessage);
    }
    throw new Error(`Unexpected request: ${init.method} ${url}`);
  };

  const result = await deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} });
  assert.equal(result.status, "posted");
  assert.equal(calls.filter((call) => call.init.method === "POST").length, 1);
  for (const call of calls) {
    assert.equal(call.init.headers.Authorization, `Bot ${TOKEN}`);
    assert.ok(!call.url.includes(TOKEN));
  }
});

test("a verified existing marker makes a rerun a no-op", async (t) => {
  const { bundle } = createFixture(t);
  const existing = discordMessage(bundle);
  let posts = 0;
  const fetchImpl = async (url, init) => {
    const preflight = preflightResponse(String(url));
    if (preflight) return preflight;
    if (String(url) === `https://discord.com/api/v10/channels/${CHANNEL_ID}/messages?limit=100`) {
      return jsonResponse([existing]);
    }
    if (String(url) === `https://discord.com/api/v10/channels/${CHANNEL_ID}/messages/${MESSAGE_ID}`) {
      return jsonResponse(existing);
    }
    if (init.method === "POST") posts += 1;
    throw new Error(`Unexpected request: ${init.method} ${url}`);
  };
  const result = await deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} });
  assert.equal(result.status, "already-posted");
  assert.equal(result.botId, BOT_ID);
  assert.equal(posts, 0);
});

test("an existing announcement reread is bound to the exact history message ID", async (t) => {
  const { bundle } = createFixture(t);
  const existing = discordMessage(bundle);
  const misbound = discordMessage(bundle, { id: OTHER_MESSAGE_ID });
  const fetchImpl = async (url) => {
    const response = preflightResponse(String(url));
    if (response) return response;
    if (String(url).endsWith("/messages?limit=100")) return jsonResponse([existing]);
    if (String(url).endsWith(`/messages/${MESSAGE_ID}`)) return jsonResponse(misbound);
    throw new Error(`Unexpected request: ${url}`);
  };

  let caught;
  try {
    await deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} });
  } catch (error) {
    caught = error;
  }
  assert.equal(caught?.code, "DELIVERY_AMBIGUOUS");
  assert.equal(caught?.ambiguous, true);
  assert.equal(createDeliveryReceipt({ bundle, error: caught }).status, "ambiguous");
});

test("a same-tag marker from different immutable inputs fails closed", async (t) => {
  const { bundle } = createFixture(t);
  const conflict = discordMessage(bundle);
  conflict.embeds = structuredClone(conflict.embeds);
  conflict.embeds[0].footer.text = `sdrel:v1:Makmatoe/SessionDock:v2.7.2:${"f".repeat(64)}`;
  let posts = 0;
  const fetchImpl = async (url, init) => {
    const preflight = preflightResponse(String(url));
    if (preflight) return preflight;
    if (String(url).endsWith("/messages?limit=100")) return jsonResponse([conflict]);
    if (init.method === "POST") posts += 1;
    throw new Error("Unexpected request");
  };
  await assert.rejects(
    deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} }),
    (error) => error instanceof ReleaseAutomationError && error.code === "DISCORD_CONFLICT",
  );
  assert.equal(posts, 0);
});

test("an immutable same-tag release link blocks a duplicate even when its footer marker is missing", async (t) => {
  const { bundle } = createFixture(t);
  const candidate = discordMessage(bundle);
  candidate.embeds = structuredClone(candidate.embeds);
  delete candidate.embeds[0].footer;
  let posts = 0;
  const fetchImpl = async (url, init) => {
    const preflight = preflightResponse(String(url));
    if (preflight) return preflight;
    if (String(url).endsWith("/messages?limit=100")) return jsonResponse([candidate]);
    if (init.method === "POST") posts += 1;
    throw new Error(`Unexpected request: ${init.method} ${url}`);
  };
  await assert.rejects(
    deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} }),
    (error) => error instanceof ReleaseAutomationError && error.code === "DISCORD_CONFLICT",
  );
  assert.equal(posts, 0);
});

test("an ambiguous POST is reconciled without a second POST", async (t) => {
  const { bundle } = createFixture(t);
  const expectedMessage = discordMessage(bundle);
  let historyReads = 0;
  let posts = 0;
  const fetchImpl = async (url, init) => {
    const preflight = preflightResponse(String(url));
    if (preflight) return preflight;
    if (String(url).endsWith("/messages?limit=100")) {
      historyReads += 1;
      return jsonResponse(historyReads === 1 ? [] : [expectedMessage]);
    }
    if (String(url) === `https://discord.com/api/v10/channels/${CHANNEL_ID}/messages` && init.method === "POST") {
      posts += 1;
      throw new Error(`socket closed after ${TOKEN}`);
    }
    if (String(url) === `https://discord.com/api/v10/channels/${CHANNEL_ID}/messages/${MESSAGE_ID}`) {
      return jsonResponse(expectedMessage);
    }
    throw new Error(`Unexpected request: ${init.method} ${url}`);
  };
  const result = await deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} });
  assert.equal(result.status, "posted");
  assert.equal(posts, 1);
});

test("an unresolved ambiguous POST never retries and never leaks the token", async (t) => {
  const { bundle } = createFixture(t);
  let posts = 0;
  const fetchImpl = async (url, init) => {
    const preflight = preflightResponse(String(url));
    if (preflight) return preflight;
    if (String(url).endsWith("/messages?limit=100")) return jsonResponse([]);
    if (init.method === "POST") {
      posts += 1;
      throw new Error(`network failure containing ${TOKEN}`);
    }
    throw new Error("Unexpected request");
  };
  let caught;
  try {
    await deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} });
  } catch (error) {
    caught = error;
  }
  assert.equal(caught.code, "DELIVERY_AMBIGUOUS");
  assert.equal(caught.ambiguous, true);
  assert.ok(!caught.message.includes(TOKEN));
  assert.equal(posts, 1);
});

test("invalid protected-environment inputs fail before any network request", async (t) => {
  const { bundle } = createFixture(t);
  for (const overrides of [
    { DISCORD_RELEASE_BOT_ID: "" },
    { DISCORD_RELEASE_BOT_ID: "not-a-snowflake" },
    { DISCORD_RELEASE_ROLE_ID: "not-a-snowflake" },
  ]) {
    let calls = 0;
    await assert.rejects(
      deliverAnnouncement({
        bundle,
        env: deliveryEnv(overrides),
        fetchImpl: async () => {
          calls += 1;
          throw new Error("must not run");
        },
      }),
      (error) => error instanceof ReleaseAutomationError && error.code === "INVALID_CONFIGURATION",
    );
    assert.equal(calls, 0);
  }
});

test("the Discord credential must resolve to the pinned Bota identity", async (t) => {
  const { bundle } = createFixture(t);
  const requests = [];
  const fetchImpl = async (url) => {
    requests.push(String(url));
    return jsonResponse({ bot: true, id: "623456789012345678" });
  };
  await assert.rejects(
    deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} }),
    (error) => error instanceof ReleaseAutomationError && error.code === "DISCORD_IDENTITY",
  );
  assert.deepEqual(requests, ["https://discord.com/api/v10/users/@me"]);
});

test("image delivery uses exact multipart bytes and verifies the Discord CDN copy", async (t) => {
  const { bundle } = createFixture(t, { withImage: true });
  const expectedMessage = discordMessage(bundle);
  let inspectedMultipart = false;
  let cdnGets = 0;
  const fetchImpl = async (url, init) => {
    const requestUrl = String(url);
    const preflight = preflightResponse(requestUrl);
    if (preflight) return preflight;
    if (requestUrl.endsWith("/messages?limit=100")) return jsonResponse([]);
    if (requestUrl === `https://discord.com/api/v10/channels/${CHANNEL_ID}/messages` && init.method === "POST") {
      assert.ok(init.body instanceof FormData);
      const payload = JSON.parse(init.body.get("payload_json"));
      assert.equal(payload.attachments[0].filename, bundle.images[0].fileName);
      assert.equal(payload.attachments[0].description, "SessionDock 2.7.2 reviewed release image");
      assert.equal(payload.attachments[0].title, undefined);
      assert.equal(payload.enforce_nonce, true);
      const uploaded = init.body.get("files[0]");
      assert.equal(uploaded.name, bundle.images[0].fileName);
      assert.deepEqual(Buffer.from(await uploaded.arrayBuffer()), PNG);
      inspectedMultipart = true;
      return jsonResponse(expectedMessage);
    }
    if (requestUrl === `https://discord.com/api/v10/channels/${CHANNEL_ID}/messages/${MESSAGE_ID}`) {
      return jsonResponse(expectedMessage);
    }
    if (requestUrl.startsWith("https://cdn.discordapp.com/attachments/")) {
      assert.equal(init.headers.Authorization, undefined);
      cdnGets += 1;
      return new Response(PNG, { status: 200, headers: { "content-type": "image/png" } });
    }
    throw new Error(`Unexpected request: ${init.method} ${url}`);
  };
  const result = await deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} });
  assert.equal(result.status, "posted");
  assert.equal(inspectedMultipart, true);
  assert.equal(cdnGets, 1);
});

test("multiple reviewed images preserve their order through schema 3 and Discord read-back", async (t) => {
  const imageFileNames = [
    "sessiondock-v2.7.2-one.png",
    "sessiondock-v2.7.2-two.png",
    "sessiondock-v2.7.2-three.png",
  ];
  const { bundle } = createFixture(t, { withImage: true, imageFileNames });
  const expectedMessage = discordMessage(bundle);
  assert.equal(bundle.artifact.announcement.message.embeds.length, imageFileNames.length);
  assert.equal(bundle.artifact.announcement.attachments.length, imageFileNames.length);
  for (let index = 0; index < imageFileNames.length; index += 1) {
    assert.equal(
      bundle.artifact.announcement.message.embeds[index].image.url,
      `attachment://${imageFileNames[index]}`,
    );
    if (index > 0) {
      assert.equal(bundle.artifact.announcement.message.embeds[index].url, undefined);
    }
  }

  const cdnFiles = [];
  const fetchImpl = async (url) => {
    const requestUrl = String(url);
    const preflight = preflightResponse(requestUrl);
    if (preflight) return preflight;
    if (requestUrl.endsWith("/messages?limit=100")) return jsonResponse([expectedMessage]);
    if (requestUrl.endsWith(`/messages/${MESSAGE_ID}`)) return jsonResponse(expectedMessage);
    if (requestUrl.startsWith("https://cdn.discordapp.com/attachments/")) {
      cdnFiles.push(new URL(requestUrl).pathname.split("/").at(-1));
      return new Response(PNG, { status: 200, headers: { "content-type": "image/png" } });
    }
    throw new Error(`Unexpected request: ${requestUrl}`);
  };
  const result = await deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} });
  assert.equal(result.status, "already-posted");
  assert.deepEqual(cdnFiles, [...imageFileNames, ...imageFileNames]);
});

test("an oversized declared attachment is rejected before reading CDN bytes", async (t) => {
  const { bundle } = createFixture(t, { withImage: true });
  const expectedMessage = discordMessage(bundle);
  let attachmentReads = 0;
  let posts = 0;
  const fetchImpl = async (url, init) => {
    const requestUrl = String(url);
    const preflight = preflightResponse(requestUrl);
    if (preflight) return preflight;
    if (requestUrl.endsWith("/messages?limit=100")) return jsonResponse([]);
    if (requestUrl.endsWith("/messages") && init.method === "POST") {
      posts += 1;
      return jsonResponse(expectedMessage);
    }
    if (requestUrl.endsWith(`/messages/${MESSAGE_ID}`)) return jsonResponse(expectedMessage);
    if (requestUrl.startsWith("https://cdn.discordapp.com/attachments/")) {
      return streamedResponse([PNG], {
        headers: { "content-length": String(PNG.length + 1) },
        onRead: () => { attachmentReads += 1; },
      });
    }
    throw new Error("Unexpected request");
  };
  await assert.rejects(
    deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} }),
    (error) => error instanceof ReleaseAutomationError && error.code === "DELIVERY_AMBIGUOUS",
  );
  assert.equal(posts, 1);
  assert.equal(attachmentReads, 0);
});

test("an oversized streamed attachment is canceled at the reviewed byte boundary", async (t) => {
  const { bundle } = createFixture(t, { withImage: true });
  const expectedMessage = discordMessage(bundle);
  let attachmentReads = 0;
  let canceled = 0;
  let posts = 0;
  const fetchImpl = async (url, init) => {
    const requestUrl = String(url);
    const preflight = preflightResponse(requestUrl);
    if (preflight) return preflight;
    if (requestUrl.endsWith("/messages?limit=100")) return jsonResponse([]);
    if (requestUrl.endsWith("/messages") && init.method === "POST") {
      posts += 1;
      return jsonResponse(expectedMessage);
    }
    if (requestUrl.endsWith(`/messages/${MESSAGE_ID}`)) return jsonResponse(expectedMessage);
    if (requestUrl.startsWith("https://cdn.discordapp.com/attachments/")) {
      return streamedResponse([PNG, new Uint8Array([0])], {
        onCancel: () => { canceled += 1; },
        onRead: () => { attachmentReads += 1; },
      });
    }
    throw new Error("Unexpected request");
  };
  await assert.rejects(
    deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} }),
    (error) => error instanceof ReleaseAutomationError && error.code === "DELIVERY_AMBIGUOUS",
  );
  assert.equal(posts, 1);
  assert.equal(attachmentReads, 2);
  assert.equal(canceled, 1);
});

test("an accepted POST with an unreadable body reconciles as confirmed without reposting", async (t) => {
  const { bundle } = createFixture(t);
  const expectedMessage = discordMessage(bundle);
  let historyReads = 0;
  let posts = 0;
  const fetchImpl = async (url, init) => {
    const preflight = preflightResponse(String(url));
    if (preflight) return preflight;
    if (String(url).endsWith("/messages?limit=100")) {
      historyReads += 1;
      return jsonResponse(historyReads === 1 ? [] : [expectedMessage]);
    }
    if (String(url).endsWith("/messages") && init.method === "POST") {
      posts += 1;
      return streamedResponse([], { readError: new Error("response stream failed") });
    }
    if (String(url).endsWith(`/messages/${MESSAGE_ID}`)) return jsonResponse(expectedMessage);
    throw new Error("Unexpected request");
  };
  const result = await deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} });
  assert.equal(posts, 1);
  assert.equal(createDeliveryReceipt({ bundle, result }).status, "confirmed");
});

test("an oversized declared POST response is rejected before its body is read and reconciled once", async (t) => {
  const { bundle } = createFixture(t);
  const expectedMessage = discordMessage(bundle);
  let bodyReads = 0;
  let historyReads = 0;
  let posts = 0;
  const fetchImpl = async (url, init) => {
    const preflight = preflightResponse(String(url));
    if (preflight) return preflight;
    if (String(url).endsWith("/messages?limit=100")) {
      historyReads += 1;
      return jsonResponse(historyReads === 1 ? [] : [expectedMessage]);
    }
    if (String(url).endsWith("/messages") && init.method === "POST") {
      posts += 1;
      return streamedResponse([Buffer.from("{}")], {
        headers: { "content-length": String(JSON_RESPONSE_LIMIT + 1) },
        onRead: () => { bodyReads += 1; },
      });
    }
    if (String(url).endsWith(`/messages/${MESSAGE_ID}`)) return jsonResponse(expectedMessage);
    throw new Error("Unexpected request");
  };
  const result = await deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} });
  assert.equal(result.status, "posted");
  assert.equal(posts, 1);
  assert.equal(bodyReads, 0);
});

test("an oversized streamed POST response is canceled and reconciled without reposting", async (t) => {
  const { bundle } = createFixture(t);
  const expectedMessage = discordMessage(bundle);
  let canceled = 0;
  let historyReads = 0;
  let posts = 0;
  let responseReads = 0;
  const fetchImpl = async (url, init) => {
    const preflight = preflightResponse(String(url));
    if (preflight) return preflight;
    if (String(url).endsWith("/messages?limit=100")) {
      historyReads += 1;
      return jsonResponse(historyReads === 1 ? [] : [expectedMessage]);
    }
    if (String(url).endsWith("/messages") && init.method === "POST") {
      posts += 1;
      return streamedResponse([new Uint8Array(JSON_RESPONSE_LIMIT), new Uint8Array([0])], {
        onCancel: () => { canceled += 1; },
        onRead: () => { responseReads += 1; },
      });
    }
    if (String(url).endsWith(`/messages/${MESSAGE_ID}`)) return jsonResponse(expectedMessage);
    throw new Error("Unexpected request");
  };
  const result = await deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} });
  assert.equal(result.status, "posted");
  assert.equal(posts, 1);
  assert.equal(responseReads, 2);
  assert.equal(canceled, 1);
});

test("an accepted POST with malformed JSON stays ambiguous and is never reposted", async (t) => {
  const { bundle } = createFixture(t);
  let posts = 0;
  const fetchImpl = async (url, init) => {
    const preflight = preflightResponse(String(url));
    if (preflight) return preflight;
    if (String(url).endsWith("/messages?limit=100")) return jsonResponse([]);
    if (String(url).endsWith("/messages") && init.method === "POST") {
      posts += 1;
      return new Response("{", { status: 200, headers: { "content-type": "application/json" } });
    }
    throw new Error("Unexpected request");
  };
  let caught;
  try {
    await deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} });
  } catch (error) {
    caught = error;
  }
  assert.equal(posts, 1);
  assert.equal(caught.code, "DELIVERY_AMBIGUOUS");
  assert.equal(createDeliveryReceipt({ bundle, error: caught }).status, "ambiguous");
});

test("an accepted POST with an empty body reconciles as confirmed without reposting", async (t) => {
  const { bundle } = createFixture(t);
  const expectedMessage = discordMessage(bundle);
  let historyReads = 0;
  let posts = 0;
  const fetchImpl = async (url, init) => {
    const preflight = preflightResponse(String(url));
    if (preflight) return preflight;
    if (String(url).endsWith("/messages?limit=100")) {
      historyReads += 1;
      return jsonResponse(historyReads === 1 ? [] : [expectedMessage]);
    }
    if (String(url).endsWith("/messages") && init.method === "POST") {
      posts += 1;
      return new Response(null, { status: 200 });
    }
    if (String(url).endsWith(`/messages/${MESSAGE_ID}`)) return jsonResponse(expectedMessage);
    throw new Error("Unexpected request");
  };
  const result = await deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} });
  const receipt = createDeliveryReceipt({ bundle, result });
  assert.equal(result.status, "posted");
  assert.equal(posts, 1);
  assert.equal(receipt.status, "confirmed");
});

test("a definitive POST rate limit honors the full Retry-After before one nonce-safe retry", async (t) => {
  const { bundle } = createFixture(t);
  const expectedMessage = discordMessage(bundle);
  const waits = [];
  let now = 0;
  let posts = 0;
  const fetchImpl = async (url, init) => {
    const preflight = preflightResponse(String(url));
    if (preflight) return preflight;
    if (String(url).endsWith("/messages?limit=100")) return jsonResponse([]);
    if (String(url).endsWith("/messages") && init.method === "POST") {
      posts += 1;
      if (posts === 1) return jsonResponse({ retry_after: 75 }, 429, { "retry-after": "75" });
      return jsonResponse(expectedMessage);
    }
    if (String(url).endsWith(`/messages/${MESSAGE_ID}`)) return jsonResponse(expectedMessage);
    throw new Error("Unexpected request");
  };
  const result = await deliverAnnouncement({
    bundle,
    env: deliveryEnv(),
    fetchImpl,
    nowImpl: () => now,
    sleepImpl: async (milliseconds) => {
      waits.push(milliseconds);
      now += milliseconds;
    },
  });
  assert.equal(result.status, "posted");
  assert.equal(posts, 2);
  assert.deepEqual(waits, [75_000]);
});

test("a delayed rate-limit wakeup cannot start a POST beyond the delivery deadline", async (t) => {
  const { bundle } = createFixture(t);
  const waits = [];
  let now = 0;
  let posts = 0;
  const fetchImpl = async (url, init) => {
    const preflight = preflightResponse(String(url));
    if (preflight) return preflight;
    if (String(url).endsWith("/messages?limit=100")) return jsonResponse([]);
    if (String(url).endsWith("/messages") && init.method === "POST") {
      posts += 1;
      return jsonResponse({ retry_after: 179 }, 429, { "retry-after": "179" });
    }
    throw new Error("Unexpected request");
  };
  await assert.rejects(
    deliverAnnouncement({
      bundle,
      env: deliveryEnv(),
      fetchImpl,
      nowImpl: () => now,
      sleepImpl: async (milliseconds) => {
        waits.push(milliseconds);
        now += milliseconds + 2_000;
      },
    }),
    (error) => error instanceof ReleaseAutomationError && error.code === "DISCORD_RATE_LIMIT",
  );
  assert.equal(posts, 1);
  assert.deepEqual(waits, [179_000]);
});

test("all Discord preflight requests share one bounded operation deadline", async (t) => {
  const { bundle } = createFixture(t);
  const waits = [];
  let now = 0;
  let identityAttempts = 0;
  const fetchImpl = async (url) => {
    const requestUrl = String(url);
    if (requestUrl.endsWith("/users/@me")) {
      identityAttempts += 1;
      if (identityAttempts === 1) return jsonResponse({ retry_after: 100 }, 429, { "retry-after": "100" });
      return jsonResponse({ bot: true, id: BOT_ID });
    }
    if (requestUrl.endsWith(`/channels/${CHANNEL_ID}`)) {
      return jsonResponse({ retry_after: 100 }, 429, { "retry-after": "100" });
    }
    throw new Error(`Unexpected request: ${url}`);
  };

  await assert.rejects(
    preflightAnnouncement({
      bundle,
      env: deliveryEnv(),
      fetchImpl,
      nowImpl: () => now,
      sleepImpl: async (milliseconds) => {
        waits.push(milliseconds);
        now += milliseconds;
      },
    }),
    (error) => error instanceof ReleaseAutomationError && error.code === "DISCORD_RATE_LIMIT",
  );
  assert.deepEqual(waits, [100_000]);
});

test("a POST 5xx is reconciled by history and is never blindly retried", async (t) => {
  const { bundle } = createFixture(t);
  const expectedMessage = discordMessage(bundle);
  let historyReads = 0;
  let posts = 0;
  const fetchImpl = async (url, init) => {
    const preflight = preflightResponse(String(url));
    if (preflight) return preflight;
    if (String(url).endsWith("/messages?limit=100")) {
      historyReads += 1;
      return jsonResponse(historyReads === 1 ? [] : [expectedMessage]);
    }
    if (String(url).endsWith("/messages") && init.method === "POST") {
      posts += 1;
      return jsonResponse({ message: "upstream error" }, 503);
    }
    if (String(url).endsWith(`/messages/${MESSAGE_ID}`)) return jsonResponse(expectedMessage);
    throw new Error("Unexpected request");
  };
  const result = await deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} });
  assert.equal(result.status, "posted");
  assert.equal(posts, 1);
});

test("history pagination finds an existing announcement on page two", async (t) => {
  const { bundle } = createFixture(t);
  const expectedMessage = discordMessage(bundle);
  const firstPage = Array.from({ length: 100 }, (_, index) => ({
    author: { bot: false, id: "623456789012345678" },
    embeds: [],
    id: String(700000000000000000n - BigInt(index)),
  }));
  let pages = 0;
  let posts = 0;
  const fetchImpl = async (url, init) => {
    const preflight = preflightResponse(String(url));
    if (preflight) return preflight;
    if (String(url).includes("/messages?limit=100")) {
      pages += 1;
      return jsonResponse(pages === 1 ? firstPage : [expectedMessage]);
    }
    if (String(url).endsWith(`/messages/${MESSAGE_ID}`)) return jsonResponse(expectedMessage);
    if (init.method === "POST") posts += 1;
    throw new Error("Unexpected request");
  };
  const result = await deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} });
  assert.equal(result.status, "already-posted");
  assert.equal(pages, 2);
  assert.equal(posts, 0);
});

test("a read-back payload mismatch is ambiguous and never causes a second POST", async (t) => {
  const { bundle } = createFixture(t);
  const posted = discordMessage(bundle);
  const changed = discordMessage(bundle, { content: "changed after posting" });
  let posts = 0;
  const fetchImpl = async (url, init) => {
    const preflight = preflightResponse(String(url));
    if (preflight) return preflight;
    if (String(url).endsWith("/messages?limit=100")) return jsonResponse([]);
    if (String(url).endsWith("/messages") && init.method === "POST") {
      posts += 1;
      return jsonResponse(posted);
    }
    if (String(url).endsWith(`/messages/${MESSAGE_ID}`)) return jsonResponse(changed);
    throw new Error("Unexpected request");
  };
  await assert.rejects(
    deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} }),
    (error) => error instanceof ReleaseAutomationError && error.code === "DELIVERY_AMBIGUOUS",
  );
  assert.equal(posts, 1);
});

test("a post read-back is bound to the exact accepted message ID", async (t) => {
  const { bundle } = createFixture(t);
  const posted = discordMessage(bundle);
  const misbound = discordMessage(bundle, { id: OTHER_MESSAGE_ID });
  let posts = 0;
  const fetchImpl = async (url, init) => {
    const response = preflightResponse(String(url));
    if (response) return response;
    if (String(url).endsWith("/messages?limit=100")) return jsonResponse([]);
    if (String(url).endsWith("/messages") && init.method === "POST") {
      posts += 1;
      return jsonResponse(posted);
    }
    if (String(url).endsWith(`/messages/${MESSAGE_ID}`)) return jsonResponse(misbound);
    throw new Error(`Unexpected request: ${init.method} ${url}`);
  };

  await assert.rejects(
    deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} }),
    (error) => error instanceof ReleaseAutomationError && error.code === "DELIVERY_AMBIGUOUS",
  );
  assert.equal(posts, 1);
});

test("a displayed embed image must be the same verified reviewed attachment", async (t) => {
  const { bundle } = createFixture(t, { withImage: true });
  const spoofed = discordMessage(bundle);
  spoofed.embeds[0].image.url = "https://example.com/different.png";
  let posts = 0;
  const fetchImpl = async (url, init) => {
    const preflight = preflightResponse(String(url));
    if (preflight) return preflight;
    if (String(url).endsWith("/messages?limit=100")) return jsonResponse([]);
    if (String(url).endsWith("/messages") && init.method === "POST") {
      posts += 1;
      return jsonResponse(spoofed);
    }
    throw new Error("Unexpected request");
  };
  await assert.rejects(
    deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} }),
    (error) => error instanceof ReleaseAutomationError && error.code === "DELIVERY_AMBIGUOUS",
  );
  assert.equal(posts, 1);
});

test("unexpected display-bearing embed fields fail closed", async (t) => {
  const cases = [
    (embed) => { embed.thumbnail = { url: "https://example.com/thumbnail.png" }; },
    (embed) => { embed.author = { name: "spoofed" }; },
    (embed) => { embed.video = { url: "https://example.com/video.mp4" }; },
    (embed) => { embed.provider = { name: "spoofed" }; },
    (embed) => { embed.type = "video"; },
    (embed) => { embed.footer.icon_url = "https://example.com/footer.png"; },
  ];
  for (const mutate of cases) {
    const { bundle } = createFixture(t, { withImage: true });
    const spoofed = discordMessage(bundle);
    mutate(spoofed.embeds[0]);
    let posts = 0;
    const fetchImpl = async (url, init) => {
      const preflight = preflightResponse(String(url));
      if (preflight) return preflight;
      if (String(url).endsWith("/messages?limit=100")) return jsonResponse([]);
      if (String(url).endsWith("/messages") && init.method === "POST") {
        posts += 1;
        return jsonResponse(spoofed);
      }
      throw new Error("Unexpected request");
    };
    await assert.rejects(
      deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} }),
      (error) => error instanceof ReleaseAutomationError && error.code === "DELIVERY_AMBIGUOUS",
    );
    assert.equal(posts, 1);
  }
});

test("unexpected top-level display state fails closed", async (t) => {
  const cases = [
    (message) => { message.components = [{ components: [], type: 1 }]; },
    (message) => { message.poll = null; },
    (message) => { message.flags = 4; },
    (message) => { message.flags = "0"; },
    (message) => { message.sticker_items = [{ format_type: 1, id: "623456789012345678", name: "sticker" }]; },
    (message) => { message.stickers = [{ id: "623456789012345678", name: "sticker" }]; },
    (message) => { message.shared_client_theme = { base_mix: 50, colors: ["5865F2"], gradient_angle: 0 }; },
    (message) => { message.mention_channels = []; },
    (message) => { message.resolved = {}; },
    (message) => { message.position = 0; },
    (message) => { message.message_reference = { message_id: "623456789012345678" }; },
    (message) => { message.referenced_message = {}; },
    (message) => { message.interaction = {}; },
    (message) => { message.interaction_metadata = {}; },
    (message) => { message.type = 19; },
    (message) => { message.tts = true; },
    (message) => { message.edited_timestamp = "2026-07-30T00:00:00.000Z"; },
    (message) => { message.pinned = true; },
  ];
  for (const mutate of cases) {
    const { bundle } = createFixture(t);
    const spoofed = discordMessage(bundle);
    mutate(spoofed);
    let posts = 0;
    const fetchImpl = async (url, init) => {
      const preflight = preflightResponse(String(url));
      if (preflight) return preflight;
      if (String(url).endsWith("/messages?limit=100")) return jsonResponse([]);
      if (String(url).endsWith("/messages") && init.method === "POST") {
        posts += 1;
        return jsonResponse(spoofed);
      }
      throw new Error("Unexpected request");
    };
    await assert.rejects(
      deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} }),
      (error) => error instanceof ReleaseAutomationError && error.code === "DELIVERY_AMBIGUOUS",
    );
    assert.equal(posts, 1);
  }
});

test("normal absent and empty Discord presentation defaults remain valid", async (t) => {
  const { bundle } = createFixture(t);
  const expectedMessage = discordMessage(bundle);
  delete expectedMessage.flags;
  expectedMessage.sticker_items = [];
  expectedMessage.stickers = [];
  expectedMessage.embeds[0].type = "rich";
  expectedMessage.components[0].id = 0;
  expectedMessage.components[0].components[0].id = 1;
  expectedMessage.components[0].components[0].disabled = false;
  expectedMessage.components[0].components[0].emoji = null;
  const fetchImpl = async (url, init) => {
    const preflight = preflightResponse(String(url));
    if (preflight) return preflight;
    if (String(url).endsWith("/messages?limit=100")) return jsonResponse([]);
    if (String(url).endsWith("/messages") && init.method === "POST") return jsonResponse(expectedMessage);
    if (String(url).endsWith(`/messages/${MESSAGE_ID}`)) return jsonResponse(expectedMessage);
    throw new Error("Unexpected request");
  };
  const result = await deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} });
  assert.equal(result.status, "posted");
});

test("normal verification accepts the harmless suppress-notifications message flag", async (t) => {
  const { bundle } = createFixture(t);
  const expectedMessage = discordMessage(bundle, { flags: 4096 });
  const fetchImpl = async (url, init) => {
    const preflight = preflightResponse(String(url));
    if (preflight) return preflight;
    if (String(url).endsWith("/messages?limit=100")) return jsonResponse([]);
    if (String(url).endsWith("/messages") && init.method === "POST") return jsonResponse(expectedMessage);
    if (String(url).endsWith(`/messages/${MESSAGE_ID}`)) return jsonResponse(expectedMessage);
    throw new Error("Unexpected request");
  };
  const result = await deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} });
  assert.equal(result.status, "posted");
});

test("attachment alt text, title, and spoiler presentation metadata fail closed on rerun", async (t) => {
  const cases = [
    (attachment) => { attachment.description = "different alt text"; },
    (attachment) => { delete attachment.description; },
    (attachment) => { attachment.title = "Different visible title"; },
    (attachment) => { attachment.flags = 1 << 3; },
    (attachment) => { attachment.ephemeral = true; },
  ];
  for (const mutate of cases) {
    const { bundle } = createFixture(t, { withImage: true });
    const changed = discordMessage(bundle);
    mutate(changed.attachments[0]);
    let posts = 0;
    const fetchImpl = async (url, init) => {
      const preflight = preflightResponse(String(url));
      if (preflight) return preflight;
      if (String(url).endsWith("/messages?limit=100")) return jsonResponse([changed]);
      if (init.method === "POST") posts += 1;
      throw new Error("Unexpected request");
    };
    await assert.rejects(
      deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} }),
      (error) => error instanceof ReleaseAutomationError && error.code === "DISCORD_VERIFICATION",
    );
    assert.equal(posts, 0);
  }
});

test("unexpected embed timestamps and changed field inline layout fail closed", async (t) => {
  const cases = [
    (embed) => { embed.timestamp = "2026-07-30T00:00:00.000Z"; },
    (embed) => { embed.fields[0].inline = true; },
    (embed) => { embed.fields[0].inline = "false"; },
  ];
  for (const mutate of cases) {
    const { bundle } = createFixture(t);
    const spoofed = discordMessage(bundle);
    mutate(spoofed.embeds[0]);
    let posts = 0;
    const fetchImpl = async (url, init) => {
      const preflight = preflightResponse(String(url));
      if (preflight) return preflight;
      if (String(url).endsWith("/messages?limit=100")) return jsonResponse([]);
      if (String(url).endsWith("/messages") && init.method === "POST") {
        posts += 1;
        return jsonResponse(spoofed);
      }
      throw new Error("Unexpected request");
    };
    await assert.rejects(
      deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} }),
      (error) => error instanceof ReleaseAutomationError && error.code === "DELIVERY_AMBIGUOUS",
    );
    assert.equal(posts, 1);
  }
});

test("an explicit false field inline value is equivalent to its Discord-default absence", async (t) => {
  const { bundle } = createFixture(t);
  const expectedMessage = discordMessage(bundle);
  expectedMessage.embeds[0].fields[0].inline = false;
  const fetchImpl = async (url, init) => {
    const preflight = preflightResponse(String(url));
    if (preflight) return preflight;
    if (String(url).endsWith("/messages?limit=100")) return jsonResponse([]);
    if (String(url).endsWith("/messages") && init.method === "POST") return jsonResponse(expectedMessage);
    if (String(url).endsWith(`/messages/${MESSAGE_ID}`)) return jsonResponse(expectedMessage);
    throw new Error("Unexpected request");
  };
  const result = await deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} });
  assert.equal(result.status, "posted");
});

test("announcement link buttons reject changed targets, labels, actions, and enabled state", async (t) => {
  const cases = [
    (message) => { message.components[0].components[0].url = "https://example.com/download"; },
    (message) => { message.components[0].components[1].label = "Different label"; },
    (message) => { message.components[0].components[0].custom_id = "unexpected-action"; },
    (message) => { message.components[0].components[0].disabled = true; },
    (message) => { message.components[0].components.push({ label: "Extra", style: 5, type: 2, url: "https://example.com" }); },
  ];
  for (const mutate of cases) {
    const { bundle } = createFixture(t);
    const changed = discordMessage(bundle);
    mutate(changed);
    let writes = 0;
    const fetchImpl = async (url, init) => {
      const response = preflightResponse(String(url));
      if (response) return response;
      if (String(url).endsWith("/messages?limit=100")) return jsonResponse([changed]);
      if (init.method !== "GET") writes += 1;
      throw new Error("Unexpected request");
    };
    await assert.rejects(
      deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} }),
      (error) =>
        error instanceof ReleaseAutomationError &&
        ["DISCORD_VERIFICATION", "DELIVERY_AMBIGUOUS"].includes(error.code),
    );
    assert.equal(writes, 0);
  }
});

test("the configured release role must explicitly be unmanaged", async (t) => {
  const { bundle } = createFixture(t);
  let requests = 0;
  const fetchImpl = async (url) => {
    requests += 1;
    if (String(url) === "https://discord.com/api/v10/users/@me") {
      return jsonResponse({ bot: true, id: BOT_ID });
    }
    if (String(url) === `https://discord.com/api/v10/channels/${CHANNEL_ID}`) {
      return jsonResponse({ guild_id: GUILD_ID, id: CHANNEL_ID, last_message_id: null, type: 0 });
    }
    if (String(url) === `https://discord.com/api/v10/guilds/${GUILD_ID}/roles`) {
      return jsonResponse([{ id: ROLE_ID, mentionable: true, name: "SessionDock" }]);
    }
    throw new Error("Unexpected request");
  };
  await assert.rejects(
    deliverAnnouncement({ bundle, env: deliveryEnv(), fetchImpl, sleepImpl: async () => {} }),
    (error) => error instanceof ReleaseAutomationError && error.code === "DISCORD_ROLE",
  );
  assert.equal(requests, 3);
});

test("the CLI refuses an existing receipt before any network request", (t) => {
  const fixture = createFixture(t);
  mkdirSync(path.join(fixture.root, "receipts"));
  writeFileSync(path.join(fixture.root, "receipts", "receipt.json"), "owned\n");
  const { fetchLog, result } = runCliChild(fixture, "receipts/receipt.json");
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /RECEIPT_RESERVATION/);
  assert.throws(() => readFileSync(fetchLog));
  assert.equal(readFileSync(path.join(fixture.root, "receipts", "receipt.json"), "utf8"), "owned\n");
});

test("the CLI refuses an unwritable receipt path before any network request", (t) => {
  const fixture = createFixture(t);
  writeFileSync(path.join(fixture.root, "blocked"), "not a directory\n");
  const { fetchLog, result } = runCliChild(fixture, "blocked/receipt.json");
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /RECEIPT_RESERVATION/);
  assert.throws(() => readFileSync(fetchLog));
});

test("the CLI finalizes a confirmed receipt and exits successfully", (t) => {
  const fixture = createFixture(t);
  const stagedScript = stageStandaloneAutomation(fixture.root);
  const { fetchLog, result } = runCliChild(fixture, "receipts/receipt.json", "confirmed", stagedScript);
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /message 523456789012345678 verified/);
  const receipt = JSON.parse(readFileSync(path.join(fixture.root, "receipts", "receipt.json"), "utf8"));
  assert.equal(receipt.status, "confirmed");
  assert.equal(receipt.verified, true);
  assert.equal(receipt.discord.messageId, MESSAGE_ID);
  assert.equal(
    readFileSync(fetchLog, "utf8")
      .split(/\r?\n/u)
      .filter((line) => line.startsWith("POST ")).length,
    1,
  );
});

test("the CLI preserves an ambiguous delivery receipt and safe exit output", (t) => {
  const fixture = createFixture(t);
  const { result } = runCliChild(fixture, "receipts/receipt.json", "ambiguous");
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /DELIVERY_AMBIGUOUS/);
  assert.ok(!result.stderr.includes(TOKEN));
  const receipt = JSON.parse(readFileSync(path.join(fixture.root, "receipts", "receipt.json"), "utf8"));
  assert.equal(receipt.status, "ambiguous");
  assert.equal(receipt.verified, false);
});

test("a confirmed delivery reports receipt finalization failure without replacing reserved evidence", (t) => {
  const fixture = createFixture(t);
  const receipts = path.join(fixture.root, "receipts");
  mkdirSync(receipts);
  const finalizing = path.join(receipts, "receipt.json.finalizing");
  writeFileSync(finalizing, "owned\n");
  const { result } = runCliChild(fixture, "receipts/receipt.json");
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /RECEIPT_FINALIZATION/);
  const receipt = JSON.parse(readFileSync(path.join(receipts, "receipt.json"), "utf8"));
  assert.equal(receipt.status, "reserved");
  assert.equal(receipt.verified, false);
  assert.equal(readFileSync(finalizing, "utf8"), "owned\n");
});

test("an ambiguous delivery keeps its classification when receipt finalization also fails", (t) => {
  const fixture = createFixture(t);
  const receipts = path.join(fixture.root, "receipts");
  mkdirSync(receipts);
  const finalizing = path.join(receipts, "receipt.json.finalizing");
  writeFileSync(finalizing, "owned\n");
  const { result } = runCliChild(fixture, "receipts/receipt.json", "ambiguous");
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /DELIVERY_AMBIGUOUS/);
  assert.doesNotMatch(result.stderr, /RECEIPT_FINALIZATION/);
  const receipt = JSON.parse(readFileSync(path.join(receipts, "receipt.json"), "utf8"));
  assert.equal(receipt.status, "reserved");
  assert.equal(receipt.verified, false);
  assert.equal(readFileSync(finalizing, "utf8"), "owned\n");
});

test("approved schema-2 migration emits one minimal PATCH and preserves understood flags", async (t) => {
  const { sourceBundle, targetBundle } = createApprovedMigrationFixture(t);
  for (const { sourceFlags, finalFlags } of [
    { sourceFlags: 4, finalFlags: 0 },
    { sourceFlags: 4096, finalFlags: 4096 },
    { sourceFlags: 4100, finalFlags: 4096 },
  ]) {
    await t.test(`${sourceFlags} -> ${finalFlags}`, async () => {
      const harness = createMigrationFetchHarness({ sourceBundle, targetBundle, sourceFlags });
      const result = await migrateExistingAnnouncement(
        approvedMigrationArguments(sourceBundle, { fetchImpl: harness.fetchImpl }),
      );
      assert.deepEqual(result, {
        kind: "sessiondock.discord-release-migration-receipt",
        patchAttempted: true,
        release: { sourceCommit: APPROVED_COMMIT, tag: APPROVED_TAG },
        schemaVersion: 1,
        source: {
          announcementId: APPROVED_SOURCE_ANNOUNCEMENT,
          artifactSha256: APPROVED_SOURCE_ARTIFACT,
          schemaVersion: 2,
        },
        status: "migrated",
        target: {
          announcementId: APPROVED_TARGET_ANNOUNCEMENT,
          artifactSha256: APPROVED_TARGET_ARTIFACT,
          schemaVersion: 3,
        },
        verified: true,
      });
      assert.equal(harness.historyReads, 1);
      assert.equal(harness.patchBodies.length, 1);
      const patchBody = harness.patchBodies[0];
      assert.deepEqual(Object.keys(patchBody).sort(), ["components", "embeds", "flags"]);
      assert.equal(patchBody.flags, finalFlags);
      assert.equal(patchBody.content, undefined);
      assert.equal(patchBody.allowed_mentions, undefined);
      assert.equal(patchBody.nonce, undefined);
      assert.equal(patchBody.attachments, undefined);
      assert.equal(patchBody.enforce_nonce, undefined);
      assert.deepEqual(
        patchBody.components[0].components.map(({ label, style, type, url }) => ({ label, style, type, url })),
        [
          {
            label: "Download portable ZIP",
            style: 5,
            type: 2,
            url: `https://github.com/Makmatoe/SessionDock/releases/download/${APPROVED_TAG}/SessionDock-win-x64-Portable.zip`,
          },
          {
            label: "View latest release",
            style: 5,
            type: 2,
            url: "https://github.com/Makmatoe/SessionDock/releases/latest",
          },
        ],
      );
      assert.deepEqual(
        harness.requests.filter(({ method }) => method !== "GET").map(({ method }) => method),
        ["PATCH"],
      );
    });
  }
});

test("migration rejects zero, unknown, components-v2, and non-integer source flags before writing", async (t) => {
  const { sourceBundle, targetBundle } = createApprovedMigrationFixture(t);
  for (const sourceFlags of [0, 1, 32768, 8192, "4"]) {
    await t.test(`flags ${String(sourceFlags)}`, async () => {
      const harness = createMigrationFetchHarness({ sourceBundle, targetBundle, sourceFlags });
      await assert.rejects(
        migrateExistingAnnouncement(approvedMigrationArguments(sourceBundle, { fetchImpl: harness.fetchImpl })),
        (error) =>
          error instanceof ReleaseAutomationError &&
          ["MIGRATION_SOURCE_MISMATCH", "DISCORD_VERIFICATION"].includes(error.code),
      );
      assert.equal(harness.patchBodies.length, 0);
      assert.equal(harness.requests.some(({ method }) => method !== "GET"), false);
    });
  }
});

test("an exact migrated target rerun is read-only and accepts an omitted nonce", async (t) => {
  const { sourceBundle, targetBundle } = createApprovedMigrationFixture(t);
  const target = migratedDiscordMessage(sourceBundle, targetBundle, 4096, {
    edited_timestamp: "2026-08-18T12:34:56Z",
  });
  delete target.nonce;
  const harness = createMigrationFetchHarness({
    sourceBundle,
    targetBundle,
    sourceFlags: 4096,
    historyMessages: [target],
    rereadSource: target,
  });
  const result = await migrateExistingAnnouncement(
    approvedMigrationArguments(sourceBundle, { fetchImpl: harness.fetchImpl }),
  );
  assert.equal(result.status, "already-migrated");
  assert.equal(result.patchAttempted, false);
  assert.equal(result.verified, true);
  assert.equal(harness.historyReads, 1);
  assert.equal(harness.patchBodies.length, 0);
  assert.equal(harness.requests.some(({ method }) => method !== "GET"), false);
});

test("an ambiguous migration PATCH reconciles by exact-ID GET without retrying", async (t) => {
  const { sourceBundle, targetBundle } = createApprovedMigrationFixture(t);
  for (const patchMode of [
    "network-ambiguous",
    "server-ambiguous",
    "timeout-ambiguous",
    "rate-limit-ambiguous",
    "accepted-unusable-body",
  ]) {
    await t.test(patchMode, async () => {
      const harness = createMigrationFetchHarness({ sourceBundle, targetBundle, patchMode });
      const result = await migrateExistingAnnouncement(
        approvedMigrationArguments(sourceBundle, { fetchImpl: harness.fetchImpl }),
      );
      assert.equal(result.status, "migrated");
      assert.equal(harness.patchBodies.length, 1);
      assert.deepEqual(
        harness.requests.filter(({ method }) => method === "PATCH").map(({ method }) => method),
        ["PATCH"],
      );
    });
  }
});

test("post-PATCH exact-target verification rejects changed identity, state, flags, or nonce", async (t) => {
  const { sourceBundle, targetBundle } = createApprovedMigrationFixture(t);
  const mutations = [
    (message) => { message.id = OTHER_MESSAGE_ID; },
    (message) => { message.author = { bot: true, id: "623456789012345678" }; },
    (message) => { message.content = "changed"; },
    (message) => { message.edited_timestamp = null; },
    (message) => { message.edited_timestamp = "not-a-timestamp"; },
    (message) => { message.flags = 4096; },
    (message) => { message.nonce = "changed-nonce"; },
  ];
  for (const mutate of mutations) {
    const changed = migratedDiscordMessage(sourceBundle, targetBundle, 0);
    mutate(changed);
    const harness = createMigrationFetchHarness({ sourceBundle, targetBundle, finalMessage: changed });
    await assert.rejects(
      migrateExistingAnnouncement(approvedMigrationArguments(sourceBundle, { fetchImpl: harness.fetchImpl })),
      (error) => error instanceof ReleaseAutomationError && error.code === "MIGRATION_AMBIGUOUS" && error.ambiguous,
    );
    assert.equal(harness.patchBodies.length, 1);
    assert.equal(harness.requests.filter(({ method }) => method === "PATCH").length, 1);
    assert.equal(harness.requests.some(({ method }) => ["POST", "PUT", "DELETE"].includes(method)), false);
  }
});

test("a migration PATCH whose exact-ID readback is not the target fails ambiguously without a second write", async (t) => {
  const { sourceBundle, targetBundle } = createApprovedMigrationFixture(t);
  const old = discordMessage(sourceBundle, { flags: 4 });
  const harness = createMigrationFetchHarness({
    sourceBundle,
    targetBundle,
    patchMode: "network-ambiguous",
    finalMessage: old,
  });
  await assert.rejects(
    migrateExistingAnnouncement(approvedMigrationArguments(sourceBundle, { fetchImpl: harness.fetchImpl })),
    (error) => error instanceof ReleaseAutomationError && error.code === "MIGRATION_AMBIGUOUS" && error.ambiguous,
  );
  assert.equal(harness.patchBodies.length, 1);
  assert.equal(harness.requests.filter(({ method }) => method === "PATCH").length, 1);
  assert.equal(harness.requests.some(({ method }) => ["POST", "PUT", "DELETE"].includes(method)), false);
});

test("migration reread rejects changed source identity or presentation before PATCH", async (t) => {
  const { sourceBundle, targetBundle } = createApprovedMigrationFixture(t);
  const mutations = [
    (message) => { message.id = OTHER_MESSAGE_ID; },
    (message) => { message.author = { bot: true, id: "623456789012345678" }; },
    (message) => { message.content = "changed"; },
    (message) => { message.edited_timestamp = "2026-08-18T12:34:56Z"; },
    (message) => { message.nonce = "changed-nonce"; },
  ];
  for (const mutate of mutations) {
    const changed = discordMessage(sourceBundle, { flags: 4 });
    mutate(changed);
    const harness = createMigrationFetchHarness({ sourceBundle, targetBundle, rereadSource: changed });
    await assert.rejects(
      migrateExistingAnnouncement(approvedMigrationArguments(sourceBundle, { fetchImpl: harness.fetchImpl })),
      (error) => error instanceof ReleaseAutomationError,
    );
    assert.equal(harness.patchBodies.length, 0);
    assert.equal(harness.requests.some(({ method }) => method !== "GET"), false);
  }
});

test("migration history fails closed for missing, duplicate, mixed, conflicting, and newer announcements", async (t) => {
  const { sourceBundle, targetBundle } = createApprovedMigrationFixture(t);
  const source = discordMessage(sourceBundle, { flags: 4 });
  const target = migratedDiscordMessage(sourceBundle, targetBundle, 0);
  const duplicate = structuredClone(source);
  duplicate.id = OTHER_MESSAGE_ID;
  const conflicting = structuredClone(source);
  conflicting.embeds[0].footer.text = `sdrel:v1:Makmatoe/SessionDock:${APPROVED_TAG}:${"a".repeat(64)}`;
  const newer = structuredClone(source);
  newer.embeds[0].footer.text = `sdrel:v1:Makmatoe/SessionDock:v3.1.3:${"b".repeat(64)}`;
  newer.embeds[0].url = "https://github.com/Makmatoe/SessionDock/releases/tag/v3.1.3";
  for (const historyMessages of [[], [source, duplicate], [source, target], [conflicting], [newer]]) {
    const harness = createMigrationFetchHarness({ sourceBundle, targetBundle, historyMessages });
    await assert.rejects(
      migrateExistingAnnouncement(approvedMigrationArguments(sourceBundle, { fetchImpl: harness.fetchImpl })),
      (error) =>
        error instanceof ReleaseAutomationError &&
        ["MIGRATION_NOT_FOUND", "DISCORD_CONFLICT"].includes(error.code),
    );
    assert.equal(harness.patchBodies.length, 0);
    assert.equal(harness.requests.some(({ method }) => method !== "GET"), false);
  }
});

test("migration target pins and attachment-free source eligibility fail before network access", async (t) => {
  const { sourceBundle } = createApprovedMigrationFixture(t);
  let networkCalls = 0;
  for (const pinOverride of [
    { expectedTargetAnnouncementId: "0".repeat(64) },
    { expectedTargetArtifactSha256: "0".repeat(64) },
  ]) {
    await assert.rejects(
      migrateExistingAnnouncement(
        approvedMigrationArguments(sourceBundle, {
          ...pinOverride,
          fetchImpl: async () => {
            networkCalls += 1;
            throw new Error("network must not run");
          },
        }),
      ),
      (error) => error instanceof ReleaseAutomationError && error.code === "MIGRATION_TARGET_MISMATCH",
    );
  }
  assert.equal(networkCalls, 0);

  const attachmentBundle = {
    ...sourceBundle,
    artifact: {
      ...sourceBundle.artifact,
      announcement: {
        ...sourceBundle.artifact.announcement,
        attachments: [{ id: "0" }],
      },
    },
    images: [{ id: "0" }],
  };
  await assert.rejects(
    migrateExistingAnnouncement(
      approvedMigrationArguments(attachmentBundle, {
        fetchImpl: async () => {
          networkCalls += 1;
          throw new Error("network must not run");
        },
      }),
    ),
    (error) => error instanceof ReleaseAutomationError && error.code === "MIGRATION_NOT_APPROVED",
  );
  assert.equal(networkCalls, 0);
});

test("the migration CLI writes a mode-0600 content-free receipt and performs one PATCH", (t) => {
  const fixture = createApprovedMigrationFixture(t);
  const stagedScript = stageStandaloneAutomation(fixture.root);
  const receiptRelative = "receipts/migration.json";
  const { fetchLog, result } = runMigrationCliChild(fixture, receiptRelative, { automationScript: stagedScript });
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /migration migrated and verified/);
  assert.doesNotMatch(`${result.stdout}\n${result.stderr}`, new RegExp(`${TOKEN}|${MESSAGE_ID}`, "u"));
  const receiptPath = path.join(fixture.root, ...receiptRelative.split("/"));
  const receiptText = readFileSync(receiptPath, "utf8");
  const receipt = JSON.parse(receiptText);
  assert.deepEqual(Object.keys(receipt).sort(), [
    "kind",
    "patchAttempted",
    "release",
    "schemaVersion",
    "source",
    "status",
    "target",
    "verified",
  ]);
  assert.equal(receipt.kind, "sessiondock.discord-release-migration-receipt");
  assert.equal(receipt.status, "migrated");
  assert.equal(receipt.patchAttempted, true);
  assert.equal(receipt.verified, true);
  assert.deepEqual(receipt.release, { sourceCommit: APPROVED_COMMIT, tag: APPROVED_TAG });
  assert.deepEqual(receipt.source, {
    announcementId: APPROVED_SOURCE_ANNOUNCEMENT,
    artifactSha256: APPROVED_SOURCE_ARTIFACT,
    schemaVersion: 2,
  });
  assert.deepEqual(receipt.target, {
    announcementId: APPROVED_TARGET_ANNOUNCEMENT,
    artifactSha256: APPROVED_TARGET_ARTIFACT,
    schemaVersion: 3,
  });
  assert.doesNotMatch(receiptText, new RegExp(`${TOKEN}|${MESSAGE_ID}|<@&|content|channel|botId|flags`, "u"));
  if (process.platform !== "win32") {
    assert.equal(statSync(receiptPath).mode & 0o777, 0o600);
  }
  const requests = readFileSync(fetchLog, "utf8");
  assert.equal(requests.split(/\r?\n/u).filter((line) => line.startsWith("PATCH ")).length, 1);
  assert.doesNotMatch(requests, /^(?:POST|PUT|DELETE) /mu);
});

test("the migration CLI rejects a mismatched target pin before receipt reservation or network access", (t) => {
  const fixture = createApprovedMigrationFixture(t);
  for (const [name, options] of [
    ["announcement", { expectedTargetAnnouncement: "0".repeat(64) }],
    ["artifact", { expectedTargetArtifactSha256: "0".repeat(64) }],
  ]) {
    const receiptRelative = `receipts/migration-${name}.json`;
    const { fetchLog, result } = runMigrationCliChild(fixture, receiptRelative, options);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /MIGRATION_TARGET_MISMATCH/);
    assert.doesNotMatch(result.stderr, new RegExp(`${TOKEN}|${MESSAGE_ID}`, "u"));
    assert.throws(() => readFileSync(fetchLog));
    assert.throws(() => readFileSync(path.join(fixture.root, ...receiptRelative.split("/"))));
  }
});
