// hosting_primitives.test.ts
//
// Verifies the smaller CircleAI.Hosting primitives: ThermalThrottleService
// (injected sampler + state transitions), MemoryPressureLevel sources, the
// HistogramRequestPredictor + PredictiveWarmupController, InMemoryToolCatalog
// search/list/provider filtering + importFromAsync, the JSON generative-UI
// parser, the schedule/idle triggers, ProactiveReasoningService, and the
// Push/Aether observer bridges.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  ThermalThrottleService,
  ThermalState,
  NullMemoryPressureSource,
  ManualMemoryPressureSource,
  MemoryPressureLevel,
  HistogramRequestPredictor,
  PredictiveWarmupController,
  InMemoryToolCatalog,
  importFromAsync,
  parseRender,
  describeCatalogForPrompt,
  JsonRenderError,
  UiCatalogs,
  ScheduleTrigger,
  IdleTrigger,
  ProactiveReasoningService,
  PushAIObserver,
  AetherAIObserver,
  type IAIService,
  type ITriggerCondition,
  type ProactiveContext,
  type ToolDescriptor,
  type IToolProvider,
  type IPushNotificationSender,
  type ICircleAetherTransport,
} from "../src/hosting/index";
import { Goal } from "../src/memory/index";
import { InMemoryGoalStore } from "../src/memory/stores";

describe("ThermalThrottleService", () => {
  it("samples immediately and fires StateChanged on transitions", () => {
    let state = ThermalState.Normal;
    const svc = new ThermalThrottleService(() => state);
    const seen: ThermalState[] = [];
    svc.onStateChanged((s) => seen.push(s));
    svc.startMonitoring(); // immediate sample = Normal (from Unknown)
    assert.equal(svc.currentState, ThermalState.Normal);
    assert.equal(svc.shouldPauseInference, false);
    // No timer wait — verify the transition path directly is covered by the
    // immediate sample; now stop cleanly.
    svc.stopMonitoring();
    svc.dispose();
    assert.deepEqual(seen, [ThermalState.Normal]);
  });

  it("shouldPauseInference is true at Serious and above", () => {
    const svc = new ThermalThrottleService(() => ThermalState.Serious);
    svc.startMonitoring();
    assert.equal(svc.shouldPauseInference, true);
    svc.dispose();
  });
});

describe("MemoryPressureLevel sources", () => {
  it("null source stays Normal and never fires", async () => {
    const s = NullMemoryPressureSource.instance;
    assert.equal(s.current, MemoryPressureLevel.Normal);
    let fired = false;
    const off = s.subscribe(async () => {
      fired = true;
    });
    off();
    assert.equal(fired, false);
  });

  it("manual source fires handlers on transitions only", async () => {
    const s = new ManualMemoryPressureSource();
    const seen: [MemoryPressureLevel, MemoryPressureLevel][] = [];
    s.subscribe(async (o, n) => {
      seen.push([o, n]);
    });
    await s.raise(MemoryPressureLevel.Critical);
    await s.raise(MemoryPressureLevel.Critical); // no-op (same level)
    await s.raise(MemoryPressureLevel.Normal);
    assert.deepEqual(seen, [
      [MemoryPressureLevel.Normal, MemoryPressureLevel.Critical],
      [MemoryPressureLevel.Critical, MemoryPressureLevel.Normal],
    ]);
  });
});

