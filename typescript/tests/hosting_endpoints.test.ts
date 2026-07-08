// hosting_endpoints.test.ts
//
// Verifies the ported CircleAI.Hosting transport surface: InProcessEndpoint,
// the HttpLoopbackEndpoint route table + X-Butler-Token auth + SSE framing, the
// AIHttpClient round-trip through the in-memory LoopbackHttpTransport, the
// FallbackAIService local/cloud selection, and the BackgroundInferenceWorker
// thermal pause. AIApiClient is exercised against a scripted fake transport.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InProcessEndpoint,
  HttpLoopbackEndpoint,
  LoopbackHttpTransport,
  AIHttpClient,
  AIApiClient,
  FallbackAIService,
  BackgroundInferenceWorker,
  ThermalState,
  type IAIService,
  type IHttpTransport,
  type HttpResponse,
  type IThermalThrottleService,
  type ThermalStateHandler,
} from "../src/hosting/index";
import type { ChatMessage } from "../src/models/index";

/** A canned IAIService for endpoint tests. */
function cannedService(over: Partial<IAIService> = {}): IAIService {
  const base: IAIService = {
    isReady: true,
    async startAsync() {},
    async stopAsync() {},
    async askAsync(q: string) {
      return `answer:${q}`;
    },
    async chatAsync(m: readonly ChatMessage[]) {
      return `chat:${m[m.length - 1]?.content ?? ""}`;
    },
    async *streamAsync() {
      yield "al";
      yield "pha";
    },
    async invokeToolAsync(inv) {
      return { toolName: inv.toolName, success: true, result: "R" };
    },
    async agenticChatAsync() {
      return "agentic";
    },
    async submitFeedbackAsync() {},
    async checkForUpgradesAsync() {
      return [];
    },
    async prewarmAsync() {},
    async disposeAsync() {},
  };
  return { ...base, ...over };
}

describe("InProcessEndpoint", () => {
  it("exposes the bound service and clears it on stop", async () => {
    const ep = new InProcessEndpoint();
    assert.equal(ep.serviceAccessor, null);
    const svc = cannedService();
    await ep.startAsync(svc);
    assert.equal(ep.serviceAccessor, svc);
    await ep.stopAsync();
    assert.equal(ep.serviceAccessor, null);
  });
});

describe("HttpLoopbackEndpoint — auth + routes", () => {
  it("rejects requests without the token", async () => {
    const ep = new HttpLoopbackEndpoint({ loopbackToken: "secret" });
    await ep.startAsync(cannedService());
    const resp = await ep.handleAsync("POST", "/butler/ask", {}, JSON.stringify({ question: "hi" }));
    assert.equal(resp.status, 401);
  });

  it("answers /butler/ask as text with a valid token", async () => {
    const ep = new HttpLoopbackEndpoint({ loopbackToken: "secret" });
    await ep.startAsync(cannedService());
    const resp = await ep.handleAsync(
      "POST",
      "/butler/ask",
      { "X-Butler-Token": "secret" },
      JSON.stringify({ question: "hi" }),
    );
    assert.equal(resp.status, 200);
    assert.equal(resp.body, "answer:hi");
  });

  it("returns 405 for non-POST and 404 for unknown routes", async () => {
    const ep = new HttpLoopbackEndpoint({ loopbackToken: "s" });
    await ep.startAsync(cannedService());
    const h = { "X-Butler-Token": "s" };
    assert.equal((await ep.handleAsync("GET", "/butler/ask", h, null)).status, 405);
    assert.equal((await ep.handleAsync("POST", "/nope", h, null)).status, 404);
  });

  it("generates a random token when none is configured and exposes a bound port", async () => {
    const ep = new HttpLoopbackEndpoint({});
    await ep.startAsync(cannedService());
    assert.ok(ep.token && ep.token.length > 0);
    assert.ok(ep.boundPort > 0);
  });
});

