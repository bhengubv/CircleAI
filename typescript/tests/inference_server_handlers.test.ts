// inference_server_handlers.test.ts
//
// Exercises the in-memory OpenAI-compatible endpoint handlers end to end:
// /v1/chat/completions (non-stream + SSE), /v1/embeddings, /v1/companion/turn,
// and /v1/admin/models/{load,unload} + /v1/admin/lifecycle.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { DeterministicChatGenerator } from '../src/inference/generator';
import {
  LocalProcessInferenceBridge,
  ModelFormat,
  type ModelDescriptor,
} from '../src/inference/server/bridge';
import {
  InferenceServerHandler,
  StatusCodes,
  buildInferenceRequest,
  normaliseInput,
  type InferenceServerHandlerDeps,
} from '../src/inference/server/handlers';
import { defaultInferenceServerOptions } from '../src/inference/server/options';
import {
  ServerCounters,
  AdmissionControl,
  InferenceServerModelRegistry,
} from '../src/inference/server/runtime';
import { ModelLifecycleManager, InMemoryCapacityProbe, LoadOutcome } from '../src/inference/server/lifecycle';
import { InMemoryBridgeFactory } from '../src/inference/server/bridge_factory';
import { InMemoryCompanionSessionResolver } from '../src/inference/server/resolver';
import type { ICompanionSessionFactory } from '../src/companion/session_factory';
import type { ICompanionSession } from '../src/companion/index';
import { InterfaceKind } from '../src/companion/index';
import type { ITextEmbedder } from '../src/embeddings/index';
import type { ChatCompletionResponse, EmbeddingsResponse } from '../src/inference/server/openai';

function descriptor(modelId: string): ModelDescriptor {
  return {
    modelId,
    version: '1.0.0',
    format: ModelFormat.Gguf,
    contextWindowTokens: 4096,
    vocabSize: 151936,
    parameterCount: 0,
    quantisationLabel: null,
    approximateMemoryBytes: 1024,
  };
}

function fakeSession(id: string, identity: string, reply: string): ICompanionSession {
  const history: { role: string; content: string; timestamp: Date }[] = [];
  return {
    sessionId: id,
    identityId: identity,
    interface: InterfaceKind.Web,
    async sendAsync(m: string) {
      history.push({ role: 'user', content: m, timestamp: new Date() });
      history.push({ role: 'assistant', content: reply, timestamp: new Date() });
      return reply;
    },
    async *streamAsync() {
      yield reply.slice(0, 2);
      yield reply.slice(2);
    },
    async agentAsync() {
      return `agentic:${reply}`;
    },
    getContext() { throw new Error('n/a'); },
    async refreshContextAsync() {},
    get history() { return history; },
    async signalFeedbackAsync() {},
    onProactiveMessageReady: null,
  };
}

function buildDeps(overrides: Partial<InferenceServerHandlerDeps> = {}): {
  deps: InferenceServerHandlerDeps;
  registry: InferenceServerModelRegistry;
} {
  const registry = new InferenceServerModelRegistry();
  const counters = new ServerCounters();
  const options = defaultInferenceServerOptions();
  const admission = new AdmissionControl(options, counters);
  const capacity = { totalPhysicalMemoryBytes: 8 * 1024 * 1024 * 1024, gpuVramBytes: 4 * 1024 * 1024 * 1024 };
  const lifecycle = new ModelLifecycleManager(registry, new InMemoryCapacityProbe(capacity));
  const bridgeFactory = new InMemoryBridgeFactory();
  const sessionFactory: ICompanionSessionFactory = {
    async createAsync(identityId) {
      return fakeSession('sess', identityId, 'companion-reply');
    },
  };
  const companionResolver = new InMemoryCompanionSessionResolver(sessionFactory);
  const deps: InferenceServerHandlerDeps = {
    registry,
    admission,
    counters,
    options,
    lifecycle,
    bridgeFactory,
    companionResolver,
    ...overrides,
  };
  return { deps, registry };
}

function readSse(frames: Uint8Array[]): string {
  return frames.map((b) => new TextDecoder().decode(b)).join('');
}

