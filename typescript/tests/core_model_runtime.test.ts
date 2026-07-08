// core_model_runtime.test.ts
//
// Exercises the CircleAI.Core model-management runtime port: sources,
// downloaders, loaders, managers, SafeModelHandle, PlatformInterop, CircleEngine.
// The network is the injected InMemoryTransport, so everything is offline +
// deterministic.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { promises as fs } from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { createHash } from 'node:crypto';
import {
  InMemoryTransport,
  ModelScopeSource,
  HuggingFaceSource,
  ModelDownloader,
  LocalModelLoader,
  LocalModelManager,
  SafeModelHandle,
  PlatformInterop,
  CircleEngine,
  isBundleEntry,
  type ModelRegistryMap,
  type INativeModelBinding,
  type IModelLoader,
} from '../src/core/index';

async function tmpDir(prefix: string): Promise<string> {
  return fs.mkdtemp(path.join(os.tmpdir(), prefix));
}

function sha256Hex(bytes: Uint8Array): string {
  return createHash('sha256').update(bytes).digest('hex');
}

const MODEL_URL = 'https://modelscope.cn/models/test/model.bin';
const MODEL_BYTES = new TextEncoder().encode('the-model-weights');

function singleFileRegistry(): ModelRegistryMap {
  return {
    'test-model': {
      fileName: 'model.bin',
      primaryUrl: MODEL_URL,
      fallbackUrl: null,
      checksum: 'sha256:' + sha256Hex(MODEL_BYTES),
      sizeBytes: MODEL_BYTES.length,
      version: '1.0',
    },
  };
}

describe('ModelScopeSource', () => {
  it('downloads via the injected transport', async () => {
    const transport = new InMemoryTransport().put(MODEL_URL, MODEL_BYTES);
    const source = new ModelScopeSource(transport);
    assert.equal(source.name, 'ModelScope');
    assert.equal(await source.isAvailableAsync(), true);

    const dir = await tmpDir('ms-');
    const out = path.join(dir, 'model.bin');
    await source.downloadAsync(MODEL_URL, out);
    const got = new Uint8Array(await fs.readFile(out));
    assert.equal(sha256Hex(got), sha256Hex(MODEL_BYTES));
  });

  it('rejects non-modelscope hosts', async () => {
    const source = new ModelScopeSource(new InMemoryTransport());
    await assert.rejects(
      () => source.downloadAsync('https://huggingface.co/x/y.bin', 'out.bin'),
      /host must be on modelscope\.cn/,
    );
  });
});

describe('HuggingFaceSource', () => {
  it('is a removed tombstone that throws on construction', () => {
    assert.throws(() => new HuggingFaceSource(), /removed/i);
  });
});

describe('ModelDownloader', () => {
  it('requires at least one source', () => {
    assert.throws(() => new ModelDownloader([]), /At least one model source/);
  });

  it('downloads a single-file model into the target directory', async () => {
    const transport = new InMemoryTransport().put(MODEL_URL, MODEL_BYTES);
    const dl = new ModelDownloader([new ModelScopeSource(transport)], singleFileRegistry());

    const dir = await tmpDir('dl-');
    const seen: number[] = [];
    dl.onProgress((p) => seen.push(p.bytesReceived));
    await dl.downloadModelAsync('test-model', dir);

    const out = new Uint8Array(await fs.readFile(path.join(dir, 'model.bin')));
    assert.equal(sha256Hex(out), sha256Hex(MODEL_BYTES));
    assert.ok(seen.length >= 1 && seen[seen.length - 1] === MODEL_BYTES.length);
  });

  it('falls through to the fallback source when the primary fails', async () => {
    const fallbackUrl = 'https://modelscope.cn/cdn/model.bin';
    const registry: ModelRegistryMap = {
      m: {
        fileName: 'model.bin',
        primaryUrl: 'https://modelscope.cn/primary/missing.bin', // not registered → fails
        fallbackUrl,
      },
    };
    const transport = new InMemoryTransport().put(fallbackUrl, MODEL_BYTES);
    const dl = new ModelDownloader([new ModelScopeSource(transport)], registry);
    const dir = await tmpDir('fb-');
    const winner = await dl.downloadFromCandidatesAsync(
      [registry.m.primaryUrl!, fallbackUrl],
      path.join(dir, 'model.bin'),
    );
    assert.equal(winner, 'ModelScope');
  });

  it('throws for unknown models and steers bundles elsewhere', async () => {
    const dl = new ModelDownloader([new ModelScopeSource(new InMemoryTransport())], {
      bundle: {
        repo: 'test/repo',
        bundleFiles: [{ name: 'llm.mnn.weight', sha256: 'abc', sizeBytes: 10 }],
      },
    });
    const dir = await tmpDir('bdl-');
    await assert.rejects(() => dl.downloadModelAsync('nope', dir), /not in the registry/);
    await assert.rejects(() => dl.downloadModelAsync('bundle', dir), /multi-file MNN bundle/);
  });

  it('reports all-sources-failed when nothing matches', async () => {
    const dl = new ModelDownloader([new ModelScopeSource(new InMemoryTransport())]);
    await assert.rejects(
      () => dl.downloadFromCandidatesAsync(['https://example.com/x.bin'], 'out.bin'),
      /All model sources failed/,
    );
  });
});

