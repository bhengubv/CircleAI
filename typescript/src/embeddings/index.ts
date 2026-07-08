// embeddings/index.ts
//
// Port of CircleAI.Embeddings:
//   • ITextEmbedder        (ITextEmbedder.cs)
//   • TextEmbedder         (TextEmbedder.cs) — orchestration shell over a backend
//   • IEmbeddingBackend    (internal backend abstraction, made injectable here)
//
// The C# production backend (MnnEmbeddingBackend) loads a native MNN embedding
// model. Per the porting contract the native backend is injected behind
// IEmbeddingBackend so the embedder is deterministic and needs no native lib.
// L2 normalisation and the model-resolve/verify handshake are ported exactly.

import type { IModelManager } from "../core/index.js";

// ─────────────────────────────────────────────────────────────────────────────
// ITextEmbedder — CircleAI.Embeddings.ITextEmbedder
// ─────────────────────────────────────────────────────────────────────────────

/** On-device text embedder contract. Returns a dense vector for `text`. */
export interface ITextEmbedder {
  generateAsync(text: string): Promise<Float32Array>;
}

// ─────────────────────────────────────────────────────────────────────────────
// IEmbeddingBackend — internal backend abstraction (injectable)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Embedding-backend abstraction — the injection point that replaces the native
 * MNN library. `embed` returns an L2-normalised vector. Not required to be
 * thread-safe; the embedder serialises initialisation.
 */
export interface IEmbeddingBackend {
  /** Number of floats returned by {@link embed}. */
  readonly dimension: number;

  /** Embeds `text` and returns an L2-normalised vector. */
  embed(text: string): Float32Array;

  /** Releases any backend resources. */
  dispose(): void;
}

/** Factory that builds a backend from a resolved model path. */
export type EmbeddingBackendFactory = (modelPath: string) => IEmbeddingBackend;

/** L2-normalise in place so cosine similarity reduces to a dot product. */
export function l2Normalize(v: Float32Array): void {
  let norm = 0.0;
  for (const x of v) norm += x * x;
  norm = Math.sqrt(norm);
  if (norm < 1e-12) return; // zero vector — leave as-is
  const scale = Math.fround(1.0 / norm);
  for (let i = 0; i < v.length; i++) v[i] = Math.fround(v[i] * scale);
}

// ─────────────────────────────────────────────────────────────────────────────
// TextEmbedder — CircleAI.Embeddings.TextEmbedder
// ─────────────────────────────────────────────────────────────────────────────

/**
 * On-device text embedder backed by an injected {@link IEmbeddingBackend}.
 * Resolves + verifies the model path via {@link IModelManager}, then lazily
 * builds the backend (serialised so concurrent callers share one init).
 */
export class TextEmbedder implements ITextEmbedder {
  private readonly modelManager: IModelManager;
  private readonly expectedChecksum: Uint8Array;
  private readonly backendFactory: EmbeddingBackendFactory;

  private backend: IEmbeddingBackend | null = null;
  /** Serialises lazy init — the analogue of the C# SemaphoreSlim initGate. */
  private initPromise: Promise<IEmbeddingBackend> | null = null;
  private disposed = false;

  /**
   * @param modelManager resolves + verifies the embedding model path.
   * @param expectedChecksum expected SHA-256 of the model's `pytorch_model.bin`.
   * @param backendFactory builds the backend from the resolved path (inject a
   *   fake in tests; the production factory wraps the native MNN backend).
   */
  constructor(
    modelManager: IModelManager,
    expectedChecksum: Uint8Array,
    backendFactory: EmbeddingBackendFactory,
  ) {
    if (modelManager === null || modelManager === undefined)
      throw new Error("modelManager is required");
    if (expectedChecksum === null || expectedChecksum === undefined)
      throw new Error("expectedChecksum is required");
    if (backendFactory === null || backendFactory === undefined)
      throw new Error("backendFactory is required");
    this.modelManager = modelManager;
    this.expectedChecksum = expectedChecksum;
    this.backendFactory = backendFactory;
  }

  async generateAsync(text: string): Promise<Float32Array> {
    if (this.disposed) throw new Error("TextEmbedder is disposed");
    if (text === null || text === undefined || text.trim() === "")
      throw new Error("Text cannot be empty.");

    const backend = await this.ensureBackend();
    return backend.embed(text);
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.backend?.dispose();
  }

  private ensureBackend(): Promise<IEmbeddingBackend> {
    if (this.backend !== null) return Promise.resolve(this.backend);
    if (this.initPromise !== null) return this.initPromise;

    this.initPromise = (async () => {
      // Resolve + verify model path via the IModelManager contract.
      const modelPath = await this.modelManager.getModelPathAsync("embedding");
      const verified = await this.modelManager.verifyModelAsync(
        modelPath,
        this.expectedChecksum,
      );
      if (!verified) {
        this.initPromise = null;
        throw new Error(
          "Embedding model checksum verification failed. " +
            "The file may be corrupt or tampered with.",
        );
      }
      this.backend = this.backendFactory(modelPath);
      return this.backend;
    })();

    return this.initPromise;
  }
}
