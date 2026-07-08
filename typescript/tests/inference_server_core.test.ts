// inference_server_core.test.ts
//
// Exercises the inference-server building blocks: LocalProcessInferenceBridge,
// ApiKeyAuthHandler, InferenceServerModelRegistry, AdmissionControl +
// ServerCounters, ModelLifecycleManager, InMemoryCompanionSessionResolver,
// NativeRuntimeStatus, InMemoryBridgeFactory.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { DeterministicChatGenerator } from '../src/inference/generator';
import {
  LocalProcessInferenceBridge,
  InferenceStatus,
  BackendKind,
  CapabilityTier,
  ModelFormat,
  parseBackendKind,
  parseCapabilityTier,
  type InferenceRequest,
  type ModelDescriptor,
} from '../src/inference/server/bridge';
import { ApiKeyAuthHandler, AuthResultKind, AuthSchemes, tryMatchKey } from '../src/inference/server/auth';
import { defaultInferenceServerOptions } from '../src/inference/server/options';
import {
  ServerCounters,
  AdmissionControl,
  InferenceServerModelRegistry,
  NativeRuntimeStatus,
  type NativeRuntimePaths,
} from '../src/inference/server/runtime';
import {
  ModelLifecycleManager,
  InMemoryCapacityProbe,
  LoadOutcome,
  UnloadOutcome,
} from '../src/inference/server/lifecycle';
import { InMemoryBridgeFactory, approxMemoryFromTier } from '../src/inference/server/bridge_factory';
import { InMemoryCompanionSessionResolver } from '../src/inference/server/resolver';
import type { ICompanionSessionFactory } from '../src/companion/session_factory';
import type { ICompanionSession } from '../src/companion/index';
import { InterfaceKind } from '../src/companion/index';
import type { ITextEmbedder } from '../src/embeddings/index';

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

function request(modelId: string, prompt: string, maxTokens = 512): InferenceRequest {
  return {
    id: 'req-1',
    modelId,
    prompt,
    maxOutputTokens: maxTokens,
    temperature: 0.7,
    topP: 0.9,
    stopSequences: [],
    metadata: {},
    requestedAt: '2026-01-01T00:00:00.000Z',
  };
}

describe('LocalProcessInferenceBridge', () => {
  it('completes a request through the wrapped generator', async () => {
    const bridge = new LocalProcessInferenceBridge(new DeterministicChatGenerator(), descriptor('m'));
    const resp = await bridge.complete(request('m', 'hi'));
    assert.equal(resp.status, InferenceStatus.Completed);
    // The bridge wraps request.prompt in a single user ChatMessage as-is, so
    // the deterministic generator echoes it verbatim.
    assert.equal(resp.outputText, 'You said: hi');
    assert.ok(resp.promptTokenCount > 0);
  });

  it('fails when the model id does not match the descriptor', async () => {
    const bridge = new LocalProcessInferenceBridge(new DeterministicChatGenerator(), descriptor('m'));
    const resp = await bridge.complete(request('other', 'hi'));
    assert.equal(resp.status, InferenceStatus.Failed);
    assert.ok(resp.failureMessage?.includes('is not loaded'));
  });

  it('surfaces reasoning fragments when the generator emits them', async () => {
    const gen = new DeterministicChatGenerator({ emitReasoning: true });
    const bridge = new LocalProcessInferenceBridge(gen, descriptor('m'));
    const frags: string[] = [];
    for await (const f of bridge.streamFragments(request('m', 'q'))) frags.push(`${f.kind}:${f.text}`);
    assert.ok(frags.some((s) => s.startsWith('1:'))); // a Reasoning fragment
    assert.ok(frags.some((s) => s.startsWith('0:'))); // a Content fragment
  });

  it('reports one loaded model + device capabilities', async () => {
    const bridge = new LocalProcessInferenceBridge(new DeterministicChatGenerator(), descriptor('m'));
    assert.deepEqual((await bridge.listLoadedModels()).map((d) => d.modelId), ['m']);
    assert.equal(await bridge.isModelLoaded('m'), true);
    assert.equal(await bridge.isModelLoaded('x'), false);
    const caps = await bridge.getDeviceCapabilities();
    assert.equal(caps.hasTransportLayerEncryption, true);
  });
});

