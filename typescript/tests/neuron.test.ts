// neuron.test.ts — the TypeScript Neuron port.
//
// Mirrors the C# CircleAI.Tests Neuron suite: the concierge decision table +
// gate, the two-slot admission gate + eviction, the router-gated slot selection
// inside AIService (specialist hot-load, generalist floor), the generalist-floor
// session round-trip, and the NeuronNode facade over the brain.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";

// Import the specific submodules (not the huge ../src/hosting barrel, which
// re-exports the mcp / multiplayer / cloud-fallback sub-hosts).
import { AIService } from "../src/hosting/service";
import type { AIOptions } from "../src/hosting/options";
import {
  HeuristicNeuronRouter,
  NeuronGate,
  ResidentSlotManager,
  SlotOutcome,
  Organ,
  NeuronNode,
} from "../src/hosting/neuron";
import type {
  INeuronRouter,
  RouteContext,
  RouteDecision,
} from "../src/hosting/neuron";
import { NullChatRuntime } from "../src/hosting/chat_runtime";
import {
  ChatCapability,
  type ChatMessage,
  type GenerationOptions,
  type IChatGenerator,
  type IModelSelector,
  type ModelSelection,
} from "../src/inference/index";
import { DeviceTier } from "../src/device";
import type { IModelLoader } from "../src/core";

// ── test doubles ─────────────────────────────────────────────────────────────

/** IChatGenerator with a fixed reply + a real (true) session round-trip. */
class NeuronGen implements IChatGenerator {
  constructor(private readonly reply: string) {}
  async generateAsync(
    _messages: readonly ChatMessage[],
    _options?: GenerationOptions,
  ): Promise<string> {
    return this.reply;
  }
  async *streamAsync(
    _messages: readonly ChatMessage[],
    _options?: GenerationOptions,
  ): AsyncGenerator<string> {
    yield this.reply;
  }
  async saveSessionAsync(_path: string): Promise<boolean> {
    return true;
  }
  async loadSessionAsync(_path: string): Promise<boolean> {
    return true;
  }
  dispose(): void {
    /* no native state */
  }
}

class FixedRouter implements INeuronRouter {
  constructor(private readonly decision: RouteDecision) {}
  route(_context: RouteContext): RouteDecision {
    return this.decision;
  }
}

class FakeSelector implements IModelSelector {
  constructor(private readonly selection: ModelSelection) {}
  bestFit(_probe: unknown, _required?: number): ModelSelection {
    return this.selection;
  }
  allCandidates(_probe: unknown): readonly ModelSelection[] {
    return [this.selection];
  }
}

class FakeLoader implements IModelLoader {
  constructor(private readonly modelPath: string) {}
  async downloadModelAsync(_modelName: string): Promise<string> {
    return this.modelPath;
  }
  getModelPath(_modelName: string): string {
    return this.modelPath;
  }
  async modelExists(_modelName: string): Promise<boolean> {
    return true;
  }
  async checkForCriticalUpdateAsync(): Promise<boolean> {
    return false;
  }
  dispose(): void {
    /* no-op */
  }
}

// ── helpers ────────────────────────────────────────────────────────────────

let tempCounter = 0;
function tempModel(): string {
  const p = path.join(os.tmpdir(), `neuron-ts-${process.pid}-${tempCounter++}.model`);
  fs.writeFileSync(p, "m");
  return p;
}

function sel(id: string, bytes: number): ModelSelection {
  return {
    modelId: id,
    requiresDownload: false,
    estimatedBytes: bytes,
    tier: DeviceTier.Desktop,
  };
}

function specialist(capability: number, reason = "t"): RouteDecision {
  return { organ: Organ.Specialist, capability, reason };
}

async function collect(stream: AsyncGenerator<string>): Promise<string> {
  let out = "";
  for await (const c of stream) out += c;
  return out;
}

// ── concierge router + gate ──────────────────────────────────────────────────

describe("Neuron — concierge router + gate", () => {
  it("plain turn → generalist(Default)", () => {
    const d = new HeuristicNeuronRouter().route({
      query: "what's the weather today?",
    });
    assert.equal(d.organ, Organ.Generalist);
    assert.equal(d.capability, ChatCapability.Default);
  });

  it("image → specialist(Vision)", () => {
    const d = new HeuristicNeuronRouter().route({
      query: "what is this?",
      hasImage: true,
    });
    assert.equal(d.organ, Organ.Specialist);
    assert.equal(d.capability, ChatCapability.Vision);
  });

  it("reasoning cue → specialist(Reasoning)", () => {
    const d = new HeuristicNeuronRouter().route({
      query: "please debug this stack trace",
    });
    assert.equal(d.organ, Organ.Specialist);
    assert.equal(d.capability, ChatCapability.Reasoning);
  });

  it("long prompt → specialist(LongContext)", () => {
    const d = new HeuristicNeuronRouter({ longContextChars: 50 }).route({
      query: "x".repeat(60),
    });
    assert.equal(d.organ, Organ.Specialist);
    assert.equal(d.capability, ChatCapability.LongContext);
  });

  it("gate veto demotes a specialist back to the generalist", () => {
    const gate = new NeuronGate(() => false);
    const d = new HeuristicNeuronRouter({ gate }).route({
      query: "solve this equation",
    });
    assert.equal(d.organ, Organ.Generalist);
  });
});

// ── resident slot manager ─────────────────────────────────────────────────────

