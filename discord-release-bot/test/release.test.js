import assert from "node:assert/strict";
import test from "node:test";
import { ChannelType, PermissionFlagsBits } from "discord.js";
import {
  DISCORD_EMBED_TEXT_LIMIT,
  MAX_IMAGE_FILE_BYTES,
  MAX_RELEASE_FOOTER_LENGTH,
  MAX_RELEASE_NOTES_LENGTH,
  MAX_RELEASE_PUBLISHER_LENGTH,
  MAX_RELEASE_TITLE_LENGTH,
  MAX_TOTAL_IMAGE_BYTES,
} from "../src/config.js";
import {
  buildReleasePayload,
  countEmbedTextCharacters,
  createReleaseCommand,
  createReleaseModal,
  downloadImages,
  handleReleaseCommand,
  handleReleaseModal,
  prepareImageDrafts,
  UserFacingError,
  validateReleaseLink,
} from "../src/release.js";

const PNG_SIGNATURE = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

function pngArrayBuffer(size = PNG_SIGNATURE.length) {
  const bytes = new Uint8Array(size);
  bytes.set(PNG_SIGNATURE);
  return bytes.buffer;
}

function imageResponse(bytes, contentType = "image/png", declaredBytes = bytes.byteLength) {
  return new Response(bytes, {
    status: 200,
    headers: {
      "content-length": String(declaredBytes),
      "content-type": contentType,
    },
  });
}

function streamedImageResponse(chunks, { contentType = "image/png", onCancel = () => {} } = {}) {
  let index = 0;
  return {
    body: {
      getReader() {
        return {
          async cancel() {
            onCancel();
          },
          async read() {
            if (index === chunks.length) return { done: true, value: undefined };
            const value = chunks[index];
            index += 1;
            return { done: false, value };
          },
          releaseLock() {},
        };
      },
    },
    headers: new Headers({ "content-type": contentType }),
    ok: true,
  };
}

test("the slash command is guild-only and admin-scoped", () => {
  const command = createReleaseCommand().toJSON();

  assert.equal(command.name, "release");
  assert.equal(command.options[0].name, "role");
  assert.equal(command.options[0].required, true);
  assert.equal(command.options.length, 2);
  assert.ok(command.default_member_permissions);
});

test("the release modal includes an optional four-image upload", () => {
  const modal = createReleaseModal("123").toJSON();
  const imageLabel = modal.components.find((component) => component.component?.type === 19);
  const titleLabel = modal.components.find((component) => component.component?.custom_id === "title");
  const notesLabel = modal.components.find((component) => component.component?.custom_id === "notes");

  assert.equal(modal.components.length, 4);
  assert.equal(titleLabel.component.max_length, MAX_RELEASE_TITLE_LENGTH);
  assert.equal(notesLabel.component.max_length, MAX_RELEASE_NOTES_LENGTH);
  assert.equal(imageLabel.component.custom_id, "images");
  assert.equal(imageLabel.component.min_values, 0);
  assert.equal(imageLabel.component.max_values, 4);
  assert.equal(imageLabel.component.required, false);
});

test("the release command stores an owner-bound draft before presenting its modal", async () => {
  const granted = new Set([
    PermissionFlagsBits.ViewChannel,
    PermissionFlagsBits.SendMessages,
    PermissionFlagsBits.EmbedLinks,
    PermissionFlagsBits.AttachFiles,
  ]);
  const botMember = { id: "123456789012345678" };
  const role = { id: "423456789012345678", mentionable: true, toString: () => "@SessionDock" };
  const channel = {
    id: "323456789012345678",
    type: ChannelType.GuildText,
    permissionsFor: (member) => {
      assert.equal(member, botMember);
      return { has: (permission) => granted.has(permission) };
    },
    send: async () => {},
    toString: () => "#releases",
  };
  let stored;
  let shownModal;
  const interaction = {
    channel: null,
    guild: { id: "223456789012345678", members: { me: botMember } },
    id: "523456789012345678",
    inCachedGuild: () => true,
    memberPermissions: { has: (permission) => permission === PermissionFlagsBits.ManageGuild },
    options: {
      getChannel: (name) => {
        assert.equal(name, "channel");
        return channel;
      },
      getRole: (name, required) => {
        assert.equal(name, "role");
        assert.equal(required, true);
        return role;
      },
    },
    showModal: async (modal) => {
      shownModal = modal.toJSON();
    },
    user: { id: "623456789012345678" },
  };
  const pendingReleases = {
    put: (id, draft) => {
      stored = { draft, id };
    },
  };

  await handleReleaseCommand(interaction, pendingReleases);

  assert.deepEqual(stored, {
    draft: {
      channelId: channel.id,
      guildId: interaction.guild.id,
      ownerId: interaction.user.id,
      roleId: role.id,
    },
    id: interaction.id,
  });
  assert.equal(shownModal.custom_id, `release-notes:${interaction.id}`);
  assert.ok(shownModal.components.some((component) => component.component?.custom_id === "notes"));
});