describe("HistogramRequestPredictor + PredictiveWarmupController", () => {
  it("cold start returns zero confidence", () => {
    const p = new HistogramRequestPredictor();
    const f = p.predict(new Date(Date.UTC(2026, 0, 1, 8, 0, 0)), 60_000);
    assert.equal(f.confidence, 0);
    assert.equal(f.probabilityOfArrival, 0);
  });

  it("learns a busy minute and forecasts a rising probability", () => {
    const p = new HistogramRequestPredictor();
    const t = new Date(Date.UTC(2026, 0, 1, 8, 0, 0));
    for (let i = 0; i < 30; i++) p.recordArrival(t);
    assert.equal(p.observedArrivals, 30);
    const f = p.predict(t, 60_000);
    assert.ok(f.probabilityOfArrival > 0);
    assert.ok(f.expectedCount > 0);
    assert.ok(f.confidence > 0);
  });

  it("controller pre-warms once a spike is forecast", async () => {
    const p = new HistogramRequestPredictor();
    const t = new Date(Date.UTC(2026, 0, 1, 8, 0, 0));
    for (let i = 0; i < 40; i++) p.recordArrival(t);

    let prewarms = 0;
    const svc = {
      prewarmAsync: async () => {
        prewarms++;
      },
    } as unknown as IAIService;

    const ctrl = new PredictiveWarmupController(
      svc,
      p,
      { enabled: true, warmupThreshold: 0.01, minTimeBetweenWarmupsMs: 0 },
      () => t,
    );
    const fired = await ctrl.tickAsync();
    assert.equal(fired, true);
    assert.equal(prewarms, 1);
    await ctrl.disposeAsync();
  });
});

describe("InMemoryToolCatalog", () => {
  const t = (name: string, provider: string, tags: string[] = [], desc = ""): ToolDescriptor => ({
    name,
    description: desc,
    provider,
    tags,
  });

  it("upserts, lists (name-ordered) and removes", async () => {
    const c = new InMemoryToolCatalog();
    await c.upsertAsync(t("gmail.send", "gmail"));
    await c.upsertAsync(t("github.pr", "github"));
    assert.equal(c.count, 2);
    assert.deepEqual(c.list().map((d) => d.name), ["github.pr", "gmail.send"]);
    assert.equal(await c.removeAsync("gmail.send"), true);
    assert.equal(c.count, 1);
  });

  it("scores search matches by name > tags > description", async () => {
    const c = new InMemoryToolCatalog();
    await c.upsertAsync(t("weather.today", "w", ["forecast"], "gets weather"));
    await c.upsertAsync(t("news.top", "n", ["weather"], "top news"));
    const res = c.search("weather");
    // name hit (5) outranks tag hit (3).
    assert.equal(res[0].name, "weather.today");
  });

  it("filters by provider case-insensitively and imports from a provider", async () => {
    const c = new InMemoryToolCatalog();
    const provider: IToolProvider = {
      providerId: "local",
      async discoverAsync() {
        return [t("a", "Local"), t("b", "local")];
      },
      async isAvailableAsync() {
        return true;
      },
    };
    const n = await importFromAsync(c, provider);
    assert.equal(n, 2);
    assert.equal(c.listByProvider("local").length, 2);
  });
});

describe("JsonRenderParser (generative UI)", () => {
  it("parses a valid card with children", () => {
    const json = JSON.stringify({
      kind: "card",
      properties: { title: "Hi" },
      children: [{ kind: "textBlock", properties: { text: "body" } }],
    });
    const c = parseRender(json, UiCatalogs.Default);
    assert.equal(c.kind, "card");
    assert.equal(c.properties["title"], "Hi");
    assert.equal(c.children?.length, 1);
    assert.equal(c.children![0].kind, "textBlock");
  });

  it("rejects unknown kinds in strict mode and downgrades in lenient mode", () => {
    const json = JSON.stringify({ kind: "mystery", properties: {} });
    assert.throws(() => parseRender(json, UiCatalogs.Default, true), JsonRenderError);
    const lenient = parseRender(json, UiCatalogs.Default, false);
    assert.equal(lenient.kind, "textBlock");
    assert.match(String(lenient.properties["text"]), /unknown kind 'mystery'/);
  });

  it("rejects properties not declared on the kind", () => {
    const json = JSON.stringify({ kind: "button", properties: { bogus: 1 } });
    assert.throws(() => parseRender(json, UiCatalogs.Default, true), JsonRenderError);
  });

  it("describes the catalog for the prompt", () => {
    const desc = describeCatalogForPrompt(UiCatalogs.Default);
    assert.match(desc, /Allowed kinds:/);
    assert.match(desc, /- card —/);
  });
});

