import assert from "node:assert/strict";
import test from "node:test";
import { PendingReleaseStore } from "../src/pending-releases.js";

test("a pending release can only be consumed once", () => {
  const store = new PendingReleaseStore();
  const draft = { title: "Example" };

  store.put("123", draft);

  assert.equal(store.take("123"), draft);
  assert.equal(store.take("123"), undefined);
});

test("expired release forms are rejected", () => {
  let now = 1_000;
  const store = new PendingReleaseStore({ ttlMs: 500, now: () => now });
  store.put("123", { title: "Example" });

  now = 1_501;

  assert.equal(store.take("123"), undefined);
});
