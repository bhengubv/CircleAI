// PlatformInterop.cs
//
// Thin shim that loads a native llama.cpp model via P/Invoke and returns it
// wrapped in a SafeModelHandle. All previous TFLite (Android) and CoreML
// (iOS) branches have been removed — llama.cpp covers every supported
// platform via a single DllImport with platform-aware library naming
// (llama.dll on Windows, libllama.so on Linux/Android, libllama.dylib on
// macOS/iOS).
//
// NOTE: For full inference (chat / generation), prefer the strongly-typed
// API in CircleAI.Inference (QwenTextGenerator). This shim exists so older
// callers in CircleAI.Embeddings can keep handing around SafeModelHandle
// values until the embedding path is rewritten on top of llama.cpp.

using System;
using System.IO;
using System.Runtime.InteropServices;
using CircleAI.Core;

/// <summary>
/// Loads native models via llama.cpp. Callers receive an opaque
/// <see cref="SafeModelHandle"/> they can pass on to inference code.
/// </summary>
public static partial class PlatformInterop
{
    private const string LibraryName = "llama";

    /// <summary>
    /// Loads a GGUF model from <paramref name="path"/> using llama.cpp.
    /// </summary>
    /// <exception cref="ArgumentException">Path is null or empty.</exception>
    /// <exception cref="FileNotFoundException">Model file does not exist.</exception>
    /// <exception cref="InvalidOperationException">Native load failed.</exception>
    public static SafeModelHandle LoadModel(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Model path is required.", nameof(path));

        if (!File.Exists(path))
            throw new FileNotFoundException("GGUF model file not found.", path);

        // Initialise backend once. llama_backend_init is idempotent in modern
        // builds so a per-call invocation is safe.
        llama_backend_init();

        var modelParams = llama_model_default_params();
        IntPtr nativeHandle = llama_model_load_from_file(path, ref modelParams);
        if (nativeHandle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"llama.cpp failed to load model at '{path}'. " +
                "Verify the file is a valid GGUF and that the native llama " +
                "library is on the search path.");

        return new SafeModelHandle(nativeHandle, FreeModel);
    }

    private static void FreeModel(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
            llama_model_free(handle);
    }

    // -- minimal native bindings (mirrors of the entries in
    //    CircleAI.Inference.LlamaCppInterop, kept here only because Core
    //    must not take a project reference on Inference). ------------------

    // THE FOUR FLAGS ARE byte AND NOT bool, AND THAT IS NOT A STYLE CHOICE.
    // [assembly: DisableRuntimeMarshalling] turns the whole assembly's P/Invokes
    // into blit-only calls, and a bool is not blittable - the [MarshalAs(I1)] that
    // used to convert it is exactly the runtime marshalling that is now switched
    // off, so the struct could not legally cross a ref parameter. llama.cpp
    // declares these as C `bool`, one byte each, so byte is what the native side
    // already saw; nothing in C# reads or writes them - the struct is filled by
    // llama_model_default_params and handed straight back - so there is no call
    // site to convert. 0 is false, non-zero is true, if one ever needs reading.
    [StructLayout(LayoutKind.Sequential)]
    private struct LlamaModelParamsCompat
    {
        public IntPtr devices;
        public IntPtr tensor_buft_overrides;
        public int    n_gpu_layers;
        public int    split_mode;
        public int    main_gpu;
        public IntPtr tensor_split;
        public IntPtr progress_callback;
        public IntPtr progress_callback_user_data;
        public IntPtr kv_overrides;
        public byte   vocab_only;
        public byte   use_mmap;
        public byte   use_mlock;
        public byte   check_tensors;
    }

    // LibraryImport, not DllImport: the source generator writes the marshalling
    // code at compile time instead of asking a runtime marshaller that is disabled.
    // CharSet/BestFitMapping/ThrowOnUnmappableChar are gone with it - they were
    // instructions to that marshaller. StringMarshalling.Utf8 is what the old
    // [MarshalAs(LPUTF8Str)] on the path meant, and it is what llama.cpp expects,
    // so a model under a non-ASCII path still loads.
    [LibraryImport(LibraryName, EntryPoint = "llama_backend_init")]
    private static partial void llama_backend_init();

    [LibraryImport(LibraryName, EntryPoint = "llama_model_default_params")]
    private static partial LlamaModelParamsCompat llama_model_default_params();

    [LibraryImport(LibraryName, EntryPoint = "llama_model_load_from_file",
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr llama_model_load_from_file(
        string path_model,
        ref LlamaModelParamsCompat @params);

    [LibraryImport(LibraryName, EntryPoint = "llama_model_free")]
    private static partial void llama_model_free(IntPtr model);
}
