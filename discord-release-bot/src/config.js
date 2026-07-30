import "dotenv/config";

const SNOWFLAKE_PATTERN = /^\d{17,20}$/;
const DEFAULT_COLOR = 0x5865f2;
export const DISCORD_EMBED_TEXT_LIMIT = 6000;
export const MAX_RELEASE_TITLE_LENGTH = 256;
export const MAX_RELEASE_NOTES_LENGTH = 4000;
export const MAX_RELEASE_PUBLISHER_LENGTH = 32;
export const RELEASE_FOOTER_SEPARATOR = " • Published by ";
export const MAX_RELEASE_FOOTER_LENGTH =
  DISCORD_EMBED_TEXT_LIMIT -
  MAX_RELEASE_TITLE_LENGTH -
  MAX_RELEASE_NOTES_LENGTH -
  MAX_RELEASE_PUBLISHER_LENGTH -
  RELEASE_FOOTER_SEPARATOR.length;
export const MAX_IMAGE_FILE_BYTES = 10 * 1024 * 1024;
export const MAX_TOTAL_IMAGE_BYTES = 20 * 1024 * 1024;
const DEFAULT_MAX_IMAGE_MB = MAX_TOTAL_IMAGE_BYTES / 1024 / 1024;

function optionalString(value) {
  const normalized = value?.trim();
  return normalized ? normalized : undefined;
}

function requiredString(env, name) {
  const value = optionalString(env[name]);
  if (!value) {
    throw new Error(`${name} is required. Copy .env.example to .env and fill it in.`);
  }

  return value;
}

function parseSnowflake(value, name) {
  if (value && !SNOWFLAKE_PATTERN.test(value)) {
    throw new Error(`${name} must be a Discord ID containing 17 to 20 digits.`);
  }

  return value;
}

export function parseEmbedColor(value) {
  if (value === undefined || value === null || value === "") {
    return DEFAULT_COLOR;
  }

  const normalized = String(value).trim().replace(/^#/, "");
  if (!/^[0-9a-fA-F]{6}$/.test(normalized)) {
    throw new Error("RELEASE_EMBED_COLOR must be a six-digit hex color such as #5865F2.");
  }

  return Number.parseInt(normalized, 16);
}

function parseMaxImageBytes(value) {
  if (value === undefined || value === null || value === "") {
    return DEFAULT_MAX_IMAGE_MB * 1024 * 1024;
  }

  const megabytes = Number(value);
  if (!Number.isFinite(megabytes) || megabytes <= 0 || megabytes > DEFAULT_MAX_IMAGE_MB) {
    throw new Error("MAX_TOTAL_IMAGE_MB must be greater than 0 and no more than 20.");
  }

  return Math.floor(megabytes * 1024 * 1024);
}

export function loadConfig({ env = process.env, requireToken = true } = {}) {
  const clientId = parseSnowflake(requiredString(env, "DISCORD_CLIENT_ID"), "DISCORD_CLIENT_ID");
  const guildId = parseSnowflake(optionalString(env.DISCORD_GUILD_ID), "DISCORD_GUILD_ID");
  const token = requireToken ? requiredString(env, "DISCORD_TOKEN") : optionalString(env.DISCORD_TOKEN);
  const footer = optionalString(env.RELEASE_FOOTER) ?? "Community update";

  if (footer.length > MAX_RELEASE_FOOTER_LENGTH) {
    throw new Error(`RELEASE_FOOTER must be ${MAX_RELEASE_FOOTER_LENGTH} characters or fewer.`);
  }

  return {
    clientId,
    token,
    guildId,
    embedColor: parseEmbedColor(optionalString(env.RELEASE_EMBED_COLOR)),
    footer,
    maxTotalImageBytes: parseMaxImageBytes(optionalString(env.MAX_TOTAL_IMAGE_MB)),
  };
}
