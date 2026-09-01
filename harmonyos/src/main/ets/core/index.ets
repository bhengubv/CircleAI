/**
 * core/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// core/index.ts
//
// Port of the CircleAI.Core model-management runtime:
//   • IModelSource / ModelScopeSource / SourceDownloadHelper   (Sources/*)
//   • HuggingFaceSource                                        (removed tombstone)
//   • IModelDownloader / ModelDownloader                       (Model*Downloader.cs)
//   • IModelLoader / LocalModelLoader                          (I/LocalModelLoader.cs)
//   • IModelManager / LocalModelManager                        (I/LocalModelManager.cs)
//   • SafeModelHandle / PlatformInterop                        (SafeModelHandle.cs, PlatformInterop.cs)
//   • CircleEngine                                             (CircleEngine.cs)
//   • ICircleModule                                            (ICircleModule.cs)
//   • IEmbeddingService                                        (IEmbeddingService.cs)
//   • DownloadProgress                                         (IModelSource.cs)
//
// The C# code downloads over HTTP and loads native model files. Per the porting
// contract, the NETWORK is injected behind an ISourceTransport so the runtime
// is fully deterministic and offline. On-disk model layout uses node's fs (the
// same local-filesystem approach the existing catalog/registry modules use).
//
// The registry (registry.json embedded resource in C#) is injected as a plain
// object map so callers control which models are known.

import { Files as fs } from "../platform";
import { Device as os } from "../platform";
import { Path as path } from "../platform";
import { createHash } from "../platform";
import type { DownloadProgress } from "../models/index";

// Re-export the auditing + multi-tenant contracts so `core/*` is the single
// import surface for the CircleAI.Core namespace.
export * from "./auditing";
export * from "./multitenant";
export * from "./shard_kv_codec";
export { DotNetRandom } from "./dotnet_random";

// ─────────────────────────────────────────────────────────────────────────────
// DownloadProgress — CircleAI.Core.DownloadProgress (Sources/IModelSource.cs)
// ─────────────────────────────────────────────────────────────────────────────
//
// The C# `DownloadProgress` (FileName / BytesReceived / TotalBytes /
// BytesPerSecond / TimeSpan EstimatedTimeRemaining) maps onto the already-ported
// `DownloadProgress` in models/index.ts (which flattens the TimeSpan into
// `estimatedTimeRemaining` seconds). We reuse that type here rather than mint a
// second, colliding definition.

export type { DownloadProgress };

/** Progress callback shape (the C# `IProgress<T>`). */
export type ProgressReporter<T> = (value: T) => void;

// ─────────────────────────────────────────────────────────────────────────────
// ISourceTransport — the injected network layer (deterministic by default)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * The single injection point that replaces real HTTP. Implementations resolve a
 * URL to its raw bytes (or report the resource missing). The default
 * {@link InMemoryTransport} serves bytes registered ahead of time, so the whole
 * model-download runtime is deterministic and offline.
 */
export interface ISourceTransport {
  /** True when the host/service is reachable (mirrors IModelSource.IsAvailableAsync). */
  isAvailable(url: string): Promise<boolean>;

  /**
   * Fetch the full byte payload for a URL. Throws if the URL is not known /
   * reachable — the downloader treats a throw as "this source failed, fall
   * through to the next".
   */
  fetch(url: string): Promise<Uint8Array>;
}

/**
 * Deterministic in-memory transport. Register `(url → bytes)` pairs; unknown
 * URLs throw (so the source falls through). Availability is `true` iff at least
 * one URL is registered for the same host as the probed URL — enough to model
 * "is this supplier online".
 */
export class InMemoryTransport implements ISourceTransport {
  private readonly files = new Map<string, Uint8Array>();

  /** Register the payload a URL should serve. */
  put(url: string, bytes: Uint8Array): this {
    this.files.set(url, bytes);
    return this;
  }

  /** Register a text payload (UTF-8 encoded). */
  putText(url: string, text: string): this {
    return this.put(url, new TextEncoder().encode(text));
  }

  has(url: string): boolean {
    return this.files.has(url);
  }

  isAvailable(url: string): Promise<boolean> {
    let host: string;
    try {
      host = new URL(url).host;
    } catch {
      return Promise.resolve(false);
    }
    for (const key of this.files.keys()) {
      try {
        if (new URL(key).host === host) return Promise.resolve(true);
      } catch {
        /* ignore malformed */
      }
    }
    return Promise.resolve(false);
  }

