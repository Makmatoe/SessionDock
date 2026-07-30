import {
  Client,
  Events,
  GatewayIntentBits,
  MessageFlags,
} from "discord.js";
import { loadConfig } from "./config.js";
import { PendingReleaseStore } from "./pending-releases.js";
import {
  handleReleaseCommand,
  handleReleaseModal,
  RELEASE_COMMAND_NAME,
  RELEASE_MODAL_PREFIX,
} from "./release.js";

const config = loadConfig();
const pendingReleases = new PendingReleaseStore();
const client = new Client({ intents: [GatewayIntentBits.Guilds] });

const pruneTimer = setInterval(() => pendingReleases.prune(), 60_000);
pruneTimer.unref();

client.once(Events.ClientReady, (readyClient) => {
  console.log(`Release bot ready as ${readyClient.user.tag}.`);
});

client.on(Events.InteractionCreate, async (interaction) => {
  try {
    if (interaction.isChatInputCommand() && interaction.commandName === RELEASE_COMMAND_NAME) {
      await handleReleaseCommand(interaction, pendingReleases);
      return;
    }

    if (interaction.isModalSubmit() && interaction.customId.startsWith(RELEASE_MODAL_PREFIX)) {
      await handleReleaseModal(interaction, pendingReleases, config);
    }
  } catch (error) {
    console.error("Failed to handle Discord interaction:", error);

    const message = "The release could not be published due to an unexpected error. Check the bot logs and try again.";
    try {
      if (interaction.deferred || interaction.replied) {
        await interaction.editReply({ content: message });
      } else if (interaction.isRepliable()) {
        await interaction.reply({ content: message, flags: MessageFlags.Ephemeral });
      }
    } catch (replyError) {
      console.error("Failed to send the interaction error response:", replyError);
    }
  }
});

function shutdown(signal) {
  console.log(`${signal} received; disconnecting.`);
  clearInterval(pruneTimer);
  client.destroy();
}

process.once("SIGINT", () => shutdown("SIGINT"));
process.once("SIGTERM", () => shutdown("SIGTERM"));

await client.login(config.token);
