// MnnInterop.cs
//
// P/Invoke bindings for the MNN inference engine (Alibaba Group).
// https://github.com/alibaba/MNN  |  https://github.com/alibaba/MNN/tree/master/transformers/llm
//
// MNN replaces llama.cpp across all Circle AI platforms. Reasons:
//   - 8.6× faster prefill on Android ARM64
//   - 3.7× faster decode on Android ARM64
//   - Up to 40% lower peak RAM
//   - Native Qwen3 / Qwen3.5 template support (no manual ChatML wrapping needed)
//   - Chinese-origin stack, no Western inference dependency
//
// Architecture — three native layers:
//   mnnbridge        — thin C wrapper over MNN-LLM C++ API (built from CircleAI/native/mnn-bridge/)
//   MNN              — core MNN runtime (Alibaba pre-built binaries per platform)
//   MNN_CL           — optional OpenCL GPU backend for mobile GPU acceleration
//
// Native library names by platform:
//   Windows   → mnnbridge.dll     + MNN.dll     (+ MNN_CL.dll  optional)
//   Android   → libmnnbridge.so   + libMNN.so   (+ libMNN_CL.so optional)
//   Linux     → libmnnbridge.so   + libMNN.so
//   macOS     → libmnnbridge.dylib + libMNN.dylib
//   iOS       → statically linked into the app bundle
//
// Build instructions: CircleAI/native/mnn-bridge/BUILD.md
//
// Priority model family:
//   Qwen3 / Qwen3.5  (Alibaba)     — all text tiers, 201 languages
//   Kimi-VL-A3B      (Moonshot AI) — vision+language, 24 GB+ devices

using System;
using System.Runtime.InteropServices;

namespace CircleAI.Inference;

// ─────────────────────────────────────────────────────────────────────────────
// Safe handles
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Native handle wrapping a MNN-LLM model instance.
/// Released via <see cref="MnnInterop.mnn_llm_free"/>.
/// </summary>
internal sealed class MnnModelHandle : SafeHandle
{
    public MnnModelHandle() : base(IntPtr.Zero, ownsHandle: true) { }
    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        if (handle != IntPtr.Zero)
        {
            MnnInterop.mnn_llm_free(handle);
            handle = IntPtr.Zero;
        }
        return true;
    }
}

/// <summary>
/// Native handle wrapping a MNN image embedding (vision input for Kimi-VL / Qwen-VL).
/// Released via <see cref="MnnInterop.mnn_llm_image_free"/>.
/// </summary>
internal sealed class MnnImageHandle : SafeHandle
{
    public MnnImageHandle() : base(IntPtr.Zero, ownsHandle: true) { }
    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        if (handle != IntPtr.Zero)
        {
            MnnInterop.mnn_llm_image_free(handle);
            handle = IntPtr.Zero;
        }
        return true;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Streaming callback
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Callback invoked per output token fragment during streaming generation.
/// </summary>
/// <param name="token">
/// Raw pointer to a NUL-terminated UTF-8 byte sequence owned by the native
/// bridge. May be <c>null</c> on flush signals. The pointer is only valid
/// for the duration of the callback — decode immediately via
/// <c>Marshal.PtrToStringUTF8((IntPtr)token)</c>; do not store the pointer.
/// </param>
/// <param name="isDone">Non-zero when the generation sequence has ended (EOS or stop sequence).</param>
/// <param name="userData">
/// Opaque pointer passed through unchanged from the generate call.
/// Use <see cref="System.Runtime.InteropServices.GCHandle"/> to carry managed state.
/// </param>
/// <remarks>
/// <para>
/// This delegate's parameter list MUST be fully blittable because the assembly
/// has <c>[DisableRuntimeMarshalling]</c> applied (see <c>AssemblyInfo.cs</c>).
/// A managed <c>string</c> here would have no marshaller and corrupt the
/// stack when the native bridge invokes the callback. The CA1420 analyzer
/// is upgraded to an error in the csproj to prevent regressions.
/// </para>
/// </remarks>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate void MnnTokenCallback(byte* token, int isDone, IntPtr userData);

// ─────────────────────────────────────────────────────────────────────────────
// P/Invoke entry points
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// P/Invoke entry points for the <c>mnnbridge</c> native library — a thin C
/// wrapper over MNN-LLM's C++ API. Internal-only; callers should use
/// <see cref="QwenTextGenerator"/> or <see cref="KimiVlGenerator"/> rather than
/// calling these directly.
/// </summary>
internal static partial class MnnInterop
{
    /// <summary>Resolved bridge library name. The resolver maps this to the platform-correct filename.</summary>
    public const string LibraryName = "mnnbridge";

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a MNN-LLM model instance from a GGUF or native MNN model file.
    /// Returns an invalid (zero) handle on failure.
    /// </summary>
    /// <param name="modelPath">Absolute path to the model file.</param>
    [LibraryImport(LibraryName, EntryPoint = "mnn_llm_create", StringMarshalling = StringMarshalling.Utf8)]
    public static partial MnnModelHandle mnn_llm_create(string modelPath);

