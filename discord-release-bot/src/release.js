import { Buffer } from "node:buffer";
import path from "node:path";
import {
  DISCORD_EMBED_TEXT_LIMIT,
  MAX_IMAGE_FILE_BYTES,
  MAX_RELEASE_FOOTER_LENGTH,
  MAX_RELEASE_NOTES_LENGTH,
  MAX_RELEASE_PUBLISHER_LENGTH,
  MAX_RELEASE_TITLE_LENGTH,
  MAX_TOTAL_IMAGE_BYTES,
  RELEASE_FOOTER_SEPARATOR,
} from "./config.js";
import {
  ChannelType,
  EmbedBuilder,
  FileUploadBuilder,
  InteractionContextType,
  LabelBuilder,
  MessageFlags,
  ModalBuilder,
  PermissionFlagsBits,
  SlashCommandBuilder,
  TextInputBuilder,
  TextInputStyle,
} from "discord.js";

export const RELEASE_COMMAND_NAME = "release";
export const RELEASE_MODAL_PREFIX = "release-notes:";

const RELEASE_CHANNEL_TYPES = new Set([ChannelType.GuildText, ChannelType.GuildAnnouncement]);
const IMAGE_EXTENSIONS = new Map([
  ["image/jpeg", ".jpg"],
  ["image/png", ".png"],
  ["image/webp", ".webp"],
  ["image/gif", ".gif"],
]);
const SUPPORTED_FILE_EXTENSIONS = new Set([".jpg", ".jpeg", ".png", ".webp", ".gif"]);
const IMAGE_MIME_TYPES_BY_EXTENSION = new Map([
  [".jpg", "image/jpeg"],
  [".jpeg", "image/jpeg"],
  [".png", "image/png"],
  [".webp", "image/webp"],
  [".gif", "image/gif"],
]);

export class UserFacingError extends Error {}

export function createReleaseCommand() {
  const command = new SlashCommandBuilder()
    .setName(RELEASE_COMMAND_NAME)
    .setDescription("Publish release notes with a role ping and optional images")
    .setContexts(InteractionContextType.Guild)
    .setDefaultMemberPermissions(PermissionFlagsBits.ManageGuild)
    .addRoleOption((option) =>
      option
        .setName("role")
        .setDescription("The community role to notify")
        .setRequired(true),
    )
    .addChannelOption((option) =>
      option
        .setName("channel")
        .setDescription("Where to publish; defaults to this channel")
        .addChannelTypes(ChannelType.GuildText, ChannelType.GuildAnnouncement),
    );

  return command;
}

export function createReleaseModal(draftId) {
  const titleInput = new TextInputBuilder()
    .setCustomId("title")
    .setPlaceholder("Version 2.7.0 — Faster and more reliable")
    .setStyle(TextInputStyle.Short)
    .setMinLength(1)
    .setMaxLength(MAX_RELEASE_TITLE_LENGTH)
    .setRequired(true);

  const notesInput = new TextInputBuilder()
    .setCustomId("notes")
    .setPlaceholder("## Highlights\n- Added ...\n- Improved ...\n- Fixed ...")
    .setStyle(TextInputStyle.Paragraph)
    .setMinLength(1)
    .setMaxLength(MAX_RELEASE_NOTES_LENGTH)
    .setRequired(true);

  const linkInput = new TextInputBuilder()
    .setCustomId("link")
    .setPlaceholder("https://example.com/releases/2.7.0")
    .setStyle(TextInputStyle.Short)
    .setMaxLength(2048)
    .setRequired(false);

  const imageInput = new FileUploadBuilder()
    .setCustomId("images")
    .setMinValues(0)
    .setMaxValues(4)
    .setRequired(false);

  return new ModalBuilder()
    .setCustomId(`${RELEASE_MODAL_PREFIX}${draftId}`)
    .setTitle("Publish release notes")
    .addLabelComponents(
      new LabelBuilder()
        .setLabel("Release title")
        .setTextInputComponent(titleInput),
      new LabelBuilder()
        .setLabel("What changed?")
        .setDescription("Discord Markdown is supported")
        .setTextInputComponent(notesInput),
      new LabelBuilder()
        .setLabel("Release/download link")
        .setDescription("Optional")
        .setTextInputComponent(linkInput),
      new LabelBuilder()
        .setLabel("Release images")
        .setDescription("Optional: upload up to four JPG, PNG, WebP, or GIF files")
        .setFileUploadComponent(imageInput),
    );
}