describe('LocalModelLoader', () => {
  it('downloads + checksum-verifies a single-file model', async () => {
    const transport = new InMemoryTransport().put(MODEL_URL, MODEL_BYTES);
    const dir = await tmpDir('lml-');
    const loader = new LocalModelLoader(singleFileRegistry(), dir, transport);

    let progress = 0;
    const p = await loader.downloadModelAsync('test-model', (f) => (progress = f));
    assert.equal(p, path.join(dir, 'model.bin'));
    assert.equal(progress, 1);
    assert.equal(await loader.modelExists('test-model'), true);
  });

  it('returns the cached file without redownload when checksum matches', async () => {
    const transport = new InMemoryTransport().put(MODEL_URL, MODEL_BYTES);
    const dir = await tmpDir('lml2-');
    const loader = new LocalModelLoader(singleFileRegistry(), dir, transport);
    await loader.downloadModelAsync('test-model');
    // Second call short-circuits on the existing verified file.
    const p2 = await loader.downloadModelAsync('test-model');
    assert.equal(p2, path.join(dir, 'model.bin'));
  });

  it('rejects unsupported models and steers bundles', async () => {
    const dir = await tmpDir('lml3-');
    const loader = new LocalModelLoader(
      {
        bundle: {
          repo: 'r',
          bundleFiles: [{ name: 'llm.mnn.weight', sha256: 'x', sizeBytes: 1 }],
        },
      },
      dir,
      new InMemoryTransport(),
    );
    await assert.rejects(() => loader.downloadModelAsync('missing'), /not supported/);
    await assert.rejects(() => loader.downloadModelAsync('bundle'), /multi-file bundle/);
    // Bundle path layout.
    assert.equal(
      loader.getModelPath('bundle'),
      path.join(dir, 'bundle', 'llm.mnn.weight'),
    );
  });

  it('checkForCriticalUpdate reads the versions manifest via transport', async () => {
    const dir = await tmpDir('lml4-');
    const transport = new InMemoryTransport().putText(
      'https://raw.githubusercontent.com/BhenguAI/models/main/versions.txt',
      'v1.0\n[CRITICAL] security fix',
    );
    const loader = new LocalModelLoader(singleFileRegistry(), dir, transport);
    assert.equal(await loader.checkForCriticalUpdateAsync(), true);

    // Missing manifest → false (fail-safe).
    const loader2 = new LocalModelLoader(singleFileRegistry(), dir, new InMemoryTransport());
    assert.equal(await loader2.checkForCriticalUpdateAsync(), false);
  });
});

describe('LocalModelManager', () => {
  it('resolves a model path by downloading pytorch_model.bin', async () => {
    // A downloader whose ModelScopeSource writes pytorch_model.bin into the dir.
    const binUrl = 'https://modelscope.cn/models/org/mymodel/pytorch_model.bin';
    const registry: ModelRegistryMap = {
      'org/mymodel': { fileName: 'pytorch_model.bin', primaryUrl: binUrl },
    };
    const transport = new InMemoryTransport().put(binUrl, MODEL_BYTES);
    const mgr = LocalModelManager.withRepository(
      'https://modelscope.cn',
      registry,
      await tmpDir('lmm-'),
      transport,
    );
    const p = await mgr.getModelPathAsync('org/mymodel');
    // Sanitised: '/' → '_'.
    assert.ok(p.endsWith('org_mymodel'));
    const bin = new Uint8Array(await fs.readFile(path.join(p, 'pytorch_model.bin')));
    assert.equal(sha256Hex(bin), sha256Hex(MODEL_BYTES));
  });

  it('throws when the model is absent and no downloader is configured', async () => {
    const mgr = new LocalModelManager(null, await tmpDir('lmm2-'));
    await assert.rejects(() => mgr.getModelPathAsync('x'), /no downloader configured/);
  });

  it('verifyModelAsync compares the bin SHA-256 to the expected bytes', async () => {
    const dir = await tmpDir('lmm3-');
    const modelPath = path.join(dir, 'model');
    await fs.mkdir(modelPath, { recursive: true });
    await fs.writeFile(path.join(modelPath, 'pytorch_model.bin'), MODEL_BYTES);

    const expected = new Uint8Array(createHash('sha256').update(MODEL_BYTES).digest());
    const mgr = new LocalModelManager(null, dir);
    assert.equal(await mgr.verifyModelAsync(modelPath, expected), true);
    // Wrong checksum.
    const wrong = new Uint8Array(32);
    assert.equal(await mgr.verifyModelAsync(modelPath, wrong), false);
    // Empty expected checksum → treated as "no check", true.
    assert.equal(await mgr.verifyModelAsync(modelPath, new Uint8Array(0)), true);
  });
});