    /// <summary>Frees the model and releases all associated native memory.</summary>
    [LibraryImport(LibraryName, EntryPoint = "mnn_llm_free")]
    public static partial void mnn_llm_free(IntPtr handle);

    /// <summary>
    /// Loads model weights into device memory. Must be called once after
    /// <see cref="mnn_llm_create"/> before any inference.
    /// </summary>
    /// <returns>0 on success; negative error code on failure.</returns>
    [LibraryImport(LibraryName, EntryPoint = "mnn_llm_load")]
    public static partial int mnn_llm_load(MnnModelHandle handle);

    // ── Model metadata ────────────────────────────────────────────────────────

    /// <summary>Returns the context window size (in tokens) for the loaded model.</summary>
    [LibraryImport(LibraryName, EntryPoint = "mnn_llm_get_context_size")]
    public static partial int mnn_llm_get_context_size(MnnModelHandle handle);

    /// <summary>Returns the vocabulary size for the loaded model.</summary>
    [LibraryImport(LibraryName, EntryPoint = "mnn_llm_get_vocab_size")]
    public static partial int mnn_llm_get_vocab_size(MnnModelHandle handle);

    /// <summary>
    /// Returns model type: <c>0</c> = text-only (Qwen3/Qwen3.5),
    /// <c>1</c> = vision+language (Kimi-VL-A3B, Qwen-VL).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "mnn_llm_get_model_type")]
    public static partial int mnn_llm_get_model_type(MnnModelHandle handle);

    // ── Tokenisation ──────────────────────────────────────────────────────────

    /// <summary>
    /// Tokenises UTF-8 <paramref name="text"/> into <paramref name="tokens"/>.
    /// Returns the number of tokens written, or a negative value when the buffer
    /// is too small (in which case <c>-result</c> is the required size).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "mnn_llm_tokenize", StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial int mnn_llm_tokenize(
        MnnModelHandle handle,
        string text,
        int* tokens,
        int maxTokens);

    /// <summary>
    /// Converts a token id into its UTF-8 string form, written into
    /// <paramref name="buf"/>. Returns bytes written (negative = required size).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "mnn_llm_token_to_text")]
    public static unsafe partial int mnn_llm_token_to_text(
        MnnModelHandle handle,
        int token,
        byte* buf,
        int bufLen);

    // ── Text inference ────────────────────────────────────────────────────────