describe('buildInferenceRequest + normaliseInput', () => {
  it('joins messages with role markers', () => {
    const req = buildInferenceRequest({
      model: 'm',
      messages: [{ role: 'user', content: 'hi' }],
      max_tokens: 10,
    });
    assert.equal(req.prompt, '<|user|>\nhi\n<|end|>');
    assert.equal(req.maxOutputTokens, 10);
  });

  it('normalises string and array input', () => {
    assert.deepEqual(normaliseInput('x'), { ok: true, inputs: ['x'] });
    assert.deepEqual(normaliseInput(['a', 'b']), { ok: true, inputs: ['a', 'b'] });
    const empty = normaliseInput([]);
    assert.equal(empty.ok, false);
  });
});

describe('chat completions handler', () => {
  it('400s on missing model / messages', async () => {
    const { deps } = buildDeps();
    const h = new InferenceServerHandler(deps);
    assert.equal((await h.chatCompletions({ model: '', messages: [] })).statusCode, StatusCodes.BadRequest);
    assert.equal((await h.chatCompletions({ model: 'm', messages: [] })).statusCode, StatusCodes.BadRequest);
  });

  it('404s when the model is not registered', async () => {
    const { deps } = buildDeps();
    const h = new InferenceServerHandler(deps);
    const r = await h.chatCompletions({ model: 'ghost', messages: [{ role: 'user', content: 'hi' }] });
    assert.equal(r.statusCode, StatusCodes.NotFound);
  });

  it('returns an OpenAI chat.completion for a registered model', async () => {
    const { deps, registry } = buildDeps();
    registry.register('m', new LocalProcessInferenceBridge(new DeterministicChatGenerator(), descriptor('m')));
    const h = new InferenceServerHandler(deps);
    const r = await h.chatCompletions({ model: 'm', messages: [{ role: 'user', content: 'hi' }] });
    assert.equal(r.statusCode, StatusCodes.Ok);
    const body = r.body as ChatCompletionResponse;
    assert.equal(body.object, 'chat.completion');
    assert.equal(body.choices[0]!.message.role, 'assistant');
    assert.ok(body.choices[0]!.message.content.length > 0);
    assert.equal(body.choices[0]!.finish_reason, 'stop');
    assert.equal(body.usage.total_tokens, body.usage.prompt_tokens + body.usage.completion_tokens);
  });

  it('streams OpenAI SSE frames ending with [DONE]', async () => {
    const { deps, registry } = buildDeps();
    registry.register('m', new LocalProcessInferenceBridge(new DeterministicChatGenerator(), descriptor('m')));
    const h = new InferenceServerHandler(deps);
    const frames: Uint8Array[] = [];
    const r = await h.chatCompletions(
      { model: 'm', messages: [{ role: 'user', content: 'stream please' }], stream: true },
      (bytes) => {
        frames.push(bytes);
      },
    );
    assert.equal(r.streamed, true);
    const wire = readSse(frames);
    assert.ok(wire.startsWith('data: '));
    assert.ok(wire.includes('"role":"assistant"'));
    assert.ok(wire.includes('"finish_reason":"stop"'));
    assert.ok(wire.trimEnd().endsWith('data: [DONE]'));
    // Reassemble the streamed content.
    const contentDeltas = [...wire.matchAll(/"content":"([^"]*)"/g)].map((m) => m[1]).join('');
    assert.ok(contentDeltas.length > 0);
  });

  it('503s when the admission gate is saturated', async () => {
    const { deps, registry } = buildDeps({
      admission: new AdmissionControl(defaultInferenceServerOptions({ maxConcurrentRequests: 1 }), new ServerCounters()),
    });
    registry.register('m', new LocalProcessInferenceBridge(new DeterministicChatGenerator(), descriptor('m')));
    // Occupy the single slot.
    const held = deps.admission.tryEnter();
    assert.ok(held);
    const h = new InferenceServerHandler(deps);
    const r = await h.chatCompletions({ model: 'm', messages: [{ role: 'user', content: 'hi' }] });
    assert.equal(r.statusCode, StatusCodes.ServiceUnavailable);
    assert.equal(r.headers?.['Retry-After'], '1');
    held!.release();
  });
});

