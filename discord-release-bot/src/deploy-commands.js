import path from "node:path";
import { pathToFileURL } from "node:url";
import { ApplicationCommandType, REST, Routes } from "discord.js";
import { loadConfig } from "./config.js";
import { createReleaseCommand, RELEASE_COMMAND_NAME } from "./release.js";

function releaseCommands(commands) {
  if (!Array.isArray(commands)) {
    throw new Error("Discord returned an invalid application-command list.");
  }

  return commands.filter(
    (command) =>
      command?.name === RELEASE_COMMAND_NAME &&
      command?.type === ApplicationCommandType.ChatInput,
  );
}

async function upsertReleaseCommand(rest, collectionRoute, commandRoute, command) {
  const existing = releaseCommands(await rest.get(collectionRoute))[0];
  if (existing) {
    await rest.patch(commandRoute(existing.id), { body: command });
    return "updated";
  }

  await rest.post(collectionRoute, { body: command });
  return "created";
}

async function removeGuildReleaseCommands(rest, clientId, guildId) {
  const collectionRoute = Routes.applicationGuildCommands(clientId, guildId);
  const staleCommands = releaseCommands(await rest.get(collectionRoute));
  for (const command of staleCommands) {
    await rest.delete(Routes.applicationGuildCommand(clientId, guildId, command.id));
  }

  return staleCommands.length;
}

export async function deployReleaseCommand({
  rest,
  clientId,
  guildId,
  promote = false,
  command = createReleaseCommand().toJSON(),
}) {
  if (guildId && !promote) {
    const action = await upsertReleaseCommand(
      rest,
      Routes.applicationGuildCommands(clientId, guildId),
      (commandId) => Routes.applicationGuildCommand(clientId, guildId, commandId),
      command,
    );
    return { action, scope: "guild", removedGuildOverrides: 0 };
  }

  const action = await upsertReleaseCommand(
    rest,
    Routes.applicationCommands(clientId),
    (commandId) => Routes.applicationCommand(clientId, commandId),
    command,
  );
  const removedGuildOverrides = guildId
    ? await removeGuildReleaseCommands(rest, clientId, guildId)
    : 0;

  return { action, scope: "global", removedGuildOverrides };
}

export function parseDeployArguments(argv) {
  const unknown = argv.filter((argument) => argument !== "--global");
  if (unknown.length > 0) {
    throw new Error(`Unknown deployment argument: ${unknown.join(", ")}`);
  }

  return { promote: argv.includes("--global") };
}

export async function main(argv = process.argv.slice(2)) {
  const { promote } = parseDeployArguments(argv);
  const config = loadConfig();
  const rest = new REST({ version: "10" }).setToken(config.token);
  const result = await deployReleaseCommand({
    rest,
    clientId: config.clientId,
    guildId: config.guildId,
    promote,
  });

  if (result.scope === "guild") {
    console.log(`${result.action === "created" ? "Registered" : "Updated"} /release in test server ${config.guildId}.`);
    return;
  }

  console.log(
    `${result.action === "created" ? "Registered" : "Updated"} /release globally. It may take a little while to appear in every server.`,
  );
  if (config.guildId) {
    console.log(
      result.removedGuildOverrides > 0
        ? `Removed ${result.removedGuildOverrides} stale /release override from test server ${config.guildId}.`
        : `No stale /release override existed in test server ${config.guildId}.`,
    );
  } else {
    console.warn(
      "No DISCORD_GUILD_ID was provided, so a stale test-server /release override could not be located or removed.",
    );
  }
}

const isMainModule =
  process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href;
if (isMainModule) {
  await main();
}