function normalizeContentType(value) {
  if (typeof value !== "string") {
    return undefined;
  }

  const contentType = value.split(";", 1)[0].trim().toLowerCase();
  return contentType || undefined;
}

function unsupportedImageError(name) {
  return new UserFacingError(
    `“${name ?? "attachment"}” is not a supported image. Use JPG, PNG, WebP, or GIF.`,
  );
}

function extensionForAttachment(attachment) {
  const contentType = normalizeContentType(attachment.contentType);
  if (contentType) {
    const extension = IMAGE_EXTENSIONS.get(contentType);
    if (!extension) {
      throw unsupportedImageError(attachment.name);
    }

    return extension;
  }

  const extension = path.extname(attachment.name ?? "").toLowerCase();
  if (SUPPORTED_FILE_EXTENSIONS.has(extension)) {
    return extension === ".jpeg" ? ".jpg" : extension;
  }

  throw unsupportedImageError(attachment.name);
}

function detectImageContentType(bytes) {
  if (
    bytes.length >= 8 &&
    bytes[0] === 0x89 &&
    bytes[1] === 0x50 &&
    bytes[2] === 0x4e &&
    bytes[3] === 0x47 &&
    bytes[4] === 0x0d &&
    bytes[5] === 0x0a &&
    bytes[6] === 0x1a &&
    bytes[7] === 0x0a
  ) {
    return "image/png";
  }

  if (bytes.length >= 3 && bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff) {
    return "image/jpeg";
  }

  if (bytes.length >= 6) {
    const signature = bytes.subarray(0, 6).toString("ascii");
    if (signature === "GIF87a" || signature === "GIF89a") {
      return "image/gif";
    }
  }

  if (
    bytes.length >= 12 &&
    bytes.subarray(0, 4).toString("ascii") === "RIFF" &&
    bytes.subarray(8, 12).toString("ascii") === "WEBP"
  ) {
    return "image/webp";
  }

  return undefined;
}

function expectedImageContentType(fileName) {
  return IMAGE_MIME_TYPES_BY_EXTENSION.get(path.extname(fileName ?? "").toLowerCase());
}

function normalizeDiscordAttachmentUrl(value, index) {
  let url;
  try {
    url = new URL(value);
  } catch {
    throw new UserFacingError(`Discord did not provide a valid URL for image ${index + 1}.`);
  }

  if (
    url.protocol !== "https:" ||
    url.hostname !== "cdn.discordapp.com" ||
    url.port !== "" ||
    url.username !== "" ||
    url.password !== "" ||
    (!url.pathname.startsWith("/attachments/") && !url.pathname.startsWith("/ephemeral-attachments/"))
  ) {
    throw new UserFacingError(`Image ${index + 1} must use a trusted Discord attachment URL.`);
  }

  return url.toString();
}

export function prepareImageDrafts(attachments, maxTotalBytes) {
  const effectiveMaxTotalBytes = normalizeMaxTotalImageBytes(maxTotalBytes);
  const drafts = attachments.map((attachment, index) => {
    const size = Number(attachment.size);
    if (!Number.isSafeInteger(size) || size <= 0) {
      throw new UserFacingError(`Discord did not provide a valid size for image ${index + 1}.`);
    }
    if (size > MAX_IMAGE_FILE_BYTES) {
      throw new UserFacingError(`Image ${index + 1} exceeds Discord's 10 MiB per-file limit.`);
    }

    return {
      url: normalizeDiscordAttachmentUrl(attachment.url, index),
      originalName: attachment.name ?? `image-${index + 1}`,
      size,
      fileName: `release-image-${index + 1}${extensionForAttachment(attachment)}`,
    };
  });

  const totalBytes = drafts.reduce((total, draft) => total + draft.size, 0);
  if (totalBytes > effectiveMaxTotalBytes) {
    const maxMb = Math.floor((effectiveMaxTotalBytes / 1024 / 1024) * 10) / 10;
    throw new UserFacingError(`The selected images exceed the ${maxMb} MiB combined limit.`);
  }

  return drafts;
}

export function validateReleaseLink(value) {
  const normalized = value.trim();
  if (!normalized) {
    return undefined;
  }

  let url;
  try {
    url = new URL(normalized);
  } catch {
    throw new UserFacingError("The release link must be a valid http:// or https:// URL.");
  }

  if (url.protocol !== "https:" && url.protocol !== "http:") {
    throw new UserFacingError("The release link must be a valid http:// or https:// URL.");
  }

  return url.toString();
}