  fetch(url: string): Promise<Uint8Array> {
    const bytes = this.files.get(url);
    if (bytes === undefined)
      throw new Error(`InMemoryTransport: no payload registered for '${url}'.`);
    return Promise.resolve(bytes);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// SourceDownloadHelper — CircleAI.Core.Sources.SourceDownloadHelper
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Shared download routine used by {@link IModelSource} implementations. Fetches
 * via the injected transport, writes the file, and reports progress + ETA. The
 * C# version streams over HTTP with resume support; here the transport yields
 * the whole payload, and progress is reported as a single 0→100% step (there is
 * no partial-stream to chunk).
 */
export const SourceDownloadHelper = {
  async downloadWithProgress(
    transport: ISourceTransport,
    url: string,
    localPath: string,
    progress: ProgressReporter<DownloadProgress> | null,
  ): Promise<void> {
    const fileName = path.basename(localPath);
    const bytes = await transport.fetch(url);

    const dir = path.dirname(localPath);
    if (dir) await fs.mkdir(dir, { recursive: true });
    await fs.writeFile(localPath, bytes);

    progress?.({
      fileName,
      bytesReceived: bytes.length,
      totalBytes: bytes.length,
      bytesPerSecond: bytes.length,
      estimatedTimeRemaining: 0,
    });
  },
};

// ─────────────────────────────────────────────────────────────────────────────
// IModelSource — CircleAI.Core.IModelSource
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Abstraction for model file sources. Allows fallback chains for sanctions
 * resilience (e.g. ModelScope API primary, ModelScope CDN fallback).
 */
export interface IModelSource {
  /** Friendly name of the source (e.g. "ModelScope"). Used in logs. */
  readonly name: string;

  /** Quick reachability check; returns false on any failure rather than throw. */
  isAvailableAsync(): Promise<boolean>;

  /** Download a single file from `url` to `localPath`, reporting progress. */
  downloadAsync(
    url: string,
    localPath: string,
    progress?: ProgressReporter<DownloadProgress> | null,
  ): Promise<void>;
}

// ─────────────────────────────────────────────────────────────────────────────
// ModelScopeSource — CircleAI.Core.Sources.ModelScopeSource
// ─────────────────────────────────────────────────────────────────────────────

/**
 * {@link IModelSource} backed by ModelScope (modelscope.cn, Alibaba). Treated as
 * the primary source for sanctions resilience. The network is the injected
 * {@link ISourceTransport}.
 */
export class ModelScopeSource implements IModelSource {
  private static readonly HostName = "modelscope.cn";
  private static readonly ProbePath = "https://modelscope.cn/";

  private readonly transport: ISourceTransport;

  get name(): string {
    return "ModelScope";
  }

  constructor(transport: ISourceTransport = new InMemoryTransport()) {
    this.transport = transport;
  }

  isAvailableAsync(): Promise<boolean> {
    return this.transport.isAvailable(ModelScopeSource.ProbePath).catch(() => false);
  }

  async downloadAsync(
    url: string,
    localPath: string,
    progress: ProgressReporter<DownloadProgress> | null = null,
  ): Promise<void> {
    if (url === null || url === undefined || url.trim() === "")
      throw new Error("url is required");
    if (localPath === null || localPath === undefined || localPath.trim() === "")
      throw new Error("localPath is required");

    let host: string;
    try {
      host = new URL(url).host;
    } catch {
      throw new Error(
        `URL host must be on ${ModelScopeSource.HostName} for ${this.name} source. Got: ${url}`,
      );
    }
    if (!host.toLowerCase().endsWith(ModelScopeSource.HostName)) {
      throw new Error(
        `URL host must be on ${ModelScopeSource.HostName} for ${this.name} source. Got: ${url}`,
      );
    }

    const dir = path.dirname(localPath);
    if (dir) await fs.mkdir(dir, { recursive: true });

    await SourceDownloadHelper.downloadWithProgress(
      this.transport,
      url,
      localPath,
      progress,
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// HuggingFaceSource — REMOVED tombstone (CircleAI.Core.Sources.HuggingFaceSource)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Removed. Use {@link ModelScopeSource} instead. HuggingFace is a Western (US)
 * company; all downloads must route through ModelScope (modelscope.cn, Alibaba)
 * to stay on Chinese-origin infrastructure. Kept as a tombstone so any code
 * still referencing it fails loudly — the constructor throws, matching the C#
 * `[Obsolete(error:true)]` + `throw new NotSupportedException(...)`.
 */
export class HuggingFaceSource {
  constructor() {
    throw new Error(
      "HuggingFaceSource has been removed. Use ModelScopeSource (modelscope.cn).",
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Registry entry shape (registry.json rows) — shared by loader + downloader
// ─────────────────────────────────────────────────────────────────────────────

/** One file inside a multi-file MNN bundle (registry `BundleFiles[]` element). */
export interface RegistryBundleFile {
  readonly name: string;
  readonly sha256: string;
  readonly sizeBytes: number;
}

/**
 * Internal registry-row shape. Supports BOTH the legacy single-file shape
 * (fileName/primaryUrl/fallbackUrl/checksum) AND the new bundle shape
 * (repo + bundleFiles[]). Exactly one shape is populated per entry;
 * {@link isBundleEntry} selects which.
 */
export interface RegistryModelEntry {
  // Legacy single-file shape.
  readonly fileName?: string | null;
  readonly primaryUrl?: string | null;
  readonly fallbackUrl?: string | null;
  readonly checksum?: string | null;
  readonly sizeBytes?: number;
  readonly version?: string;
  readonly architecture?: string;
  readonly quantizationType?: string;

  // Bundle shape.
  readonly repo?: string | null;
  readonly totalBytes?: number;
  readonly bundleFiles?: readonly RegistryBundleFile[] | null;
}

/** True when the entry is a multi-file bundle (has a non-empty bundleFiles[]). */
export function isBundleEntry(entry: RegistryModelEntry): boolean {
  return !!entry.bundleFiles && entry.bundleFiles.length > 0;
}

/**
 * The registry map injected in place of C#'s embedded `registry.json`. Keys are
 * model ids (case-insensitive lookups are performed by the loader/downloader).
 */
export type ModelRegistryMap = Readonly<Record<string, RegistryModelEntry>>;

function registryLookup(
  registry: ModelRegistryMap,
  modelId: string,
): RegistryModelEntry | undefined {
  if (Object.prototype.hasOwnProperty.call(registry, modelId))
    return registry[modelId];
  // Case-insensitive fallback (StringComparer.OrdinalIgnoreCase).
  const lower = modelId.toLowerCase();
  for (const key of Object.keys(registry)) {
    if (key.toLowerCase() === lower) return registry[key];
  }
  return undefined;
}

// ─────────────────────────────────────────────────────────────────────────────
// IModelDownloader — CircleAI.Core.IModelDownloader
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Downloads a model file (or set of files) to local storage. Implementations
 * walk a chain of {@link IModelSource} so that, e.g., ModelScope API is tried
 * first and ModelScope CDN second.
 */
export interface IModelDownloader {
  /** Download a model identified by `modelId` to `localPath`. */
  downloadModelAsync(modelId: string, localPath: string): Promise<void>;

  /**
   * Download a single model file by trying each candidate URL in order. The
   * first URL is the primary; subsequent URLs are fallbacks. Returns the name
   * of the source that succeeded.
   */
  downloadFromCandidatesAsync(
    candidateUrls: readonly string[],
    localFilePath: string,
    progress?: ProgressReporter<DownloadProgress> | null,
  ): Promise<string>;
}

// ─────────────────────────────────────────────────────────────────────────────
// ModelDownloader — CircleAI.Core.ModelDownloader
// ─────────────────────────────────────────────────────────────────────────────

/** Progress report shape emitted during downloads (the C# `DownloadProgressReport`). */
export interface DownloadProgressReport {
  fileName: string;
  bytesReceived: number;
  totalBytes: number;
  bytesPerSecond: number;
  estimatedTimeRemaining: number;
}

/**
 * Source-agnostic model downloader. Walks a list of {@link IModelSource} in
 * order, falling through on failure so that one supplier going dark does not
 * break model bootstrap.
 */
export class ModelDownloader implements IModelDownloader {
  private readonly sources: readonly IModelSource[];
  private readonly registry: ModelRegistryMap;
  private disposed = false;

  /** Registered progress handlers (the C# `event DownloadProgressHandler ProgressChanged`). */
  private readonly progressHandlers: Array<(p: DownloadProgressReport) => void> =
    [];

  /**
   * @param sources ordered fallback chain (at least one).
   * @param registry the injected model registry (replaces the embedded resource).
   */
  constructor(sources: readonly IModelSource[], registry: ModelRegistryMap = {}) {
    if (sources === null || sources === undefined)
      throw new Error("sources is required");
    if (sources.length === 0)
      throw new Error("At least one model source is required");
    this.sources = sources;
    this.registry = registry;
  }

  /** Subscribe to progress reports. */
  onProgress(handler: (p: DownloadProgressReport) => void): void {
    this.progressHandlers.push(handler);
  }

  private emitProgress(p: DownloadProgressReport): void {
    for (const h of this.progressHandlers) h(p);
  }

  async downloadModelAsync(modelId: string, localPath: string): Promise<void> {
    if (this.disposed) throw new Error("ModelDownloader is disposed");
    if (modelId === null || modelId === undefined || modelId.trim() === "")
      throw new Error("modelId is required");
    if (localPath === null || localPath === undefined || localPath.trim() === "")
      throw new Error("localPath is required");

    const entry = registryLookup(this.registry, modelId);
    if (entry === undefined) {
      throw new Error(
        `Model '${modelId}' is not in the registry. Known models: ` +
          Object.keys(this.registry).join(", "),
      );
    }

    await fs.mkdir(localPath, { recursive: true });

    if (isBundleEntry(entry)) {
      throw new Error(
        `Model '${modelId}' is a multi-file MNN bundle (registry entry has BundleFiles[]). ` +
          "Use CircleAI.Inference.ModelDownloadService.EnsureBundleAsync from " +
          "MnnInferenceBridgeFactory instead — this legacy single-file downloader " +
          "cannot fetch a multi-file bundle.",
      );
    }

    const targetFile = path.join(localPath, entry.fileName ?? "");

    const candidates = ModelDownloader.buildCandidateList(entry);
    if (candidates.length === 0) {
      throw new Error(
        `Model '${modelId}' has no PrimaryUrl or FallbackUrl configured.`,
      );
    }

    const bridge: ProgressReporter<DownloadProgress> = (p) =>
      this.emitProgress({
        fileName: p.fileName,
        bytesReceived: p.bytesReceived,
        totalBytes: p.totalBytes,
        bytesPerSecond: p.bytesPerSecond,
        estimatedTimeRemaining: p.estimatedTimeRemaining,
      });

    try {
      await this.downloadFromCandidatesAsync(candidates, targetFile, bridge);
    } catch (err) {
      await ModelDownloader.cleanupPartialFile(targetFile);
      throw err;
    }
  }

  async downloadFromCandidatesAsync(
    candidateUrls: readonly string[],
    localFilePath: string,
    progress: ProgressReporter<DownloadProgress> | null = null,
  ): Promise<string> {
    if (this.disposed) throw new Error("ModelDownloader is disposed");
    if (candidateUrls === null || candidateUrls === undefined)
      throw new Error("candidateUrls is required");
    if (candidateUrls.length === 0)
      throw new Error("At least one candidate URL is required");
    if (localFilePath === null || localFilePath === undefined || localFilePath.trim() === "")
      throw new Error("localFilePath is required");

    const dir = path.dirname(localFilePath);
    if (dir) await fs.mkdir(dir, { recursive: true });

    const failures: string[] = [];

    for (const url of candidateUrls) {
      if (url === null || url === undefined || url.trim() === "") continue;

      const source = this.matchSource(url);
      if (source === undefined) {
        failures.push(`(no registered source for '${url}')`);
        continue;
      }

      try {
        await source.downloadAsync(url, localFilePath, progress);
        return source.name;
      } catch (ex) {
        failures.push(`${source.name}: ${(ex as Error).message}`);
        // Drop the partial so the next source can start clean.
        await ModelDownloader.cleanupPartialFile(localFilePath);
      }
    }

    throw new Error("All model sources failed:\n  " + failures.join("\n  "));
  }

  private matchSource(url: string): IModelSource | undefined {
    let host: string;
    try {
      host = new URL(url).host;
    } catch {
      return undefined;
    }

    // Heuristic match by source Name (host substring).
    for (const s of this.sources) {
      if (host.toLowerCase().includes(s.name.toLowerCase())) return s;
    }

    // All registered download URLs are on modelscope.cn (Alibaba).
    if (host.toLowerCase().includes("modelscope")) {
      return this.sources.find(
        (s) => s.name.toLowerCase() === "modelscope",
      );
    }

    return undefined;
  }

  private static buildCandidateList(entry: RegistryModelEntry): string[] {
    const list: string[] = [];
    if (entry.primaryUrl && entry.primaryUrl.trim() !== "")
      list.push(entry.primaryUrl);
    if (entry.fallbackUrl && entry.fallbackUrl.trim() !== "")
      list.push(entry.fallbackUrl);
    return list;
  }

  private static async cleanupPartialFile(p: string): Promise<void> {
    try {
      await fs.rm(p, { force: true });
    } catch {
      // Best effort.
    }
  }

  dispose(): void {
    this.disposed = true;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// IModelLoader — CircleAI.Core.IModelLoader
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Acquires + caches model files. Disposable. `progress` reports 0→1 fraction
 * (the C# `IProgress<float>`).
 */
export interface IModelLoader {
  downloadModelAsync(
    modelName: string,
    progress?: ProgressReporter<number> | null,
  ): Promise<string>;
  getModelPath(modelName: string): string;
  modelExists(modelName: string): Promise<boolean>;
  checkForCriticalUpdateAsync(): Promise<boolean>;
  dispose(): void;
}

// ─────────────────────────────────────────────────────────────────────────────
// LocalModelLoader — CircleAI.Core.LocalModelLoader
// ─────────────────────────────────────────────────────────────────────────────

function defaultModelDir(): string {
  // C# uses ApplicationData/CircleAI/Models. Node's closest cross-platform
  // analogue is homedir()/.circleai/Models — matching the catalog module.
  return path.join(os.homedir(), ".circleai", "Models");
}

async function fileExists(p: string): Promise<boolean> {
  try {
    const st = await fs.stat(p);
    return st.isFile();
  } catch {
    return false;
  }
}

async function computeSha256Hex(filePath: string): Promise<string> {
  const buf = await fs.readFile(filePath);
  return createHash("sha256").update(buf).digest("hex");
}

/**
 * (LocalModelLoader) Single-file / bundle model loader with SHA-256 integrity
 * checking. The registry (embedded `registry.json` in C#) is injected; the
 * network is injected through the {@link IModelDownloader} / {@link ISourceTransport}
 * plumbing that supplies the file bytes.
 *
 * NOTE: like the C# type, DownloadModelAsync only services legacy single-file
 * entries; bundle entries throw and steer callers to the bundle path.
 */
export class LocalModelLoader implements IModelLoader {
  /** Canonical anchor file present in every MNN-LLM bundle. */
  private static readonly BundleAnchorFileName = "llm.mnn.weight";
  /** Version-manifest URL the critical-update probe reads. */
  private static readonly VersionsUrl =
    "https://raw.githubusercontent.com/BhenguAI/models/main/versions.txt";

  private readonly modelDir: string;
  private readonly registry: ModelRegistryMap;
  private readonly transport: ISourceTransport;
  private disposed = false;

  /**
   * @param registry model registry (replaces embedded registry.json).
   * @param modelDirectory where model files live. Defaults to ~/.circleai/Models.
   * @param transport injected network — supplies download bytes + the versions probe.
   */
  constructor(
    registry: ModelRegistryMap,
    modelDirectory: string | null = null,
    transport: ISourceTransport = new InMemoryTransport(),
  ) {
    this.registry = registry;
    this.modelDir = modelDirectory ?? defaultModelDir();
    this.transport = transport;
  }

  /** Creates the model directory. Call once before use (async ctor stand-in). */
  async ensureDirectory(): Promise<void> {
    await fs.mkdir(this.modelDir, { recursive: true });
  }

  async downloadModelAsync(
    modelName: string,
    progress: ProgressReporter<number> | null = null,
  ): Promise<string> {
    if (this.disposed) throw new Error("LocalModelLoader is disposed");
    const entry = registryLookup(this.registry, modelName);
    if (entry === undefined)
      throw new Error(`Model ${modelName} not supported`);

    if (isBundleEntry(entry)) {
      throw new Error(
        `Model '${modelName}' is a multi-file bundle (registry entry has BundleFiles[]); ` +
          "use ModelDownloadService.EnsureBundleAsync via MnnInferenceBridgeFactory instead. " +
          "LocalModelLoader.DownloadModelAsync only handles legacy single-file entries.",
      );
    }

    await this.ensureDirectory();
    const localPath = path.join(this.modelDir, entry.fileName ?? "");

    if (await fileExists(localPath)) {
      if (!entry.checksum || entry.checksum.startsWith("sha256:TBD")) {
        return localPath; // no verified checksum — integrity check skipped
      }
      if (await this.verifyChecksum(localPath, entry.checksum)) return localPath;
      await fs.rm(localPath, { force: true });
    }

    // Try primary (ModelScope) first, then fallback.
    const sources = [entry.primaryUrl, entry.fallbackUrl];
    let lastError: Error | null = null;
    for (const url of sources) {
      if (!url || url.trim() === "") continue;
      try {
        await this.downloadFile(url, localPath, progress);
        if (!entry.checksum || entry.checksum.startsWith("sha256:TBD")) {
          return localPath;
        }
        if (await this.verifyChecksum(localPath, entry.checksum)) return localPath;
        await fs.rm(localPath, { force: true });
        lastError = new Error("Downloaded model failed checksum verification.");
      } catch (ex) {
        lastError = ex as Error;
      }
    }

    throw lastError ?? new Error("All sources failed.");
  }

  private async downloadFile(
    url: string,
    outputPath: string,
    progress: ProgressReporter<number> | null,
  ): Promise<void> {
    const bytes = await this.transport.fetch(url);
    const dir = path.dirname(outputPath);
    if (dir) await fs.mkdir(dir, { recursive: true });
    await fs.writeFile(outputPath, bytes);
    progress?.(1);
  }

  getModelPath(modelName: string): string {
    if (this.disposed) throw new Error("LocalModelLoader is disposed");
    const entry = registryLookup(this.registry, modelName);
    if (entry === undefined)
      throw new Error(`Model ${modelName} not found`);

    if (isBundleEntry(entry)) {
      return path.join(
        this.modelDir,
        modelName,
        LocalModelLoader.BundleAnchorFileName,
      );
    }
    return path.join(this.modelDir, entry.fileName ?? "");
  }

  async modelExists(modelName: string): Promise<boolean> {
    try {
      const entry = registryLookup(this.registry, modelName);
      if (entry === undefined) return false;

      const p = this.getModelPath(modelName);
      if (!(await fileExists(p))) return false;

      if (isBundleEntry(entry)) {
        const anchor = entry.bundleFiles?.find(
          (f) =>
            f.name.toLowerCase() ===
            LocalModelLoader.BundleAnchorFileName.toLowerCase(),
        );
        if (anchor === undefined) return false;
        return this.verifyChecksum(p, anchor.sha256);
      }

      return !!entry.checksum && (await this.verifyChecksum(p, entry.checksum));
    } catch {
      return false;
    }
  }

  async checkForCriticalUpdateAsync(): Promise<boolean> {
    try {
      const bytes = await this.transport.fetch(LocalModelLoader.VersionsUrl);
      const text = new TextDecoder().decode(bytes);
      return text.includes("[CRITICAL]");
    } catch {
      return false;
    }
  }

  private async verifyChecksum(
    filePath: string,
    expectedChecksum: string,
  ): Promise<boolean> {
    const actualHex = await computeSha256Hex(filePath);

    // Accept both "sha256:<hex>" and bare-hex forms.
    let expected = (expectedChecksum ?? "").trim();
    if (expected.toLowerCase().startsWith("sha256:"))
      expected = expected.substring("sha256:".length).trim();

    return expected.toLowerCase() === actualHex.toLowerCase();
  }

  dispose(): void {
    this.disposed = true;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// IModelManager — CircleAI.Core.IModelManager
// ─────────────────────────────────────────────────────────────────────────────

/** Resolves + verifies model paths. Disposable. */
export interface IModelManager {
  getModelPathAsync(modelId: string): Promise<string>;
  verifyModelAsync(modelPath: string, expectedChecksum: Uint8Array): Promise<boolean>;
  dispose(): void;
}

// ─────────────────────────────────────────────────────────────────────────────
// LocalModelManager — CircleAI.Core.LocalModelManager
// ─────────────────────────────────────────────────────────────────────────────

function sequenceEqual(a: Uint8Array, b: Uint8Array): boolean {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
  return true;
}

async function computeSha256Bytes(filePath: string): Promise<Uint8Array> {
  const buf = await fs.readFile(filePath);
  return new Uint8Array(createHash("sha256").update(buf).digest());
}

/**
 * (LocalModelManager) Downloads a model to a per-model directory and verifies
 * its `pytorch_model.bin` against an expected SHA-256. The downloader is
 * injected via {@link IModelDownloader}; when none is supplied a
 * {@link ModelDownloader} over a single {@link ModelScopeSource} is created (the
 * C# `Uri`-constructor behaviour).
 */
export class LocalModelManager implements IModelManager {
  private readonly modelsDirectory: string;
  private readonly modelDownloader: IModelDownloader | null;
  private disposed = false;

  /**
   * Construct from a downloader (or null to disable downloading), plus the
   * models directory.
   */
  constructor(
    modelDownloader: IModelDownloader | null,
    modelsDirectory = "Models",
  ) {
    this.modelDownloader = modelDownloader ?? null;
    this.modelsDirectory = modelsDirectory;
  }

  /**
   * Factory mirroring the C# `LocalModelManager(Uri?, string)` constructor:
   * when a repository URL is present, a ModelDownloader over one ModelScopeSource
   * is wired; when null, downloading is disabled.
   */
  static withRepository(
    modelRepositoryUrl: string | null,
    registry: ModelRegistryMap = {},
    modelsDirectory = "Models",
    transport: ISourceTransport = new InMemoryTransport(),
  ): LocalModelManager {
    const downloader =
      modelRepositoryUrl !== null && modelRepositoryUrl !== undefined
        ? new ModelDownloader([new ModelScopeSource(transport)], registry)
        : null;
    return new LocalModelManager(downloader, modelsDirectory);
  }

  /** Creates the models directory. Call once before use. */
  async ensureDirectory(): Promise<void> {
    await fs.mkdir(this.modelsDirectory, { recursive: true });
  }

  async getModelPathAsync(modelId: string): Promise<string> {
    if (this.disposed) throw new Error("LocalModelManager is disposed");
    await this.ensureDirectory();

    const modelPath = path.join(
      this.modelsDirectory,
      LocalModelManager.sanitizeModelId(modelId),
    );

    const binPath = path.join(modelPath, "pytorch_model.bin");
    const dirExists = await LocalModelManager.directoryExists(modelPath);
    if (!dirExists || !(await fileExists(binPath))) {
      if (this.modelDownloader === null) {
        throw new Error("Model not found and no downloader configured");
      }
      await this.modelDownloader.downloadModelAsync(modelId, modelPath);
    }

    return modelPath;
  }

  /**
   * Verifies `modelPath`'s `pytorch_model.bin` against `expectedChecksum`.
   * Mirrors the checksum check inside the C# `GetModelPathAsync(modelId,
   * expectedChecksum)` overload, exposed here as the IModelManager contract.
   */
  async verifyModelAsync(
    modelPath: string,
    expectedChecksum: Uint8Array,
  ): Promise<boolean> {
    if (this.disposed) throw new Error("LocalModelManager is disposed");
    if (expectedChecksum === null || expectedChecksum === undefined)
      return true;
    if (expectedChecksum.length === 0) return true;

    const binPath = path.join(modelPath, "pytorch_model.bin");
    const actual = await computeSha256Bytes(binPath);
    return sequenceEqual(actual, expectedChecksum);
  }

  private static sanitizeModelId(modelId: string): string {
    return modelId.split("/").join("_").split("\\").join("_");
  }

  private static async directoryExists(p: string): Promise<boolean> {
    try {
      const st = await fs.stat(p);
      return st.isDirectory();
    } catch {
      return false;
    }
  }

  dispose(): void {
    if (this.disposed) return;
    (this.modelDownloader as { dispose?: () => void } | null)?.dispose?.();
    this.disposed = true;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// SafeModelHandle — CircleAI.Core.SafeModelHandle
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Wrapper around an opaque native model pointer (a `llama_model*` in C#). The
 * release callback is supplied by the loader so this module stays free of
 * native imports. `handle` is a number (the native pointer value), 0 == invalid.
 */
export class SafeModelHandle {
  private handleValue: number;
  private releaseCallback: ((handle: number) => void) | null;
  private released = false;

  /**
   * Construct around a native pointer with a release callback. With no
   * arguments the handle is invalid until {@link setHandle} +
   * {@link withReleaseCallback} run (the P/Invoke default-ctor path).
   */
  constructor(nativeHandle = 0, releaseCallback: ((handle: number) => void) | null = null) {
    this.handleValue = nativeHandle;
    if (nativeHandle !== 0 && releaseCallback === null) {
      throw new Error("releaseCallback is required for a non-zero handle");
    }
    this.releaseCallback = releaseCallback;
  }

  get isInvalid(): boolean {
    return this.handleValue === 0;
  }

  /** The raw native pointer value (0 when invalid). */
  get handle(): number {
    return this.handleValue;
  }

  /** Sets the native pointer (the loader path when constructed via marshalling). */
  setHandle(handle: number): void {
    this.handleValue = handle;
  }

  /** Wires up the release callback after construction. Returns `this`. */
  withReleaseCallback(releaseCallback: (handle: number) => void): this {
    if (releaseCallback === null || releaseCallback === undefined)
      throw new Error("releaseCallback is required");
    this.releaseCallback = releaseCallback;
    return this;
  }

  /** Releases the handle via the callback. Idempotent. */
  release(): boolean {
    if (this.handleValue !== 0 && !this.released) {
      this.releaseCallback?.(this.handleValue);
      this.handleValue = 0;
      this.released = true;
    }
    return true;
  }

  /** Alias for {@link release} — matches the C# IDisposable surface. */
  dispose(): void {
    this.release();
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// PlatformInterop — CircleAI.Core.PlatformInterop
// ─────────────────────────────────────────────────────────────────────────────

/**
 * The injected native binding. `loadFromFile` returns a native pointer value
 * (non-zero on success, 0 on failure) and `free` releases it — the P/Invoke
 * surface that C#'s PlatformInterop imports from llama.cpp. In this port the
 * native library is injected so the module has no FFI dependency.
 */
export interface INativeModelBinding {
  loadFromFile(path: string): number;
  free(handle: number): void;
}

/**
 * Loads native models. Callers receive an opaque {@link SafeModelHandle}. The
 * native llama.cpp binding is injected (the C# version P/Invokes it directly).
 */
export const PlatformInterop = {
  /**
   * Loads a GGUF model from `path` using the injected `binding`.
   * @throws Error when the path is empty, the file does not exist, or the
   *   native load fails.
   */
  async loadModel(
    path_: string,
    binding: INativeModelBinding,
  ): Promise<SafeModelHandle> {
    if (path_ === null || path_ === undefined || path_.trim() === "")
      throw new Error("Model path is required.");
    if (!(await fileExists(path_)))
      throw new Error(`GGUF model file not found: ${path_}`);

    const nativeHandle = binding.loadFromFile(path_);
    if (nativeHandle === 0) {
      throw new Error(
        `llama.cpp failed to load model at '${path_}'. ` +
          "Verify the file is a valid GGUF and that the native llama " +
          "library is on the search path.",
      );
    }

    return new SafeModelHandle(nativeHandle, (h) => {
      if (h !== 0) binding.free(h);
    });
  },
};

// ─────────────────────────────────────────────────────────────────────────────
// ICircleModule / IEmbeddingService / CircleEngine
// ─────────────────────────────────────────────────────────────────────────────

/** (ICircleModule) A pluggable CircleAI module. Disposable. */
export interface ICircleModule {
  readonly moduleName: string;
  initAsync(engine: CircleEngine): Promise<void>;
  readonly isModelLoaded: boolean;
  dispose(): void;
}

/** (IEmbeddingService) A module that turns text into a fixed-size vector. */
export interface IEmbeddingService extends ICircleModule {
  generateEmbedding(text: string): Float32Array;
  readonly embeddingSize: number;
}

/**
 * A key by which a module is registered / retrieved. In C# the key is a `Type`;
 * here callers use a stable string (typically the interface name).
 */
export type ModuleKey = string;

/**
 * (CircleEngine) Top-level facade for the CircleAI on-device stack. Holds the
 * {@link IModelLoader} and a keyed registry of attached modules. Core knows
 * nothing about downstream assemblies — they attach via
 * {@link registerModule} / {@link getModule}, or the settable
 * {@link embeddingService}.
 */
export class CircleEngine {
  private readonly modules = new Map<ModuleKey, unknown>();

  /** The model loader used to acquire + cache model files. */
  readonly modelLoader: IModelLoader;

  /**
   * Optional embedding service. Kept untyped (the C# `object?`) so Core does not
   * reference downstream embedding implementations.
   */
  embeddingService: unknown = null;

  constructor(modelLoader: IModelLoader) {
    if (modelLoader === null || modelLoader === undefined)
      throw new Error("modelLoader is required");
    this.modelLoader = modelLoader;
  }

  /** Register a module instance keyed by `key` (typically its interface name). */
  registerModule<T>(key: ModuleKey, module: T): this {
    if (module === null || module === undefined)
      throw new Error("module is required");
    this.modules.set(key, module);
    return this;
  }

  /** Retrieve a previously-registered module, or null if none was registered. */
  getModule<T>(key: ModuleKey): T | null {
    return this.modules.has(key) ? (this.modules.get(key) as T) : null;
  }

  /** True if a module has been registered under `key`. */
  hasModule(key: ModuleKey): boolean {
    return this.modules.has(key);
  }
}
