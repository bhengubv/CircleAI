// hosting_service.test.ts
//
// Verifies CircleAI.Hosting.AIService: startup via an injected generator
// factory, ask/chat, system-prompt enrichment (device context + persona +
// skills + RAG), streaming with observer events, tool-call parsing + the
// agentic loop, feedback-driven persona adaptation, and the RT-04 brownout
// hot-swap through a fallback-chain selector.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  AIService,
  AIObserverBase,
  type AIChatEvent,
  type IFallbackChainModelSelector,
  type AIOptions,
} from "../src/hosting/index";
import type {
  GenerationOptions,
  IChatGenerator,
  ModelSelection,
} from "../src/inference/index";
import type { ChatMessage } from "../src/models/index";
import type { DeviceProbe } from "../src/device/index";
import { NullDeviceContext } from "../src/device/index";
import {
  InMemoryPersonaStore,
  InMemoryFeedbackStore,
  InMemoryEpisodicStore,
} from "../src/memory/stores";
import { FeedbackPolarity, type FeedbackSignal } from "../src/memory/index";
import { InMemorySkillStore } from "../src/hosting/index";

/** Records the messages each call received and replies with a scripted string. */
class ScriptedGenerator implements IChatGenerator {
  lastMessages: readonly ChatMessage[] = [];
  calls = 0;
  disposed = false;
  constructor(private readonly replies: string[] | ((n: number) => string)) {}

  private reply(): string {
    if (typeof this.replies === "function") return this.replies(this.calls);
    return this.replies[Math.min(this.calls, this.replies.length - 1)];
  }

  async generateAsync(messages: readonly ChatMessage[]): Promise<string> {
    this.lastMessages = messages;
    const r = this.reply();
    this.calls++;
    return r;
  }

  async *streamAsync(messages: readonly ChatMessage[]): AsyncGenerator<string> {
    this.lastMessages = messages;
    const r = this.reply();
    this.calls++;
    for (const word of r.split(" ")) yield word;
  }

  dispose(): void {
    this.disposed = true;
  }
}

function optionsWith(over: Partial<AIOptions>): AIOptions {
  return { modelPath: "/fake/model.gguf", warmOnStart: false, ...over };
}