describe("Triggers", () => {
  it("IdleTrigger fires past the threshold", async () => {
    const trig = new IdleTrigger(60_000);
    const ctx: ProactiveContext = {
      userId: "u",
      nowUtc: new Date(),
      timeSinceLastInteractionMs: 120_000,
      affectState: null,
      activeGoals: [],
    };
    assert.equal(await trig.isMetAsync(ctx), true);
    const notYet = { ...ctx, timeSinceLastInteractionMs: 10_000 };
    assert.equal(await trig.isMetAsync(notYet), false);
  });

  it("ScheduleTrigger fires once in-window then not again that day", async () => {
    // Build a local time inside the 5-minute window of the trigger.
    const now = new Date();
    const trig = new ScheduleTrigger(now.getHours(), now.getMinutes(), "daily");
    const ctx: ProactiveContext = {
      userId: "u",
      nowUtc: now,
      timeSinceLastInteractionMs: 0,
      affectState: null,
      activeGoals: [],
    };
    assert.equal(await trig.isMetAsync(ctx), true);
    // Same day, still in window → must not re-fire.
    assert.equal(await trig.isMetAsync(ctx), false);
  });
});

describe("ProactiveReasoningService", () => {
  it("fires the first met trigger and raises a message", async () => {
    let asked = "";
    const svc = {
      askAsync: async (p: string) => {
        asked = p;
        return "hey, checking in!";
      },
    } as unknown as IAIService;

    const goals = new InMemoryGoalStore();
    const g = Object.assign(new Goal(), { id: "g1", userId: "u", title: "Learn Zulu", status: "Active" });
    await goals.upsertAsync(g);

    const alwaysTrigger: ITriggerCondition = {
      name: "always",
      async isMetAsync() {
        return true;
      },
    };
    const svc2 = new ProactiveReasoningService(svc, goals, null, [alwaysTrigger]);
    const captured: { message: string; triggerName: string }[] = [];
    svc2.onProactiveMessageReady((a) => captured.push(a));

    await svc2.checkAsync("u");
    assert.equal(captured.length, 1);
    assert.equal(captured[0].triggerName, "always");
    assert.equal(captured[0].message, "hey, checking in!");
    assert.match(asked, /Learn Zulu/);
  });

  it("does nothing when no trigger fires", async () => {
    const svc = { askAsync: async () => "x" } as unknown as IAIService;
    const never: ITriggerCondition = {
      name: "never",
      async isMetAsync() {
        return false;
      },
    };
    const s = new ProactiveReasoningService(svc, null, null, [never]);
    let fired = false;
    s.onProactiveMessageReady(() => (fired = true));
    await s.checkAsync("u");
    assert.equal(fired, false);
  });
});

describe("Observer bridges", () => {
  it("PushAIObserver truncates long responses to 100 chars + ellipsis", async () => {
    const sent: { title: string; body: string }[] = [];
    const sender: IPushNotificationSender = {
      async sendAsync(_token, title, body) {
        sent.push({ title, body });
      },
    };
    const obs = new PushAIObserver(sender, "device-token");
    const long = "x".repeat(250);
    await obs.onChatCompletedAsync({
      correlationId: "c",
      messages: [],
      response: long,
      elapsedMs: 1,
      timestamp: new Date().toISOString(),
    });
    // Fire-and-forget — allow the microtask to run.
    await Promise.resolve();
    assert.equal(sent.length, 1);
    assert.equal(sent[0].title, "B!");
    assert.equal(sent[0].body.length, 101); // 100 chars + ellipsis
  });

  it("AetherAIObserver publishes a JSON response payload to butler/response", async () => {
    const published: { topic: string; payload: Uint8Array }[] = [];
    const transport: ICircleAetherTransport = {
      async publishAsync(topic, payload) {
        published.push({ topic, payload });
      },
    };
    const obs = new AetherAIObserver(transport);
    await obs.onChatCompletedAsync({
      correlationId: "c",
      messages: [],
      response: "hello mesh",
      elapsedMs: 1,
      timestamp: new Date().toISOString(),
    });
    await Promise.resolve();
    assert.equal(published.length, 1);
    assert.equal(published[0].topic, "butler/response");
    const decoded = JSON.parse(new TextDecoder().decode(published[0].payload));
    assert.equal(decoded.response, "hello mesh");
  });
});
