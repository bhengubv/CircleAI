/**
 * inference/download
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// inference/download.ts
//
// Port of CircleAI.Inference.IModelDownloadService + ModelDownloadService +
// BundleFileSpec. The C# service does HTTP downloads to a real filesystem with
// SHA-256 verification. Per the port brief, the two external seams — the
// network (HttpClient) and the disk (System.IO) — are injected behind
// interfaces, with deterministic in-memory implementations. Everything that is
// algorithmically load-bearing is byte-faithful:
//
//   • Single-file path {storage}/{modelId}.gguf; bundle path {storage}/{modelId}/.
//   • ModelScope Primary (API) + Fallback (CDN) URL construction.
//   • SHA-256 verification with StripShaAlgorithmPrefix normalisation.
//   • Overall + per-file progress reporting (capped at 0.999 mid-flight).
//   • installed.json manifest stamping (InstalledManifest shape).
//   • Cached-and-valid skip, temp-then-move atomicity, cleanup on failure.

import { createHash } from "../platform";
import type { BundleFile, InstalledManifest } from "../models/index";

/**
 * One file in a model bundle. Compatible shape with CircleAI.Core.Models.
 * Ported from CircleAI.Inference.BundleFileSpec.
 */
export interface BundleFileSpec {
  readonly name: string;
  /** SHA-256 in `sha256:<hex>` or bare-hex form. */
  readonly sha256: string;
  readonly sizeBytes: number;
}

/** Progress callback (0-1). Mirrors IProgress<double>. */
export type ProgressCallback = (fraction: number) => void;

/**
 * Downloads and manages model files. Ported from
 * CircleAI.Inference.IModelDownloadService. The `ct` cancellation token of the
 * C# signature maps to an optional AbortSignal.
 */
export interface IModelDownloadService {
  /**
   * Ensures a single model file is present and matches `expectedSha256`.
   * Returns the absolute path to the cached file.
   */
  ensureModel(
    modelId: string,
    downloadUri: string,
    expectedSha256: string | null,
    progress: ProgressCallback | null,
    signal?: AbortSignal,
  ): Promise<string>;

  /**
   * Ensures every file in `bundleFiles` is present under a per-model directory
   * and matches its pinned SHA-256. Returns the model directory path.
   */
  ensureBundle(
    modelId: string,
    repo: string,
    bundleFiles: readonly BundleFileSpec[],
    progress: ProgressCallback | null,
    signal?: AbortSignal,
  ): Promise<string>;

  /** true when the single-file model exists on disk. */
  isModelCached(modelId: string, signal?: AbortSignal): Promise<boolean>;

  /** Deletes the model file or directory if it exists. No-op when absent. */
  deleteModel(modelId: string, signal?: AbortSignal): Promise<void>;

  /** Free bytes on the drive hosting the storage directory. */
  getAvailableDiskSpaceBytes(signal?: AbortSignal): Promise<number>;
}

// ── injected seams ────────────────────────────────────────────────────────────

/**
 * Network seam — the bytes for a URL. Mirrors the HttpClient.GetAsync stream in
 * ModelDownloadService.DownloadToFileAsync. Progress is reported as bytes arrive.
 */
export interface IByteSource {
  /** Fetch the full byte content of `uri`. Throws when the resource is unreachable. */
  fetch(uri: string, progress: ProgressCallback | null, signal?: AbortSignal): Promise<Uint8Array>;
}

/**
 * Disk seam — an in-memory-or-real filesystem. Mirrors the System.IO surface
 * ModelDownloadService uses (create dir, write/move/delete, exists, free space).
 */
export interface IFileStore {
  ensureDir(path: string): void;
  fileExists(path: string): boolean;
  dirExists(path: string): boolean;
  writeBytes(path: string, bytes: Uint8Array): void;
  readBytes(path: string): Uint8Array;
  move(src: string, dest: string): void;
  deleteFile(path: string): void;
  deleteDir(path: string): void;
  /** Free bytes on the volume hosting `path`. */
  availableFreeSpace(path: string): number;
}

/**
 * Deterministic in-memory byte source. Register (uri -> bytes) pairs; unknown
 * URIs throw (as a 404 would). This is the injected stand-in for HttpClient.
 */
export class InMemoryByteSource implements IByteSource {
  private readonly responses = new Map<string, Uint8Array>();

  /** Register the byte payload served for `uri`. */
  register(uri: string, bytes: Uint8Array): void {
    this.responses.set(uri, bytes);
  }

