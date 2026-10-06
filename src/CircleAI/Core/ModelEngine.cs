namespace CircleAI.Core;

/// <summary>
/// The inference engine a model's files are meant for. This is the term that
/// makes the catalogue's <c>compatible</c> bit honest: a device ships a fixed
/// set of engines, and a model can only be <c>compatible</c> if its engine is
/// one of them. A GGUF model stays <c>compatible = 0</c> on an MNN-only device
/// until a GGUF engine is shipped — the bit tells the truth rather than offering
/// a download that could never load.
/// </summary>
/// <remarks>
/// SEPARATE FROM <see cref="ModelModality"/> and <c>ChatCapability</c>. Modality
/// says WHAT KIND of model it is (chat / vision / TTS); capability says what a
/// chat model can DO (tools / vision / long context); engine says WHICH RUNTIME
/// can open the file at all. The chat ladder shipped to date is entirely
/// <see cref="Mnn"/>; the field exists so the day a llama.cpp or ONNX chat model
/// is worth cataloguing, it can enter the table as a row and be correctly gated
/// off every device that cannot run it — no app release, no code change.
/// <para>
/// <see cref="Mnn"/> is <c>0</c> on purpose: it is the default for every entry
/// deserialised from a registry that predates this field, which matches reality —
/// the whole curated catalogue is MNN. The seeder re-derives the engine per row
/// from the entry's quantisation / architecture before it writes the catalogue,
/// so speech models that are really ONNX / ggml are stamped correctly there.
/// </para>
/// </remarks>
public enum ModelEngine
{
    /// <summary>MNN / <c>mnnbridge</c> — the engine the app ships today. Every curated chat bundle.</summary>
    Mnn = 0,

    /// <summary>llama.cpp — GGUF weights. Not shipped yet; GGUF rows stay <c>compatible = 0</c> until it is.</summary>
    LlamaCpp = 1,

    /// <summary>ONNX Runtime — the speech stack (Piper / VITS / MMS TTS, Silero VAD, some ASR).</summary>
    Onnx = 2,

    /// <summary>ggml — whisper.cpp-style ASR bundles that are ggml but not llama.cpp.</summary>
    Ggml = 3,
}
