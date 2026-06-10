// upgrade.test.ts — Parity test: 7 upgrade-detection cases + correlation ID.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { BundleFile, UpgradeReason } from '../src/models_v15';
import { ModelEntry, ModelRegistry } from '../src/catalog';
import { ModelRegistryService, writeInstalledManifest } from '../src/registry';
import { createAgentMessage, AgentMessageKind } from '../src/agents';

function tempDir(): string {
  return mkdtempSync(join(tmpdir(), 'circleai-harmonyos-up-'));
}

function makeEntry(name: string, version: string, files: BundleFile[]): ModelEntry {
  return {
    name, version,
    quantization: 'Q4',
    repo: `MNN/${name}`,
    totalBytes: files.reduce((a, f) => a + f.sizeBytes, 0),
    bundleFiles: files,
    capabilities: null,
  };
}

function makeService(entries: ModelEntry[]): ModelRegistryService {
  const reg: ModelRegistry = {
    registryUrl: 'https://stub',
    lastUpdated: new Date().toISOString(),
    models: entries,
  };
  return new ModelRegistryService(reg);
}

test('case 1: not installed → empty', async () => {
  const d = tempDir();
  try {
    const svc = makeService([makeEntry('Qwen3-0.6B-MNN', '1.0.0', [
      { name: 'config.json', sha256: 'abc', sizeBytes: 100 },
      { name: 'llm.mnn', sha256: 'def', sizeBytes: 200 },
    ])]);
    const ups = await svc.checkForUpgrades(d);
    assert.equal(ups.length, 0);
  } finally { rmSync(d, { recursive: true, force: true }); }
});

test('case 2: model dir exists, no manifest → Unknown', async () => {
  const d = tempDir();
  try {
    const mDir = join(d, 'Qwen3-0.6B-MNN');
    mkdirSync(mDir, { recursive: true });
    writeFileSync(join(mDir, 'config.json'), 'stub');
    const svc = makeService([makeEntry('Qwen3-0.6B-MNN', '1.0.0', [
      { name: 'config.json', sha256: 'abc', sizeBytes: 100 },
    ])]);
    const ups = await svc.checkForUpgrades(d);
    assert.equal(ups.length, 1);
    assert.equal(ups[0].reason, UpgradeReason.Unknown);
    assert.equal(ups[0].installedVersion, null);
  } finally { rmSync(d, { recursive: true, force: true }); }
});

test('case 3: all SHAs match → empty', async () => {
  const d = tempDir();
  try {
    await writeInstalledManifest(join(d, 'Qwen3-0.6B-MNN'), 'Qwen3-0.6B-MNN', '1.0.0', 'MNN/Qwen3-0.6B-MNN', [
      { name: 'config.json', sha256: 'abc', sizeBytes: 100 },
      { name: 'llm.mnn', sha256: 'def', sizeBytes: 200 },
    ]);
    const svc = makeService([makeEntry('Qwen3-0.6B-MNN', '1.0.0', [
      { name: 'config.json', sha256: 'abc', sizeBytes: 100 },
      { name: 'llm.mnn', sha256: 'def', sizeBytes: 200 },
    ])]);
    const ups = await svc.checkForUpgrades(d);
    assert.equal(ups.length, 0);
  } finally { rmSync(d, { recursive: true, force: true }); }
});

test('case 4: version drift → VersionChanged, 0 bytes', async () => {
  const d = tempDir();
  try {
    await writeInstalledManifest(join(d, 'Qwen3-0.6B-MNN'), 'Qwen3-0.6B-MNN', '1.0.0', 'MNN/Qwen3-0.6B-MNN', [
      { name: 'config.json', sha256: 'abc', sizeBytes: 100 },
      { name: 'llm.mnn', sha256: 'def', sizeBytes: 200 },
    ]);
    const svc = makeService([makeEntry('Qwen3-0.6B-MNN', '1.1.0', [
      { name: 'config.json', sha256: 'abc', sizeBytes: 100 },
      { name: 'llm.mnn', sha256: 'def', sizeBytes: 200 },
    ])]);
    const ups = await svc.checkForUpgrades(d);
    assert.equal(ups.length, 1);
    assert.equal(ups[0].reason, UpgradeReason.VersionChanged);
    assert.equal(ups[0].estimatedDownloadBytes, 0);
  } finally { rmSync(d, { recursive: true, force: true }); }
});