test("release payload pings only the selected role and embeds every image", () => {
  const payload = buildReleasePayload({
    title: "Version 2.7.0",
    notes: "- Added a feature\n- Fixed a bug\n<@123> @everyone",
    link: "https://example.com/releases/2.7.0",
    roleId: "987654321098765432",
    publishedBy: "release-manager",
    color: 0x5865f2,
    footer: "Community update",
    imageFileNames: ["release-image-1.png", "release-image-2.jpg"],
  });

  assert.equal(payload.content, "<@&987654321098765432>");
  assert.deepEqual(payload.allowedMentions.roles, ["987654321098765432"]);
  assert.deepEqual(payload.allowedMentions.users, []);
  assert.equal(payload.allowedMentions.parse, undefined);
  assert.equal(payload.embeds.length, 2);
  assert.equal(payload.embeds[0].toJSON().image.url, "attachment://release-image-1.png");
  assert.equal(payload.embeds[1].toJSON().image.url, "attachment://release-image-2.jpg");
});

test("release embed text has an exact 6000-character aggregate budget", () => {
  const payload = buildReleasePayload({
    title: "t".repeat(MAX_RELEASE_TITLE_LENGTH),
    notes: "n".repeat(MAX_RELEASE_NOTES_LENGTH),
    roleId: "987654321098765432",
    publishedBy: "p".repeat(MAX_RELEASE_PUBLISHER_LENGTH),
    color: 0x5865f2,
    footer: "f".repeat(MAX_RELEASE_FOOTER_LENGTH),
  });

  assert.equal(countEmbedTextCharacters(payload.embeds), DISCORD_EMBED_TEXT_LIMIT);
  assert.throws(
    () =>
      buildReleasePayload({
        title: "t".repeat(MAX_RELEASE_TITLE_LENGTH + 1),
        notes: "Notes",
        roleId: "987654321098765432",
        publishedBy: "publisher",
        color: 0x5865f2,
        footer: "Community update",
      }),
    /Release title must be/,
  );
  assert.throws(
    () =>
      buildReleasePayload({
        title: "Release",
        notes: "n".repeat(MAX_RELEASE_NOTES_LENGTH + 1),
        roleId: "987654321098765432",
        publishedBy: "publisher",
        color: 0x5865f2,
        footer: "Community update",
      }),
    /Release notes must be/,
  );
  assert.throws(
    () =>
      buildReleasePayload({
        title: "Release",
        notes: "Notes",
        roleId: "987654321098765432",
        publishedBy: "publisher",
        color: 0x5865f2,
        footer: "f".repeat(MAX_RELEASE_FOOTER_LENGTH + 1),
      }),
    /Release footer must be/,
  );
  assert.throws(
    () =>
      buildReleasePayload({
        title: "Release",
        notes: "Notes",
        roleId: "987654321098765432",
        publishedBy: "p".repeat(MAX_RELEASE_PUBLISHER_LENGTH + 1),
        color: 0x5865f2,
        footer: "Community update",
      }),
    /Publisher name must be/,
  );
});

test("aggregate embed text counting includes author and field names and values", () => {
  assert.equal(
    countEmbedTextCharacters([
      {
        title: "1",
        description: "22",
        author: { name: "333" },
        footer: { text: "4444" },
        fields: [{ name: "55555", value: "666666" }],
      },
    ]),
    21,
  );
});