function normalizeMaxTotalImageBytes(maxTotalBytes) {
  if (!Number.isSafeInteger(maxTotalBytes) || maxTotalBytes <= 0) {
    throw new UserFacingError("The configured combined image size limit is invalid.");
  }

  return Math.min(maxTotalBytes, MAX_TOTAL_IMAGE_BYTES);
}

function assertReleaseTextLength(value, name, maxLength) {
  if (typeof value !== "string" || value.length > maxLength) {
    throw new UserFacingError(`${name} must be ${maxLength} characters or fewer.`);
  }
}

function textLength(value) {
  return typeof value === "string" ? value.length : 0;
}

export function countEmbedTextCharacters(embeds) {
  return embeds.reduce((total, embed) => {
    const data = typeof embed?.toJSON === "function" ? embed.toJSON() : embed;
    const fieldsLength = (data?.fields ?? []).reduce(
      (fieldsTotal, field) => fieldsTotal + textLength(field?.name) + textLength(field?.value),
      0,
    );

    return (
      total +
      textLength(data?.title) +
      textLength(data?.description) +
      textLength(data?.footer?.text) +
      textLength(data?.author?.name) +
      fieldsLength
    );
  }, 0);
}

export function buildReleasePayload({
  title,
  notes,
  link,
  roleId,
  publishedBy,
  color,
  footer,
  imageFileNames = [],
}) {
  assertReleaseTextLength(title, "Release title", MAX_RELEASE_TITLE_LENGTH);
  assertReleaseTextLength(notes, "Release notes", MAX_RELEASE_NOTES_LENGTH);
  assertReleaseTextLength(footer, "Release footer", MAX_RELEASE_FOOTER_LENGTH);
  assertReleaseTextLength(publishedBy, "Publisher name", MAX_RELEASE_PUBLISHER_LENGTH);
  const footerText = `${footer}${RELEASE_FOOTER_SEPARATOR}${publishedBy}`;
  const releaseEmbed = new EmbedBuilder()
    .setColor(color)
    .setTitle(title)
    .setDescription(notes)
    .setFooter({ text: footerText })
    .setTimestamp();

  if (link) {
    releaseEmbed.setURL(link);
  }

  if (imageFileNames[0]) {
    releaseEmbed.setImage(`attachment://${imageFileNames[0]}`);
  }

  const embeds = [releaseEmbed];
  for (const fileName of imageFileNames.slice(1)) {
    embeds.push(
      new EmbedBuilder()
        .setColor(color)
        .setImage(`attachment://${fileName}`),
    );
  }

  if (countEmbedTextCharacters(embeds) > DISCORD_EMBED_TEXT_LIMIT) {
    throw new UserFacingError(
      `Release embed text exceeds Discord's ${DISCORD_EMBED_TEXT_LIMIT}-character combined limit.`,
    );
  }

  return {
    content: `<@&${roleId}>`,
    allowedMentions: {
      roles: [roleId],
      users: [],
      repliedUser: false,
    },
    embeds,
  };
}

async function readBoundedImage(response, index, expectedBytes) {
  const declaredValue = response.headers?.get?.("content-length");
  if (declaredValue !== null && declaredValue !== undefined) {
    if (!/^(?:0|[1-9]\d*)$/u.test(declaredValue)) {
      throw new UserFacingError(`Downloaded image ${index + 1} has an invalid Content-Length.`);
    }
    const declaredBytes = Number(declaredValue);
    if (!Number.isSafeInteger(declaredBytes) || declaredBytes !== expectedBytes) {
      throw new UserFacingError(`Downloaded image ${index + 1} does not match its declared size.`);
    }
  }

  if (!response.body || typeof response.body.getReader !== "function") {
    throw new UserFacingError(`Downloaded image ${index + 1} does not expose a readable byte stream.`);
  }
  let reader;
  try {
    reader = response.body.getReader();
  } catch {
    throw new UserFacingError(`Downloaded image ${index + 1} could not be read from Discord.`);
  }

  const chunks = [];
  let totalBytes = 0;
  try {
    while (true) {
      const chunk = await reader.read();
      if (!chunk || typeof chunk.done !== "boolean") {
        throw new Error("invalid stream result");
      }
      if (chunk.done) {
        break;
      }
      if (!(chunk.value instanceof Uint8Array) || chunk.value.byteLength > expectedBytes - totalBytes) {
        throw new Error("oversized or invalid stream chunk");
      }
      chunks.push(Buffer.from(chunk.value));
      totalBytes += chunk.value.byteLength;
    }
  } catch {
    try {
      await reader.cancel();
    } catch {
      // The bounded-read failure is authoritative.
    }
    throw new UserFacingError(`Downloaded image ${index + 1} exceeds or does not match its declared size.`);
  }
  try {
    reader.releaseLock();
  } catch {
    // Reading already completed; releasing a synthetic reader is best effort.
  }
  if (totalBytes !== expectedBytes) {
    throw new UserFacingError(`Downloaded image ${index + 1} does not match its declared size.`);
  }
  return Buffer.concat(chunks, totalBytes);
}

