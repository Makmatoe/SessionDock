import { PermissionFlagsBits, PermissionsBitField } from "discord.js";
import { loadConfig } from "./config.js";

const config = loadConfig({ requireToken: false });
const permissions = new PermissionsBitField([
  PermissionFlagsBits.ViewChannel,
  PermissionFlagsBits.ReadMessageHistory,
  PermissionFlagsBits.SendMessages,
  PermissionFlagsBits.EmbedLinks,
  PermissionFlagsBits.AttachFiles,
]);

const inviteUrl = new URL("https://discord.com/oauth2/authorize");
inviteUrl.searchParams.set("client_id", config.clientId);
inviteUrl.searchParams.set("scope", "bot applications.commands");
inviteUrl.searchParams.set("permissions", permissions.bitfield.toString());

console.log(inviteUrl.toString());
