// registry.ts — ModelRegistryService + checkForUpgrades + writeInstalledManifest.

import { BundleFile, InstalledManifest, UpgradeInfo, UpgradeReason } from './models_v15';
import { ModelEntry, ModelRegistry } from './catalog';

export class ModelRegistryService {
  private registry: ModelRegistry | null = null;

  constructor(registry?: ModelRegistry) {
    if (registry) this.registry = registry;
  }

  setRegistry(reg: ModelRegistry): void { this.registry = reg; }

  allModels(): ReadonlyArray<ModelEntry> { return this.registry?.models ?? []; }

  getLatestModel(name: string): ModelEntry | null {
    const l = name.toLowerCase();
    return this.allModels().find(m => m.name.toLowerCase() === l) ?? null;
  }

  /**
   * Walks the storage dir and emits an UpgradeInfo for every installed model
   * whose manifest is missing or drifts from the catalog.
   */
  async checkForUpgrades(storageDirectory: string): Promise<UpgradeInfo[]> {
    if (!storageDirectory) throw new Error('storageDirectory is required');
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const fs = require('node:fs/promises') as typeof import('node:fs/promises');
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const path = require('node:path') as typeof import('node:path');

    const now = new Date().toISOString();
    const out: UpgradeInfo[] = [];

    for (const entry of this.allModels()) {
      const modelDir = path.join(storageDirectory, entry.name);
      try {
        const st = await fs.stat(modelDir);
        if (!st.isDirectory()) continue;
      } catch { continue; }

      const manifestPath = path.join(modelDir, 'installed.json');
      let manifest: InstalledManifest | null = null;
      try {
        const buf = await fs.readFile(manifestPath, 'utf-8');
        manifest = JSON.parse(buf) as InstalledManifest;
      } catch { /* missing manifest */ }

      if (!manifest) {
        out.push({
          modelId: entry.name,
          installedVersion: null,
          availableVersion: entry.version,
          reason: UpgradeReason.Unknown,
          estimatedDownloadBytes: entry.totalBytes,
          detectedAt: now,
        });
        continue;
      }

      const versionChanged = manifest.version !== entry.version;
      const { changed: shaChanged, driftBytes } = compareBundleSha(manifest.files, entry.bundleFiles);
      if (!versionChanged && !shaChanged) continue;

      let reason: UpgradeReason;
      if (versionChanged && shaChanged) reason = UpgradeReason.Both;
      else if (versionChanged) reason = UpgradeReason.VersionChanged;
      else reason = UpgradeReason.ShaChanged;

      out.push({
        modelId: entry.name,
        installedVersion: manifest.version,
        availableVersion: entry.version,
        reason,
        estimatedDownloadBytes: driftBytes,
        detectedAt: now,
      });
    }
    return out;
  }
}

function compareBundleSha(
  installed: ReadonlyArray<BundleFile>,
  available: ReadonlyArray<BundleFile>,
): { changed: boolean; driftBytes: number } {
  if (available.length === 0) return { changed: false, driftBytes: 0 };
  const byName = new Map(installed.map(f => [f.name, f]));
  let changed = false;
  let bytes = 0;
  for (const av of available) {
    const inst = byName.get(av.name);
    if (!inst || inst.sha256.toLowerCase() !== av.sha256.toLowerCase()) {
      changed = true;
      bytes += av.sizeBytes;
    }
  }
  return { changed, driftBytes: bytes };
}

export async function writeInstalledManifest(
  modelDir: string,
  modelId: string,
  version: string,
  repo: string | null,
  bundleFiles: ReadonlyArray<BundleFile>,
): Promise<void> {
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const fs = require('node:fs/promises') as typeof import('node:fs/promises');
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const path = require('node:path') as typeof import('node:path');
  await fs.mkdir(modelDir, { recursive: true });
  const total = bundleFiles.reduce((acc, f) => acc + Math.max(0, f.sizeBytes), 0);
  const manifest: InstalledManifest = {
    modelId, version, repo,
    totalBytes: total,
    files: bundleFiles,
    installedAtUtc: new Date().toISOString(),
  };
  await fs.writeFile(path.join(modelDir, 'installed.json'), JSON.stringify(manifest, null, 2));
}