export async function downloadImages(imageDrafts, maxTotalBytes, fetchImpl = globalThis.fetch) {
  const effectiveMaxTotalBytes = normalizeMaxTotalImageBytes(maxTotalBytes);
  const files = await Promise.all(
    imageDrafts.map(async (draft, index) => {
      if (!Number.isSafeInteger(draft.size) || draft.size <= 0) {
        throw new UserFacingError(`Discord did not provide a valid size for image ${index + 1}.`);
      }
      if (draft.size > MAX_IMAGE_FILE_BYTES) {
        throw new UserFacingError(`Image ${index + 1} exceeds Discord's 10 MiB per-file limit.`);
      }

      const url = normalizeDiscordAttachmentUrl(draft.url, index);
      let response;
      try {
        response = await fetchImpl(url, {
          redirect: "error",
          signal: AbortSignal.timeout(20_000),
        });
      } catch {
        throw new UserFacingError(`Image ${index + 1} could not be downloaded from Discord.`);
      }

      if (!response.ok) {
        throw new UserFacingError(`Image ${index + 1} could not be downloaded from Discord.`);
      }

      const responseContentType = normalizeContentType(response.headers?.get?.("content-type"));
      if (responseContentType && !IMAGE_EXTENSIONS.has(responseContentType)) {
        throw new UserFacingError(`Downloaded image ${index + 1} is not a supported image type.`);
      }

      const attachment = await readBoundedImage(response, index, draft.size);

      const expectedContentType = expectedImageContentType(draft.fileName);
      const detectedContentType = detectImageContentType(attachment);
      if (
        !expectedContentType ||
        !detectedContentType ||
        detectedContentType !== expectedContentType ||
        (responseContentType && responseContentType !== detectedContentType)
      ) {
        throw new UserFacingError(
          `Downloaded image ${index + 1} does not match its declared JPG, PNG, WebP, or GIF type.`,
        );
      }

      return {
        attachment,
        name: draft.fileName,
        description: `Release image ${index + 1}: ${draft.originalName}`.slice(0, 1024),
      };
    }),
  );

  const actualBytes = files.reduce((total, file) => total + file.attachment.byteLength, 0);
  if (actualBytes > effectiveMaxTotalBytes) {
    throw new UserFacingError("The downloaded images exceed the configured combined size limit.");
  }

  return files;
}

function requireReleaseChannel(channel) {
  if (!channel || !RELEASE_CHANNEL_TYPES.has(channel.type) || typeof channel.send !== "function") {
    throw new UserFacingError("Choose a regular text channel or announcement channel for the release.");
  }

  return channel;
}

function assertPublisherPermissions(interaction) {
  if (!interaction.memberPermissions?.has(PermissionFlagsBits.ManageGuild)) {
    throw new UserFacingError("You need the Manage Server permission to publish release notes.");
  }
}