describe("Neuron — ResidentSlotManager admission gate", () => {
  it("admits a specialist within the RAM budget", async () => {
    const m = new ResidentSlotManager(1000, () => 1_000_000);
    const a = await m.ensureSpecialist(sel("spec", 5000), () => new NeuronGen("S"));
    assert.equal(a.outcome, SlotOutcome.Admitted);
    assert.equal(m.residentSpecialistModelId, "spec");
  });

  it("denies a specialist over the RAM budget", async () => {
    const m = new ResidentSlotManager(900_000, () => 1_000_000);
    const a = await m.ensureSpecialist(sel("spec", 500_000), () => new NeuronGen("S"));
    assert.equal(a.outcome, SlotOutcome.InsufficientRam);
    assert.equal(m.residentSpecialistModelId, null);
  });

  it("does not rebuild an already-resident specialist", async () => {
    const m = new ResidentSlotManager(0, () => 1_000_000);
    let builds = 0;
    const build = () => {
      builds++;
      return new NeuronGen("S");
    };
    await m.ensureSpecialist(sel("spec", 1), build);
    const second = await m.ensureSpecialist(sel("spec", 1), build);
    assert.equal(second.outcome, SlotOutcome.AlreadyResident);
    assert.equal(builds, 1);
  });

  it("a different pick evicts the incumbent (one specialist at a time)", async () => {
    const m = new ResidentSlotManager(0, () => 1_000_000);
    await m.ensureSpecialist(sel("A", 1), () => new NeuronGen("A"));
    await m.ensureSpecialist(sel("B", 1), () => new NeuronGen("B"));
    assert.equal(m.residentSpecialistModelId, "B");
  });

  it("a null build reports BuildFailed and leaves the slot empty", async () => {
    const m = new ResidentSlotManager(0, () => 1_000_000);
    const a = await m.ensureSpecialist(sel("spec", 1), () => null);
    assert.equal(a.outcome, SlotOutcome.BuildFailed);
    assert.equal(m.residentSpecialistModelId, null);
  });

  it("evictSpecialist clears the slot", async () => {
    const m = new ResidentSlotManager(0, () => 1_000_000);
    await m.ensureSpecialist(sel("spec", 1), () => new NeuronGen("S"));
    m.evictSpecialist();
    assert.equal(m.residentSpecialistModelId, null);
  });
});

// ── AIService two-slot residency ───────────────────────────────────────────────

describe("Neuron — AIService router-gated slot selection", () => {
  it("router null keeps the single-slot generalist path", async () => {
    const opts: AIOptions = { modelPath: tempModel(), warmOnStart: false };
    const svc = new AIService(opts, null, () => new NeuronGen("GEN"));
    await svc.startAsync();
    const r = await svc.askAsync("solve this equation"); // reasoning cue, but no router
    assert.equal(r, "GEN");
    await svc.stopAsync();
  });

  it("hot-loads a capability-matched specialist", async () => {
    const genPath = tempModel();
    const specPath = tempModel();
    const gen = new NeuronGen("GEN");
    const spec = new NeuronGen("SPEC");
    const opts: AIOptions = {
      modelId: "gen-model",
      modelPath: genPath,
      warmOnStart: false,
      router: new FixedRouter(specialist(ChatCapability.Reasoning)),
    };
    const svc = new AIService(
      opts,
      new FakeLoader(specPath),
      (p) => (p === specPath ? spec : gen),
      new FakeSelector(sel("spec-model", 1024)),
    );
    await svc.startAsync();
    const r = await svc.askAsync("anything");
    assert.equal(r, "SPEC");
    await svc.stopAsync();
  });

  it("best-fit resolving to the generalist answers from the floor", async () => {
    const genPath = tempModel();
    const gen = new NeuronGen("GEN");
    const opts: AIOptions = {
      modelId: "gen-model",
      modelPath: genPath,
      warmOnStart: false,
      router: new FixedRouter(specialist(ChatCapability.Reasoning)),
    };
    const svc = new AIService(
      opts,
      new FakeLoader(genPath),
      () => gen,
      new FakeSelector(sel("gen-model", 1024)), // best-fit == generalist
    );
    await svc.startAsync();
    const r = await svc.askAsync("anything");
    assert.equal(r, "GEN");
    await svc.stopAsync();
  });

  it("generalist-floor session round-trip", async () => {
    const opts: AIOptions = { modelPath: tempModel(), warmOnStart: false };
    const svc = new AIService(opts, null, () => new NeuronGen("GEN"));
    await svc.startAsync();
    const snap = tempModel();
    assert.equal(await svc.saveSessionAsync(snap), true);
    assert.equal(await svc.loadSessionAsync(snap), true);
    await svc.stopAsync();
  });
});

// ── NeuronNode facade + NullChatRuntime ────────────────────────────────────────

describe("Neuron — NeuronNode facade", () => {
  it("composes the host-neutral runtime over the brain", async () => {
    const opts: AIOptions = {
      modelId: "qwen-x",
      modelPath: tempModel(),
      warmOnStart: false,
    };
    const svc = new AIService(opts, null, () => new NeuronGen("hello"));
    const node = new NeuronNode(svc);

    assert.equal(node.id, "circleai-neuron");
    assert.equal(node.isReady, false);
    assert.equal(node.statusMessage, "loading model…");

    await svc.startAsync();
    assert.equal(node.isReady, true);
    assert.equal(node.statusMessage, "ready");
    assert.ok(node.engineLabel.includes("qwen-x"));

    const out = await collect(node.streamAsync([{ role: "user", content: "hi" }]));
    assert.equal(out, "hello");
    assert.notEqual(node.sessionSnapshotPath, null);
    await svc.stopAsync();
  });

  it("NullChatRuntime is never ready and streams a notice", async () => {
    const nul = new NullChatRuntime();
    assert.equal(nul.isReady, false);
    const out = await collect(nul.streamAsync([{ role: "user", content: "hi" }]));
    assert.ok(out.includes("No chat engine"));
  });
});