  async fetch(uri: string, progress: ProgressCallback | null, signal?: AbortSignal): Promise<Uint8Array> {
    signal?.throwIfAborted();
    const bytes = this.responses.get(uri);
    if (!bytes) throw new Error(`InMemoryByteSource: no response registered for '${uri}'.`);
    // Report deterministic completion progress.
    progress?.(1.0);
    return bytes;
  }
}

/** Deterministic in-memory filesystem. Stand-in for System.IO. */
export class InMemoryFileStore implements IFileStore {
  private readonly files = new Map<string, Uint8Array>();
  private readonly dirs = new Set<string>();
  private freeSpace = 64 * 1024 * 1024 * 1024; // 64 GiB default

  constructor(freeSpaceBytes?: number) {
    if (freeSpaceBytes !== undefined) this.freeSpace = freeSpaceBytes;
  }

  ensureDir(path: string): void {
    this.dirs.add(normalize(path));
  }

  fileExists(path: string): boolean {
    return this.files.has(normalize(path));
  }

  dirExists(path: string): boolean {
    const p = normalize(path);
    if (this.dirs.has(p)) return true;
    // A directory exists implicitly if any file lives under it.
    const prefix = p.endsWith("/") ? p : p + "/";
    for (const f of this.files.keys()) if (f.startsWith(prefix)) return true;
    return false;
  }

  writeBytes(path: string, bytes: Uint8Array): void {
    this.files.set(normalize(path), bytes);
  }

  readBytes(path: string): Uint8Array {
    const b = this.files.get(normalize(path));
    if (!b) throw new Error(`InMemoryFileStore: file not found '${path}'.`);
    return b;
  }

  move(src: string, dest: string): void {
    const b = this.files.get(normalize(src));
    if (!b) throw new Error(`InMemoryFileStore: move source not found '${src}'.`);
    this.files.delete(normalize(src));
    this.files.set(normalize(dest), b);
  }

  deleteFile(path: string): void {
    this.files.delete(normalize(path));
  }

  deleteDir(path: string): void {
    const p = normalize(path);
    const prefix = p.endsWith("/") ? p : p + "/";
    for (const f of [...this.files.keys()]) if (f.startsWith(prefix)) this.files.delete(f);
    for (const d of [...this.dirs]) if (d === p || d.startsWith(prefix)) this.dirs.delete(d);
  }

  availableFreeSpace(_path: string): number {
    return this.freeSpace;
  }
}

// ── the service ───────────────────────────────────────────────────────────────

/**
 * Default IModelDownloadService. Ported from
 * CircleAI.Inference.ModelDownloadService. Single-file entries land at
 * {storage}/{modelId}.gguf; bundle entries at {storage}/{modelId}/ with every
 * bundle file written under it.
 */
export class ModelDownloadService implements IModelDownloadService {
  private readonly storageDirectory: string;
  private readonly source: IByteSource;
  private readonly store: IFileStore;

  constructor(storageDirectory: string, source?: IByteSource, store?: IFileStore) {
    if (!storageDirectory || storageDirectory.trim().length === 0) {
      throw new Error("Storage directory must not be empty.");
    }
    this.storageDirectory = storageDirectory;
    this.source = source ?? new InMemoryByteSource();
    this.store = store ?? new InMemoryFileStore();
    this.store.ensureDir(this.storageDirectory);
  }

  // ── Single-file (legacy) ────────────────────────────────────────────────────

  async ensureModel(
    modelId: string,
    downloadUri: string,
    expectedSha256: string | null,
    progress: ProgressCallback | null,
    signal?: AbortSignal,
  ): Promise<string> {
    validateModelId(modelId);
    if (!downloadUri) throw new Error("downloadUri required");

    const filePath = this.singleFilePath(modelId);

    if (this.store.fileExists(filePath) && expectedSha256 !== null) {
      if (await this.verifySha256(filePath, expectedSha256)) {
        progress?.(1.0);
        return filePath;
      }
      this.store.deleteFile(filePath);
    } else if (this.store.fileExists(filePath) && expectedSha256 === null) {
      progress?.(1.0);
      return filePath;
    }

    const tempPath = filePath + ".tmp";
    try {
      const bytes = await this.source.fetch(downloadUri, progress, signal);
      this.store.writeBytes(tempPath, bytes);

      if (expectedSha256 !== null) {
        if (!(await this.verifySha256(tempPath, expectedSha256))) {
          this.store.deleteFile(tempPath);
          throw new Error(
            `SHA-256 mismatch for model '${modelId}'. The downloaded file has been deleted.`,
          );
        }
      }

      if (this.store.fileExists(filePath)) this.store.deleteFile(filePath);
      this.store.move(tempPath, filePath);
    } catch (e) {
      if (this.store.fileExists(tempPath)) this.store.deleteFile(tempPath);
      throw e;
    }
    return filePath;
  }

