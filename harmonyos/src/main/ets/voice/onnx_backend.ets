/**
 * voice/onnx_backend
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// voice/onnx_backend.ts
//
// Injection seam for the ONNX Runtime dependency used by OnnxTtsEngine.cs,
// KwsWakeWordDetector.cs, OnnxSpeakerIdentity.cs and OnnxSpeechEmotionDetector.cs.
//
// Per the porting contract (see embeddings/index.ts), the native runtime is
// injected behind an interface so the port is deterministic and needs no native
// library. This mirrors the C# Microsoft.ML.OnnxRuntime.InferenceSession +
// DenseTensor<T> / NamedOnnxValue surface, reduced to what the voice components
// actually use: build a session from a model path, discover the first input /
// output name, and run a single named tensor to a single named output tensor.

/** A dense tensor: flat row-major data + its shape. Mirrors `DenseTensor<T>`. */
export interface DenseTensor<T extends Float32Array | BigInt64Array> {
  readonly data: T;
  readonly dims: readonly number[];
}

/** Build a float32 dense tensor from flat data + shape. */
export function floatTensor(data: Float32Array, dims: readonly number[]): DenseTensor<Float32Array> {
  return { data, dims };
}

/** Build an int64 dense tensor from flat data + shape (VITS token ids / lengths). */
export function int64Tensor(data: BigInt64Array, dims: readonly number[]): DenseTensor<BigInt64Array> {
  return { data, dims };
}

/**
 * An ONNX inference session — the injected analogue of
 * `Microsoft.ML.OnnxRuntime.InferenceSession`. Implementations wrap a real
 * runtime (e.g. onnxruntime-node) or a deterministic fake in tests.
 */
export interface IOnnxSession {
  /** Name of the first model input (C# `InputMetadata.Keys.First()`). */
  readonly inputName: string;
  /** Name of the first model output (C# `OutputMetadata.Keys.First()`). */
  readonly outputName: string;

  /**
   * Run inference. `feeds` maps input names to tensors; the return maps output
   * names to float32 output tensors. Callers read the first output.
   */
  run(feeds: Readonly<Record<string, DenseTensor<Float32Array> | DenseTensor<BigInt64Array>>>): Record<
    string,
    DenseTensor<Float32Array>
  >;

  /** Release native session resources. */
  dispose(): void;
}

/**
 * Factory that builds an {@link IOnnxSession} from a model file path. The
 * production factory wraps the ONNX runtime; inject a fake in tests. The
 * analogue of `new InferenceSession(modelPath, opts)` behind the session-name
 * discovery that the C# constructors perform.
 */
export type OnnxSessionFactory = (modelPath: string) => IOnnxSession;