    /// <summary>
    /// Synchronous (non-streaming) generation with sampling parameters.
    /// Writes the full response into <paramref name="output"/>.
    /// For UI use, prefer <see cref="mnn_llm_generate_stream_ex"/>.
    /// </summary>
    /// <param name="temperature">Sampling temperature. 0.0 = greedy.</param>
    /// <param name="topP">Nucleus sampling p. 1.0 = disabled.</param>
    /// <param name="topK">Top-K sampling. 0 = disabled.</param>
    /// <param name="seed">RNG seed. 0 = use MNN internal RNG.</param>
    /// <returns>Number of characters written, or negative on error.</returns>
    [LibraryImport(LibraryName, EntryPoint = "mnn_llm_generate_ex", StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial int mnn_llm_generate_ex(
        MnnModelHandle handle,
        string prompt,
        byte* output,
        int maxOutputLen,
        int maxNewTokens,
        float temperature,
        float topP,
        int topK,
        uint seed);

    /// <summary>
    /// Streaming generation with sampling parameters.
    /// Calls <paramref name="callback"/> once per output token fragment.
    /// When <c>isDone != 0</c> the sequence has ended (EOS or stop token).
    /// <paramref name="userData"/> is passed through to the callback unchanged;
    /// use a pinned <see cref="System.Runtime.InteropServices.GCHandle"/> for managed state.
    /// </summary>
    /// <returns>Total tokens generated, or negative on error.</returns>
    [LibraryImport(LibraryName, EntryPoint = "mnn_llm_generate_stream_ex", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mnn_llm_generate_stream_ex(
        MnnModelHandle handle,
        string prompt,
        int maxNewTokens,
        float temperature,
        float topP,
        int topK,
        uint seed,
        MnnTokenCallback callback,
        IntPtr userData);

    // ── KV-cache / session state ──────────────────────────────────────────────

    /// <summary>
    /// Saves the KV-cache for the current conversation to <paramref name="path"/>.
    /// Allows resuming without re-prefilling the context.
    /// </summary>
    /// <returns>0 on success.</returns>
    [LibraryImport(LibraryName, EntryPoint = "mnn_llm_save_session", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mnn_llm_save_session(MnnModelHandle handle, string path);

    /// <summary>Loads a previously saved KV-cache session from <paramref name="path"/>.</summary>
    /// <returns>0 on success.</returns>
    [LibraryImport(LibraryName, EntryPoint = "mnn_llm_load_session", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mnn_llm_load_session(MnnModelHandle handle, string path);

    /// <summary>Clears the KV-cache and resets the conversation state.</summary>
    [LibraryImport(LibraryName, EntryPoint = "mnn_llm_reset_session")]
    public static partial void mnn_llm_reset_session(MnnModelHandle handle);

    // ── Vision (Kimi-VL / Qwen-VL) ───────────────────────────────────────────

    /// <summary>
    /// Creates an image embedding from raw bytes (JPEG, PNG, or WebP).
    /// Used with Kimi-VL-A3B-Thinking-2506 and Qwen-VL vision models.
    /// Returns an invalid handle on failure.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "mnn_llm_image_from_bytes")]
    public static unsafe partial MnnImageHandle mnn_llm_image_from_bytes(byte* data, int len);

    /// <summary>Frees a vision image embedding.</summary>
    [LibraryImport(LibraryName, EntryPoint = "mnn_llm_image_free")]
    public static partial void mnn_llm_image_free(IntPtr handle);

    /// <summary>
    /// Streaming multimodal generation. Encodes <paramref name="image"/> into
    /// the context alongside the text prompt, then streams output tokens via
    /// <paramref name="callback"/>. Requires a vision model (type == 1).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "mnn_llm_generate_with_image_stream_ex", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mnn_llm_generate_with_image_stream_ex(
        MnnModelHandle handle,
        string prompt,
        MnnImageHandle image,
        int maxNewTokens,
        float temperature,
        float topP,
        int topK,
        uint seed,
        MnnTokenCallback callback,
        IntPtr userData);

    // ── Embeddings ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the embedding vector dimension for a model loaded in embedding mode.
    /// Returns a positive integer on success; 0 or negative if the model does not
    /// support embedding output.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "mnn_embed_get_dim")]
    public static partial int mnn_embed_get_dim(MnnModelHandle handle);

    /// <summary>
    /// Embeds UTF-8 <paramref name="text"/> into a dense float vector.
    /// Writes at most <paramref name="maxDims"/> floats into <paramref name="output"/>.
    /// Returns the number of floats written on success, or a negative error code.
    /// The caller is responsible for L2-normalisation if required.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "mnn_embed_text", StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial int mnn_embed_text(
        MnnModelHandle handle,
        string text,
        float* output,
        int maxDims);

    // ── KV cache compression (mnnbridge 1.2.0 — wired to MNN ATTENTION_OPTION) ──
    //
    // As of mnnbridge 1.2.0, non-zero modes translate to MNN's
    // ATTENTION_OPTION runtime hint (CPUAttention.cpp). The mode is applied
    // at load() time. Mapping:
    //   Off (0)            -> attention_mode 8  (flash on, FP16 KV)
    //   TurboQuant4Bit (1) -> attention_mode 14 (flash on, K+V TQ4)
    //   TurboQuant3Bit (2) -> attention_mode 12 (flash on, K+V TQ3)
    //   TurboQuant2Bit (3) -> attention_mode 12 (MNN has no native 2-bit; -> TQ3)
    // KvCompressionApplyResult.NotImplemented stays for back-compat but is no
    // longer returned by current mnnbridge builds.

    /// <summary>
    /// Sets the requested KV cache compression mode on a loaded handle.
    /// Returns the raw C ABI status code (0 = applied, 1 = invalid mode,
    /// 2 = scaffolded but not yet implemented natively, &lt;0 = handle invalid).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "mnn_llm_set_kv_compression_mode")]
    public static partial int mnn_llm_set_kv_compression_mode(MnnModelHandle handle, int mode);

    /// <summary>
    /// Returns the currently-stored KV compression mode (0..3), or -1 on
    /// invalid handle. Reflects the last value passed to
    /// <see cref="mnn_llm_set_kv_compression_mode"/>, not whether the native
    /// path is honouring it.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "mnn_llm_get_kv_compression_mode")]
    public static partial int mnn_llm_get_kv_compression_mode(MnnModelHandle handle);

    // ── Convenience wrappers ──────────────────────────────────────────────────

    // ── TurboQuant codec (parity surface) ────────────────────────────────────
    //
    // Round-trip a vector through the native TurboQuantCodec port that lives
    // alongside mnnbridge. Used by tests to validate the managed
    // CircleAI.Core.Compression.TurboQuantCodec produces numerically
    // identical results to the C++ port.
    [LibraryImport(LibraryName, EntryPoint = "mnn_turboquant_round_trip")]
    public static unsafe partial int mnn_turboquant_round_trip(
        float* vector,
        int    dim,
        int    bitsPerDim,
        float* output);

    /// <summary>Save the KV-cache session. Returns <c>true</c> on success.</summary>
    public static bool SaveSession(MnnModelHandle handle, string path)
        => mnn_llm_save_session(handle, path) == 0;

    /// <summary>Load a KV-cache session. Returns <c>true</c> on success.</summary>
    public static bool LoadSession(MnnModelHandle handle, string path)
        => mnn_llm_load_session(handle, path) == 0;
}

/// <summary>
/// KV cache compression mode. Mirrors the C ABI's integer encoding so the
/// managed and native layers agree without translation tables.
/// </summary>
public enum KvCompressionMode
{
    /// <summary>Full FP16 KV cache — default behaviour, always supported.</summary>
    Off = 0,

    /// <summary>TurboQuant at 4 bits per channel — ~4× shrink, &lt; 1% accuracy loss expected.</summary>
    TurboQuant4Bit = 1,

    /// <summary>TurboQuant at 3 bits per channel — ~5× shrink, marginal accuracy loss expected.</summary>
    TurboQuant3Bit = 2,

    /// <summary>TurboQuant at 2 bits per channel — ~8× shrink, noticeable accuracy loss expected.</summary>
    TurboQuant2Bit = 3,
}

/// <summary>
/// Outcome of <see cref="MnnInterop.mnn_llm_set_kv_compression_mode"/> after
/// being translated into a typed result. Mirrors the C ABI status codes.
/// </summary>
public enum KvCompressionApplyResult
{
    /// <summary>Native path accepted the mode and will use it.</summary>
    Applied = 0,

    /// <summary>The mode value was outside the valid 0..3 range.</summary>
    InvalidMode = 1,

    /// <summary>
    /// LEGACY (mnnbridge ≤ 1.1.0) — scaffolding-only response. As of
    /// mnnbridge 1.2.0 the native path is wired through MNN's
    /// ATTENTION_OPTION hint, so this status is no longer returned.
    /// Kept for binary back-compat with older bridges.
    /// </summary>
    NotImplemented = 2,

    /// <summary>Handle pointer was invalid.</summary>
    HandleInvalid = -1,
}

/// <summary>
/// Typed wrapper over the KV-compression C ABI so callers don't deal with
/// raw integers. Internal because <see cref="MnnModelHandle"/> is internal.
/// As of mnnbridge 1.2.0, non-Off modes route through MNN's native
/// ATTENTION_OPTION hint (TurboQuant TQ3/TQ4 attention path in
/// <c>CPUAttention.cpp</c>) — actual KV compression takes effect at
/// load() time.
/// </summary>
internal static class MnnKvCompression
{
    /// <summary>Applies the requested mode and returns the typed result.</summary>
    public static KvCompressionApplyResult Set(MnnModelHandle handle, KvCompressionMode mode)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var raw = MnnInterop.mnn_llm_set_kv_compression_mode(handle, (int)mode);
        return raw switch
        {
            0 => KvCompressionApplyResult.Applied,
            1 => KvCompressionApplyResult.InvalidMode,
            2 => KvCompressionApplyResult.NotImplemented,
            _ => KvCompressionApplyResult.HandleInvalid,
        };
    }

    /// <summary>Reads the last-set mode (or <see cref="KvCompressionMode.Off"/> on invalid handle).</summary>
    public static KvCompressionMode Get(MnnModelHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var raw = MnnInterop.mnn_llm_get_kv_compression_mode(handle);
        return raw is >= 0 and <= 3 ? (KvCompressionMode)raw : KvCompressionMode.Off;
    }
}
