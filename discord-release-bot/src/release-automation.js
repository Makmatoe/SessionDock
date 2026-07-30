import { createHash } from "node:crypto";
import {
  closeSync,
  existsSync,
  fsyncSync,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  openSync,
  readFileSync,
  readdirSync,
  realpathSync,
  renameSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const PRODUCT = "SessionDock";
const REPOSITORY = "Makmatoe/SessionDock";
const KIND = "sessiondock.discord-release-announcement";
const SCHEMA_VERSION = 1;
const DISCORD_API = "https://discord.com/api/v10";
const USER_AGENT = "DiscordBot (https://github.com/Makmatoe/SessionDock, 1.0)";
const EMBED_COLOR = 0x5865f2;
const MAX_NOTES_BYTES = 64 * 1024;
const MAX_IMAGE_BYTES = 8 * 1024 * 1024;
const MAX_TOTAL_IMAGE_BYTES = 20 * 1024 * 1024;
const MAX_IMAGES = 4;
const MAX_HISTORY_PAGES = 100;
const MAX_JSON_RESPONSE_BYTES = 1024 * 1024;
const MAX_DISCORD_OPERATION_MILLISECONDS = 180_000;
const ATTACHMENT_FLAG_IS_SPOILER = 1 << 3;
const ATTACHMENT_FLAG_IS_ANIMATED = 1 << 5;
const PERMISSION_ADMINISTRATOR = 1n << 3n;
const PERMISSION_MANAGE_GUILD = 1n << 5n;
const PERMISSION_VIEW_CHANNEL = 1n << 10n;
const PERMISSION_SEND_MESSAGES = 1n << 11n;
const PERMISSION_MANAGE_MESSAGES = 1n << 13n;
const PERMISSION_EMBED_LINKS = 1n << 14n;
const PERMISSION_ATTACH_FILES = 1n << 15n;
const PERMISSION_READ_MESSAGE_HISTORY = 1n << 16n;
const PERMISSION_MENTION_EVERYONE = 1n << 17n;
const SNOWFLAKE_PATTERN = /^\d{17,20}$/;
const VERSION_PATTERN = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;
const COMMIT_PATTERN = /^[0-9a-f]{40}$/;
const DIGEST_PATTERN = /^[0-9a-f]{64}$/;
const SAFE_FILE_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$/;
const MARKER_PATTERN = /^sdrel:v1:Makmatoe\/SessionDock:(v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)):[0-9a-f]{64}$/;

export class ReleaseAutomationError extends Error {
  constructor(code, message, { ambiguous = false } = {}) {
    super(message);
    this.name = "ReleaseAutomationError";
    this.code = code;
    this.ambiguous = ambiguous;
  }
}

function fail(code, message, options) {
  throw new ReleaseAutomationError(code, message, options);
}

export function sha256(value) {
  return createHash("sha256").update(value).digest("hex");
}

function sortObject(value) {
  if (Array.isArray(value)) {
    return value.map(sortObject);
  }

  if (value && typeof value === "object" && Object.getPrototypeOf(value) === Object.prototype) {
    return Object.fromEntries(
      Object.keys(value)
        .sort()
        .map((key) => [key, sortObject(value[key])]),
    );
  }

  return value;
}

export function canonicalJson(value) {
  return JSON.stringify(sortObject(value));
}

function prettyJson(value) {
  return `${JSON.stringify(sortObject(value), null, 2)}\n`;
}

function isPlainObject(value) {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function assertExactKeys(value, keys, label) {
  if (!isPlainObject(value)) {
    fail("INVALID_ARTIFACT", `${label} must be an object.`);
  }

  const actual = Object.keys(value).sort();
  const expected = [...keys].sort();
  if (actual.length !== expected.length || actual.some((key, index) => key !== expected[index])) {
    fail("INVALID_ARTIFACT", `${label} has an unexpected shape.`);
  }
}

function decodeUtf8(buffer, label, { requireTerminalLf = true } = {}) {
  if (buffer.length >= 3 && buffer[0] === 0xef && buffer[1] === 0xbb && buffer[2] === 0xbf) {
    fail("INVALID_TEXT", `${label} must not contain a UTF-8 byte-order mark.`);
  }

  let text;
  try {
    text = new TextDecoder("utf-8", { fatal: true }).decode(buffer);
  } catch {
    fail("INVALID_TEXT", `${label} must be valid UTF-8.`);
  }

  if (text.includes("\r")) {
    fail("INVALID_TEXT", `${label} must use LF line endings.`);
  }
  if (/[\u0000-\u0008\u000b-\u001f\u007f]/u.test(text)) {
    fail("INVALID_TEXT", `${label} contains a prohibited control character.`);
  }
  if (requireTerminalLf && (!text.endsWith("\n") || text.endsWith("\n\n"))) {
    fail("INVALID_TEXT", `${label} must end in exactly one LF.`);
  }

  return text;
}

function parseJsonBuffer(buffer, label, { canonical = false } = {}) {
  const text = decodeUtf8(buffer, label);
  let value;
  try {
    value = JSON.parse(text);
  } catch {
    fail("INVALID_JSON", `${label} must contain valid JSON.`);
  }

  if (canonical && text !== prettyJson(value)) {
    fail("INVALID_JSON", `${label} must use canonical key ordering and formatting.`);
  }
  return { text, value };
}

function normalizeRelativePath(value, label) {
  if (
    typeof value !== "string" ||
    value.length === 0 ||
    value.includes("\\") ||
    path.isAbsolute(value) ||
    /^[A-Za-z]:/u.test(value)
  ) {
    fail("INVALID_PATH", `${label} must be a repository-relative POSIX path.`);
  }

  const segments = value.split("/");
  if (segments.some((segment) => !segment || segment === "." || segment === "..")) {
    fail("INVALID_PATH", `${label} must not contain empty or traversal segments.`);
  }
  if (path.posix.normalize(value) !== value) {
    fail("INVALID_PATH", `${label} is not normalized.`);
  }
  return value;
}

function resolveInside(root, relativePath, label) {
  const normalized = normalizeRelativePath(relativePath, label);
  const rootPath = realpathSync(root);
  const resolved = path.resolve(rootPath, ...normalized.split("/"));
  const prefix = `${rootPath}${path.sep}`;
  if (resolved !== rootPath && !resolved.startsWith(prefix)) {
    fail("INVALID_PATH", `${label} leaves the repository root.`);
  }
  return resolved;
}

function readRegularFile(root, relativePath, label, maxBytes) {
  const resolved = resolveInside(root, relativePath, label);
  let stat;
  try {
    stat = lstatSync(resolved);
  } catch {
    fail("MISSING_FILE", `${label} is missing.`);
  }
  if (!stat.isFile() || stat.isSymbolicLink()) {
    fail("INVALID_FILE", `${label} must be a regular non-symlink file.`);
  }
  if (stat.size > maxBytes) {
    fail("FILE_TOO_LARGE", `${label} exceeds its size limit.`);
  }

  const real = realpathSync(resolved);
  const rootReal = realpathSync(root);
  if (!real.startsWith(`${rootReal}${path.sep}`)) {
    fail("INVALID_FILE", `${label} resolves outside the repository root.`);
  }
  return readFileSync(resolved);
}

function assertDirectory(root, relativePath, label) {
  const resolved = resolveInside(root, relativePath, label);
  let stat;
  try {
    stat = lstatSync(resolved);
  } catch {
    fail("MISSING_DIRECTORY", `${label} is missing.`);
  }
  if (!stat.isDirectory() || stat.isSymbolicLink()) {
    fail("INVALID_DIRECTORY", `${label} must be a regular non-symlink directory.`);
  }
  return resolved;
}

function validateVersion(version) {
  if (typeof version !== "string" || !VERSION_PATTERN.test(version)) {
    fail("INVALID_VERSION", "The release version must be a stable X.Y.Z version.");
  }
  return version;
}

function validateCommit(commit) {
  if (typeof commit !== "string" || !COMMIT_PATTERN.test(commit)) {
    fail("INVALID_COMMIT", "The source commit must be exactly 40 lowercase hexadecimal characters.");
  }
  return commit;
}

export function parseCanonicalNotes(buffer, version) {
  validateVersion(version);
  if (!Buffer.isBuffer(buffer) || buffer.length === 0 || buffer.length > MAX_NOTES_BYTES) {
    fail("INVALID_NOTES", "Canonical release notes must be nonempty and no larger than 64 KiB.");
  }

  const text = decodeUtf8(buffer, "Canonical release notes");
  const lines = text.slice(0, -1).split("\n");
  if (lines[0] !== `${PRODUCT} ${version}` || lines[1] !== "") {
    fail("INVALID_NOTES", `Canonical release notes must start with '${PRODUCT} ${version}' and a blank line.`);
  }
  const description = lines.slice(2).join("\n");
  if (!description.trim()) {
    fail("INVALID_NOTES", "Canonical release notes must contain an announcement body.");
  }
  if (description.length > 4096) {
    fail("INVALID_NOTES", "Canonical release notes exceed Discord's 4,096-character description limit.");
  }

  return {
    buffer,
    text,
    description,
    bytes: buffer.length,
    digest: sha256(buffer),
  };
}

function imageType(buffer, fileName) {
  const extension = path.posix.extname(fileName);
  if (
    extension === ".png" &&
    buffer.length >= 8 &&
    buffer.subarray(0, 8).equals(Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]))
  ) {
    return "image/png";
  }
  if (
    (extension === ".jpg" || extension === ".jpeg") &&
    buffer.length >= 3 &&
    buffer[0] === 0xff &&
    buffer[1] === 0xd8 &&
    buffer[2] === 0xff
  ) {
    return "image/jpeg";
  }
  if (
    extension === ".gif" &&
    buffer.length >= 6 &&
    ["GIF87a", "GIF89a"].includes(buffer.subarray(0, 6).toString("ascii"))
  ) {
    return "image/gif";
  }
  if (
    extension === ".webp" &&
    buffer.length >= 12 &&
    buffer.subarray(0, 4).toString("ascii") === "RIFF" &&
    buffer.subarray(8, 12).toString("ascii") === "WEBP"
  ) {
    return "image/webp";
  }
  fail("INVALID_IMAGE", `Reviewed image '${fileName}' does not match its supported file type.`);
}