describe('embeddings handler', () => {
  const embedder: ITextEmbedder = {
    async generateAsync(text: string) {
      // Deterministic 2-dim vector from length.
      return new Float32Array([text.length, text.length * 2]);
    },
  };

  it('returns an embeddings list for a registered embedder', async () => {
    const { deps, registry } = buildDeps();
    registry.registerEmbedder('emb', embedder);
    const h = new InferenceServerHandler(deps);
    const r = await h.embeddings({ model: 'emb', input: ['ab', 'cde'] });
    assert.equal(r.statusCode, StatusCodes.Ok);
    const body = r.body as EmbeddingsResponse;
    assert.equal(body.object, 'list');
    assert.equal(body.data.length, 2);
    assert.deepEqual(body.data[0]!.embedding, [2, 4]);
    assert.deepEqual(body.data[1]!.embedding, [3, 6]);
    assert.equal(body.usage.completion_tokens, 0);
  });

  it('404s for an unregistered embedding model and 400s bad input', async () => {
    const { deps, registry } = buildDeps();
    registry.registerEmbedder('emb', embedder);
    const h = new InferenceServerHandler(deps);
    assert.equal((await h.embeddings({ model: 'ghost', input: 'x' })).statusCode, StatusCodes.NotFound);
    assert.equal((await h.embeddings({ model: 'emb', input: [] })).statusCode, StatusCodes.BadRequest);
  });
});

describe('companion turn handler', () => {
  it('validates required fields', async () => {
    const { deps } = buildDeps();
    const h = new InferenceServerHandler(deps);
    const r = await h.companionTurn({ sessionId: '', identityId: 'u', message: 'hi' });
    assert.equal(r.statusCode, StatusCodes.BadRequest);
  });

  it('returns a reply and the turn index', async () => {
    const { deps } = buildDeps();
    const h = new InferenceServerHandler(deps);
    const r = await h.companionTurn({ sessionId: 's', identityId: 'u', message: 'hello' });
    assert.equal(r.statusCode, StatusCodes.Ok);
    const body = r.body as { reply: string; turnIndex: number; agentic: boolean };
    assert.equal(body.reply, 'companion-reply');
    assert.equal(body.turnIndex, 2);
    assert.equal(body.agentic, false);
  });

  it('routes agentic turns to agentAsync', async () => {
    const { deps } = buildDeps();
    const h = new InferenceServerHandler(deps);
    const r = await h.companionTurn({ sessionId: 's', identityId: 'u', message: 'hello', agentic: true });
    const body = r.body as { reply: string };
    assert.equal(body.reply, 'agentic:companion-reply');
  });

  it('streams companion deltas over SSE', async () => {
    const { deps } = buildDeps();
    const h = new InferenceServerHandler(deps);
    const frames: Uint8Array[] = [];
    const r = await h.companionTurn(
      { sessionId: 's', identityId: 'u', message: 'hi', stream: true },
      (b) => {
        frames.push(b);
      },
    );
    assert.equal(r.streamed, true);
    const wire = readSse(frames);
    assert.ok(wire.includes('"delta"'));
    assert.ok(wire.trimEnd().endsWith('data: [DONE]'));
  });
});

describe('admin handlers', () => {
  it('loads then unloads a model and lists the footprint', async () => {
    const { deps, registry } = buildDeps();
    const h = new InferenceServerHandler(deps);

    const loadR = await h.adminLoad({ modelId: 'qwen', backend: 'Cpu', tier: 'Tier1_Small', ramRequiredBytes: 1024 });
    assert.equal(loadR.statusCode, StatusCodes.Ok);
    assert.equal((loadR.body as { outcome: string }).outcome, LoadOutcome[LoadOutcome.Loaded]);
    assert.ok(registry.resolve('qwen'));

    const lifecycle = h.adminLifecycle();
    assert.equal((lifecycle.body as { loaded: unknown[] }).loaded.length, 1);

    const unloadR = await h.adminUnload('qwen');
    assert.equal(unloadR.statusCode, StatusCodes.Ok);
    const notFound = await h.adminUnload('qwen');
    assert.equal(notFound.statusCode, StatusCodes.NotFound);
  });

  it('400s on unknown backend / tier', async () => {
    const { deps } = buildDeps();
    const h = new InferenceServerHandler(deps);
    assert.equal((await h.adminLoad({ modelId: 'm', backend: 'Quantum' })).statusCode, StatusCodes.BadRequest);
    assert.equal((await h.adminLoad({ modelId: 'm', tier: 'Tier9' })).statusCode, StatusCodes.BadRequest);
    assert.equal((await h.adminLoad({ modelId: '' })).statusCode, StatusCodes.BadRequest);
  });

  it('507s when the load exceeds RAM', async () => {
    const { deps } = buildDeps();
    const h = new InferenceServerHandler(deps);
    const r = await h.adminLoad({ modelId: 'huge', backend: 'Cpu', ramRequiredBytes: 999 * 1024 * 1024 * 1024 });
    assert.equal(r.statusCode, StatusCodes.InsufficientStorage);
  });
});
