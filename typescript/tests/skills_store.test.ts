// skills_store.test.ts
// Verifies the scoped CircleAI.Skills port: the InMemorySkillStore (upsert with
// slug generation, list/search ordering, delete), the slug helper, the
// KnownSkillPacks catalog, and the deterministic in-memory pack downloader.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  SkillSource,
  InMemorySkillStore,
  KnownSkillPacks,
  InMemoryPackDownloader,
  skillPackSourcesOptions,
  skillDraft,
} from "../src/skills/index";

describe("InMemorySkillStore", () => {
  it("auto-generates a slug id on upsert when none is given", async () => {
    const store = new InMemorySkillStore();
    const detail = await store.upsertAsync(null, skillDraft("My Cool Skill", "does things", "step 1", ["a"]));
    assert.equal(detail.id, "my-cool-skill");
    assert.equal(detail.source, SkillSource.InMemory);
    assert.equal((await store.getAsync("my-cool-skill"))?.name, "My Cool Skill");
  });

  it("lists summaries ordered by name (case-insensitive)", async () => {
    const store = new InMemorySkillStore();
    await store.upsertAsync("z", skillDraft("Zebra", "", "", []));
    await store.upsertAsync("a", skillDraft("apple", "", "", []));
    const list = await store.listAsync();
    assert.deepEqual(
      list.map((s) => s.name),
      ["apple", "Zebra"],
    );
  });

  it("searches name/description/tags case-insensitively", async () => {
    const store = new InMemorySkillStore();
    await store.upsertAsync("1", skillDraft("Calendar", "summarise events", "", ["productivity"]));
    await store.upsertAsync("2", skillDraft("Weather", "forecast", "", ["outdoors"]));
    assert.equal((await store.searchAsync("summar")).length, 1);
    assert.equal((await store.searchAsync("PRODUCTIVITY")).length, 1);
    assert.equal((await store.searchAsync("")).length, 0);
  });

  it("deletes a skill", async () => {
    const store = new InMemorySkillStore();
    await store.upsertAsync("k", skillDraft("K", "", "", []));
    await store.deleteAsync("k");
    assert.equal(await store.getAsync("k"), null);
  });

  it("generateSlug lowercases and strips punctuation", () => {
    assert.equal(InMemorySkillStore.generateSlug("Hello,  World!!"), "hello-world");
    // Empty-after-strip falls back to a 32-char hex id.
    assert.match(InMemorySkillStore.generateSlug("!!!"), /^[0-9a-f]{32}$/);
  });
});

describe("KnownSkillPacks", () => {
  it("lists all eight packs with the right defaults", () => {
    assert.equal(KnownSkillPacks.all.length, 8);
    assert.equal(KnownSkillPacks.awesomeAgentSkills.gitRef, "main");
    assert.equal(KnownSkillPacks.careerOps.isDefaultEnabled, false);
    assert.deepEqual([...(KnownSkillPacks.claudeBugHunter.defaultTags ?? [])], ["security", "bug-bounty"]);
  });
});

describe("InMemoryPackDownloader", () => {
  it("materialises a deterministic path per pack and records ensures", async () => {
    const dl = new InMemoryPackDownloader();
    const opts = skillPackSourcesOptions();
    const path = await dl.ensureAsync(KnownSkillPacks.claudeBugHunter, opts.cacheDirectory, opts.cacheTtlMs);
    // C# Sanitize replaces only Path.GetInvalidFileNameChars() with '_'
    // (SkillPackAutoImporter.cs line 156-159). The hyphen is a valid file-name
    // character on every platform, so "Claude-BugHunter" is kept verbatim — the
    // dash is NOT converted to an underscore.
    assert.equal(path, `${opts.cacheDirectory}/Claude-BugHunter`);
    assert.equal(dl.ensured.length, 1);
  });
});