describe("AIService — startup + ask/chat", () => {
  it("loads via the injected factory and answers a question", async () => {
    const gen = new ScriptedGenerator(["hi there"]);
    const svc = new AIService(optionsWith({ generatorFactory: () => gen }));
    assert.equal(svc.isReady, false);
    await svc.startAsync();
    assert.equal(svc.isReady, true);
    const answer = await svc.askAsync("hello");
    assert.equal(answer, "hi there");
    await svc.disposeAsync();
    assert.equal(gen.disposed, true);
  });

  it("throws when neither a model path nor a factory can resolve a model", async () => {
    const svc = new AIService({ warmOnStart: false });
    await assert.rejects(() => svc.startAsync());
  });

  it("prepends an enriched system prompt with device context", async () => {
    const gen = new ScriptedGenerator(["ok"]);
    const ctx = {
      ...new NullDeviceContext(),
      activeAppId: "com.example.app",
      networkType: "wifi",
      batteryLevel: 0.5,
      isCharging: true,
    };
    const svc = new AIService(
      optionsWith({ generatorFactory: () => gen, deviceContext: ctx }),
    );
    await svc.chatAsync([{ role: "user", content: "hey" }]);
    const sys = gen.lastMessages.find((m) => m.role === "system");
    assert.ok(sys, "a system message should be injected");
    assert.match(sys!.content, /\[Device context\]/);
    assert.match(sys!.content, /Active app: com.example.app/);
    assert.match(sys!.content, /Battery: 50% \(charging\)/);
  });

  it("honours a caller-supplied system message verbatim (no enrichment)", async () => {
    const gen = new ScriptedGenerator(["ok"]);
    const ctx = { ...new NullDeviceContext(), activeAppId: "app" };
    const svc = new AIService(
      optionsWith({ generatorFactory: () => gen, deviceContext: ctx }),
    );
    await svc.chatAsync([
      { role: "system", content: "CUSTOM" },
      { role: "user", content: "hi" },
    ]);
    const systems = gen.lastMessages.filter((m) => m.role === "system");
    assert.equal(systems.length, 1);
    assert.equal(systems[0].content, "CUSTOM");
  });

  it("injects skill context when a skill store is configured", async () => {
    const gen = new ScriptedGenerator(["ok"]);
    const skills = new InMemorySkillStore();
    await skills.upsertAsync("summarise", {
      name: "Summarise",
      description: "Summarise long text",
      instructions: "Be concise.",
      tags: ["text"],
    });
    const svc = new AIService(
      optionsWith({ generatorFactory: () => gen, skillStore: skills }),
    );
    await svc.chatAsync([{ role: "user", content: "please summarise this" }]);
    const sys = gen.lastMessages.find((m) => m.role === "system");
    assert.match(sys!.content, /## Available Skills/);
    assert.match(sys!.content, /\*\*summarise\*\*/);
  });
});

describe("AIService — streaming + observer", () => {
  it("streams pieces and fires stream started/completed", async () => {
    const gen = new ScriptedGenerator(["one two three"]);
    const events: string[] = [];
    let completedTokens = -1;
    const observer = new (class extends AIObserverBase {
      override async onStreamStartedAsync() {
        events.push("start");
      }
      override async onStreamCompletedAsync(e: { tokenCount: number }) {
        events.push("complete");
        completedTokens = e.tokenCount;
      }
    })();
    const svc = new AIService(optionsWith({ generatorFactory: () => gen, observer }));
    const out: string[] = [];
    for await (const p of svc.streamAsync([{ role: "user", content: "go" }])) out.push(p);
    assert.deepEqual(out, ["one", "two", "three"]);
    assert.deepEqual(events, ["start", "complete"]);
    assert.equal(completedTokens, 3);
  });

  it("stores the exchange in episodic memory and fires onChatCompleted", async () => {
    const gen = new ScriptedGenerator(["stored"]);
    const episodic = new InMemoryEpisodicStore();
    const captured: AIChatEvent[] = [];
    const observer = new (class extends AIObserverBase {
      override async onChatCompletedAsync(e: AIChatEvent) {
        captured.push(e);
      }
    })();
    const svc = new AIService(
      optionsWith({ generatorFactory: () => gen, episodicMemory: episodic, observer }),
    );
    await svc.chatAsync([{ role: "user", content: "remember me" }]);
    assert.equal(await episodic.countAsync(), 1);
    assert.equal(captured.length, 1);
    assert.equal(captured[0].response, "stored");
  });
});

describe("AIService — tool parsing + agentic loop", () => {
  it("parseToolCall extracts name + arguments from the Qwen tag", () => {
    const inv = AIService.parseToolCall(
      'sure <tool_call>{"name":"weather","arguments":{"city":"Durban","days":3}}</tool_call>',
    );
    assert.ok(inv);
    assert.equal(inv!.toolName, "weather");
    assert.equal(inv!.arguments["city"], "Durban");
    // Non-string args become raw JSON text (matches the C# ToManaged path).
    assert.equal(inv!.arguments["days"], "3");
  });

  it("returns null when no tool call is present", () => {
    assert.equal(AIService.parseToolCall("just text"), null);
  });

  it("agentic loop invokes the tool bridge then re-prompts to a plain answer", async () => {
    // First turn emits a tool call; second turn returns plain text.
    const gen = new ScriptedGenerator((n) =>
      n === 0
        ? '<tool_call>{"name":"echo","arguments":{"v":"hi"}}</tool_call>'
        : "final answer",
    );
    const bridge = {
      availableTools: [],
      async invoke(inv: { toolName: string; arguments: Record<string, unknown> }) {
        return { toolName: inv.toolName, success: true, result: inv.arguments["v"] };
      },
    };
    const svc = new AIService(
      optionsWith({ generatorFactory: () => gen, toolBridge: bridge }),
    );
    const result = await svc.agenticChatAsync("do it");
    assert.equal(result, "final answer");
    assert.equal(gen.calls, 2);
  });

  it("invokeTool returns a failure when no bridge is configured", async () => {
    const gen = new ScriptedGenerator(["x"]);
    const svc = new AIService(optionsWith({ generatorFactory: () => gen }));
    const res = await svc.invokeToolAsync({ toolName: "t", arguments: {} });
    assert.equal(res.success, false);
    assert.match(res.error ?? "", /No tool bridge configured/);
  });
});

describe("AIService — feedback persona adaptation", () => {
  it("negative feedback nudges verbosity toward brief and persists", async () => {
    const gen = new ScriptedGenerator(["x"]);
    const personaStore = new InMemoryPersonaStore();
    const feedbackStore = new InMemoryFeedbackStore();
    const svc = new AIService(
      optionsWith({ generatorFactory: () => gen, personaStore, feedbackStore }),
    );

    // 10 negative signals → analyser drives verbosity down.
    for (let i = 0; i < 10; i++) {
      const sig: FeedbackSignal = {
        id: `s${i}`,
        recordedAtUtc: new Date(),
        userText: "u",
        assistantText: "a",
        polarity: FeedbackPolarity.Negative,
      };
      await svc.submitFeedbackAsync(sig);
    }

    const persona = await personaStore.loadAsync("default");
    assert.equal(persona.verbosity, "brief");
    assert.equal(persona.negativeSignals, 10);
    assert.equal(await feedbackStore.countAsync(), 10);
  });
});

describe("AIService — RT-04 brownout", () => {
  it("hot-swaps to the next model in the fallback chain", async () => {
    const made: string[] = [];
    const loader = {
      async downloadModelAsync(id: string) {
        return `/models/${id}.gguf`;
      },
      getModelPath(id: string) {
        return `/models/${id}.gguf`;
      },
      async modelExists() {
        return true;
      },
      async checkForCriticalUpdateAsync() {
        return false;
      },
      dispose() {},
    };
    const selector: IFallbackChainModelSelector = {
      bestFit(_probe: DeviceProbe): ModelSelection {
        return {
          modelId: "big",
          requiresDownload: false,
          estimatedBytes: 0,
          tier: 3 as ModelSelection["tier"],
        };
      },
      allCandidates() {
        return [];
      },
      chainFor(id: string) {
        return id === "big" ? ["big", "small"] : ["small"];
      },
    };
    const gen = () => {
      const g = new ScriptedGenerator(["x"]);
      return g;
    };
    let swappedTo = "";
    const observer = new (class extends AIObserverBase {
      override async onBrownoutAsync(_from: string, to: string) {
        swappedTo = to;
      }
    })();
    const svc = new AIService(
      { modelId: "big", warmOnStart: false, observer },
      loader,
      gen,
      selector,
    );
    await svc.startAsync();
    void made;
    const ok = await svc.brownoutAsync(3 /* Manual */);
    assert.equal(ok, true);
    assert.equal(swappedTo, "small");
    await svc.disposeAsync();
  });

  it("brownout is a no-op without a chainFor-capable selector", async () => {
    const gen = new ScriptedGenerator(["x"]);
    const svc = new AIService(optionsWith({ generatorFactory: () => gen }));
    await svc.startAsync();
    const ok = await svc.brownoutAsync(0);
    assert.equal(ok, false);
    await svc.disposeAsync();
  });
});

// GenerationOptions is referenced only for typing; silence unused-import lints.
export type _Options = GenerationOptions;