function assertBotPermissions(guild, channel, role) {
  const botMember = guild.members.me;
  if (!botMember) {
    throw new UserFacingError("The bot could not check its server permissions. Please try again.");
  }

  const permissions = channel.permissionsFor(botMember);
  const required = [
    [PermissionFlagsBits.ViewChannel, "View Channel"],
    [PermissionFlagsBits.SendMessages, "Send Messages"],
    [PermissionFlagsBits.EmbedLinks, "Embed Links"],
    [PermissionFlagsBits.AttachFiles, "Attach Files"],
  ];
  const missing = required.filter(([permission]) => !permissions?.has(permission)).map(([, name]) => name);

  if (missing.length > 0) {
    throw new UserFacingError(`The bot is missing these permissions in ${channel}: ${missing.join(", ")}.`);
  }

  const prohibited = [
    [PermissionFlagsBits.Administrator, "Administrator"],
    [PermissionFlagsBits.ManageGuild, "Manage Server"],
    [PermissionFlagsBits.ManageMessages, "Manage Messages"],
    [PermissionFlagsBits.MentionEveryone, "Mention Everyone"],
  ];
  const grantedProhibited = prohibited
    .filter(([permission]) => permissions?.has(permission))
    .map(([, name]) => name);
  if (grantedProhibited.length > 0) {
    throw new UserFacingError(`Remove these unnecessary bot permissions: ${grantedProhibited.join(", ")}.`);
  }

  if (!role.mentionable) {
    throw new UserFacingError(`${role} is not mentionable. Make the role mentionable before publishing.`);
  }
}

async function replyEphemeral(interaction, content) {
  if (interaction.deferred || interaction.replied) {
    await interaction.editReply({ content });
    return;
  }

  await interaction.reply({ content, flags: MessageFlags.Ephemeral });
}

export async function handleReleaseCommand(interaction, pendingReleases) {
  try {
    if (!interaction.inCachedGuild()) {
      throw new UserFacingError("Release notes can only be published inside a server.");
    }

    assertPublisherPermissions(interaction);
    const role = interaction.options.getRole("role", true);
    if (role.id === interaction.guild.id) {
      throw new UserFacingError("Choose a specific community role instead of @everyone.");
    }

    const channel = requireReleaseChannel(
      interaction.options.getChannel("channel") ?? interaction.channel,
    );
    assertBotPermissions(interaction.guild, channel, role);

    pendingReleases.put(interaction.id, {
      ownerId: interaction.user.id,
      guildId: interaction.guild.id,
      channelId: channel.id,
      roleId: role.id,
    });

    await interaction.showModal(createReleaseModal(interaction.id));
  } catch (error) {
    if (error instanceof UserFacingError) {
      await replyEphemeral(interaction, error.message);
      return;
    }

    throw error;
  }
}

export async function handleReleaseModal(interaction, pendingReleases, config) {
  const draftId = interaction.customId.slice(RELEASE_MODAL_PREFIX.length);
  const draft = pendingReleases.take(draftId);

  try {
    if (!draft) {
      throw new UserFacingError("This release form expired. Run `/release` again.");
    }

    if (
      interaction.user.id !== draft.ownerId ||
      interaction.guildId !== draft.guildId ||
      !interaction.inCachedGuild()
    ) {
      throw new UserFacingError("This release form is not valid for your account or server.");
    }

    assertPublisherPermissions(interaction);
    await interaction.deferReply({ flags: MessageFlags.Ephemeral });

    const uploadedImages = interaction.fields.getUploadedFiles("images")?.values() ?? [];
    const images = prepareImageDrafts([...uploadedImages], config.maxTotalImageBytes);

    const [channel, role] = await Promise.all([
      interaction.guild.channels.fetch(draft.channelId),
      interaction.guild.roles.fetch(draft.roleId),
    ]);
    requireReleaseChannel(channel);

    if (!role) {
      throw new UserFacingError("The selected notification role no longer exists.");
    }

    assertBotPermissions(interaction.guild, channel, role);

    const title = interaction.fields.getTextInputValue("title").trim();
    const notes = interaction.fields.getTextInputValue("notes").trim();
    const link = validateReleaseLink(interaction.fields.getTextInputValue("link"));
    if (!title || !notes) {
      throw new UserFacingError("The release title and notes cannot be blank.");
    }

    const files = await downloadImages(images, config.maxTotalImageBytes);
    const payload = buildReleasePayload({
      title,
      notes,
      link,
      roleId: role.id,
      publishedBy: interaction.user.username,
      color: config.embedColor,
      footer: config.footer,
      imageFileNames: files.map((file) => file.name),
    });

    const publishedMessage = await channel.send({ ...payload, files });
    try {
      await interaction.editReply({
        content: `Release notes published in ${channel}: ${publishedMessage.url}`,
      });
    } catch {
      console.error(
        `Release message ${publishedMessage.id} was sent, but its private acknowledgement could not be updated.`,
      );
    }
  } catch (error) {
    if (error instanceof UserFacingError) {
      await replyEphemeral(interaction, error.message);
      return;
    }

    throw error;
  }
}
