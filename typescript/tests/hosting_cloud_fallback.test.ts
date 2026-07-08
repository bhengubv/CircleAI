// hosting_cloud_fallback.test.ts
//
// Verifies CircleAI.Hosting.CloudFallback: the CloudFallbackChain start-of-call
// ordering (first configured generator wins; fail-soft frames skipped), the
// BackupBrainOrchestrator mid-turn failover with degrade + cool-down, and the
// OpenAI/Anthropic/Gemini streaming generators parsing their wire formats over
// a fake IHttpTransport.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  CloudFallbackChain,
  BackupBrainOrchestrator,
  BrainHealth,
  FakeConfigurableChatGenerator,
  OpenAiChatGenerator,
  AnthropicChatGenerator,
  GeminiChatGenerator,
  type HttpResponse,
  type IHttpTransport,
} from "../src/hosting/index";
import type { ChatMessage } from "../src/models/index";

const MSG: ChatMessage[] = [{ role: "user", content: "hi" }];

async function collect(gen: AsyncGenerator<string>): Promise<string[]> {
  const out: string[] = [];
  for await (const p of gen) out.push(p);
  return out;
}

describe("CloudFallbackChain", () => {
  it("uses the first configured generator", async () => {
    const chain = new CloudFallbackChain([
      new FakeConfigurableChatGenerator({ reply: "primary" }),
      new FakeConfigurableChatGenerator({ reply: "backup" }),
    ]);
    assert.equal(await chain.generateAsync(MSG), "primary");
  });

  it("skips an unconfigured generator (fail-soft frame) and uses the next", async () => {
    const chain = new CloudFallbackChain([
      new FakeConfigurableChatGenerator({ configured: false }),
      new FakeConfigurableChatGenerator({ reply: "second" }),
    ]);
    assert.equal(await chain.generateAsync(MSG), "second");
    assert.deepEqual(await collect(chain.streamAsync(MSG)), ["second"]);
  });

  it("returns the sentinel when nothing can serve", async () => {
    const chain = new CloudFallbackChain([
      new FakeConfigurableChatGenerator({ configured: false }),
    ]);
    assert.match(await chain.generateAsync(MSG), /no configured generator/);
  });

  it("falls through a generator that throws", async () => {
    const chain = new CloudFallbackChain([
      new FakeConfigurableChatGenerator({ throwOnCall: true }),
      new FakeConfigurableChatGenerator({ reply: "recovered" }),
    ]);
    assert.equal(await chain.generateAsync(MSG), "recovered");
  });
});

describe("BackupBrainOrchestrator", () => {
  it("returns the primary result and keeps it healthy", async () => {
    const orch = new BackupBrainOrchestrator([
      new FakeConfigurableChatGenerator({ reply: "ok", engineLabel: "A" }),
      new FakeConfigurableChatGenerator({ reply: "b", engineLabel: "B" }),
    ]);
    assert.equal(await orch.generateAsync(MSG), "ok");
    assert.equal(orch.statuses[0].health, BrainHealth.Healthy);
  });

  it("fails over to the backup and degrades the primary after the threshold", async () => {
    let now = 0;
    const orch = new BackupBrainOrchestrator(
      [
        new FakeConfigurableChatGenerator({ throwOnCall: true, engineLabel: "bad" }),
        new FakeConfigurableChatGenerator({ reply: "backup", engineLabel: "good" }),
      ],
      { degradedAfterFailures: 1 },
      () => now,
    );
    const result = await orch.generateAsync(MSG);
    assert.equal(result, "backup");
    // Primary recorded a failure ≥ threshold → degraded.
    assert.equal(orch.statuses[0].health, BrainHealth.Degraded);

    // After the cool-down elapses it becomes CoolingDown (half-open).
    now += 40_000;
    assert.equal(orch.statuses[0].health, BrainHealth.CoolingDown);
  });

  it("returns the all-failed sentinel when every brain throws", async () => {
    const orch = new BackupBrainOrchestrator([
      new FakeConfigurableChatGenerator({ throwOnCall: true }),
      new FakeConfigurableChatGenerator({ throwOnCall: true }),
    ]);
    assert.equal(await orch.generateAsync(MSG), "[All brains failed.]");
  });
});

describe("Cloud generators over a fake transport", () => {
  function sse(...frames: string[]): HttpResponse {
    const gen = (async function* () {
      for (const f of frames) yield f;
    })();
    return { status: 200, contentType: "text/event-stream", sse: gen };
  }

  it("OpenAI generator parses choices[].delta.content", async () => {
    const transport: IHttpTransport = {
      async sendAsync(_m, path): Promise<HttpResponse> {
        assert.equal(path, "/v1/chat/completions");
        return sse(
          'data: {"choices":[{"delta":{"content":"Hel"}}]}\n',
          'data: {"choices":[{"delta":{"content":"lo"}}]}\n',
          "data: [DONE]\n",
        );
      },
    };
    const gen = new OpenAiChatGenerator(transport, { apiKey: "k", model: "gpt-4o-mini" });
    assert.equal(gen.isConfigured, true);
    assert.equal(gen.engineLabel, "OpenAI · gpt-4o-mini");
    assert.equal(await gen.generateAsync(MSG), "Hello");
  });

  it("OpenAI generator emits a fail-soft frame when the key is missing", async () => {
    const transport: IHttpTransport = {
      async sendAsync(): Promise<HttpResponse> {
        throw new Error("should not be called");
      },
    };
    const gen = new OpenAiChatGenerator(transport, { apiKey: null });
    assert.equal(gen.isConfigured, false);
    const out = await collect(gen.streamAsync(MSG));
    assert.equal(out.length, 1);
    assert.match(out[0], /not configured/);
  });

  it("Anthropic generator parses content_block_delta.delta.text and splits system", async () => {
    let sentBody: unknown = null;
    const transport: IHttpTransport = {
      async sendAsync(_m, path, _h, body): Promise<HttpResponse> {
        assert.equal(path, "/v1/messages");
        sentBody = JSON.parse(body ?? "{}");
        return sse(
          'data: {"type":"content_block_delta","delta":{"text":"Hi"}}\n',
          'data: {"type":"message_stop"}\n',
        );
      },
    };
    const gen = new AnthropicChatGenerator(transport, { apiKey: "k" });
    const out = await gen.generateAsync([
      { role: "system", content: "be terse" },
      { role: "user", content: "hi" },
    ]);
    assert.equal(out, "Hi");
    assert.equal((sentBody as { system?: string }).system, "be terse");
  });

  it("Gemini generator parses candidates[].content.parts[].text", async () => {
    const transport: IHttpTransport = {
      async sendAsync(_m, path): Promise<HttpResponse> {
        assert.match(path, /streamGenerateContent\?alt=sse&key=/);
        return sse(
          'data: {"candidates":[{"content":{"parts":[{"text":"G'  + '"}]}}]}\n',
          'data: {"candidates":[{"content":{"parts":[{"text":"emini"}]}}]}\n',
        );
      },
    };
    const gen = new GeminiChatGenerator(transport, { apiKey: "k" });
    assert.equal(await gen.generateAsync(MSG), "Gemini");
  });
});
