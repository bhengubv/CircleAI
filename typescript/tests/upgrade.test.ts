// upgrade.test.ts
// Parity test — 7 upgrade-detection cases matching the C# ModelUpgradeTests.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { promises as fs } from 'node:fs';
import { tmpdir } from 'node:os';
import * as path from 'node:path';

import { ModelRegistryService, writeInstalledManifest } from '../src/registry/index.js';
import { UpgradeReason } from '../src/models/index.js';
import type { ModelEntry } from '../src/catalog/index.js';
import type { BundleFile } from '../src/models/index.js';

async function mkTmp(): Promise<string> {
  return await fs.mkdtemp(path.join(tmpdir(), 'circleai-ts-up-'));
}

function makeRegistry(...entries: ModelEntry[]): ModelRegistryService {
  const svc = new ModelRegistryService();
  svc.setRegistry({
    registryUrl: 'https://stub',
    lastUpdated: new Date().toISOString(),
    models: entries,
  });
  return svc;
}

function makeEntry(name: string, version: string, ...files: BundleFile[]): ModelEntry {
  return {
    name,
    version,
    quantization: 'Q4',
    repo: 'MNN/' + name,
    totalBytes: files.reduce((a, f) => a + f.sizeBytes, 0),
    bundleFiles: files,
  };
}

