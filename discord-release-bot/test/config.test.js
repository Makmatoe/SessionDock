import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { parse as parseDotenv } from "dotenv";
import {
  loadConfig,
  MAX_RELEASE_FOOTER_LENGTH,
  MAX_TOTAL_IMAGE_BYTES,
  parseEmbedColor,
} from "../src/config.js";

const BASE_ENV = {
  DISCORD_CLIENT_ID: "123456789012345678",
  DISCORD_TOKEN: "secret",
};

test("parseEmbedColor accepts Discord-style hex colors", () => {
  assert.equal(parseEmbedColor("#5865F2"), 0x5865f2);
  assert.equal(parseEmbedColor("00ff7f"), 0x00ff7f);
});

test("parseEmbedColor rejects malformed colors", () => {
  assert.throws(() => parseEmbedColor("blue"), /six-digit hex color/);
});

test("loadConfig parses a complete environment", () => {
  const config = loadConfig({
    env: {
      ...BASE_ENV,
      DISCORD_GUILD_ID: "987654321098765432",
      RELEASE_EMBED_COLOR: "#112233",
      RELEASE_FOOTER: "Patch notes",
      MAX_TOTAL_IMAGE_MB: "10",
    },
  });

  assert.equal(config.clientId, "123456789012345678");
  assert.equal(config.guildId, "987654321098765432");
  assert.equal(config.embedColor, 0x112233);
  assert.equal(config.maxTotalImageBytes, 10 * 1024 * 1024);
});

test("the configured footer and aggregate image limits have safe exact boundaries", () => {
  const config = loadConfig({
    env: {
      ...BASE_ENV,
      RELEASE_FOOTER: "f".repeat(MAX_RELEASE_FOOTER_LENGTH),
      MAX_TOTAL_IMAGE_MB: "20",
    },
  });

  assert.equal(config.footer.length, MAX_RELEASE_FOOTER_LENGTH);
  assert.equal(config.maxTotalImageBytes, MAX_TOTAL_IMAGE_BYTES);
  assert.throws(
    () =>
      loadConfig({
        env: { ...BASE_ENV, RELEASE_FOOTER: "f".repeat(MAX_RELEASE_FOOTER_LENGTH + 1) },
      }),
    new RegExp(`${MAX_RELEASE_FOOTER_LENGTH} characters or fewer`),
  );
  assert.throws(
    () => loadConfig({ env: { ...BASE_ENV, MAX_TOTAL_IMAGE_MB: "20.1" } }),
    /no more than 20/,
  );
});

test("the checked-in dotenv example preserves the hash-prefixed embed color", () => {
  const example = parseDotenv(readFileSync(new URL("../.env.example", import.meta.url)));

  assert.equal(example.RELEASE_EMBED_COLOR, "#5865F2");
  assert.equal(parseEmbedColor(example.RELEASE_EMBED_COLOR), 0x5865f2);
});