describe('ApiKeyAuthHandler', () => {
  it('succeeds with an anonymous principal when auth is disabled', () => {
    const opts = defaultInferenceServerOptions({ auth: { apiKey: { enabled: false } } });
    const handler = new ApiKeyAuthHandler(() => opts);
    const r = handler.authenticate({});
    assert.equal(r.kind, AuthResultKind.Success);
    assert.ok(r.claims?.some((c) => c.type === 'auth_disabled' && c.value === 'true'));
  });

  it('returns NoResult when no key is presented', () => {
    const opts = defaultInferenceServerOptions({ auth: { apiKey: { keys: ['secret'] } } });
    const handler = new ApiKeyAuthHandler(() => opts);
    assert.equal(handler.authenticate({}).kind, AuthResultKind.NoResult);
  });

  it('fails a wrong key and succeeds a right key (case-insensitive header)', () => {
    const opts = defaultInferenceServerOptions({ auth: { apiKey: { keys: ['right'], headerName: 'X-CircleAI-Api-Key' } } });
    const handler = new ApiKeyAuthHandler(() => opts);
    assert.equal(handler.authenticate({ 'x-circleai-api-key': 'wrong' }).kind, AuthResultKind.Fail);
    const ok = handler.authenticate({ 'X-CircleAI-Api-Key': 'right' });
    assert.equal(ok.kind, AuthResultKind.Success);
    assert.ok(ok.claims?.some((c) => c.type === 'scheme' && c.value === AuthSchemes.ApiKey));
  });

  it('tryMatchKey is length-aware and constant-time-safe', () => {
    assert.equal(tryMatchKey('abc', ['abc']), true);
    assert.equal(tryMatchKey('abc', ['abcd']), false);
    assert.equal(tryMatchKey('abc', []), false);
  });
});

describe('backend/tier parsing', () => {
  it('parses case-insensitively and rejects unknowns', () => {
    assert.equal(parseBackendKind('cuda'), BackendKind.Cuda);
    assert.equal(parseBackendKind('CoreML'), BackendKind.CoreML);
    assert.equal(parseBackendKind('nope'), null);
    assert.equal(parseCapabilityTier('tier2_medium'), CapabilityTier.Tier2_Medium);
    assert.equal(parseCapabilityTier('nope'), null);
  });
});

describe('ServerCounters + AdmissionControl', () => {
  it('admits up to the cap then rejects', () => {
    const counters = new ServerCounters();
    const gate = new AdmissionControl(defaultInferenceServerOptions({ maxConcurrentRequests: 2 }), counters);
    const a = gate.tryEnter();
    const b = gate.tryEnter();
    const c = gate.tryEnter();
    assert.ok(a && b);
    assert.equal(c, null);
    assert.equal(counters.activeRequests, 2);
    assert.equal(counters.rejectedRequests, 1);
    a!.release();
    assert.equal(counters.activeRequests, 1);
    const d = gate.tryEnter();
    assert.ok(d);
  });

  it('release is idempotent', () => {
    const counters = new ServerCounters();
    const gate = new AdmissionControl(defaultInferenceServerOptions({ maxConcurrentRequests: 1 }), counters);
    const slot = gate.tryEnter()!;
    slot.release();
    slot.release();
    assert.equal(counters.activeRequests, 0);
  });
});