  // ── Bundle ──────────────────────────────────────────────────────────────────

  async ensureBundle(
    modelId: string,
    repo: string,
    bundleFiles: readonly BundleFileSpec[],
    progress: ProgressCallback | null,
    signal?: AbortSignal,
  ): Promise<string> {
    validateModelId(modelId);
    if (!repo || repo.trim().length === 0) throw new Error("Repo path is required for bundle entries.");
    if (!bundleFiles) throw new Error("bundleFiles required");
    if (bundleFiles.length === 0) throw new Error("Bundle file list must not be empty.");

    const modelDir = joinPath(this.storageDirectory, modelId);
    this.store.ensureDir(modelDir);

    let totalBytes = 0;
    for (const f of bundleFiles) totalBytes += Math.max(0, f.sizeBytes);
    let doneBytes = 0;

    for (const file of bundleFiles) {
      signal?.throwIfAborted();
      if (!file.name || file.name.trim().length === 0) {
        throw new Error(`Bundle for '${modelId}' contains a file with no Name.`);
      }

      const destPath = joinPath(modelDir, file.name);
      this.store.ensureDir(dirName(destPath));

      // Skip when cached + valid.
      if (this.store.fileExists(destPath) && (await this.verifySha256(destPath, file.sha256))) {
        doneBytes += file.sizeBytes;
        reportOverall(progress, doneBytes, totalBytes);
        continue;
      }
      if (this.store.fileExists(destPath)) this.store.deleteFile(destPath);

      const tempPath = destPath + ".tmp";
      try {
        const perFile: ProgressCallback | null = progress
          ? (p) => reportOverall(progress, doneBytes + Math.floor(file.sizeBytes * p), totalBytes)
          : null;

        // PrimaryUrl (API) -> FallbackUrl (CDN). Try both before giving up.
        const primary = buildPrimaryUrl(repo, file.name);
        const fallback = buildFallbackUrl(repo, file.name);
        let bytes: Uint8Array;
        try {
          bytes = await this.source.fetch(primary, perFile, signal);
        } catch {
          bytes = await this.source.fetch(fallback, perFile, signal);
        }
        this.store.writeBytes(tempPath, bytes);

        if (!(await this.verifySha256(tempPath, file.sha256))) {
          this.store.deleteFile(tempPath);
          throw new Error(
            `SHA-256 mismatch for bundle file '${file.name}' of model '${modelId}'. ` +
              "The downloaded file has been deleted.",
          );
        }
        if (this.store.fileExists(destPath)) this.store.deleteFile(destPath);
        this.store.move(tempPath, destPath);
        doneBytes += file.sizeBytes;
        reportOverall(progress, doneBytes, totalBytes);
      } catch (e) {
        if (this.store.fileExists(tempPath)) {
          try {
            this.store.deleteFile(tempPath);
          } catch {
            /* best effort */
          }
        }
        throw e;
      }
    }

    progress?.(1.0);
    return modelDir;
  }

  /**
   * Stamps an installed.json manifest into `modelDir`. Best-effort — failures
   * are swallowed. Ported from ModelDownloadService.WriteInstalledManifestAsync.
   */
  async writeInstalledManifest(
    modelDir: string,
    modelId: string,
    version: string,
    repo: string | null,
    bundleFiles: readonly BundleFileSpec[],
    _signal?: AbortSignal,
  ): Promise<void> {
    try {
      if (!modelDir || modelDir.trim().length === 0) throw new Error("modelDir required");
      if (!modelId || modelId.trim().length === 0) throw new Error("modelId required");
      if (!bundleFiles) throw new Error("bundleFiles required");

      let totalBytes = 0;
      const files: BundleFile[] = [];
      for (const f of bundleFiles) {
        files.push({ name: f.name, sha256: f.sha256, sizeBytes: f.sizeBytes });
        totalBytes += Math.max(0, f.sizeBytes);
      }

      const manifest: InstalledManifest = {
        modelId,
        version: version ?? "",
        repo,
        totalBytes,
        files,
        installedAtUtc: new Date().toISOString(),
      };

      const path = joinPath(modelDir, "installed.json");
      const json = JSON.stringify(manifest, null, 2);
      this.store.writeBytes(path, new TextEncoder().encode(json));
    } catch {
      // Best-effort — a missing manifest just downgrades upgrade detection.
    }
  }

