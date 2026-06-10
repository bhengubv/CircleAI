// catalog/index.ts
//
// ModelScope catalog client + signature verifier.
// Port of CircleAI.Core.Models.ModelScopeCatalogClient (Node native fetch).

import { promises as fs } from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import type { BundleFile } from "../models/index.js";

// ─────────────────────────────────────────────────────────────────────────────
// Signature verifier
// ─────────────────────────────────────────────────────────────────────────────

export enum CatalogSignatureResult {
  Valid = 0,
  Invalid = 1,
  Missing = 2,
  NotConfigured = 3,
}

export interface ICatalogSignatureVerifier {
  verify(payload: Uint8Array, signatureBase64: string | null): CatalogSignatureResult;
}

/**
 * Default verifier — always returns NotConfigured.
 * The catalog client treats this as "do not apply fetched catalog, keep
 * cached version" — fail-closed. Ships as the registered default until
 * a real Ed25519 verifier with an embedded public key replaces it.
 */
export class NullCatalogSignatureVerifier implements ICatalogSignatureVerifier {
  static readonly instance = new NullCatalogSignatureVerifier();
  verify(_payload: Uint8Array, _signature: string | null): CatalogSignatureResult {
    return CatalogSignatureResult.NotConfigured;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Catalog records
// ─────────────────────────────────────────────────────────────────────────────

export interface ModelEntry {
  readonly name: string;
  readonly version: string;
  readonly quantization?: string;
  readonly url?: string | null;
  readonly checksum?: string | null;
  readonly repo?: string | null;
  readonly totalBytes?: number;
  readonly bundleFiles?: readonly BundleFile[] | null;
  readonly minRamGb?: number;
  readonly minStorageGb?: number;
  readonly capabilities?: readonly string[] | null;
  readonly qualityRank?: number;
}

export interface ModelRegistry {
  readonly registryUrl: string;
  readonly lastUpdated: string; // ISO 8601
  readonly models: readonly ModelEntry[];
}

// ─────────────────────────────────────────────────────────────────────────────
// Options + cadence
// ─────────────────────────────────────────────────────────────────────────────

export enum CatalogRefreshCadence {
  OnStartup = 0,
  Daily = 1,
  Manual = 2,
  Never = 3,
}

export interface ModelScopeCatalogOptions {
  readonly baseUri?: string;
  readonly cacheDirectory?: string;
  readonly cadence?: CatalogRefreshCadence;
  readonly filter?: string;
  readonly pageSize?: number;
  readonly userAgent?: string;
}

function defaultCacheDir(): string {
  const home = os.homedir();
  return path.join(home, ".circleai", "catalog");
}

// ─────────────────────────────────────────────────────────────────────────────
// ModelScopeCatalogClient
// ─────────────────────────────────────────────────────────────────────────────

export class ModelScopeCatalogClient {
  private readonly baseUri: string;
  private readonly cacheDirectory: string;
  private readonly cadence: CatalogRefreshCadence;
  private readonly filter: string;
  private readonly pageSize: number;
  private readonly userAgent: string;
  private refreshedThisProcess = false;

  constructor(
    options: ModelScopeCatalogOptions = {},
    private readonly verifier: ICatalogSignatureVerifier = NullCatalogSignatureVerifier.instance,
    /** Optional callable returning "online" / "none" / null. */
    private readonly networkTypeProvider?: () => string | null,
  ) {
    this.baseUri = options.baseUri ?? "https://www.modelscope.cn";
    this.cacheDirectory = options.cacheDirectory ?? defaultCacheDir();
    this.cadence = options.cadence ?? CatalogRefreshCadence.OnStartup;
    this.filter = options.filter ?? "MNN";
    this.pageSize = options.pageSize ?? 100;
    this.userAgent =
      options.userAgent ?? "Mozilla/5.0 (Circle AI SDK) CircleAI-TS/1.5";
  }

  get cacheFilePath(): string {
    return path.join(this.cacheDirectory, "catalog.json");
  }

  get signatureFilePath(): string {
    return path.join(this.cacheDirectory, "catalog.sig");
  }

  async isRefreshDue(): Promise<boolean> {
    if (this.cadence === CatalogRefreshCadence.Never) return false;
    if (this.cadence === CatalogRefreshCadence.Manual) return false;

    if (this.networkTypeProvider) {
      try {
        const net = this.networkTypeProvider();
        if (net !== null && net !== undefined && net.toLowerCase() === "none")
          return false;
      } catch {
        /* fall through */
      }
    }

    const cacheStat = await statQuiet(this.cacheFilePath);
    if (cacheStat === null) return true;

    if (this.cadence === CatalogRefreshCadence.OnStartup) {
      return !this.refreshedThisProcess;
    }

    // Daily — refresh if last write was on a different UTC date.
    const mtime = new Date(cacheStat.mtimeMs);
    const now = new Date();
    return mtime.toISOString().slice(0, 10) < now.toISOString().slice(0, 10);
  }

  async loadFromDisk(): Promise<ModelRegistry | null> {
    try {
      const raw = await fs.readFile(this.cacheFilePath, "utf-8");
      return JSON.parse(raw) as ModelRegistry;
    } catch {
      return null;
    }
  }

  async getCachedCatalog(
    acceptStaleOnError = true,
  ): Promise<ModelRegistry | null> {
    if (await this.isRefreshDue()) {
      try {
        return await this.refresh();
      } catch (err) {
        if (!acceptStaleOnError) throw err;
      }
    }
    return this.loadFromDisk();
  }

  async refresh(): Promise<ModelRegistry> {
    const registry = await this.fetchLive();
    const jsonBytes = new TextEncoder().encode(
      JSON.stringify(registry, null, 2),
    );

    let existingSig: string | null = null;
    try {
      existingSig = (await fs.readFile(this.signatureFilePath, "utf-8")).trim() || null;
    } catch {
      /* no sig — treat as null */
    }

    const sigResult = this.verifier.verify(jsonBytes, existingSig);
    if (sigResult === CatalogSignatureResult.Invalid) {
      throw new Error(
        "Catalog signature did not verify against the configured public key. " +
          "Keeping previous cache; not applying fetched payload.",
      );
    }

    await fs.mkdir(this.cacheDirectory, { recursive: true });
    await fs.writeFile(this.cacheFilePath, jsonBytes);
    this.refreshedThisProcess = true;
    return registry;
  }

  private async fetchLive(): Promise<ModelRegistry> {
    const listingUrl =
      `${this.baseUri}/api/v1/models` +
      `?Name=${encodeURIComponent(this.filter)}` +
      `&PageSize=${this.pageSize}`;
    const listing = await this.fetchJson(listingUrl);

    interface ListingItem {
      Name?: string;
      Path?: string;
      Revision?: string;
      Quantization?: string;
    }
    interface FileItem {
      Name?: string;
      Path?: string;
      Sha256?: string;
      Size?: number;
    }
    const data = (listing as { Data?: { Model?: ListingItem[] } }).Data;
    const items: ListingItem[] = data?.Model ?? [];
    const entries: ModelEntry[] = [];

    for (const m of items) {
      const name = m.Name ?? "";
      const repoPath = m.Path ?? "";
      if (!name || !repoPath) continue;

      const filesUrl = `${this.baseUri}/api/v1/models/${repoPath}/repo/files?Revision=master`;
      let filesResp: { Data?: { Files?: FileItem[] } };
      try {
        filesResp = (await this.fetchJson(filesUrl)) as {
          Data?: { Files?: FileItem[] };
        };
      } catch {
        continue;
      }
      const fileList = filesResp.Data?.Files ?? [];

      const bundle: BundleFile[] = fileList
        .filter((f) => f.Path || f.Name)
        .map((f) => ({
          name: (f.Path ?? f.Name ?? "") as string,
          sha256: String(f.Sha256 ?? ""),
          sizeBytes: Number(f.Size ?? 0),
        }));
      const total = bundle.reduce((acc, b) => acc + b.sizeBytes, 0);

      entries.push({
        name,
        version: String(m.Revision ?? "master"),
        quantization: m.Quantization ?? "",
        repo: repoPath,
        totalBytes: total,
        bundleFiles: bundle,
      });
    }

    return {
      registryUrl: this.baseUri,
      lastUpdated: new Date().toISOString(),
      models: entries,
    };
  }

  private async fetchJson(url: string): Promise<unknown> {
    const res = await fetch(url, {
      headers: { "User-Agent": this.userAgent },
    });
    if (!res.ok) throw new Error(`HTTP ${res.status} fetching ${url}`);
    return res.json();
  }
}

async function statQuiet(p: string): Promise<{ mtimeMs: number } | null> {
  try {
    return await fs.stat(p);
  } catch {
    return null;
  }
}
