// registry/index.ts
//
// Model registry service + check-for-upgrades — port of
// CircleAI.Core.Models.ModelRegistryService.

import { promises as fs } from "node:fs";
import * as path from "node:path";
import type {
  ModelEntry,
  ModelRegistry,
  ModelScopeCatalogClient,
} from "../catalog/index.js";
import type {
  BundleFile,
  InstalledManifest,
  UpgradeInfo,
} from "../models/index.js";
import { UpgradeReason } from "../models/index.js";

export class ModelRegistryService {
  private registry: ModelRegistry | null = null;

  constructor(private readonly catalogClient?: ModelScopeCatalogClient) {}

  /** Refresh the cached catalog from the client. Never throws. */
  async primeFromCatalog(): Promise<void> {
    if (!this.catalogClient) return;
    try {
      const reg = await this.catalogClient.getCachedCatalog(true);
      if (reg !== null) this.registry = reg;
    } catch {
      /* swallow */
    }
  }

  /** Hot-load from a previously-saved cache without firing a refresh. */
  async loadFromDisk(): Promise<void> {
    if (!this.catalogClient) return;
    this.registry = await this.catalogClient.loadFromDisk();
  }

  /** Inject a registry directly (mainly for tests). */
  setRegistry(reg: ModelRegistry): void {
    this.registry = reg;
  }

  get allModels(): readonly ModelEntry[] {
    return this.registry?.models ?? [];
  }

  getLatestModel(name: string): ModelEntry | null {
    if (!name) return null;
    const lower = name.toLowerCase();
    return this.allModels.find((m) => m.name.toLowerCase() === lower) ?? null;
  }

  /**
   * Walks every installed model under `storageDirectory`, compares
   * installed.json against the active registry, returns one UpgradeInfo
   * per detected drift. Never throws on per-model failures.
   */
  async checkForUpgrades(storageDirectory: string): Promise<UpgradeInfo[]> {
    if (!storageDirectory) throw new Error("storageDirectory is required");

    const upgrades: UpgradeInfo[] = [];
    const now = new Date().toISOString();

    for (const entry of this.allModels) {
      const modelDir = path.join(storageDirectory, entry.name);
      if (!(await isDirectory(modelDir))) continue;

      const manifestPath = path.join(modelDir, "installed.json");
      const manifest = await readManifest(manifestPath);

      if (manifest === null) {
        upgrades.push({
          modelId: entry.name,
          installedVersion: null,
          availableVersion: entry.version,
          reason: UpgradeReason.Unknown,
          estimatedDownloadBytes: entry.totalBytes ?? 0,
          detectedAt: now,
        });
        continue;
      }

      const versionChanged = manifest.version !== entry.version;
      const { driftDetected, driftBytes } = compareBundleSha(
        manifest.files,
        entry.bundleFiles ?? null,
      );

      if (!versionChanged && !driftDetected) continue;

      let reason: UpgradeReason;
      if (versionChanged && driftDetected) reason = UpgradeReason.Both;
      else if (versionChanged) reason = UpgradeReason.VersionChanged;
      else reason = UpgradeReason.ShaChanged;

      upgrades.push({
        modelId: entry.name,
        installedVersion: manifest.version,
        availableVersion: entry.version,
        reason,
        estimatedDownloadBytes: driftBytes,
        detectedAt: now,
      });
    }

    return upgrades;
  }
}

/** Best-effort write of installed.json after a successful bundle install. */
export async function writeInstalledManifest(
  modelDir: string,
  modelId: string,
  version: string,
  repo: string | null,
  bundleFiles: readonly BundleFile[],
): Promise<void> {
  try {
    const manifest: InstalledManifest = {
      modelId,
      version: version || "",
      repo,
      totalBytes: bundleFiles.reduce(
        (acc, f) => acc + Math.max(0, f.sizeBytes),
        0,
      ),
      files: bundleFiles.slice(),
      installedAtUtc: new Date().toISOString(),
    };
    await fs.mkdir(modelDir, { recursive: true });
    await fs.writeFile(
      path.join(modelDir, "installed.json"),
      JSON.stringify(manifest, null, 2),
    );
  } catch {
    /* best-effort — silent */
  }
}

// ── Helpers ──────────────────────────────────────────────────────────────

async function isDirectory(p: string): Promise<boolean> {
  try {
    const stat = await fs.stat(p);
    return stat.isDirectory();
  } catch {
    return false;
  }
}

async function readManifest(p: string): Promise<InstalledManifest | null> {
  try {
    const raw = await fs.readFile(p, "utf-8");
    return JSON.parse(raw) as InstalledManifest;
  } catch {
    return null;
  }
}

function compareBundleSha(
  installed: readonly BundleFile[] | undefined,
  available: readonly BundleFile[] | null,
): { driftDetected: boolean; driftBytes: number } {
  if (!available || available.length === 0) {
    return { driftDetected: false, driftBytes: 0 };
  }
  const byName = new Map<string, BundleFile>();
  for (const f of installed ?? []) byName.set(f.name, f);

  let drift = false;
  let bytes = 0;
  for (const av of available) {
    const inst = byName.get(av.name);
    if (!inst || inst.sha256.toLowerCase() !== av.sha256.toLowerCase()) {
      drift = true;
      bytes += av.sizeBytes;
    }
  }
  return { driftDetected: drift, driftBytes: bytes };
}