describe('InferenceServerModelRegistry', () => {
  it('registers, resolves, and lists chat + embedder models', () => {
    const reg = new InferenceServerModelRegistry();
    const bridge = new LocalProcessInferenceBridge(new DeterministicChatGenerator(), descriptor('chat'));
    const embedder: ITextEmbedder = { async generateAsync() { return new Float32Array([1]); } };
    reg.register('chat', bridge);
    reg.registerEmbedder('emb', embedder);
    assert.equal(reg.resolve('chat'), bridge);
    assert.equal(reg.resolveEmbedder('emb'), embedder);
    assert.equal(reg.resolve('emb'), null);
    assert.deepEqual([...reg.chatModelIds()], ['chat']);
    assert.deepEqual([...reg.allModelIds()].sort(), ['chat', 'emb']);
    assert.equal(reg.deregister('chat'), true);
    assert.equal(reg.resolve('chat'), null);
  });
});

describe('ModelLifecycleManager', () => {
  const capacity = { totalPhysicalMemoryBytes: 8 * 1024 * 1024 * 1024, gpuVramBytes: 4 * 1024 * 1024 * 1024 };

  function makeManager() {
    const registry = new InferenceServerModelRegistry();
    const mgr = new ModelLifecycleManager(registry, new InMemoryCapacityProbe(capacity));
    const factory = new InMemoryBridgeFactory();
    return { registry, mgr, factory };
  }

  it('loads a model and registers its bridge', async () => {
    const { registry, mgr, factory } = makeManager();
    const result = await mgr.load({
      modelId: 'qwen',
      backend: BackendKind.Cpu,
      requestedTier: CapabilityTier.Tier1_Small,
      vramRequiredBytes: 0,
      ramRequiredBytes: 1024,
      bridgeFactory: (s) => factory.create('qwen', BackendKind.Cpu, CapabilityTier.Tier1_Small, s),
    });
    assert.equal(result.outcome, LoadOutcome.Loaded);
    assert.ok(registry.resolve('qwen'));
    assert.equal(mgr.totalAllocatedRamBytes, 1024);
  });

  it('is idempotent for an already-loaded model', async () => {
    const { mgr, factory } = makeManager();
    const desc = {
      modelId: 'qwen',
      backend: BackendKind.Cpu,
      requestedTier: CapabilityTier.Tier1_Small,
      vramRequiredBytes: 0,
      ramRequiredBytes: 1024,
      bridgeFactory: (s?: AbortSignal) => factory.create('qwen', BackendKind.Cpu, CapabilityTier.Tier1_Small, s),
    };
    await mgr.load(desc);
    const again = await mgr.load(desc);
    assert.equal(again.outcome, LoadOutcome.AlreadyLoaded);
  });

  it('rejects a load that exceeds RAM', async () => {
    const { mgr, factory } = makeManager();
    const result = await mgr.load({
      modelId: 'huge',
      backend: BackendKind.Cpu,
      requestedTier: CapabilityTier.Tier4_Frontier,
      vramRequiredBytes: 0,
      ramRequiredBytes: 999 * 1024 * 1024 * 1024,
      bridgeFactory: (s) => factory.create('huge', BackendKind.Cpu, CapabilityTier.Tier4_Frontier, s),
    });
    assert.equal(result.outcome, LoadOutcome.InsufficientRam);
  });

  it('rejects a GPU load that exceeds VRAM', async () => {
    const { mgr, factory } = makeManager();
    const result = await mgr.load({
      modelId: 'gpu',
      backend: BackendKind.Cuda,
      requestedTier: CapabilityTier.Tier3_Large,
      vramRequiredBytes: 999 * 1024 * 1024 * 1024,
      ramRequiredBytes: 0,
      bridgeFactory: (s) => factory.create('gpu', BackendKind.Cuda, CapabilityTier.Tier3_Large, s),
    });
    assert.equal(result.outcome, LoadOutcome.InsufficientVram);
  });

  it('reports FactoryFailed and rolls the reservation back', async () => {
    const { mgr } = makeManager();
    const result = await mgr.load({
      modelId: 'boom',
      backend: BackendKind.Cpu,
      requestedTier: CapabilityTier.Tier1_Small,
      vramRequiredBytes: 0,
      ramRequiredBytes: 1,
      bridgeFactory: async () => {
        throw new Error('kaboom');
      },
    });
    assert.equal(result.outcome, LoadOutcome.FactoryFailed);
    assert.equal(mgr.list().length, 0);
  });

  it('unloads a loaded model and reports NotLoaded otherwise', async () => {
    const { mgr, factory } = makeManager();
    await mgr.load({
      modelId: 'qwen',
      backend: BackendKind.Cpu,
      requestedTier: CapabilityTier.Tier1_Small,
      vramRequiredBytes: 0,
      ramRequiredBytes: 1,
      bridgeFactory: (s) => factory.create('qwen', BackendKind.Cpu, CapabilityTier.Tier1_Small, s),
    });
    assert.equal(await mgr.unload('qwen'), UnloadOutcome.Unloaded);
    assert.equal(await mgr.unload('qwen'), UnloadOutcome.NotLoaded);
  });
});

