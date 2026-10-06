#nullable enable

// LlamaInterop.cs
//
// P/Invoke for native/llama-bridge (libllamabridge), mirroring MnnInterop:
// source-generated [LibraryImport], a SafeHandle for the native pointer, and
// the same return convention (0 = ok, <0 = error, >0 = data-bearing).
//
// ⚠️ NO RUNTIME MARSHALLING IN THIS ASSEMBLY. CircleAI.Inference sets
// [DisableRuntimeMarshalling] and turns CA1420 into an ERROR on purpose: a
// delegate or SafeHandle in a DllImport here corrupts the stack, and it
// surfaced once as a 0xC0000005 on the first streamed token. So:
//   * the streaming callback is an UNMANAGED FUNCTION POINTER, never a
//     delegate marshalled by the runtime;
//   * strings cross as UTF-8 byte* the caller pins, never as string;
//   * the handle crosses as IntPtr via DangerousGetHandle, never as SafeHandle.
// Keep it that way. The build will stop you, which is the point.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CircleAI.Inference;

/// <summary>
/// Owns the native llama-bridge handle. Mirrors <c>MnnModelHandle</c>.
/// </summary>
internal sealed class LlamaModelHandle : SafeHandle
{
    public LlamaModelHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        LlamaInterop.Free(handle);
        return true;
    }
}

internal static unsafe partial class LlamaInterop
{
    /// <summary>
    /// Native library name. Resolves to <c>llamabridge.dll</c> on Windows and
    /// <c>libllamabridge.so</c> on Android/Linux.
    /// </summary>
    public const string LibraryName = "llamabridge";

    // ---- lifecycle ---------------------------------------------------------

    [LibraryImport(LibraryName, EntryPoint = "llama_bridge_create", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial LlamaModelHandle Create(string ggufPath, int nCtx, int nThreads);

    [LibraryImport(LibraryName, EntryPoint = "llama_bridge_free")]
    internal static partial void Free(IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "llama_bridge_load")]
    internal static partial int Load(LlamaModelHandle handle);

    // ---- model facts -------------------------------------------------------

    [LibraryImport(LibraryName, EntryPoint = "llama_bridge_get_context_size")]
    internal static partial int GetContextSize(LlamaModelHandle handle);

    [LibraryImport(LibraryName, EntryPoint = "llama_bridge_get_vocab_size")]
    internal static partial int GetVocabSize(LlamaModelHandle handle);

    [LibraryImport(LibraryName, EntryPoint = "llama_bridge_get_arch")]
    internal static partial int GetArch(LlamaModelHandle handle, Span<byte> buffer, int bufferSize);

    // ---- tokens ------------------------------------------------------------

    [LibraryImport(LibraryName, EntryPoint = "llama_bridge_tokenize", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Tokenize(
        LlamaModelHandle handle, string text, Span<int> outTokens, int maxTokens, int addSpecial);

    [LibraryImport(LibraryName, EntryPoint = "llama_bridge_token_to_text")]
    internal static partial int TokenToText(
        LlamaModelHandle handle, int tokenId, Span<byte> buffer, int bufferSize);

    [LibraryImport(LibraryName, EntryPoint = "llama_bridge_reset")]
    internal static partial void Reset(LlamaModelHandle handle);

    // ---- generation --------------------------------------------------------

    /// <summary>
    /// Streams generated text. Everything here is blittable by design — see
    /// the file header. <paramref name="handle"/> is the raw pointer from
    /// <c>DangerousGetHandle</c>, <paramref name="promptUtf8"/> is a
    /// NUL-terminated UTF-8 buffer the caller keeps alive, and
    /// <paramref name="callback"/> is an unmanaged function pointer obtained
    /// from an <c>[UnmanagedCallersOnly]</c> method.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "llama_bridge_generate_stream_text")]
    internal static partial int GenerateStreamText(
        IntPtr handle,
        byte* promptUtf8,
        int maxTokens,
        delegate* unmanaged[Cdecl]<byte*, int, void*, int> callback,
        void* userData);

    [LibraryImport(LibraryName, EntryPoint = "llama_bridge_version")]
    private static partial IntPtr VersionPtr();

    /// <summary>
    /// Bridge + llama.cpp build string, or null when the native library is not
    /// present. Null means this device has no GGUF backend — a fact to report,
    /// not a crash.
    /// </summary>
    internal static string? TryGetVersion()
    {
        try
        {
            var p = VersionPtr();
            return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
        }
        catch (DllNotFoundException)        { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }
}