describe('SafeModelHandle + PlatformInterop', () => {
  it('wraps a native pointer and releases via the injected callback', () => {
    let freed = 0;
    const h = new SafeModelHandle(0x1234, (ptr) => {
      if (ptr === 0x1234) freed++;
    });
    assert.equal(h.isInvalid, false);
    assert.equal(h.handle, 0x1234);
    assert.equal(h.release(), true);
    assert.equal(h.isInvalid, true);
    // Idempotent — a second release does not double-free.
    h.dispose();
    assert.equal(freed, 1);
  });

  it('default handle is invalid until set + callback wired', () => {
    const h = new SafeModelHandle();
    assert.equal(h.isInvalid, true);
    let freed = false;
    h.setHandle(42);
    h.withReleaseCallback(() => (freed = true));
    h.release();
    assert.equal(freed, true);
  });

  it('PlatformInterop.loadModel loads through an injected native binding', async () => {
    const dir = await tmpDir('pi-');
    const modelFile = path.join(dir, 'model.gguf');
    await fs.writeFile(modelFile, MODEL_BYTES);

    let freedHandle = 0;
    const binding: INativeModelBinding = {
      loadFromFile: (p) => (p === modelFile ? 0xabcd : 0),
      free: (h) => {
        freedHandle = h;
      },
    };
    const handle = await PlatformInterop.loadModel(modelFile, binding);
    assert.equal(handle.handle, 0xabcd);
    handle.dispose();
    assert.equal(freedHandle, 0xabcd);
  });

  it('PlatformInterop.loadModel rejects empty path, missing file, and native failure', async () => {
    const binding: INativeModelBinding = { loadFromFile: () => 0, free: () => {} };
    await assert.rejects(() => PlatformInterop.loadModel('', binding), /Model path is required/);
    await assert.rejects(
      () => PlatformInterop.loadModel('C:/nope/missing.gguf', binding),
      /not found/,
    );
    const dir = await tmpDir('pi2-');
    const f = path.join(dir, 'm.gguf');
    await fs.writeFile(f, MODEL_BYTES);
    await assert.rejects(() => PlatformInterop.loadModel(f, binding), /failed to load/);
  });
});

describe('CircleEngine', () => {
  it('holds the loader and a keyed module registry', async () => {
    const loader: IModelLoader = new LocalModelLoader(singleFileRegistry(), await tmpDir('ce-'));
    const engine = new CircleEngine(loader);
    assert.equal(engine.modelLoader, loader);

    const svc = { moduleName: 'FakeEmbeddings' };
    engine.registerModule('IEmbeddingService', svc);
    assert.equal(engine.hasModule('IEmbeddingService'), true);
    assert.equal(engine.getModule('IEmbeddingService'), svc);
    assert.equal(engine.getModule('IMissing'), null);

    engine.embeddingService = svc;
    assert.equal(engine.embeddingService, svc);
  });

  it('rejects a null loader / null module', async () => {
    assert.throws(() => new CircleEngine(null as unknown as IModelLoader), /modelLoader is required/);
    const engine = new CircleEngine(new LocalModelLoader({}, await tmpDir('ce2-')));
    assert.throws(() => engine.registerModule('k', null), /module is required/);
  });
});

describe('registry helpers', () => {
  it('isBundleEntry distinguishes bundle from single-file entries', () => {
    assert.equal(isBundleEntry({ fileName: 'x.bin' }), false);
    assert.equal(
      isBundleEntry({ bundleFiles: [{ name: 'a', sha256: 'b', sizeBytes: 1 }] }),
      true,
    );
    assert.equal(isBundleEntry({ bundleFiles: [] }), false);
  });
});