function validateReviewedMetadata({ selectionBuffer, manifestBuffer, version, loadImage }) {
  const selection = parseJsonBuffer(selectionBuffer, "Discord image selection", { canonical: true }).value;
  assertExactKeys(selection, ["images", "product", "schemaVersion", "version"], "Discord image selection");
  if (
    selection.schemaVersion !== 1 ||
    selection.product !== PRODUCT ||
    selection.version !== version ||
    !Array.isArray(selection.images) ||
    selection.images.length < 1 ||
    selection.images.length > MAX_IMAGES
  ) {
    fail("INVALID_IMAGE_SELECTION", "Discord image selection does not match this SessionDock release.");
  }

  const selectedNames = new Set();
  for (const fileName of selection.images) {
    if (
      typeof fileName !== "string" ||
      !SAFE_FILE_PATTERN.test(fileName) ||
      fileName.startsWith("SPOILER_") ||
      selectedNames.has(fileName)
    ) {
      fail("INVALID_IMAGE_SELECTION", "Discord image selection contains an unsafe or duplicate file name.");
    }
    selectedNames.add(fileName);
  }

  const manifest = parseJsonBuffer(manifestBuffer, "Reviewed image manifest").value;
  if (
    !isPlainObject(manifest) ||
    manifest.product !== PRODUCT ||
    manifest.version !== version ||
    !Array.isArray(manifest.outputs)
  ) {
    fail("INVALID_IMAGE_MANIFEST", "Reviewed image manifest does not match this SessionDock release.");
  }

  const outputs = new Map();
  for (const output of manifest.outputs) {
    if (
      !isPlainObject(output) ||
      typeof output.file !== "string" ||
      !SAFE_FILE_PATTERN.test(output.file) ||
      typeof output.sha256 !== "string" ||
      !DIGEST_PATTERN.test(output.sha256) ||
      outputs.has(output.file)
    ) {
      fail("INVALID_IMAGE_MANIFEST", "Reviewed image manifest contains an invalid or duplicate output.");
    }
    outputs.set(output.file, output);
  }

  let totalBytes = 0;
  const images = selection.images.map((fileName, index) => {
    const output = outputs.get(fileName);
    if (!output) {
      fail("INVALID_IMAGE_SELECTION", `Reviewed image '${fileName}' is not covered by the image manifest.`);
    }
    const buffer = loadImage(fileName);
    if (!Buffer.isBuffer(buffer) || buffer.length === 0 || buffer.length > MAX_IMAGE_BYTES) {
      fail("INVALID_IMAGE", `Reviewed image '${fileName}' exceeds its per-file size limit.`);
    }
    totalBytes += buffer.length;
    if (totalBytes > MAX_TOTAL_IMAGE_BYTES) {
      fail("INVALID_IMAGE", "Reviewed images exceed the 20 MiB combined limit.");
    }
    const digest = sha256(buffer);
    if (digest !== output.sha256) {
      fail("INVALID_IMAGE", `Reviewed image '${fileName}' does not match its manifest digest.`);
    }

    return {
      id: index,
      fileName,
      buffer,
      bytes: buffer.length,
      sha256: digest,
      mediaType: imageType(buffer, fileName),
      sourcePath: `docs/images/sessiondock-v${version}/${fileName}`,
    };
  });

  return {
    images,
    selectionBuffer,
    manifestBuffer,
    selectionSha256: sha256(selectionBuffer),
    manifestSha256: sha256(manifestBuffer),
  };
}

function loadReviewedImagesFromRepository(root, relativeDirectory, version) {
  const expectedDirectory = `docs/images/sessiondock-v${version}`;
  if (normalizeRelativePath(relativeDirectory, "Reviewed image directory") !== expectedDirectory) {
    fail("INVALID_IMAGE_SELECTION", `Reviewed images must come from '${expectedDirectory}'.`);
  }
  assertDirectory(root, relativeDirectory, "Reviewed image directory");
  const selectionBuffer = readRegularFile(
    root,
    `${relativeDirectory}/discord.json`,
    "Discord image selection",
    MAX_NOTES_BYTES,
  );
  const manifestBuffer = readRegularFile(
    root,
    `${relativeDirectory}/manifest.json`,
    "Reviewed image manifest",
    MAX_NOTES_BYTES,
  );
  return validateReviewedMetadata({
    selectionBuffer,
    manifestBuffer,
    version,
    loadImage: (fileName) =>
      readRegularFile(root, `${relativeDirectory}/${fileName}`, `Reviewed image '${fileName}'`, MAX_IMAGE_BYTES),
  });
}

function buildArtifact({ version, sourceCommit, notes, reviewedImages }) {
  const tag = `v${version}`;
  const releaseUrl = `https://github.com/${REPOSITORY}/releases/tag/${tag}`;
  const installerUrl = `https://github.com/${REPOSITORY}/releases/download/${tag}/SessionDock-win-x64-Setup.exe`;
  const images = reviewedImages?.images ?? [];
  const attachments = images.map((image) => ({
    artifactPath: `images/${image.fileName}`,
    bytes: image.bytes,
    fileName: image.fileName,
    id: image.id,
    mediaType: image.mediaType,
    sha256: image.sha256,
    sourcePath: image.sourcePath,
  }));

  const firstEmbed = {
    color: EMBED_COLOR,
    description: notes.description,
    fields: [
      {
        name: "Download",
        value: `[Windows x64 installer](${installerUrl})`,
      },
    ],
    title: `${PRODUCT} ${version}`,
    url: releaseUrl,
  };
  if (images[0]) {
    firstEmbed.image = { url: `attachment://${images[0].fileName}` };
  }
  const embeds = [firstEmbed];
  for (const image of images.slice(1)) {
    embeds.push({
      color: EMBED_COLOR,
      image: { url: `attachment://${image.fileName}` },
      url: releaseUrl,
    });
  }

  const sources = {
    releaseNotes: {
      artifactPath: "notes.md",
      bytes: notes.bytes,
      canonicalPath: `SessionDock/ReleaseNotes/${version}.en-US.md`,
      sha256: notes.digest,
    },
    reviewedImages: reviewedImages
      ? {
          directory: `docs/images/sessiondock-v${version}`,
          manifestArtifactPath: "image-manifest.json",
          manifestPath: `docs/images/sessiondock-v${version}/manifest.json`,
          manifestSha256: reviewedImages.manifestSha256,
          selectionArtifactPath: "reviewed-images.json",
          selectionPath: `docs/images/sessiondock-v${version}/discord.json`,
          selectionSha256: reviewedImages.selectionSha256,
        }
      : null,
  };
  const release = {
    installerUrl,
    product: PRODUCT,
    repository: REPOSITORY,
    sourceCommit,
    sourceRef: `refs/tags/${tag}`,
    tag,
    url: releaseUrl,
    version,
  };
  const core = {
    attachments,
    kind: KIND,
    message: { embeds },
    release,
    schemaVersion: SCHEMA_VERSION,
    sources,
  };
  const announcementId = sha256(Buffer.from(canonicalJson(core), "utf8"));
  const marker = `sdrel:v1:${REPOSITORY}:${tag}:${announcementId}`;
  const nonce = `sd-${Buffer.from(announcementId, "hex").subarray(0, 16).toString("base64url")}`;
  const message = {
    embeds: embeds.map((embed, index) =>
      index === 0 ? { ...embed, footer: { text: marker } } : embed,
    ),
  };
  const payload = {
    announcement: {
      attachments,
      id: announcementId,
      marker,
      message,
      nonce,
    },
    kind: KIND,
    release,
    schemaVersion: SCHEMA_VERSION,
    sources,
  };
  return {
    ...payload,
    integrity: {
      algorithm: "sha256",
      payloadSha256: sha256(Buffer.from(canonicalJson(payload), "utf8")),
    },
  };
}