describe("AIHttpClient ↔ HttpLoopbackEndpoint round-trip", () => {
  it("chat + stream + tool travel over the loopback transport", async () => {
    const ep = new HttpLoopbackEndpoint({ loopbackToken: "tok" });
    await ep.startAsync(cannedService());
    const client = new AIHttpClient(new LoopbackHttpTransport(ep), "tok");

    assert.equal(await client.askAsync("q"), "answer:q");
    assert.equal(await client.chatAsync([{ role: "user", content: "hey" }]), "chat:hey");

    const pieces: string[] = [];
    for await (const p of client.streamAsync([{ role: "user", content: "go" }])) pieces.push(p);
    assert.deepEqual(pieces, ["al", "pha"]);

    const tool = await client.invokeToolAsync({ toolName: "t", arguments: {} });
    assert.equal(tool.success, true);
    assert.equal(tool.result, "R");
  });

  it("a wrong token is rejected by the endpoint", async () => {
    const ep = new HttpLoopbackEndpoint({ loopbackToken: "right" });
    await ep.startAsync(cannedService());
    const client = new AIHttpClient(new LoopbackHttpTransport(ep), "wrong");
    await assert.rejects(() => client.askAsync("q"));
  });
});

describe("AIApiClient — cloud proxy over a fake transport", () => {
  function scriptedTransport(): { transport: IHttpTransport; seen: string[] } {
    const seen: string[] = [];
    const transport: IHttpTransport = {
      async sendAsync(method, path, _headers, body): Promise<HttpResponse> {
        seen.push(`${method} ${path}`);
        if (path === "api/butler/health")
          return { status: 200, contentType: "text/plain", body: "ok" };
        if (path === "api/butler/ask") {
          const q = JSON.parse(body ?? "{}").question;
          return { status: 200, contentType: "application/json", body: JSON.stringify({ text: `cloud:${q}` }) };
        }
        if (path === "api/butler/stream") {
          const sse = (async function* () {
            yield "data: hello\n\n";
            yield "data: world\n\n";
            yield "data: [DONE]\n\n";
          })();
          return { status: 200, contentType: "text/event-stream", sse };
        }
        return { status: 404, contentType: "text/plain", body: "" };
      },
    };
    return { transport, seen };
  }

  it("health gates isReady, ask returns the text payload", async () => {
    const { transport } = scriptedTransport();
    const client = new AIApiClient(transport, "bearer");
    assert.equal(client.isReady, false);
    await client.startAsync();
    assert.equal(client.isReady, true);
    assert.equal(await client.askAsync("hi"), "cloud:hi");
  });

  it("stream parses SSE data frames and stops at [DONE]", async () => {
    const { transport } = scriptedTransport();
    const client = new AIApiClient(transport);
    const out: string[] = [];
    for await (const p of client.streamAsync([{ role: "user", content: "x" }])) out.push(p);
    assert.deepEqual(out, ["hello", "world"]);
  });
});

describe("FallbackAIService", () => {
  it("uses local when RAM is above threshold", async () => {
    const local = cannedService({ async askAsync() {
      return "local";
    } });
    const cloud = new AIApiClient({
      async sendAsync(): Promise<HttpResponse> {
        return { status: 200, contentType: "text/plain", body: "ok" };
      },
    });
    const fb = new FallbackAIService(local, cloud, 1024, () => 1024 * 1024 * 1024);
    await fb.startAsync();
    assert.equal(await fb.askAsync("q"), "local");
  });

  it("falls back to cloud when RAM is below threshold", async () => {
    const local = cannedService({ async startAsync() {
      throw new Error("should not start local");
    } });
    let cloudAsked = false;
    const cloud = new AIApiClient({
      async sendAsync(_m, path): Promise<HttpResponse> {
        if (path === "api/butler/health") return { status: 200, contentType: "text/plain", body: "ok" };
        cloudAsked = true;
        return { status: 200, contentType: "application/json", body: JSON.stringify({ text: "cloud" }) };
      },
    });
    const fb = new FallbackAIService(local, cloud, 4 * 1024 * 1024 * 1024, () => 1);
    await fb.startAsync();
    assert.equal(await fb.askAsync("q"), "cloud");
    assert.equal(cloudAsked, true);
  });
});

describe("BackgroundInferenceWorker — thermal pause", () => {
  it("pauses on Serious and resumes on Normal", async () => {
    let handler: ThermalStateHandler | null = null;
    const thermal: IThermalThrottleService = {
      currentState: ThermalState.Normal,
      shouldPauseInference: false,
      onStateChanged(h) {
        handler = h;
        return () => {};
      },
      startMonitoring() {},
      stopMonitoring() {},
      dispose() {},
    };
    const worker = new BackgroundInferenceWorker(cannedService(), thermal);
    await worker.startAsync();
    assert.equal(worker.isPaused, false);
    handler!(ThermalState.Serious);
    assert.equal(worker.isPaused, true);
    handler!(ThermalState.Normal);
    assert.equal(worker.isPaused, false);
    await worker.stopAsync();
  });
});