describe('ModelRegistryService.checkForUpgrades', () => {
  it('Case 1 — not installed -> empty', async () => {
    const d = await mkTmp();
    try {
      const svc = makeRegistry(makeEntry('Qwen3-0.6B-MNN', '1.0.0',
        { name: 'config.json', sha256: 'abc', sizeBytes: 100 },
        { name: 'llm.mnn', sha256: 'def', sizeBytes: 200 }));
      assert.equal((await svc.checkForUpgrades(d)).length, 0);
    } finally {
      await fs.rm(d, { recursive: true, force: true });
    }
  });

  it('Case 2 — no manifest, files exist -> Unknown', async () => {
    const d = await mkTmp();
    try {
      const mDir = path.join(d, 'Qwen3-0.6B-MNN');
      await fs.mkdir(mDir, { recursive: true });
      await fs.writeFile(path.join(mDir, 'config.json'), 'stub');
      const svc = makeRegistry(makeEntry('Qwen3-0.6B-MNN', '1.0.0',
        { name: 'config.json', sha256: 'abc', sizeBytes: 100 }));
      const ups = await svc.checkForUpgrades(d);
      assert.equal(ups.length, 1);
      assert.equal(ups[0].reason, UpgradeReason.Unknown);
      assert.equal(ups[0].installedVersion, null);
    } finally {
      await fs.rm(d, { recursive: true, force: true });
    }
  });

  it('Case 3 — all SHAs match -> empty', async () => {
    const d = await mkTmp();
    try {
      await writeInstalledManifest(path.join(d, 'Qwen3-0.6B-MNN'),
        'Qwen3-0.6B-MNN', '1.0.0', 'MNN/Qwen3-0.6B-MNN',
        [{ name: 'config.json', sha256: 'abc', sizeBytes: 100 },
         { name: 'llm.mnn', sha256: 'def', sizeBytes: 200 }]);
      const svc = makeRegistry(makeEntry('Qwen3-0.6B-MNN', '1.0.0',
        { name: 'config.json', sha256: 'abc', sizeBytes: 100 },
        { name: 'llm.mnn', sha256: 'def', sizeBytes: 200 }));
      assert.equal((await svc.checkForUpgrades(d)).length, 0);
    } finally {
      await fs.rm(d, { recursive: true, force: true });
    }
  });

  it('Case 4 — Version drift only -> VersionChanged, 0 bytes', async () => {
    const d = await mkTmp();
    try {
      await writeInstalledManifest(path.join(d, 'Qwen3-0.6B-MNN'),
        'Qwen3-0.6B-MNN', '1.0.0', 'MNN/Qwen3-0.6B-MNN',
        [{ name: 'config.json', sha256: 'abc', sizeBytes: 100 },
         { name: 'llm.mnn', sha256: 'def', sizeBytes: 200 }]);
      const svc = makeRegistry(makeEntry('Qwen3-0.6B-MNN', '1.1.0',
        { name: 'config.json', sha256: 'abc', sizeBytes: 100 },
        { name: 'llm.mnn', sha256: 'def', sizeBytes: 200 }));
      const ups = await svc.checkForUpgrades(d);
      assert.equal(ups.length, 1);
      assert.equal(ups[0].reason, UpgradeReason.VersionChanged);
      assert.equal(ups[0].installedVersion, '1.0.0');
      assert.equal(ups[0].availableVersion, '1.1.0');
      assert.equal(ups[0].estimatedDownloadBytes, 0);
    } finally {
      await fs.rm(d, { recursive: true, force: true });
    }
  });

  it('Case 5 — SHA drift only -> ShaChanged, only drifted bytes', async () => {
    const d = await mkTmp();
    try {
      await writeInstalledManifest(path.join(d, 'Qwen3-0.6B-MNN'),
        'Qwen3-0.6B-MNN', '1.0.0', 'MNN/Qwen3-0.6B-MNN',
        [{ name: 'config.json', sha256: 'abc', sizeBytes: 100 },
         { name: 'llm.mnn', sha256: 'OLD', sizeBytes: 200 }]);
      const svc = makeRegistry(makeEntry('Qwen3-0.6B-MNN', '1.0.0',
        { name: 'config.json', sha256: 'abc', sizeBytes: 100 },
        { name: 'llm.mnn', sha256: 'NEW', sizeBytes: 200 }));
      const ups = await svc.checkForUpgrades(d);
      assert.equal(ups.length, 1);
      assert.equal(ups[0].reason, UpgradeReason.ShaChanged);
      assert.equal(ups[0].estimatedDownloadBytes, 200);
    } finally {
      await fs.rm(d, { recursive: true, force: true });
    }
  });

  it('Case 6 — Version + SHA -> Both, total bytes', async () => {
    const d = await mkTmp();
    try {
      await writeInstalledManifest(path.join(d, 'Qwen3-0.6B-MNN'),
        'Qwen3-0.6B-MNN', '1.0.0', 'MNN/Qwen3-0.6B-MNN',
        [{ name: 'config.json', sha256: 'abc', sizeBytes: 100 },
         { name: 'llm.mnn', sha256: 'OLD', sizeBytes: 200 }]);
      const svc = makeRegistry(makeEntry('Qwen3-0.6B-MNN', '2.0.0',
        { name: 'config.json', sha256: 'abc2', sizeBytes: 100 },
        { name: 'llm.mnn', sha256: 'NEW', sizeBytes: 200 }));
      const ups = await svc.checkForUpgrades(d);
      assert.equal(ups.length, 1);
      assert.equal(ups[0].reason, UpgradeReason.Both);
      assert.equal(ups[0].estimatedDownloadBytes, 300);
    } finally {
      await fs.rm(d, { recursive: true, force: true });
    }
  });

  it('Case 7 — writeInstalledManifest round-trip -> empty', async () => {
    const d = await mkTmp();
    try {
      await writeInstalledManifest(path.join(d, 'Qwen3-0.6B-MNN'),
        'Qwen3-0.6B-MNN', '1.0.0', 'MNN/Qwen3-0.6B-MNN',
        [{ name: 'config.json', sha256: 'abc', sizeBytes: 100 },
         { name: 'llm.mnn', sha256: 'def', sizeBytes: 200 }]);
      const svc = makeRegistry(makeEntry('Qwen3-0.6B-MNN', '1.0.0',
        { name: 'config.json', sha256: 'abc', sizeBytes: 100 },
        { name: 'llm.mnn', sha256: 'def', sizeBytes: 200 }));
      assert.equal((await svc.checkForUpgrades(d)).length, 0);
    } finally {
      await fs.rm(d, { recursive: true, force: true });
    }
  });
});