function buildSummary(artifact, artifactDigest) {
  const imageLines = artifact.announcement.attachments.length
    ? artifact.announcement.attachments.map(
        (image) => `  - \`${image.fileName}\` (${image.bytes} bytes, SHA-256 \`${image.sha256}\`)`,
      )
    : ["  - None for this version"];
  return [
    "# Discord release announcement",
    "",
    "> Deterministic audit artifact. Generation sends nothing; Bota delivers it automatically only after the guarded GitHub release succeeds.",
    "",
    `- Release: \`${artifact.release.product} ${artifact.release.version}\``,
    `- Tag: \`${artifact.release.tag}\``,
    `- Source commit: \`${artifact.release.sourceCommit}\``,
    `- Notes SHA-256: \`${artifact.sources.releaseNotes.sha256}\``,
    `- Announcement ID: \`${artifact.announcement.id}\``,
    `- Artifact SHA-256: \`${artifactDigest}\``,
    "- Reviewed images:",
    ...imageLines,
    "",
    "No form, preview confirmation, or manual publish action is part of this workflow.",
    "",
  ].join("\n");
}

function writeNewFile(filePath, value) {
  writeFileSync(filePath, value, { flag: "wx" });
}

function listBundleFiles(root) {
  const files = [];
  function visit(directory, prefix = "") {
    for (const entry of readdirSync(directory, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
      const relative = prefix ? `${prefix}/${entry.name}` : entry.name;
      const absolute = path.join(directory, entry.name);
      const stat = lstatSync(absolute);
      if (stat.isSymbolicLink()) {
        fail("INVALID_BUNDLE", `Announcement bundle contains a symlink: ${relative}`);
      }
      if (stat.isDirectory()) {
        visit(absolute, relative);
      } else if (stat.isFile()) {
        files.push(relative);
      } else {
        fail("INVALID_BUNDLE", `Announcement bundle contains a non-regular entry: ${relative}`);
      }
    }
  }
  visit(root);
  return files.sort();
}

function validateReleaseShape(artifact, expected = {}) {
  assertExactKeys(
    artifact,
    ["announcement", "integrity", "kind", "release", "schemaVersion", "sources"],
    "Announcement artifact",
  );
  if (artifact.schemaVersion !== SCHEMA_VERSION || artifact.kind !== KIND) {
    fail("INVALID_ARTIFACT", "Announcement artifact uses an unsupported schema.");
  }
  assertExactKeys(
    artifact.release,
    ["installerUrl", "product", "repository", "sourceCommit", "sourceRef", "tag", "url", "version"],
    "Announcement release identity",
  );
  validateVersion(artifact.release.version);
  validateCommit(artifact.release.sourceCommit);
  const tag = `v${artifact.release.version}`;
  const expectedRelease = {
    installerUrl: `https://github.com/${REPOSITORY}/releases/download/${tag}/SessionDock-win-x64-Setup.exe`,
    product: PRODUCT,
    repository: REPOSITORY,
    sourceRef: `refs/tags/${tag}`,
    tag,
    url: `https://github.com/${REPOSITORY}/releases/tag/${tag}`,
  };
  for (const [key, value] of Object.entries(expectedRelease)) {
    if (artifact.release[key] !== value) {
      fail("INVALID_ARTIFACT", `Announcement release identity has an invalid ${key}.`);
    }
  }
  if (expected.tag && artifact.release.tag !== expected.tag) {
    fail("RELEASE_MISMATCH", "Announcement tag does not match the guarded release job.");
  }
  if (expected.ref && artifact.release.sourceRef !== expected.ref) {
    fail("RELEASE_MISMATCH", "Announcement source ref does not match the guarded release job.");
  }
  if (expected.commit && artifact.release.sourceCommit !== expected.commit) {
    fail("RELEASE_MISMATCH", "Announcement source commit does not match the guarded release job.");
  }
}

function loadReviewedImagesFromBundle(root, artifact) {
  const source = artifact.sources.reviewedImages;
  if (source === null) {
    return null;
  }
  assertExactKeys(
    source,
    [
      "directory",
      "manifestArtifactPath",
      "manifestPath",
      "manifestSha256",
      "selectionArtifactPath",
      "selectionPath",
      "selectionSha256",
    ],
    "Reviewed image source",
  );
  const version = artifact.release.version;
  const directory = `docs/images/sessiondock-v${version}`;
  if (
    source.directory !== directory ||
    source.manifestArtifactPath !== "image-manifest.json" ||
    source.manifestPath !== `${directory}/manifest.json` ||
    source.selectionArtifactPath !== "reviewed-images.json" ||
    source.selectionPath !== `${directory}/discord.json` ||
    !DIGEST_PATTERN.test(source.manifestSha256) ||
    !DIGEST_PATTERN.test(source.selectionSha256)
  ) {
    fail("INVALID_ARTIFACT", "Reviewed image source metadata is invalid.");
  }
  const selectionBuffer = readRegularFile(root, source.selectionArtifactPath, "Reviewed image selection", MAX_NOTES_BYTES);
  const manifestBuffer = readRegularFile(root, source.manifestArtifactPath, "Reviewed image manifest", MAX_NOTES_BYTES);
  if (sha256(selectionBuffer) !== source.selectionSha256 || sha256(manifestBuffer) !== source.manifestSha256) {
    fail("INVALID_BUNDLE", "Reviewed image source metadata does not match the bundle.");
  }
  return validateReviewedMetadata({
    selectionBuffer,
    manifestBuffer,
    version,
    loadImage: (fileName) =>
      readRegularFile(root, `images/${fileName}`, `Bundled image '${fileName}'`, MAX_IMAGE_BYTES),
  });
}

export function readAnnouncementBundle({ artifactDirectory, expectedTag, expectedRef, expectedCommit }) {
  const stat = lstatSync(artifactDirectory);
  if (!stat.isDirectory() || stat.isSymbolicLink()) {
    fail("INVALID_BUNDLE", "Announcement bundle must be a regular non-symlink directory.");
  }
  const root = realpathSync(artifactDirectory);
  const artifactBuffer = readRegularFile(root, "announcement.json", "Announcement artifact", MAX_NOTES_BYTES * 4);
  const { text: artifactText, value: artifact } = parseJsonBuffer(artifactBuffer, "Announcement artifact", {
    canonical: true,
  });
  validateReleaseShape(artifact, { tag: expectedTag, ref: expectedRef, commit: expectedCommit });
  assertExactKeys(artifact.sources, ["releaseNotes", "reviewedImages"], "Announcement sources");
  assertExactKeys(
    artifact.sources.releaseNotes,
    ["artifactPath", "bytes", "canonicalPath", "sha256"],
    "Release-note source",
  );
  const expectedNotesPath = `SessionDock/ReleaseNotes/${artifact.release.version}.en-US.md`;
  if (
    artifact.sources.releaseNotes.artifactPath !== "notes.md" ||
    artifact.sources.releaseNotes.canonicalPath !== expectedNotesPath ||
    !Number.isSafeInteger(artifact.sources.releaseNotes.bytes) ||
    !DIGEST_PATTERN.test(artifact.sources.releaseNotes.sha256)
  ) {
    fail("INVALID_ARTIFACT", "Release-note source metadata is invalid.");
  }
  const notesBuffer = readRegularFile(root, "notes.md", "Bundled canonical release notes", MAX_NOTES_BYTES);
  const notes = parseCanonicalNotes(notesBuffer, artifact.release.version);
  if (
    notes.bytes !== artifact.sources.releaseNotes.bytes ||
    notes.digest !== artifact.sources.releaseNotes.sha256
  ) {
    fail("INVALID_BUNDLE", "Bundled release notes do not match their source metadata.");
  }

  const reviewedImages = loadReviewedImagesFromBundle(root, artifact);
  const expectedArtifact = buildArtifact({
    version: artifact.release.version,
    sourceCommit: artifact.release.sourceCommit,
    notes,
    reviewedImages,
  });
  const expectedText = prettyJson(expectedArtifact);
  if (artifactText !== expectedText) {
    fail("INVALID_ARTIFACT", "Announcement artifact does not match its canonical sources.");
  }
  const artifactDigest = sha256(Buffer.from(artifactText, "utf8"));
  const digestText = decodeUtf8(
    readRegularFile(root, "announcement.sha256", "Announcement digest", 256),
    "Announcement digest",
  );
  if (digestText !== `${artifactDigest}  announcement.json\n`) {
    fail("INVALID_BUNDLE", "Announcement digest sidecar does not match the artifact.");
  }
  const summaryText = decodeUtf8(
    readRegularFile(root, "summary.md", "Announcement summary", MAX_NOTES_BYTES),
    "Announcement summary",
  );
  if (summaryText !== buildSummary(artifact, artifactDigest)) {
    fail("INVALID_BUNDLE", "Announcement summary does not match the artifact.");
  }

  const expectedFiles = ["announcement.json", "announcement.sha256", "notes.md", "summary.md"];
  if (reviewedImages) {
    expectedFiles.push("image-manifest.json", "reviewed-images.json");
    expectedFiles.push(...reviewedImages.images.map((image) => `images/${image.fileName}`));
  }
  const actualFiles = listBundleFiles(root);
  expectedFiles.sort();
  if (
    actualFiles.length !== expectedFiles.length ||
    actualFiles.some((file, index) => file !== expectedFiles[index])
  ) {
    fail("INVALID_BUNDLE", "Announcement bundle contains an unexpected file inventory.");
  }

  return {
    artifact,
    artifactDigest,
    artifactDirectory: root,
    images: reviewedImages?.images ?? [],
    notes,
    summaryText,
  };
}

export function generateAnnouncement({ root = process.cwd(), version, sourceCommit, notesPath, imagesPath, outputPath }) {
  validateVersion(version);
  validateCommit(sourceCommit);
  const expectedNotesPath = `SessionDock/ReleaseNotes/${version}.en-US.md`;
  if (normalizeRelativePath(notesPath, "Release notes path") !== expectedNotesPath) {
    fail("INVALID_NOTES", `Release notes must come from '${expectedNotesPath}'.`);
  }
  const outputRelative = normalizeRelativePath(outputPath, "Announcement output path");
  const output = resolveInside(root, outputRelative, "Announcement output path");
  if (existsSync(output)) {
    fail("OUTPUT_EXISTS", "Announcement output directory already exists.");
  }

  const notesBuffer = readRegularFile(root, notesPath, "Canonical release notes", MAX_NOTES_BYTES);
  const notes = parseCanonicalNotes(notesBuffer, version);
  const reviewedImages = imagesPath
    ? loadReviewedImagesFromRepository(root, imagesPath, version)
    : null;
  const artifact = buildArtifact({ version, sourceCommit, notes, reviewedImages });
  const artifactText = prettyJson(artifact);
  const artifactDigest = sha256(Buffer.from(artifactText, "utf8"));
  const parent = path.dirname(output);
  mkdirSync(parent, { recursive: true });
  const temporary = mkdtempSync(path.join(parent, ".discord-announcement-"));

  try {
    writeNewFile(path.join(temporary, "announcement.json"), artifactText);
    writeNewFile(path.join(temporary, "announcement.sha256"), `${artifactDigest}  announcement.json\n`);
    writeNewFile(path.join(temporary, "notes.md"), notes.buffer);
    writeNewFile(path.join(temporary, "summary.md"), buildSummary(artifact, artifactDigest));
    if (reviewedImages) {
      writeNewFile(path.join(temporary, "reviewed-images.json"), reviewedImages.selectionBuffer);
      writeNewFile(path.join(temporary, "image-manifest.json"), reviewedImages.manifestBuffer);
      mkdirSync(path.join(temporary, "images"));
      for (const image of reviewedImages.images) {
        writeNewFile(path.join(temporary, "images", image.fileName), image.buffer);
      }
    }
    readAnnouncementBundle({
      artifactDirectory: temporary,
      expectedTag: `v${version}`,
      expectedRef: `refs/tags/v${version}`,
      expectedCommit: sourceCommit,
    });
    renameSync(temporary, output);
  } catch (error) {
    if (existsSync(temporary)) {
      rmSync(temporary, { recursive: true, force: true });
    }
    throw error;
  }

  return { artifact, artifactDigest, outputDirectory: output };
}

function requiredEnvironmentValue(env, name, { snowflake = false, token = false } = {}) {
  const value = env[name];
  if (typeof value !== "string" || !value || value.trim() !== value) {
    fail("INVALID_CONFIGURATION", `${name} is required and must not contain surrounding whitespace.`);
  }
  if (/[\u0000-\u001f\u007f]/u.test(value)) {
    fail("INVALID_CONFIGURATION", `${name} contains a prohibited control character.`);
  }
  if (snowflake && !SNOWFLAKE_PATTERN.test(value)) {
    fail("INVALID_CONFIGURATION", `${name} must contain a 17 to 20 digit Discord ID.`);
  }
  if (token && (value.length < 20 || value.length > 512)) {
    fail("INVALID_CONFIGURATION", `${name} does not have a safe token length.`);
  }
  return value;
}

export function loadDeliveryConfig(env = process.env) {
  return {
    token: requiredEnvironmentValue(env, "DISCORD_RELEASE_BOT_TOKEN", { token: true }),
    botId: requiredEnvironmentValue(env, "DISCORD_RELEASE_BOT_ID", { snowflake: true }),
    channelId: requiredEnvironmentValue(env, "DISCORD_RELEASE_CHANNEL_ID", { snowflake: true }),
    roleId: requiredEnvironmentValue(env, "DISCORD_RELEASE_ROLE_ID", { snowflake: true }),
  };
}

function delay(milliseconds) {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

async function cancelResponseReader(reader) {
  try {
    await reader.cancel();
  } catch {
    // The original bounded-read error is authoritative.
  }
}

async function readBoundedResponseBytes(
  response,
  { maxBytes, code = "DISCORD_RESPONSE", description = "Discord response", ambiguous = false },
) {
  if (!Number.isSafeInteger(maxBytes) || maxBytes < 0) {
    fail(code, `${description} has an invalid byte limit.`, { ambiguous });
  }

  let declaredLength;
  try {
    const header = response?.headers?.get("content-length");
    if (header !== null) {
      if (!/^(?:0|[1-9]\d*)$/u.test(header)) {
        fail(code, `${description} has an invalid Content-Length.`, { ambiguous });
      }
      declaredLength = Number(header);
      if (!Number.isSafeInteger(declaredLength) || declaredLength > maxBytes) {
        fail(code, `${description} exceeds the permitted byte limit.`, { ambiguous });
      }
    }
  } catch (error) {
    if (error instanceof ReleaseAutomationError) {
      throw error;
    }
    fail(code, `${description} has unreadable response headers.`, { ambiguous });
  }

  if (response?.body === null && (declaredLength === undefined || declaredLength === 0)) {
    return Buffer.alloc(0);
  }
  if (!response?.body || typeof response.body.getReader !== "function") {
    fail(code, `${description} does not expose a readable byte stream.`, { ambiguous });
  }

  let reader;
  try {
    reader = response.body.getReader();
  } catch {
    fail(code, `${description} byte stream could not be opened.`, { ambiguous });
  }

  const chunks = [];
  let totalBytes = 0;
  while (true) {
    let chunk;
    try {
      chunk = await reader.read();
    } catch {
      await cancelResponseReader(reader);
      fail(code, `${description} byte stream could not be read.`, { ambiguous });
    }
    if (!chunk || typeof chunk.done !== "boolean") {
      await cancelResponseReader(reader);
      fail(code, `${description} returned an invalid byte stream result.`, { ambiguous });
    }
    if (chunk.done) {
      break;
    }
    if (!(chunk.value instanceof Uint8Array)) {
      await cancelResponseReader(reader);
      fail(code, `${description} returned a non-byte stream chunk.`, { ambiguous });
    }
    if (chunk.value.byteLength > maxBytes - totalBytes) {
      await cancelResponseReader(reader);
      fail(code, `${description} exceeds the permitted byte limit.`, { ambiguous });
    }
    chunks.push(Buffer.from(chunk.value));
    totalBytes += chunk.value.byteLength;
  }
  try {
    reader.releaseLock();
  } catch {
    // Reading completed; releasing a synthetic or already-released reader is best effort.
  }
  return Buffer.concat(chunks, totalBytes);
}

async function readResponseJson(response) {
  const buffer = await readBoundedResponseBytes(response, {
    maxBytes: MAX_JSON_RESPONSE_BYTES,
    description: "Discord response",
  });
  if (buffer.length === 0) {
    return null;
  }
  try {
    return JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(buffer));
  } catch {
    fail("DISCORD_RESPONSE", "Discord returned malformed JSON.");
  }
}

async function discordRequest({
  fetchImpl,
  token,
  deadline,
  method = "GET",
  url,
  body,
  isForm = false,
  sleepImpl = delay,
  nowImpl = () => performance.now(),
}) {
  const retryableGet = method === "GET";
  const maxAttempts = retryableGet ? 3 : 2;
  if (!Number.isFinite(deadline)) {
    fail("INVALID_DELIVERY", "Discord requests require one bounded operation deadline.");
  }
  for (let attempt = 0; attempt < maxAttempts; attempt += 1) {
    const remainingMilliseconds = deadline - nowImpl();
    if (!Number.isFinite(remainingMilliseconds) || remainingMilliseconds <= 0) {
      fail(
        method === "POST" ? "DISCORD_RATE_LIMIT" : "DISCORD_NETWORK",
        "Discord request retries exceeded the bounded delivery deadline.",
      );
    }
    const headers = {
      Accept: "application/json",
      Authorization: `Bot ${token}`,
      "User-Agent": USER_AGENT,
    };
    if (body !== undefined && !isForm) {
      headers["Content-Type"] = "application/json";
    }
    let response;
    try {
      response = await fetchImpl(url, {
        body: body === undefined || isForm ? body : JSON.stringify(body),
        headers,
        method,
        redirect: "error",
        signal: AbortSignal.timeout(Math.max(1, Math.min(20_000, Math.floor(remainingMilliseconds)))),
      });
    } catch {
      if (retryableGet && attempt < 2) {
        await sleepImpl(250 * 4 ** attempt);
        continue;
      }
      fail("DISCORD_NETWORK", "Discord could not be reached safely.", { ambiguous: method === "POST" });
    }

    let data;
    try {
      data = await readResponseJson(response);
    } catch (error) {
      if (method === "POST") {
        fail("DISCORD_RESPONSE", "Discord returned an unreadable POST response.", { ambiguous: true });
      }
      throw error;
    }
    if (response.ok) {
      if (method === "POST" && data === null) {
        fail("DISCORD_RESPONSE", "Discord returned an empty POST response.", { ambiguous: true });
      }
      return data;
    }
    if (response.status === 429 && attempt < maxAttempts - 1) {
      let milliseconds = 250 * 4 ** attempt;
      const headerSeconds = Number(response.headers.get("retry-after"));
      const bodySeconds = Number(data?.retry_after);
      const seconds = Math.max(
        Number.isFinite(headerSeconds) && headerSeconds >= 0 ? headerSeconds : 0,
        Number.isFinite(bodySeconds) && bodySeconds >= 0 ? bodySeconds : 0,
      );
      milliseconds = Math.max(milliseconds, Math.ceil(seconds * 1000));
      if (nowImpl() + milliseconds >= deadline) {
        fail("DISCORD_RATE_LIMIT", "Discord's full Retry-After exceeds the bounded delivery deadline.");
      }
      await sleepImpl(milliseconds);
      continue;
    }
    if (retryableGet && attempt < maxAttempts - 1 && response.status >= 500) {
      const milliseconds = 250 * 4 ** attempt;
      if (nowImpl() + milliseconds >= deadline) {
        fail("DISCORD_NETWORK", "Discord read retries exceeded the bounded delivery deadline.");
      }
      await sleepImpl(milliseconds);
      continue;
    }
    fail(
      `DISCORD_HTTP_${response.status}`,
      `Discord rejected the ${method} request with HTTP ${response.status}.`,
      { ambiguous: method === "POST" && (response.status >= 500 || response.status === 408) },
    );
  }
  fail("DISCORD_NETWORK", "Discord could not be reached safely.");
}

async function fetchAttachment({ fetchImpl, url, expectedBytes, expectedDigest, deadline, nowImpl }) {
  if (!Number.isSafeInteger(expectedBytes) || expectedBytes <= 0 || expectedBytes > MAX_IMAGE_BYTES) {
    fail("DISCORD_VERIFICATION", "Reviewed attachment metadata has an invalid byte size.", {
      ambiguous: true,
    });
  }
  let parsed;
  try {
    parsed = new URL(url);
  } catch {
    fail("DISCORD_VERIFICATION", "Discord returned an invalid attachment URL.", { ambiguous: true });
  }
  if (
    parsed.protocol !== "https:" ||
    parsed.hostname !== "cdn.discordapp.com" ||
    !parsed.pathname.startsWith("/attachments/")
  ) {
    fail("DISCORD_VERIFICATION", "Discord returned an untrusted attachment URL.", { ambiguous: true });
  }
  let response;
  const remainingMilliseconds = deadline - nowImpl();
  if (!Number.isFinite(remainingMilliseconds) || remainingMilliseconds <= 0) {
    fail("DISCORD_VERIFICATION", "Discord attachment verification exceeded the bounded operation deadline.", {
      ambiguous: true,
    });
  }
  try {
    response = await fetchImpl(parsed.toString(), {
      headers: { "User-Agent": USER_AGENT },
      method: "GET",
      redirect: "error",
      signal: AbortSignal.timeout(Math.max(1, Math.min(20_000, Math.floor(remainingMilliseconds)))),
    });
  } catch {
    fail("DISCORD_VERIFICATION", "Discord attachment verification could not complete.", { ambiguous: true });
  }
  if (!response.ok) {
    fail("DISCORD_VERIFICATION", "Discord attachment verification was rejected.", { ambiguous: true });
  }
  const bytes = await readBoundedResponseBytes(response, {
    maxBytes: Math.min(expectedBytes, MAX_IMAGE_BYTES),
    code: "DISCORD_VERIFICATION",
    description: "Discord attachment",
    ambiguous: true,
  });
  if (bytes.length !== expectedBytes || sha256(bytes) !== expectedDigest) {
    fail("DISCORD_VERIFICATION", "Discord attachment bytes do not match the reviewed image.", { ambiguous: true });
  }
}

function markerFromMessage(message) {
  if (!Array.isArray(message?.embeds)) {
    return null;
  }
  const markers = message.embeds
    .map((embed) => embed?.footer?.text)
    .filter((text) => typeof text === "string" && MARKER_PATTERN.test(text));
  if (markers.length > 1) {
    fail("DISCORD_CONFLICT", "A Discord message contains multiple release markers.");
  }
  return markers[0] ?? null;
}

function compareVersions(left, right) {
  const a = left.split(".").map(Number);
  const b = right.split(".").map(Number);
  for (let index = 0; index < 3; index += 1) {
    if (a[index] !== b[index]) {
      return a[index] - b[index];
    }
  }
  return 0;
}

function validateEmbed(actual, expected, displayedAttachment) {
  if (
    actual?.thumbnail !== undefined ||
    actual?.author !== undefined ||
    actual?.video !== undefined ||
    actual?.provider !== undefined ||
    (actual?.type !== undefined && actual.type !== "rich")
  ) {
    fail("DISCORD_VERIFICATION", "Discord returned an unexpected display-bearing embed field.", {
      ambiguous: true,
    });
  }
  if (actual?.footer && Object.keys(actual.footer).some((key) => key !== "text")) {
    fail("DISCORD_VERIFICATION", "Discord returned an unexpected footer image field.", { ambiguous: true });
  }
  if (Object.hasOwn(actual ?? {}, "timestamp")) {
    fail("DISCORD_VERIFICATION", "Discord returned an unexpected embed timestamp.", { ambiguous: true });
  }
  for (const key of ["title", "description", "url", "color"]) {
    if ((actual?.[key] ?? undefined) !== (expected?.[key] ?? undefined)) {
      fail("DISCORD_VERIFICATION", `Discord changed the announcement embed ${key}.`, { ambiguous: true });
    }
  }
  const actualFields = actual?.fields ?? [];
  const expectedFields = expected?.fields ?? [];
  if (!Array.isArray(actualFields) || actualFields.length !== expectedFields.length) {
    fail("DISCORD_VERIFICATION", "Discord changed the announcement embed fields.", { ambiguous: true });
  }
  for (let index = 0; index < expectedFields.length; index += 1) {
    const actualField = actualFields[index];
    const expectedField = expectedFields[index];
    const actualInline = Object.hasOwn(actualField ?? {}, "inline") ? actualField.inline : false;
    const expectedInline = Object.hasOwn(expectedField ?? {}, "inline") ? expectedField.inline : false;
    if (
      actualField?.name !== expectedField.name ||
      actualField?.value !== expectedField.value ||
      typeof actualInline !== "boolean" ||
      typeof expectedInline !== "boolean" ||
      actualInline !== expectedInline
    ) {
      fail("DISCORD_VERIFICATION", "Discord changed an announcement embed field.", { ambiguous: true });
    }
  }
  if ((actual?.footer?.text ?? undefined) !== (expected?.footer?.text ?? undefined)) {
    fail("DISCORD_VERIFICATION", "Discord changed the release idempotency marker.", { ambiguous: true });
  }
  if (Boolean(actual?.image) !== Boolean(expected?.image)) {
    fail("DISCORD_VERIFICATION", "Discord changed the announcement image layout.", { ambiguous: true });
  }
  if (expected?.image && actual.image.url !== displayedAttachment?.url) {
    fail("DISCORD_VERIFICATION", "Discord displayed an image other than the reviewed attachment.", {
      ambiguous: true,
    });
  }
}

function isAbsentOrEmptyArray(value) {
  return value === undefined || (Array.isArray(value) && value.length === 0);
}

function reviewedAttachmentDescription(bundle) {
  return `${PRODUCT} ${bundle.artifact.release.version} reviewed release image`;
}

async function verifyDiscordMessage({
  message,
  expectedMessageId,
  bundle,
  botId,
  channelId,
  roleId,
  fetchImpl,
  verifyAttachments,
  deadline,
  nowImpl,
}) {
  const expected = bundle.artifact.announcement;
  if (
    !isPlainObject(message) ||
    !SNOWFLAKE_PATTERN.test(expectedMessageId ?? "") ||
    message.id !== expectedMessageId ||
    message.channel_id !== channelId ||
    message.author?.id !== botId ||
    message.author?.bot !== true ||
    message.webhook_id !== undefined ||
    message.content !== `<@&${roleId}>` ||
    message.type !== 0 ||
    message.tts !== false ||
    message.edited_timestamp !== null ||
    message.pinned !== false ||
    message.mention_everyone !== false ||
    !Array.isArray(message.mentions) ||
    message.mentions.length !== 0 ||
    !Array.isArray(message.mention_roles) ||
    message.mention_roles.length !== 1 ||
    message.mention_roles[0] !== roleId ||
    (message.flags !== undefined && message.flags !== 0) ||
    !isAbsentOrEmptyArray(message.components) ||
    !isAbsentOrEmptyArray(message.sticker_items) ||
    !isAbsentOrEmptyArray(message.stickers) ||
    [
      "activity",
      "application",
      "application_id",
      "call",
      "interaction",
      "interaction_metadata",
      "mention_channels",
      "message_reference",
      "message_snapshots",
      "poll",
      "position",
      "referenced_message",
      "resolved",
      "role_subscription_data",
      "shared_client_theme",
      "thread",
    ].some((key) => Object.hasOwn(message, key))
  ) {
    fail("DISCORD_VERIFICATION", "Discord message identity or presentation verification failed.", { ambiguous: true });
  }
  if (message.nonce !== undefined && String(message.nonce) !== expected.nonce) {
    fail("DISCORD_VERIFICATION", "Discord message nonce verification failed.", { ambiguous: true });
  }
  if (!Array.isArray(message.attachments) || message.attachments.length !== expected.attachments.length) {
    fail("DISCORD_VERIFICATION", "Discord message attachment count verification failed.", { ambiguous: true });
  }
  if (!Array.isArray(message.embeds) || message.embeds.length !== expected.message.embeds.length) {
    fail("DISCORD_VERIFICATION", "Discord message embed count verification failed.", { ambiguous: true });
  }
  for (let index = 0; index < expected.message.embeds.length; index += 1) {
    validateEmbed(message.embeds[index], expected.message.embeds[index], message.attachments[index]);
  }
  const expectedAttachmentDescription = reviewedAttachmentDescription(bundle);
  for (let index = 0; index < expected.attachments.length; index += 1) {
    const actual = message.attachments[index];
    const attachment = expected.attachments[index];
    const attachmentFlags = actual?.flags;
    if (
      actual?.filename !== attachment.fileName ||
      actual?.size !== attachment.bytes ||
      actual?.description !== expectedAttachmentDescription ||
      Object.hasOwn(actual ?? {}, "title") ||
      (actual?.ephemeral !== undefined && actual.ephemeral !== false) ||
      (attachmentFlags !== undefined &&
        ((attachmentFlags & ATTACHMENT_FLAG_IS_SPOILER) !== 0 ||
          (attachmentFlags !== 0 && attachmentFlags !== ATTACHMENT_FLAG_IS_ANIMATED)))
    ) {
      fail("DISCORD_VERIFICATION", "Discord message attachment metadata verification failed.", { ambiguous: true });
    }
    if (verifyAttachments) {
      await fetchAttachment({
        fetchImpl,
        url: actual.url,
        expectedBytes: attachment.bytes,
        expectedDigest: attachment.sha256,
        deadline,
        nowImpl,
      });
    }
  }
  return message;
}

async function scanHistory({ fetchImpl, sleepImpl, nowImpl, deadline, token, channel, botId, roleId, bundle }) {
  let before;
  const exact = [];
  for (let page = 0; page < MAX_HISTORY_PAGES; page += 1) {
    const query = before ? `?limit=100&before=${before}` : "?limit=100";
    const messages = await discordRequest({
      fetchImpl,
      sleepImpl,
      nowImpl,
      deadline,
      token,
      url: `${DISCORD_API}/channels/${channel.id}/messages${query}`,
    });
    if (!Array.isArray(messages) || messages.length > 100) {
      fail("DISCORD_RESPONSE", "Discord returned an invalid channel history response.");
    }
    if (page === 0 && messages.length === 0 && channel.last_message_id !== null) {
      fail("DISCORD_HISTORY", "Discord channel history could not be proven readable.");
    }
    for (const message of messages) {
      if (message?.author?.id !== botId) {
        continue;
      }
      const marker = markerFromMessage(message);
      if (!marker) {
        continue;
      }
      const match = MARKER_PATTERN.exec(marker);
      const tag = match[1];
      const version = tag.slice(1);
      if (tag === bundle.artifact.release.tag && marker !== bundle.artifact.announcement.marker) {
        fail("DISCORD_CONFLICT", "Bota already posted this release tag from different immutable inputs.");
      }
      if (compareVersions(version, bundle.artifact.release.version) > 0) {
        fail("DISCORD_CONFLICT", "A newer SessionDock release is already present in the configured channel.");
      }
      if (marker === bundle.artifact.announcement.marker) {
        exact.push(message);
      }
    }
    if (messages.length < 100) {
      if (exact.length > 1) {
        fail("DISCORD_CONFLICT", "Bota has more than one matching release announcement.");
      }
      if (exact.length === 1) {
        await verifyDiscordMessage({
          message: exact[0],
          expectedMessageId: exact[0].id,
          bundle,
          botId,
          channelId: channel.id,
          roleId,
          fetchImpl,
          verifyAttachments: true,
          deadline,
          nowImpl,
        });
      }
      return exact[0] ?? null;
    }
    const lastId = messages.at(-1)?.id;
    if (!SNOWFLAKE_PATTERN.test(lastId ?? "") || lastId === before) {
      fail("DISCORD_HISTORY", "Discord channel history pagination is invalid.");
    }
    before = lastId;
  }
  fail("DISCORD_HISTORY", "Discord channel history exceeded the safe reconciliation window.");
}

function parsePermissionBits(value, label) {
  if (typeof value !== "string" || !/^(?:0|[1-9]\d*)$/u.test(value)) {
    fail("DISCORD_PERMISSIONS", `${label} has invalid Discord permission bits.`);
  }
  try {
    return BigInt(value);
  } catch {
    fail("DISCORD_PERMISSIONS", `${label} has unreadable Discord permission bits.`);
  }
}

function applyPermissionOverwrite(permissions, overwrite, label) {
  const allow = parsePermissionBits(overwrite.allow, `${label} allow`);
  const deny = parsePermissionBits(overwrite.deny, `${label} deny`);
  return (permissions & ~deny) | allow;
}

function assertBotChannelPermissions({ bundle, channel, roles, member, botId }) {
  if (!Array.isArray(roles) || roles.length === 0 || roles.length > 1_000) {
    fail("DISCORD_PERMISSIONS", "Discord returned an invalid guild role list.");
  }
  const roleById = new Map();
  for (const role of roles) {
    if (!SNOWFLAKE_PATTERN.test(role?.id ?? "") || roleById.has(role.id)) {
      fail("DISCORD_PERMISSIONS", "Discord returned an invalid or duplicate guild role.");
    }
    parsePermissionBits(role.permissions, `Role ${role.id}`);
    roleById.set(role.id, role);
  }
  const everyone = roleById.get(channel.guild_id);
  if (!everyone) {
    fail("DISCORD_PERMISSIONS", "Discord did not return the guild's @everyone permission role.");
  }
  if (
    member?.user?.id !== botId ||
    !Array.isArray(member.roles) ||
    member.roles.length > roles.length ||
    member.roles.some(
      (roleId) => roleId === channel.guild_id || !SNOWFLAKE_PATTERN.test(roleId) || !roleById.has(roleId),
    )
  ) {
    fail("DISCORD_PERMISSIONS", "Discord returned invalid Bota guild membership data.");
  }

  let permissions = parsePermissionBits(everyone.permissions, "@everyone role");
  for (const roleId of new Set(member.roles)) {
    permissions |= parsePermissionBits(roleById.get(roleId).permissions, `Role ${roleId}`);
  }
  if ((permissions & PERMISSION_ADMINISTRATOR) !== 0n) {
    fail("DISCORD_PERMISSIONS", "Bota must not have the Administrator permission.");
  }

  if (!Array.isArray(channel.permission_overwrites) || channel.permission_overwrites.length > 1_000) {
    fail("DISCORD_PERMISSIONS", "Discord returned invalid channel permission overwrites.");
  }
  const overwriteByKey = new Map();
  for (const overwrite of channel.permission_overwrites) {
    if (!SNOWFLAKE_PATTERN.test(overwrite?.id ?? "") || ![0, 1].includes(overwrite?.type)) {
      fail("DISCORD_PERMISSIONS", "Discord returned an invalid channel permission overwrite.");
    }
    parsePermissionBits(overwrite.allow, `Overwrite ${overwrite.id} allow`);
    parsePermissionBits(overwrite.deny, `Overwrite ${overwrite.id} deny`);
    const key = `${overwrite.type}:${overwrite.id}`;
    if (overwriteByKey.has(key)) {
      fail("DISCORD_PERMISSIONS", "Discord returned duplicate channel permission overwrites.");
    }
    overwriteByKey.set(key, overwrite);
  }

  const everyoneOverwrite = overwriteByKey.get(`0:${channel.guild_id}`);
  if (everyoneOverwrite) {
    permissions = applyPermissionOverwrite(permissions, everyoneOverwrite, "@everyone channel overwrite");
  }
  let roleAllow = 0n;
  let roleDeny = 0n;
  for (const roleId of new Set(member.roles)) {
    const overwrite = overwriteByKey.get(`0:${roleId}`);
    if (overwrite) {
      roleAllow |= parsePermissionBits(overwrite.allow, `Role ${roleId} channel overwrite allow`);
      roleDeny |= parsePermissionBits(overwrite.deny, `Role ${roleId} channel overwrite deny`);
    }
  }
  permissions = (permissions & ~roleDeny) | roleAllow;
  const memberOverwrite = overwriteByKey.get(`1:${botId}`);
  if (memberOverwrite) {
    permissions = applyPermissionOverwrite(permissions, memberOverwrite, "Bota member channel overwrite");
  }

  const required =
    PERMISSION_VIEW_CHANNEL |
    PERMISSION_SEND_MESSAGES |
    PERMISSION_EMBED_LINKS |
    PERMISSION_READ_MESSAGE_HISTORY |
    (bundle.images.length > 0 ? PERMISSION_ATTACH_FILES : 0n);
  if ((permissions & required) !== required) {
    fail(
      "DISCORD_PERMISSIONS",
      "Bota lacks View Channel, Read Message History, Send Messages, Embed Links, or required Attach Files access.",
    );
  }
  const prohibited =
    PERMISSION_ADMINISTRATOR |
    PERMISSION_MANAGE_GUILD |
    PERMISSION_MANAGE_MESSAGES |
    PERMISSION_MENTION_EVERYONE;
  if ((permissions & prohibited) !== 0n) {
    fail(
      "DISCORD_PERMISSIONS",
      "Bota has a prohibited Administrator, Manage Server, Manage Messages, or Mention Everyone permission.",
    );
  }
}

async function inspectDeliveryTarget({
  bundle,
  env,
  fetchImpl,
  sleepImpl,
  nowImpl,
  deadline,
  rejectExisting,
}) {
  const config = loadDeliveryConfig(env);
  const user = await discordRequest({
    fetchImpl,
    sleepImpl,
    nowImpl,
    deadline,
    token: config.token,
    url: `${DISCORD_API}/users/@me`,
  });
  if (user?.id !== config.botId || user?.bot !== true) {
    fail("DISCORD_IDENTITY", "The configured Discord credential does not belong to the expected Bota bot identity.");
  }
  const channel = await discordRequest({
    fetchImpl,
    sleepImpl,
    nowImpl,
    deadline,
    token: config.token,
    url: `${DISCORD_API}/channels/${config.channelId}`,
  });
  if (
    channel?.id !== config.channelId ||
    !SNOWFLAKE_PATTERN.test(channel?.guild_id ?? "") ||
    ![0, 5].includes(channel?.type) ||
    (channel.last_message_id !== null && !SNOWFLAKE_PATTERN.test(channel.last_message_id ?? ""))
  ) {
    fail("DISCORD_CHANNEL", "The configured Discord channel is not a guild text or announcement channel.");
  }
  if (config.roleId === channel.guild_id) {
    fail("DISCORD_ROLE", "The configured Discord role must not be @everyone.");
  }
  const roles = await discordRequest({
    fetchImpl,
    sleepImpl,
    nowImpl,
    deadline,
    token: config.token,
    url: `${DISCORD_API}/guilds/${channel.guild_id}/roles`,
  });
  const role = Array.isArray(roles) ? roles.find((candidate) => candidate?.id === config.roleId) : null;
  if (!role || role.managed !== false || role.mentionable !== true) {
    fail("DISCORD_ROLE", "The configured SessionDock role must exist, be unmanaged, and be mentionable.");
  }
  const member = await discordRequest({
    fetchImpl,
    sleepImpl,
    nowImpl,
    deadline,
    token: config.token,
    url: `${DISCORD_API}/guilds/${channel.guild_id}/members/${config.botId}`,
  });
  assertBotChannelPermissions({ bundle, channel, roles, member, botId: config.botId });

  const existing = await scanHistory({
    fetchImpl,
    sleepImpl,
    nowImpl,
    deadline,
    token: config.token,
    channel,
    botId: config.botId,
    roleId: config.roleId,
    bundle,
  });
  if (rejectExisting && existing) {
    fail("DISCORD_EARLY_DISCLOSURE", "The release announcement already exists before GitHub publication.");
  }
  return { channel, config, existing };
}

export async function preflightAnnouncement({
  bundle,
  env = process.env,
  fetchImpl = globalThis.fetch,
  sleepImpl = delay,
  nowImpl = () => performance.now(),
}) {
  if (!bundle?.artifact || typeof fetchImpl !== "function") {
    fail("INVALID_DELIVERY", "A verified announcement bundle and fetch implementation are required.");
  }
  const deadline = nowImpl() + MAX_DISCORD_OPERATION_MILLISECONDS;
  const { channel, config } = await inspectDeliveryTarget({
    bundle,
    env,
    fetchImpl,
    sleepImpl,
    nowImpl,
    deadline,
    rejectExisting: true,
  });
  return {
    botId: config.botId,
    channelId: channel.id,
    guildId: channel.guild_id,
    roleId: config.roleId,
    status: "ready",
    verifiedDelivery: false,
  };
}

function buildDiscordPayload(bundle, roleId) {
  const announcement = bundle.artifact.announcement;
  const payload = {
    allowed_mentions: {
      replied_user: false,
      roles: [roleId],
      users: [],
    },
    content: `<@&${roleId}>`,
    embeds: announcement.message.embeds,
    enforce_nonce: true,
    nonce: announcement.nonce,
  };
  if (announcement.attachments.length) {
    payload.attachments = announcement.attachments.map((attachment) => ({
      description: reviewedAttachmentDescription(bundle),
      filename: attachment.fileName,
      id: attachment.id,
    }));
  }
  return payload;
}

function buildMultipartPayload(bundle, payload) {
  const form = new FormData();
  form.append("payload_json", JSON.stringify(payload));
  for (const image of bundle.images) {
    form.append(`files[${image.id}]`, new Blob([image.buffer], { type: image.mediaType }), image.fileName);
  }
  return form;
}

export async function deliverAnnouncement({
  bundle,
  env = process.env,
  fetchImpl = globalThis.fetch,
  sleepImpl = delay,
  nowImpl = () => performance.now(),
}) {
  if (!bundle?.artifact || typeof fetchImpl !== "function") {
    fail("INVALID_DELIVERY", "A verified announcement bundle and fetch implementation are required.");
  }
  const deadline = nowImpl() + MAX_DISCORD_OPERATION_MILLISECONDS;
  const { channel, config, existing } = await inspectDeliveryTarget({
    bundle,
    env,
    fetchImpl,
    sleepImpl,
    nowImpl,
    deadline,
    rejectExisting: false,
  });
  if (existing) {
    try {
      const current = await discordRequest({
        fetchImpl,
        sleepImpl,
        nowImpl,
        deadline,
        token: config.token,
        url: `${DISCORD_API}/channels/${channel.id}/messages/${existing.id}`,
      });
      await verifyDiscordMessage({
        message: current,
        expectedMessageId: existing.id,
        bundle,
        botId: config.botId,
        channelId: channel.id,
        roleId: config.roleId,
        fetchImpl,
        verifyAttachments: true,
        deadline,
        nowImpl,
      });
    } catch {
      fail("DELIVERY_AMBIGUOUS", "An existing Discord announcement was found, but final verification failed.", {
        ambiguous: true,
      });
    }
    return {
      botId: config.botId,
      channelId: channel.id,
      guildId: channel.guild_id,
      messageId: existing.id,
      roleId: config.roleId,
      status: "already-posted",
    };
  }

  const payload = buildDiscordPayload(bundle, config.roleId);
  const hasImages = bundle.images.length > 0;
  let posted;
  try {
    posted = await discordRequest({
      fetchImpl,
      sleepImpl,
      nowImpl,
      deadline,
      token: config.token,
      method: "POST",
      url: `${DISCORD_API}/channels/${channel.id}/messages`,
      body: hasImages ? buildMultipartPayload(bundle, payload) : payload,
      isForm: hasImages,
    });
  } catch (error) {
    if (!(error instanceof ReleaseAutomationError) || !error.ambiguous) {
      throw error;
    }
    let reconciled;
    try {
      reconciled = await scanHistory({
        fetchImpl,
        sleepImpl,
        nowImpl,
        deadline,
        token: config.token,
        channel,
        botId: config.botId,
        roleId: config.roleId,
        bundle,
      });
    } catch {
      fail("DELIVERY_AMBIGUOUS", "Discord delivery may have completed, but reconciliation failed.", {
        ambiguous: true,
      });
    }
    if (!reconciled) {
      fail("DELIVERY_AMBIGUOUS", "Discord delivery may have completed; rerun only after inspecting the release channel.", {
        ambiguous: true,
      });
    }
    posted = reconciled;
  }

  try {
    await verifyDiscordMessage({
      message: posted,
      expectedMessageId: posted?.id,
      bundle,
      botId: config.botId,
      channelId: channel.id,
      roleId: config.roleId,
      fetchImpl,
      verifyAttachments: false,
      deadline,
      nowImpl,
    });
    const current = await discordRequest({
      fetchImpl,
      sleepImpl,
      nowImpl,
      deadline,
      token: config.token,
      url: `${DISCORD_API}/channels/${channel.id}/messages/${posted.id}`,
    });
    await verifyDiscordMessage({
      message: current,
      expectedMessageId: posted.id,
      bundle,
      botId: config.botId,
      channelId: channel.id,
      roleId: config.roleId,
      fetchImpl,
      verifyAttachments: true,
      deadline,
      nowImpl,
    });
  } catch {
    fail("DELIVERY_AMBIGUOUS", "Discord accepted a message, but read-back verification failed.", {
      ambiguous: true,
    });
  }

  return {
    botId: config.botId,
    channelId: channel.id,
    guildId: channel.guild_id,
    messageId: posted.id,
    roleId: config.roleId,
    status: "posted",
  };
}

export function createDeliveryReceipt({ bundle, result, error }) {
  if (!bundle?.artifact || Boolean(result) === Boolean(error)) {
    fail("INVALID_RECEIPT", "A receipt requires exactly one delivery result or delivery error.");
  }
  const receipt = {
    announcementId: bundle.artifact.announcement.id,
    artifactSha256: bundle.artifactDigest,
    release: {
      sourceCommit: bundle.artifact.release.sourceCommit,
      tag: bundle.artifact.release.tag,
    },
    schemaVersion: 1,
  };
  if (result) {
    return {
      ...receipt,
      discord: {
        botId: result.botId,
        channelId: result.channelId,
        guildId: result.guildId,
        messageId: result.messageId,
        messageUrl: `https://discord.com/channels/${result.guildId}/${result.channelId}/${result.messageId}`,
        roleId: result.roleId,
      },
      status: "confirmed",
      verified: true,
    };
  }
  return {
    ...receipt,
    errorCode: error instanceof ReleaseAutomationError ? error.code : "UNEXPECTED_FAILURE",
    status: error instanceof ReleaseAutomationError && error.ambiguous ? "ambiguous" : "not-sent",
    verified: false,
  };
}

function parseArguments(argv, allowed) {
  const result = {};
  for (let index = 0; index < argv.length; index += 2) {
    const name = argv[index];
    const value = argv[index + 1];
    if (!allowed.includes(name) || value === undefined || value.startsWith("--") || result[name] !== undefined) {
      fail("INVALID_ARGUMENT", `Invalid or duplicate command argument: ${name ?? "<missing>"}`);
    }
    result[name] = value;
  }
  return result;
}

function requireArguments(args, names) {
  for (const name of names) {
    if (!args[name]) {
      fail("INVALID_ARGUMENT", `Required command argument is missing: ${name}`);
    }
  }
}

function receiptBase(bundle) {
  return {
    announcementId: bundle.artifact.announcement.id,
    artifactSha256: bundle.artifactDigest,
    release: {
      sourceCommit: bundle.artifact.release.sourceCommit,
      tag: bundle.artifact.release.tag,
    },
    schemaVersion: 1,
  };
}

function reserveReceipt(receiptPath, bundle) {
  const relative = normalizeRelativePath(receiptPath, "Receipt path");
  const root = realpathSync(process.cwd());
  const resolved = path.resolve(root, ...relative.split("/"));
  const parent = path.dirname(resolved);
  let descriptor;
  try {
    mkdirSync(parent, { recursive: true });
    const parentReal = realpathSync(parent);
    if (parentReal !== root && !parentReal.startsWith(`${root}${path.sep}`)) {
      throw new Error("Receipt parent escapes the working directory.");
    }
    descriptor = openSync(resolved, "wx", 0o600);
    writeFileSync(
      descriptor,
      prettyJson({
        ...receiptBase(bundle),
        status: "reserved",
        verified: false,
      }),
    );
    fsyncSync(descriptor);
    closeSync(descriptor);
    descriptor = undefined;
  } catch {
    if (descriptor !== undefined) {
      closeSync(descriptor);
    }
    fail("RECEIPT_RESERVATION", "The delivery receipt could not be reserved before network access.");
  }
  return {
    close() {},
    finalize(value) {
      const temporary = `${resolved}.finalizing`;
      let temporaryDescriptor;
      let temporaryCreated = false;
      try {
        const bytes = Buffer.from(prettyJson(value), "utf8");
        temporaryDescriptor = openSync(temporary, "wx", 0o600);
        temporaryCreated = true;
        writeFileSync(temporaryDescriptor, bytes);
        fsyncSync(temporaryDescriptor);
        closeSync(temporaryDescriptor);
        temporaryDescriptor = undefined;
        renameSync(temporary, resolved);
      } catch {
        if (temporaryDescriptor !== undefined) {
          closeSync(temporaryDescriptor);
        }
        if (temporaryCreated && existsSync(temporary)) {
          rmSync(temporary, { force: true });
        }
        fail("RECEIPT_FINALIZATION", "The delivery receipt could not be finalized.", {
          ambiguous: true,
        });
      }
    },
  };
}

async function runCli(argv) {
  const [command, ...rest] = argv;
  if (command === "generate") {
    const args = parseArguments(rest, ["--version", "--source-commit", "--notes", "--images", "--output"]);
    requireArguments(args, ["--version", "--source-commit", "--notes", "--output"]);
    const result = generateAnnouncement({
      version: args["--version"],
      sourceCommit: args["--source-commit"],
      notesPath: args["--notes"],
      imagesPath: args["--images"],
      outputPath: args["--output"],
    });
    console.log(`Generated Discord announcement ${result.artifact.announcement.id}.`);
    return;
  }
  if (command === "verify" || command === "preflight" || command === "post") {
    const allowed = ["--artifact-dir", "--expected-tag", "--expected-ref", "--expected-commit"];
    if (command === "post") {
      allowed.push("--receipt");
    }
    const args = parseArguments(rest, allowed);
    const required = ["--artifact-dir", "--expected-tag", "--expected-ref", "--expected-commit"];
    if (command === "post") {
      required.push("--receipt");
    }
    requireArguments(args, required);
    const artifactDirectory = resolveInside(process.cwd(), args["--artifact-dir"], "Artifact directory");
    const bundle = readAnnouncementBundle({
      artifactDirectory,
      expectedTag: args["--expected-tag"],
      expectedRef: args["--expected-ref"],
      expectedCommit: args["--expected-commit"],
    });
    if (command === "verify") {
      console.log(`Verified Discord announcement ${bundle.artifact.announcement.id}.`);
      return;
    }
    if (command === "preflight") {
      await preflightAnnouncement({ bundle });
      console.log(`Discord announcement preflight is ready for ${bundle.artifact.release.tag}.`);
      return;
    }

    const receipt = reserveReceipt(args["--receipt"], bundle);
    let result;
    let deliveryError;
    try {
      result = await deliverAnnouncement({ bundle });
    } catch (error) {
      deliveryError = error;
    }
    if (deliveryError) {
      try {
        receipt.finalize(createDeliveryReceipt({ bundle, error: deliveryError }));
      } catch {
        // Preserve the delivery classification; the reserved receipt still proves an attempt began.
      } finally {
        receipt.close();
      }
      throw deliveryError;
    }
    try {
      receipt.finalize(createDeliveryReceipt({ bundle, result }));
    } finally {
      receipt.close();
    }
    console.log(`Discord announcement ${result.status}; message ${result.messageId} verified.`);
    return;
  }
  fail("INVALID_ARGUMENT", "Command must be 'generate', 'verify', 'preflight', or 'post'.");
}

const isMain = process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1]);
if (isMain) {
  runCli(process.argv.slice(2)).catch((error) => {
    const safeError =
      error instanceof ReleaseAutomationError
        ? error
        : new ReleaseAutomationError("UNEXPECTED_FAILURE", "Release automation failed unexpectedly.");
    console.error(`Release automation failed [${safeError.code}]: ${safeError.message}`);
    process.exitCode = 1;
  });
}
