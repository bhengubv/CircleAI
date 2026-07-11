// operator_board.test.ts
// Verifies the CircleAI.Operator port: the deployment lifecycle state machine,
// per-transition observer notifications, delete, status lookup, and Null* defaults.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  ModelLifecyclePhase,
  InMemoryModelOperator,
  NullModelOperator,
  NullDeploymentObserver,
  modelDeployment,
  type ModelStatus,
} from "../src/operator/index";

describe("InMemoryModelOperator", () => {
  it("drives a deployment through Pending → Downloading → Loading → Ready", async () => {
    const op = new InMemoryModelOperator();
    const phases: ModelLifecyclePhase[] = [];
    const sub = op.subscribe(async (s) => {
      phases.push(s.phase);
    });
    await op.applyAsync(modelDeployment("m1", "prod", 3, "tier2"));
    sub.dispose();
    assert.deepEqual(phases, [
      ModelLifecyclePhase.Pending,
      ModelLifecyclePhase.Downloading,
      ModelLifecyclePhase.Loading,
      ModelLifecyclePhase.Ready,
    ]);
    const status = await op.getStatusAsync("m1", "prod");
    assert.equal(status?.phase, ModelLifecyclePhase.Ready);
    assert.equal(status?.readyReplicas, 3);
  });

  it("validates the deployment", async () => {
    const op = new InMemoryModelOperator();
    await assert.rejects(() => op.applyAsync(modelDeployment("", "prod", 1, "t")));
    await assert.rejects(() => op.applyAsync(modelDeployment("m", "", 1, "t")));
    await assert.rejects(() => op.applyAsync(modelDeployment("m", "prod", -1, "t")));
  });

  it("deletes a deployment", async () => {
    const op = new InMemoryModelOperator();
    await op.applyAsync(modelDeployment("m1", "prod", 1, "t"));
    await op.deleteAsync("m1", "prod");
    assert.equal(await op.getStatusAsync("m1", "prod"), null);
  });

  it("keys by namespace + id", async () => {
    const op = new InMemoryModelOperator();
    await op.applyAsync(modelDeployment("m", "ns1", 1, "t"));
    await op.applyAsync(modelDeployment("m", "ns2", 2, "t"));
    assert.equal((await op.getStatusAsync("m", "ns1"))?.readyReplicas, 1);
    assert.equal((await op.getStatusAsync("m", "ns2"))?.readyReplicas, 2);
  });

  it("does not let a throwing observer break the transition loop", async () => {
    const op = new InMemoryModelOperator();
    let good = 0;
    op.subscribe(async () => {
      throw new Error("bad observer");
    });
    op.subscribe(async () => {
      good++;
    });
    await op.applyAsync(modelDeployment("m", "prod", 1, "t"));
    assert.equal(good, 4);
  });
});

describe("Null operator defaults", () => {
  it("NullModelOperator is a no-op returning null status", async () => {
    const op = NullModelOperator.instance;
    assert.equal(op.backendId, "null");
    await op.applyAsync(modelDeployment("m", "ns", 1, "t"));
    assert.equal(await op.getStatusAsync("m", "ns"), null);
  });

  it("NullDeploymentObserver returns a no-op subscription", () => {
    const sub = NullDeploymentObserver.instance.subscribe(async (_s: ModelStatus) => {});
    assert.doesNotThrow(() => sub.dispose());
  });
});