describe('approxMemoryFromTier', () => {
  it('maps tiers to the C# byte constants', () => {
    const GiB = 1024 * 1024 * 1024;
    assert.equal(approxMemoryFromTier(CapabilityTier.Tier0_Tiny), 1 * GiB);
    assert.equal(approxMemoryFromTier(CapabilityTier.Tier2_Medium), 6 * GiB);
    assert.equal(approxMemoryFromTier(CapabilityTier.Tier4_Frontier), 24 * GiB);
  });
});

describe('InMemoryCompanionSessionResolver', () => {
  function fakeSession(id: string, identity: string): ICompanionSession {
    return {
      sessionId: id,
      identityId: identity,
      interface: InterfaceKind.Web,
      async sendAsync() { return 'ok'; },
      async *streamAsync() { yield 'ok'; },
      async agentAsync() { return 'ok'; },
      getContext() { throw new Error('n/a'); },
      async refreshContextAsync() {},
      history: [],
      async signalFeedbackAsync() {},
      onProactiveMessageReady: null,
    };
  }

  it('single-flights construction per (session, identity) and caches', async () => {
    let created = 0;
    const factory: ICompanionSessionFactory = {
      async createAsync(identityId, _surface) {
        created++;
        return fakeSession('s1', identityId);
      },
    };
    const resolver = new InMemoryCompanionSessionResolver(factory);
    const [a, b] = await Promise.all([resolver.resolve('s1', 'u1'), resolver.resolve('s1', 'u1')]);
    assert.equal(a, b);
    assert.equal(created, 1);
    assert.equal(resolver.cachedSessionCount, 1);
  });

  it('drops a poisoned slot so the next caller can retry', async () => {
    let attempts = 0;
    const factory: ICompanionSessionFactory = {
      async createAsync(identityId) {
        attempts++;
        if (attempts === 1) throw new Error('first fails');
        return fakeSession('s1', identityId);
      },
    };
    const resolver = new InMemoryCompanionSessionResolver(factory);
    await assert.rejects(() => resolver.resolve('s1', 'u1'));
    const ok = await resolver.resolve('s1', 'u1');
    assert.ok(ok);
    assert.equal(attempts, 2);
  });

  it('returns null for blank ids', async () => {
    const factory: ICompanionSessionFactory = { async createAsync() { throw new Error('unused'); } };
    const resolver = new InMemoryCompanionSessionResolver(factory);
    assert.equal(await resolver.resolve('', 'u'), null);
    assert.equal(await resolver.resolve('s', ''), null);
  });
});

describe('NativeRuntimeStatus', () => {
  it('starts null and records the latest paths', () => {
    const status = new NativeRuntimeStatus();
    assert.equal(status.latest === null, true);
    const paths: NativeRuntimePaths = {
      rid: 'win-x64',
      expectedNativeDir: '/n',
      mnnBridgePath: '/n/mnnbridge.dll',
      mnnBridgeLoaded: true,
      mnnCoreFetchedPath: '/c/MNN.dll',
      mnnCoreFlattenedPath: '/n/MNN.dll',
      mnnCorePreloaded: true,
      flattenError: null,
      preloadError: null,
    };
    status.update(paths);
    assert.equal(status.latest?.rid, 'win-x64');
  });
});
