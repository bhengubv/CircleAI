// buildfarm.test.ts
// Verifies the CircleAI.BuildFarm port: agent-pool acquire/release, the job-runner
// state machine, the artifact store, and Null* defaults.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  BuildAgentKind,
  BuildJobPhase,
  InMemoryBuildAgentPool,
  InMemoryBuildJobRunner,
  InMemoryBuildArtifactStore,
  NullBuildAgentPool,
  NullBuildJobRunner,
  buildAgent,
  buildArtifact,
} from "../src/buildfarm/index";

describe("InMemoryBuildAgentPool", () => {
  it("acquires a free agent of a kind and releases it", async () => {
    const pool = new InMemoryBuildAgentPool();
    pool.register(buildAgent("l1", BuildAgentKind.Linux, "ubuntu", null));
    pool.register(buildAgent("m1", BuildAgentKind.Mac, "sonoma", "M2"));
    const a = await pool.acquireAsync(BuildAgentKind.Linux);
    assert.equal(a?.agentId, "l1");
    // No more Linux agents free.
    assert.equal(await pool.acquireAsync(BuildAgentKind.Linux), null);
    await pool.releaseAsync("l1");
    assert.equal((await pool.acquireAsync(BuildAgentKind.Linux))?.agentId, "l1");
    assert.equal((await pool.listAsync()).length, 2);
  });
});

describe("InMemoryBuildJobRunner", () => {
  it("starts a Running job and completes it", async () => {
    const runner = new InMemoryBuildJobRunner();
    const job = await runner.startAsync("l1", "repo", "main");
    assert.equal(job.phase, BuildJobPhase.Running);
    runner.complete(job.jobId, true);
    assert.equal((await runner.getAsync(job.jobId))?.phase, BuildJobPhase.Succeeded);
  });

  it("marks a job Failed", async () => {
    const runner = new InMemoryBuildJobRunner();
    const job = await runner.startAsync("l1", "repo", "dev");
    runner.complete(job.jobId, false);
    assert.equal((await runner.getAsync(job.jobId))?.phase, BuildJobPhase.Failed);
  });

  it("throws completing an unknown job", () => {
    assert.throws(() => new InMemoryBuildJobRunner().complete("nope", true));
  });
});

describe("InMemoryBuildArtifactStore", () => {
  it("saves and fetches an artifact", async () => {
    const store = new InMemoryBuildArtifactStore();
    await store.saveAsync(buildArtifact("art1", "job1", "app.apk", new Uint8Array([1, 2])));
    const got = await store.getAsync("art1");
    assert.equal(got?.name, "app.apk");
    assert.equal(await store.getAsync("missing"), null);
  });
});

describe("Null build-farm defaults", () => {
  it("NullBuildAgentPool acquires nothing", async () => {
    assert.equal(await NullBuildAgentPool.instance.acquireAsync(BuildAgentKind.Ios), null);
  });
  it("NullBuildJobRunner returns a failed job", async () => {
    const job = await NullBuildJobRunner.instance.startAsync("a", "r", "b");
    assert.equal(job.phase, BuildJobPhase.Failed);
  });
});
