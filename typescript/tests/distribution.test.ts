// distribution.test.ts
// Verifies the scoped CircleAI.Distribution port: app-store submission validation,
// HMAC-verified signed delta updates with version-chain enforcement, and the OEM /
// carrier preload catalogs.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import { createHmac } from "node:crypto";
import {
  DefaultAppStoreSubmitter,
  DefaultSignedDeltaUpdater,
  DefaultOemPreloadCatalog,
  DefaultCarrierPreloadCatalog,
  appStorePackage,
  deltaUpdate,
} from "../src/distribution/index";

describe("DefaultAppStoreSubmitter", () => {
  it("accepts a known store and records the submission", async () => {
    const s = new DefaultAppStoreSubmitter();
    const ok = await s.submitAsync(appStorePackage("PlayStore", "/pkg.aab", "1.0.0", new Map()));
    assert.equal(ok, true);
    assert.equal(s.submitted.length, 1);
  });

  it("rejects an unknown store", async () => {
    const s = new DefaultAppStoreSubmitter();
    assert.equal(await s.submitAsync(appStorePackage("MySideStore", "/p", "1.0.0", new Map())), false);
  });

  it("validates required fields", async () => {
    const s = new DefaultAppStoreSubmitter();
    await assert.rejects(() => s.submitAsync(appStorePackage("", "/p", "1", new Map())));
  });
});

describe("DefaultSignedDeltaUpdater", () => {
  const key = new Uint8Array(32).fill(7);

  function sign(channel: string, from: string, to: string, payload: Uint8Array): Uint8Array {
    const prefix = Buffer.from(`${channel}|${from}|${to}|`, "utf8");
    const msg = Buffer.concat([prefix, Buffer.from(payload)]);
    return new Uint8Array(createHmac("sha256", Buffer.from(key)).update(msg).digest());
  }

  it("applies a correctly-signed update and advances the channel", async () => {
    const u = new DefaultSignedDeltaUpdater(key);
    const payload = new Uint8Array([9, 9]);
    const sig = sign("stable", "", "1.0.0", payload);
    assert.equal(await u.applyAsync(deltaUpdate("stable", "", "1.0.0", payload, sig)), true);
    assert.equal(u.currentVersion("stable"), "1.0.0");
  });

  it("rejects a bad signature", async () => {
    const u = new DefaultSignedDeltaUpdater(key);
    const payload = new Uint8Array([1]);
    assert.equal(
      await u.applyAsync(deltaUpdate("stable", "", "1.0.0", payload, new Uint8Array([0, 0, 0]))),
      false,
    );
    assert.equal(u.currentVersion("stable"), null);
  });

  it("rejects an update whose fromVersion does not match the current chain", async () => {
    const u = new DefaultSignedDeltaUpdater(key);
    const p1 = new Uint8Array([1]);
    await u.applyAsync(deltaUpdate("stable", "", "1.0.0", p1, sign("stable", "", "1.0.0", p1)));
    // Now current is 1.0.0; an update claiming from "" must be rejected.
    const p2 = new Uint8Array([2]);
    assert.equal(await u.applyAsync(deltaUpdate("stable", "", "2.0.0", p2, sign("stable", "", "2.0.0", p2))), false);
  });

  it("requires a 16+ byte key", () => {
    assert.throws(() => new DefaultSignedDeltaUpdater(new Uint8Array(8)));
  });
});

describe("Preload catalogs", () => {
  it("expose the default partner / carrier lists", () => {
    assert.ok(new DefaultOemPreloadCatalog().partners.includes("Tecno"));
    assert.ok(new DefaultCarrierPreloadCatalog().carriers.includes("Vodacom"));
  });
});
