// inference_download.test.ts
//
// Exercises ModelDownloadService (single-file + bundle), SHA-256 verification,
// StripShaAlgorithmPrefix, ModelScope URL building, and installed.json manifest.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import {
  ModelDownloadService,
  InMemoryByteSource,
  InMemoryFileStore,
  stripShaAlgorithmPrefix,
  buildPrimaryUrl,
  buildFallbackUrl,
  type BundleFileSpec,
} from '../src/inference/download';

function sha256Hex(bytes: Uint8Array): string {
  return createHash('sha256').update(bytes).digest('hex');
}

describe('stripShaAlgorithmPrefix', () => {
  it('strips a sha256: prefix but leaves bare hex', () => {
    assert.equal(stripShaAlgorithmPrefix('sha256:ABC123'), 'ABC123');
    assert.equal(stripShaAlgorithmPrefix('SHA-256: ABC '), 'ABC');
    assert.equal(stripShaAlgorithmPrefix('deadbeef'), 'deadbeef');
    assert.equal(stripShaAlgorithmPrefix(''), '');
  });
});

describe('ModelScope URL construction', () => {
  it('builds the API and CDN forms', () => {
    assert.equal(
      buildPrimaryUrl('MNN/Qwen3-0.6B-MNN', 'config.json'),
      'https://modelscope.cn/api/v1/models/MNN/Qwen3-0.6B-MNN/repo?Revision=master&FilePath=config.json',
    );
    assert.equal(
      buildFallbackUrl('MNN/Qwen3-0.6B-MNN', 'config.json'),
      'https://modelscope.cn/models/MNN/Qwen3-0.6B-MNN/resolve/master/config.json',
    );
  });
});

describe('ModelDownloadService — single file', () => {
  it('downloads, verifies SHA-256, and returns the .gguf path', async () => {
    const bytes = new TextEncoder().encode('weights');
    const source = new InMemoryByteSource();
    source.register('https://host/model', bytes);
    const store = new InMemoryFileStore();
    const svc = new ModelDownloadService('/models', source, store);

    const progress: number[] = [];
    const path = await svc.ensureModel(
      'qwen',
      'https://host/model',
      `sha256:${sha256Hex(bytes)}`,
      (p) => progress.push(p),
      undefined,
    );
    assert.equal(path, '/models/qwen.gguf');
    assert.equal(await svc.isModelCached('qwen'), true);
    assert.ok(progress.includes(1.0));
  });

  it('deletes the temp file and throws on SHA mismatch', async () => {
    const source = new InMemoryByteSource();
    source.register('https://host/bad', new TextEncoder().encode('x'));
    const store = new InMemoryFileStore();
    const svc = new ModelDownloadService('/models', source, store);
    await assert.rejects(
      () => svc.ensureModel('m', 'https://host/bad', 'sha256:00', null, undefined),
      /SHA-256 mismatch/,
    );
    assert.equal(await svc.isModelCached('m'), false);
  });

  it('deletes then re-downloads when cached bytes fail the hash', async () => {
    const good = new TextEncoder().encode('good');
    const source = new InMemoryByteSource();
    source.register('https://host/m', good);
    const store = new InMemoryFileStore();
    const svc = new ModelDownloadService('/models', source, store);
    // Pre-seed a stale file at the target path.
    store.writeBytes('/models/m.gguf', new TextEncoder().encode('stale'));
    const path = await svc.ensureModel('m', 'https://host/m', `sha256:${sha256Hex(good)}`, null, undefined);
    assert.deepEqual(store.readBytes(path), good);
  });
});

describe('ModelDownloadService — bundle', () => {
  it('downloads every file, verifies, and stamps installed.json', async () => {
    const cfg = new TextEncoder().encode('{"a":1}');
    const wts = new TextEncoder().encode('WEIGHTS');
    const repo = 'MNN/Qwen3-0.6B-MNN';
    const source = new InMemoryByteSource();
    source.register(buildPrimaryUrl(repo, 'config.json'), cfg);
    source.register(buildPrimaryUrl(repo, 'model.mnn'), wts);
    const store = new InMemoryFileStore();
    const svc = new ModelDownloadService('/models', source, store);

    const files: BundleFileSpec[] = [
      { name: 'config.json', sha256: `sha256:${sha256Hex(cfg)}`, sizeBytes: cfg.length },
      { name: 'model.mnn', sha256: sha256Hex(wts), sizeBytes: wts.length },
    ];
    const dir = await svc.ensureBundle('qwen', repo, files, null, undefined);
    assert.equal(dir, '/models/qwen');
    assert.deepEqual(store.readBytes('/models/qwen/config.json'), cfg);

    await svc.writeInstalledManifest(dir, 'qwen', '1.2.3', repo, files);
    const manifestBytes = store.readBytes('/models/qwen/installed.json');
    const manifest = JSON.parse(new TextDecoder().decode(manifestBytes));
    assert.equal(manifest.modelId, 'qwen');
    assert.equal(manifest.version, '1.2.3');
    assert.equal(manifest.repo, repo);
    assert.equal(manifest.files.length, 2);
    assert.equal(manifest.totalBytes, cfg.length + wts.length);
  });

  it('falls back to the CDN URL when the primary fails', async () => {
    const cfg = new TextEncoder().encode('cfg');
    const repo = 'MNN/X';
    const source = new InMemoryByteSource();
    // Only the fallback URL is registered — primary fetch throws.
    source.register(buildFallbackUrl(repo, 'config.json'), cfg);
    const svc = new ModelDownloadService('/models', source, new InMemoryFileStore());
    const files: BundleFileSpec[] = [
      { name: 'config.json', sha256: sha256Hex(cfg), sizeBytes: cfg.length },
    ];
    const dir = await svc.ensureBundle('x', repo, files, null, undefined);
    assert.equal(dir, '/models/x');
  });

  it('reports free disk space and deletes a model dir', async () => {
    const store = new InMemoryFileStore(123);
    const svc = new ModelDownloadService('/models', new InMemoryByteSource(), store);
    assert.equal(await svc.getAvailableDiskSpaceBytes(), 123);
    store.writeBytes('/models/z/config.json', new Uint8Array([1]));
    assert.equal(await svc.isModelCached('z'), true);
    await svc.deleteModel('z');
    assert.equal(await svc.isModelCached('z'), false);
  });
});
