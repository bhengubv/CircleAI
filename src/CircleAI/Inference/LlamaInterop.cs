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
    /// <remarks>
    /// ASKED ONCE PER PROCESS, AND ASKED QUIETLY. This is a hot property in
    /// disguise: <c>LlamaGenerator.IsAvailable</c> is <c>NativeVersion is not
    /// null</c>, and both <c>LlamaQuantSupport</c> and <c>DeviceModelAssessor</c>
    /// read it while classifying models. Each read used to re-enter the p/invoke,
    /// and on a device without the native library the Android runtime logs
    ///
    ///   monodroid-assembly: Shared library 'llamabridge' not loaded,
    ///                       p/invoke 'llama_bridge_version' may fail
    ///
    /// once per attempt — three times during service startup on the P30 on
    /// 2026-10-08 — and then throws DllNotFoundException, which this method
    /// catches. Exception-driven control flow on a path taken for every model in
    /// the catalogue, to re-answer a question whose answer cannot change while
    /// the process lives.
    ///
    /// NativeLibrary.TryLoad asks the loader directly instead: it returns false
    /// rather than throwing, so the absence of a GGUF backend costs one quiet
    /// probe and no stack unwinding. The catches stay — TryLoad succeeding does
    /// not guarantee the ENTRY POINT resolves, which is a different failure (a
    /// stale .so built before an API change) and still must not crash.
    /// </remarks>
    internal static string? TryGetVersion()
    {
        if (Volatile.Read(ref _probed)) return _version;

        var v = Probe();
        _version = v;
        Volatile.Write(ref _probed, true);
        return v;
    }

    // A separate flag, not "_version is null", because null IS the answer on a
    // device with no native library - and that is precisely the device that must
    // not re-probe on every model in the catalogue. Two threads racing here both
    // compute the same answer, so the worst case is one wasted probe.
    private static bool _probed;
    private static string? _version;

    private static string? Probe()
    {
        // Does the loader have it at all? No exception either way.
        if (!NativeLibrary.TryLoad(LibraryName, typeof(LlamaInterop).Assembly, null, out _))
            return null;

        try
        {
            var p = VersionPtr();
            return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
        }
        catch (DllNotFoundException)        { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }
}
