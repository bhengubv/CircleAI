// vision/onnx_backend.ts
//
// Injection seam for the ONNX Runtime dependency used by OnnxFaceDetector.cs,
// OnnxFaceEmbedder.cs and OnnxPlateRecognizer.cs.
//
// Per the porting contract (see voice/onnx_backend.ts + embeddings/index.ts),
// the native runtime is injected behind an interface so the port is
// deterministic and needs no native library. This mirrors the C#
// Microsoft.ML.OnnxRuntime.InferenceSession + DenseTensor<T> / NamedOnnxValue
// surface, reduced to what the vision components use: build a session from a
// model path, discover the first input / output name, and run a single named
// float tensor to a single named float output tensor.

/** A dense float32 tensor: flat row-major data + its shape. Mirrors `DenseTensor<float>`. */
export interface DenseTensor {
  readonly data: Float32Array;
  readonly dims: readonly number[];
}

/** Build a float32 dense tensor from flat data + shape. */
export function floatTensor(data: Float32Array, dims: readonly number[]): DenseTensor {
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
  run(feeds: Readonly<Record<string, DenseTensor>>): Record<string, DenseTensor>;

  /** Release native session resources. */
  dispose(): void;
}

/**
 * Factory that builds an {@link IOnnxSession} from a model file path. The
 * production factory wraps the ONNX runtime; inject a fake in tests. The
 * analogue of `new InferenceSession(modelPath, opts)` behind the input/output
 * name discovery that the C# constructors perform.
 */
export type OnnxSessionFactory = (modelPath: string) => IOnnxSession;
