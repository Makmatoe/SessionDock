import assert from "node:assert/strict";
import test from "node:test";
import { ApplicationCommandType, Routes } from "discord.js";
import {
  deployReleaseCommand,
  parseDeployArguments,
} from "../src/deploy-commands.js";

const CLIENT_ID = "123456789012345678";
const GUILD_ID = "987654321098765432";
const RELEASE_COMMAND = {
  name: "release",
  description: "Publish release notes",
  type: ApplicationCommandType.ChatInput,
};

class FakeRest {
  constructor(commandLists) {
    this.commandLists = commandLists;
    this.calls = [];
  }

  async get(route) {
    this.calls.push({ method: "get", route });
    return this.commandLists.get(route) ?? [];
  }

  async post(route, options) {
    this.calls.push({ method: "post", route, options });
  }

  async patch(route, options) {
    this.calls.push({ method: "patch", route, options });
  }

  async delete(route) {
    this.calls.push({ method: "delete", route });
  }
}

test("guild deployment updates only the existing release command", async () => {
  const collectionRoute = Routes.applicationGuildCommands(CLIENT_ID, GUILD_ID);
  const rest = new FakeRest(
    new Map([
      [
        collectionRoute,
        [
          { id: "111111111111111111", name: "unrelated", type: ApplicationCommandType.ChatInput },
          { id: "222222222222222222", name: "release", type: ApplicationCommandType.ChatInput },
        ],
      ],
    ]),
  );

  const result = await deployReleaseCommand({
    rest,
    clientId: CLIENT_ID,
    guildId: GUILD_ID,
    command: RELEASE_COMMAND,
  });

  assert.deepEqual(result, { action: "updated", scope: "guild", removedGuildOverrides: 0 });
  assert.deepEqual(rest.calls, [
    { method: "get", route: collectionRoute },
    {
      method: "patch",
      route: Routes.applicationGuildCommand(CLIENT_ID, GUILD_ID, "222222222222222222"),
      options: { body: RELEASE_COMMAND },
    },
  ]);
});

test("global promotion preserves unrelated commands and removes only the test-guild release override", async () => {
  const globalRoute = Routes.applicationCommands(CLIENT_ID);
  const guildRoute = Routes.applicationGuildCommands(CLIENT_ID, GUILD_ID);
  const rest = new FakeRest(
    new Map([
      [
        globalRoute,
        [{ id: "333333333333333333", name: "status", type: ApplicationCommandType.ChatInput }],
      ],
      [
        guildRoute,
        [
          { id: "444444444444444444", name: "release", type: ApplicationCommandType.ChatInput },
          { id: "555555555555555555", name: "moderate", type: ApplicationCommandType.ChatInput },
        ],
      ],
    ]),
  );

  const result = await deployReleaseCommand({
    rest,
    clientId: CLIENT_ID,
    guildId: GUILD_ID,
    promote: true,
    command: RELEASE_COMMAND,
  });

  assert.deepEqual(result, { action: "created", scope: "global", removedGuildOverrides: 1 });
  assert.deepEqual(rest.calls, [
    { method: "get", route: globalRoute },
    { method: "post", route: globalRoute, options: { body: RELEASE_COMMAND } },
    { method: "get", route: guildRoute },
    {
      method: "delete",
      route: Routes.applicationGuildCommand(CLIENT_ID, GUILD_ID, "444444444444444444"),
    },
  ]);
});

test("global deployment updates an existing release command without bulk overwrite", async () => {
  const globalRoute = Routes.applicationCommands(CLIENT_ID);
  const rest = new FakeRest(
    new Map([
      [
        globalRoute,
        [
          { id: "666666666666666666", name: "release", type: ApplicationCommandType.ChatInput },
          { id: "777777777777777777", name: "status", type: ApplicationCommandType.ChatInput },
        ],
      ],
    ]),
  );

  const result = await deployReleaseCommand({
    rest,
    clientId: CLIENT_ID,
    command: RELEASE_COMMAND,
  });

  assert.deepEqual(result, { action: "updated", scope: "global", removedGuildOverrides: 0 });
  assert.deepEqual(rest.calls, [
    { method: "get", route: globalRoute },
    {
      method: "patch",
      route: Routes.applicationCommand(CLIENT_ID, "666666666666666666"),
      options: { body: RELEASE_COMMAND },
    },
  ]);
});

test("deployment arguments require an explicit, known promotion flag", () => {
  assert.deepEqual(parseDeployArguments([]), { promote: false });
  assert.deepEqual(parseDeployArguments(["--global"]), { promote: true });
  assert.throws(() => parseDeployArguments(["--guild"]), /Unknown deployment argument/);
});