test('case 5: SHA drift → ShaChanged, only drifted bytes', async () => {
  const d = tempDir();
  try {
    await writeInstalledManifest(join(d, 'Qwen3-0.6B-MNN'), 'Qwen3-0.6B-MNN', '1.0.0', 'MNN/Qwen3-0.6B-MNN', [
      { name: 'config.json', sha256: 'abc', sizeBytes: 100 },
      { name: 'llm.mnn', sha256: 'OLD', sizeBytes: 200 },
    ]);
    const svc = makeService([makeEntry('Qwen3-0.6B-MNN', '1.0.0', [
      { name: 'config.json', sha256: 'abc', sizeBytes: 100 },
      { name: 'llm.mnn', sha256: 'NEW', sizeBytes: 200 },
    ])]);
    const ups = await svc.checkForUpgrades(d);
    assert.equal(ups.length, 1);
    assert.equal(ups[0].reason, UpgradeReason.ShaChanged);
    assert.equal(ups[0].estimatedDownloadBytes, 200);
  } finally { rmSync(d, { recursive: true, force: true }); }
});

test('case 6: version + SHA drift → Both, total drift bytes', async () => {
  const d = tempDir();
  try {
    await writeInstalledManifest(join(d, 'Qwen3-0.6B-MNN'), 'Qwen3-0.6B-MNN', '1.0.0', 'MNN/Qwen3-0.6B-MNN', [
      { name: 'config.json', sha256: 'abc', sizeBytes: 100 },
      { name: 'llm.mnn', sha256: 'OLD', sizeBytes: 200 },
    ]);
    const svc = makeService([makeEntry('Qwen3-0.6B-MNN', '2.0.0', [
      { name: 'config.json', sha256: 'abc2', sizeBytes: 100 },
      { name: 'llm.mnn', sha256: 'NEW', sizeBytes: 200 },
    ])]);
    const ups = await svc.checkForUpgrades(d);
    assert.equal(ups.length, 1);
    assert.equal(ups[0].reason, UpgradeReason.Both);
    assert.equal(ups[0].estimatedDownloadBytes, 300);
  } finally { rmSync(d, { recursive: true, force: true }); }
});

test('case 7: writeInstalledManifest round-trips → empty', async () => {
  const d = tempDir();
  try {
    await writeInstalledManifest(join(d, 'Qwen3-0.6B-MNN'), 'Qwen3-0.6B-MNN', '1.0.0', 'MNN/Qwen3-0.6B-MNN', [
      { name: 'config.json', sha256: 'abc', sizeBytes: 100 },
      { name: 'llm.mnn', sha256: 'def', sizeBytes: 200 },
    ]);
    const svc = makeService([makeEntry('Qwen3-0.6B-MNN', '1.0.0', [
      { name: 'config.json', sha256: 'abc', sizeBytes: 100 },
      { name: 'llm.mnn', sha256: 'def', sizeBytes: 200 },
    ])]);
    const ups = await svc.checkForUpgrades(d);
    assert.equal(ups.length, 0);
  } finally { rmSync(d, { recursive: true, force: true }); }
});

test('agent message correlation ID auto-synthesises 32 hex chars', () => {
  const m1 = createAgentMessage({
    kind: AgentMessageKind.Greet,
    fromUhid: 'a', toUhid: 'b',
    contentType: 'text/plain',
    payload: new Uint8Array([1, 2, 3]),
    signature: new Uint8Array([4, 5, 6]),
  });
  assert.equal(m1.correlationId.length, 32);
  assert.match(m1.correlationId, /^[0-9a-f]{32}$/);

  const m2 = createAgentMessage({
    kind: AgentMessageKind.Greet,
    fromUhid: 'a', toUhid: 'b',
    contentType: 'text/plain',
    payload: new Uint8Array([1, 2, 3]),
    signature: new Uint8Array([4, 5, 6]),
    correlationId: 'trace-abc',
  });
  assert.equal(m2.correlationId, 'trace-abc');

  const m3 = createAgentMessage({
    kind: AgentMessageKind.Greet,
    fromUhid: 'a', toUhid: 'b',
    contentType: 'text/plain',
    payload: new Uint8Array([1, 2, 3]),
    signature: new Uint8Array([4, 5, 6]),
  });
  assert.notEqual(m1.correlationId, m3.correlationId);
});
