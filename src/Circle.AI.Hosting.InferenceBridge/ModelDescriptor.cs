// ModelDescriptor.cs
//
// Canonical metadata for a single loaded model in the inference bridge.
// One descriptor per loaded model — independent of how many apps reference it.

namespace Circle.AI.Hosting.InferenceBridge;

/// <summary>
/// On-disk encoding format of a model weight artefact.
/// </summary>
public enum ModelFormat
{
    /// <summary>llama.cpp GGUF (general GGML universal format).</summary>
    Gguf,

    /// <summary>ONNX Runtime model file.</summary>
    Onnx,

    /// <summary>Apple Core ML model package.</summary>
    CoreMl,

    /// <summary>TensorFlow Lite flatbuffer.</summary>
    Tflite,

    /// <summary>Format not recognised or not yet classified.</summary>
    Unknown,
}

/// <summary>
/// Canonical descriptor for a single loaded model. The inference bridge
/// publishes one of these per loaded model so callers can decide whether a
/// candidate model is suitable for their request.
/// </summary>
/// <param name="ModelId">
/// Canonical, human-readable model name (e.g. <c>"llama-3.1-8b-instruct"</c>).
/// Unique within a single bridge instance.
/// </param>
/// <param name="Version">Semantic version or model-card checkpoint identifier.</param>
/// <param name="Format">On-disk encoding of the weights.</param>
/// <param name="ContextWindowTokens">Maximum context length the model was trained or fine-tuned for.</param>
/// <param name="VocabSize">Tokeniser vocabulary size.</param>
/// <param name="ParameterCount">Total trainable parameter count (e.g. <c>8_000_000_000</c> for an 8B model).</param>
/// <param name="QuantisationLabel">
/// Quantisation profile (<c>"Q4_K_M"</c>, <c>"INT8"</c>, <c>"FP16"</c>, …).
/// <c>null</c> when the model is full-precision or no label is published.
/// </param>
/// <param name="ApproximateMemoryBytes">
/// Approximate working-set bytes the model occupies once loaded
/// (weights + KV cache headroom). Used by callers to decide whether a device
/// has room.
/// </param>
public sealed record ModelDescriptor(
    string ModelId,
    string Version,
    ModelFormat Format,
    int ContextWindowTokens,
    int VocabSize,
    long ParameterCount,
    string? QuantisationLabel,
    long ApproximateMemoryBytes);