test("image drafts reject unsupported files and oversized selections", () => {
  const textFile = {
    name: "notes.txt",
    contentType: "text/plain",
    size: 100,
    url: "https://cdn.discordapp.com/attachments/1/2/notes.txt",
  };
  assert.throws(() => prepareImageDrafts([textFile], 1_000), UserFacingError);
  assert.throws(
    () => prepareImageDrafts([{ ...textFile, name: "not-really-an-image.png" }], 1_000),
    /not a supported image/,
  );

  const extensionOnlyImage = {
    name: "discord-omitted-the-type.png",
    size: 100,
    url: "https://cdn.discordapp.com/attachments/1/2/discord-omitted-the-type.png",
  };
  assert.equal(
    prepareImageDrafts([extensionOnlyImage], 1_000)[0].fileName,
    "release-image-1.png",
  );
  assert.throws(
    () => prepareImageDrafts([{ ...extensionOnlyImage, url: "https://example.com/image.png" }], 1_000),
    /trusted Discord attachment URL/,
  );

  const image = {
    name: "huge.png",
    contentType: "image/png",
    size: 2_000,
    url: "https://cdn.discordapp.com/attachments/1/2/huge.png",
  };
  assert.throws(() => prepareImageDrafts([image], 1_000), /combined limit/);
});

test("image drafts enforce exact per-file and hard aggregate boundaries", () => {
  const image = (name, size) => ({
    name,
    contentType: "image/png",
    size,
    url: `https://cdn.discordapp.com/attachments/1/2/${name}`,
  });

  assert.equal(
    prepareImageDrafts([image("one.png", MAX_IMAGE_FILE_BYTES)], MAX_TOTAL_IMAGE_BYTES)[0].size,
    MAX_IMAGE_FILE_BYTES,
  );
  assert.throws(
    () => prepareImageDrafts([image("too-large.png", MAX_IMAGE_FILE_BYTES + 1)], MAX_TOTAL_IMAGE_BYTES),
    /10 MiB per-file limit/,
  );
  assert.equal(
    prepareImageDrafts(
      [image("one.png", MAX_IMAGE_FILE_BYTES), image("two.png", MAX_IMAGE_FILE_BYTES)],
      MAX_TOTAL_IMAGE_BYTES,
    ).length,
    2,
  );
  assert.throws(
    () =>
      prepareImageDrafts(
        [image("one.png", 7 * 1024 * 1024), image("two.png", 7 * 1024 * 1024), image("three.png", 7 * 1024 * 1024)],
        24 * 1024 * 1024,
      ),
    /20 MiB combined limit/,
  );
});

test("downloadImages prepares Discord attachment data for publishing", async () => {
  const files = await downloadImages(
    [
      {
        url: "https://cdn.discordapp.com/ephemeral-attachments/1/2/image.png",
        originalName: "image.png",
        fileName: "release-image-1.png",
        size: PNG_SIGNATURE.length,
      },
    ],
    1_000,
    async (_url, init) => {
      assert.equal(init.redirect, "error");
      return imageResponse(PNG_SIGNATURE);
    },
  );

  assert.equal(files.length, 1);
  assert.equal(files[0].name, "release-image-1.png");
  assert.deepEqual([...files[0].attachment], [...PNG_SIGNATURE]);
});

test("downloadImages recognizes each advertised image signature", async () => {
  const cases = [
    ["jpg", "image/jpeg", new Uint8Array([0xff, 0xd8, 0xff])],
    ["png", "image/png", PNG_SIGNATURE],
    ["gif", "image/gif", new TextEncoder().encode("GIF89a")],
    ["webp", "image/webp", new Uint8Array([0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50])],
  ];

  for (const [extension, contentType, bytes] of cases) {
    const [file] = await downloadImages(
      [
        {
          url: `https://cdn.discordapp.com/ephemeral-attachments/1/2/image.${extension}`,
          originalName: `image.${extension}`,
          fileName: `release-image-1.${extension}`,
          size: bytes.byteLength,
        },
      ],
      1_000,
      async () => imageResponse(bytes, contentType),
    );

    assert.equal(file.name, `release-image-1.${extension}`);
  }
});

test("downloadImages rejects unsupported response types and spoofed image bytes", async () => {
  const draft = {
    url: "https://cdn.discordapp.com/ephemeral-attachments/1/2/image.png",
    originalName: "image.png",
    fileName: "release-image-1.png",
    size: PNG_SIGNATURE.length,
  };

  let untrustedFetches = 0;
  await assert.rejects(
    downloadImages(
      [{ ...draft, url: "https://example.com/image.png" }],
      1_000,
      async () => {
        untrustedFetches += 1;
        return imageResponse(PNG_SIGNATURE);
      },
    ),
    /trusted Discord attachment URL/,
  );
  assert.equal(untrustedFetches, 0);

  await assert.rejects(
    downloadImages([draft], 1_000, async () => imageResponse(PNG_SIGNATURE, "text/plain")),
    /not a supported image type/,
  );
  await assert.rejects(
    downloadImages(
      [draft],
      1_000,
      async () => imageResponse(new TextEncoder().encode("notapng!"), "image/png"),
    ),
    /does not match its declared/,
  );
  await assert.rejects(
    downloadImages([draft], 1_000, async () => imageResponse(new TextEncoder().encode("GIF89a"), "image/gif")),
    /does not match its declared/,
  );
});

