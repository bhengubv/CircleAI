// workflows.test.ts
// Verifies the scoped CircleAI.Workflows port: the durable-workflow contracts
// (definition store, runner, state checkpointing) with in-memory + Null* impls,
// and the PacaConversationRuntime state machine (queue → run → finish, executor
// step emission, and stop → Stopped).

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  WorkflowPhase,
  ConversationState,
  InMemoryWorkflowDefinitionStore,
  InMemoryWorkflowRunner,
  InMemoryWorkflowState,
  NullWorkflowRunner,
  PacaConversationRuntime,
  workflowDefinition,
  checkpointPayload,
  conversationPermissions,
  conversationStep,
  type IConversationExecutor,
} from "../src/workflows/index";

describe("InMemoryWorkflowDefinitionStore", () => {
  it("upserts and gets a definition", async () => {
    const store = new InMemoryWorkflowDefinitionStore();
    await store.upsertAsync(workflowDefinition("d1", "Deploy", "1.0.0", "desc"));
    assert.equal((await store.getAsync("d1"))?.name, "Deploy");
    assert.equal(await store.getAsync("missing"), null);
  });
});

describe("InMemoryWorkflowRunner", () => {
  it("starts a Running run and cancels it to Failed", async () => {
    const runner = new InMemoryWorkflowRunner();
    const run = await runner.startAsync("d1");
    assert.equal(run.phase, WorkflowPhase.Running);
    await runner.cancelAsync(run.runId);
    assert.equal((await runner.getAsync(run.runId))?.phase, WorkflowPhase.Failed);
  });
});

describe("InMemoryWorkflowState", () => {
  it("checkpoints and loads per run+step", async () => {
    const state = new InMemoryWorkflowState();
    await state.checkpointAsync(checkpointPayload("r1", "s1", new Uint8Array([1, 2, 3])));
    const loaded = await state.loadAsync("r1", "s1");
    assert.deepEqual([...(loaded?.stateBlob ?? [])], [1, 2, 3]);
    assert.equal(await state.loadAsync("r1", "other"), null);
  });
});

describe("Null workflow defaults", () => {
  it("NullWorkflowRunner returns a failed execution", async () => {
    const run = await NullWorkflowRunner.instance.startAsync("d");
    assert.equal(run.phase, WorkflowPhase.Failed);
    assert.equal(run.failureReason, "NullWorkflowRunner");
  });
});

describe("PacaConversationRuntime", () => {
  const permissions = conversationPermissions(true, false);

  it("queues, runs to Finished, and records executor steps", async () => {
    const executor: IConversationExecutor = {
      async runAsync(conv, _perms, onStep) {
        onStep(conversationStep(conv.id, 0, "agent", '{"msg":"hi"}', new Date()));
      },
    };
    const rt = new PacaConversationRuntime(executor);
    rt.queue("c1", "proj", "agentA", "do the thing", "humanH");
    assert.equal(rt.get("c1")?.state, ConversationState.Queued);
    await rt.startAsync("c1", permissions);
    assert.equal(rt.get("c1")?.state, ConversationState.Finished);
    assert.equal(rt.getSteps("c1").length, 1);
    assert.equal(rt.getSteps("c1")[0].speaker, "agent");
  });

  it("marks a conversation Failed when the executor throws", async () => {
    const executor: IConversationExecutor = {
      async runAsync() {
        throw new Error("executor boom");
      },
    };
    const rt = new PacaConversationRuntime(executor);
    rt.queue("c2", "proj", "agentA", "x");
    await rt.startAsync("c2", permissions);
    assert.equal(rt.get("c2")?.state, ConversationState.Failed);
    assert.equal(rt.get("c2")?.failureReason, "executor boom");
  });

  it("stop() aborts a running conversation to Stopped", async () => {
    const executor: IConversationExecutor = {
      runAsync(_conv, _perms, _onStep, signal) {
        return new Promise((resolve, reject) => {
          const timer = setTimeout(resolve, 10_000);
          signal?.addEventListener("abort", () => {
            clearTimeout(timer);
            reject(new Error("aborted"));
          });
        });
      },
    };
    const rt = new PacaConversationRuntime(executor);
    rt.queue("c3", "proj", "agentA", "x");
    const running = rt.startAsync("c3", permissions);
    // Let the run reach the executor, then stop it.
    await new Promise((r) => setTimeout(r, 5));
    rt.stop("c3");
    await running;
    assert.equal(rt.get("c3")?.state, ConversationState.Stopped);
  });

  it("throws on a duplicate id or starting a non-queued conversation", async () => {
    const executor: IConversationExecutor = { async runAsync() {} };
    const rt = new PacaConversationRuntime(executor);
    rt.queue("c4", "p", "a", "x");
    assert.throws(() => rt.queue("c4", "p", "a", "y"));
    await rt.startAsync("c4", permissions);
    await assert.rejects(() => rt.startAsync("c4", permissions));
  });
});