  // ── Common ────────────────────────────────────────────────────────────────

  async isModelCached(modelId: string, signal?: AbortSignal): Promise<boolean> {
    validateModelId(modelId);
    signal?.throwIfAborted();
    if (this.store.fileExists(this.singleFilePath(modelId))) return true;
    return this.store.dirExists(joinPath(this.storageDirectory, modelId));
  }

  async deleteModel(modelId: string, signal?: AbortSignal): Promise<void> {
    validateModelId(modelId);
    signal?.throwIfAborted();
    const singleFile = this.singleFilePath(modelId);
    if (this.store.fileExists(singleFile)) this.store.deleteFile(singleFile);
    const dir = joinPath(this.storageDirectory, modelId);
    if (this.store.dirExists(dir)) this.store.deleteDir(dir);
  }

  async getAvailableDiskSpaceBytes(signal?: AbortSignal): Promise<number> {
    signal?.throwIfAborted();
    return this.store.availableFreeSpace(this.storageDirectory);
  }

  // ── Helpers ─────────────────────────────────────────────────────────────────

  private singleFilePath(modelId: string): string {
    return joinPath(this.storageDirectory, `${modelId}.gguf`);
  }

  private async verifySha256(filePath: string, expectedHex: string): Promise<boolean> {
    const bytes = this.store.readBytes(filePath);
    const actualHex = createHash("sha256").update(bytes).digest("hex").toUpperCase();
    const expectedNormalised = stripShaAlgorithmPrefix(expectedHex).toUpperCase();
    return actualHex === expectedNormalised;
  }
}

/**
 * Returns the hex portion of a SHA-256 checksum, stripping an optional leading
 * algorithm token of the form `sha256:`, `SHA-256:`, etc. Ported verbatim from
 * ModelDownloadService.StripShaAlgorithmPrefix.
 */
export function stripShaAlgorithmPrefix(raw: string): string {
  if (!raw) return "";
  const trimmed = raw.trim();
  const colon = trimmed.indexOf(":");
  if (colon < 0) return trimmed;
  const prefix = trimmed.substring(0, colon);
  if (prefix.length > 0 && prefix.length <= 16) {
    let isAlgName = true;
    for (const c of prefix) {
      if (!(isLetterOrDigit(c) || c === "-" || c === "_")) {
        isAlgName = false;
        break;
      }
    }
    if (isAlgName) return trimmed.substring(colon + 1).trim();
  }
  return trimmed;
}

/** ModelScope API-form URL. Matches BuildPrimaryUrl. */
export function buildPrimaryUrl(repo: string, fileName: string): string {
  return `https://modelscope.cn/api/v1/models/${repo}/repo?Revision=master&FilePath=${encodeURIComponent(fileName)}`;
}

/** ModelScope CDN-form URL. Matches BuildFallbackUrl. */
export function buildFallbackUrl(repo: string, fileName: string): string {
  return `https://modelscope.cn/models/${repo}/resolve/master/${encodeURIComponent(fileName)}`;
}

// ── module-local helpers ──────────────────────────────────────────────────────

function reportOverall(p: ProgressCallback | null, done: number, total: number): void {
  if (!p) return;
  if (total <= 0) p(0.0);
  else p(Math.min(0.999, done / total));
}

function validateModelId(modelId: string): void {
  if (!modelId || modelId.trim().length === 0) throw new Error("Model ID must not be empty.");
}

function isLetterOrDigit(c: string): boolean {
  return /[\p{L}\p{N}]/u.test(c);
}

function normalize(path: string): string {
  return path.replace(/\\/g, "/");
}

function joinPath(a: string, b: string): string {
  const left = a.endsWith("/") || a.endsWith("\\") ? a.slice(0, -1) : a;
  return normalize(`${left}/${b}`);
}

function dirName(path: string): string {
  const p = normalize(path);
  const idx = p.lastIndexOf("/");
  return idx < 0 ? "" : p.substring(0, idx);
}