test("downloadImages enforces the 10 MiB boundary before and after download", async () => {
  const draft = {
    url: "https://cdn.discordapp.com/ephemeral-attachments/1/2/image.png",
    originalName: "image.png",
    fileName: "release-image-1.png",
    size: MAX_IMAGE_FILE_BYTES,
  };
  const exact = await downloadImages(
    [draft],
    MAX_TOTAL_IMAGE_BYTES,
    async () => imageResponse(new Uint8Array(pngArrayBuffer(MAX_IMAGE_FILE_BYTES))),
  );
  assert.equal(exact[0].attachment.byteLength, MAX_IMAGE_FILE_BYTES);

  let fetched = false;
  await assert.rejects(
    downloadImages(
      [{ ...draft, size: MAX_IMAGE_FILE_BYTES + 1 }],
      MAX_TOTAL_IMAGE_BYTES,
      async () => {
        fetched = true;
        return imageResponse(new Uint8Array(0));
      },
    ),
    /10 MiB per-file limit/,
  );
  assert.equal(fetched, false);

  let canceled = 0;
  const exactBytes = new Uint8Array(pngArrayBuffer(MAX_IMAGE_FILE_BYTES));
  await assert.rejects(
    downloadImages(
      [draft],
      MAX_TOTAL_IMAGE_BYTES,
      async () => streamedImageResponse(
        [exactBytes, new Uint8Array([0])],
        { onCancel: () => { canceled += 1; } },
      ),
    ),
    /Downloaded image 1 exceeds or does not match its declared size/,
  );
  assert.equal(canceled, 1);
});

test("release links only allow http and https", () => {
  assert.equal(validateReleaseLink(""), undefined);
  assert.equal(validateReleaseLink("https://example.com/release"), "https://example.com/release");
  assert.throws(() => validateReleaseLink("javascript:alert(1)"), UserFacingError);
});

test("a sent community release stays successful when only its private acknowledgement fails", async (t) => {
  let sends = 0;
  const granted = new Set([
    PermissionFlagsBits.ViewChannel,
    PermissionFlagsBits.SendMessages,
    PermissionFlagsBits.EmbedLinks,
    PermissionFlagsBits.AttachFiles,
  ]);
  const channel = {
    id: "323456789012345678",
    type: ChannelType.GuildText,
    permissionsFor: () => ({ has: (permission) => granted.has(permission) }),
    send: async () => {
      sends += 1;
      return { id: "523456789012345678", url: "https://discord.com/channels/1/2/3" };
    },
    toString: () => "#releases",
  };
  const role = { id: "423456789012345678", mentionable: true, toString: () => "@SessionDock" };
  const interaction = {
    customId: "release-notes:draft",
    deferReply: async () => {},
    editReply: async () => { throw new Error("acknowledgement unavailable"); },
    fields: {
      getTextInputValue: (name) => ({ link: "", notes: "A guarded update", title: "SessionDock 2.7.4" })[name],
      getUploadedFiles: () => undefined,
    },
    guild: {
      channels: { fetch: async () => channel },
      members: { me: { id: "123456789012345678" } },
      roles: { fetch: async () => role },
    },
    guildId: "223456789012345678",
    inCachedGuild: () => true,
    memberPermissions: { has: (permission) => permission === PermissionFlagsBits.ManageGuild },
    user: { id: "623456789012345678", username: "maintainer" },
  };
  const pendingReleases = {
    take: () => ({
      channelId: channel.id,
      guildId: interaction.guildId,
      ownerId: interaction.user.id,
      roleId: role.id,
    }),
  };
  const originalError = console.error;
  const logs = [];
  console.error = (message) => logs.push(message);
  t.after(() => { console.error = originalError; });

  await handleReleaseModal(interaction, pendingReleases, {
    embedColor: 0x5865f2,
    footer: "Community update",
    maxTotalImageBytes: MAX_TOTAL_IMAGE_BYTES,
  });

  assert.equal(sends, 1);
  assert.equal(logs.length, 1);
  assert.match(logs[0], /was sent/);
});
