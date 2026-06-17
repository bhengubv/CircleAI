// TurboVecInterop.cs
//
// P/Invoke bindings for turbovecbridge. Signatures derived from
// native/turbovec-bridge/include/turbovecbridge.h — keep in lock-step.
//
// Native lib is loaded from runtimes/<RID>/native/ by the .NET host.

using System;
using System.Runtime.InteropServices;

namespace CircleAI.Embeddings.Local;

internal static class TurboVecInterop
{
    private const string Lib = "turbovecbridge";

    // Status codes (mirror turbovecbridge.h).
    public const int TVB_OK                = 0;
    public const int TVB_ERR_NULL_HANDLE   = -1;
    public const int TVB_ERR_INVALID_ARG   = -2;
    public const int TVB_ERR_PANIC         = -3;
    public const int TVB_ERR_CONSTRUCT     = -4;
    public const int TVB_ERR_ADD           = -5;
    public const int TVB_ERR_IO            = -6;
    public const int TVB_ERR_INVALID_UTF8  = -7;

    // ─── Lifecycle ────────────────────────────────────────────────────────

    [DllImport(Lib, EntryPoint = "tvb_index_new", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr IndexNew(int dim, int bitWidth);

    [DllImport(Lib, EntryPoint = "tvb_index_free", CallingConvention = CallingConvention.Cdecl)]
    public static extern void IndexFree(IntPtr handle);

    // ─── Accessors ────────────────────────────────────────────────────────

    [DllImport(Lib, EntryPoint = "tvb_index_len", CallingConvention = CallingConvention.Cdecl)]
    public static extern long IndexLen(IntPtr handle);

    [DllImport(Lib, EntryPoint = "tvb_index_dim", CallingConvention = CallingConvention.Cdecl)]
    public static extern int IndexDim(IntPtr handle);

    [DllImport(Lib, EntryPoint = "tvb_index_bit_width", CallingConvention = CallingConvention.Cdecl)]
    public static extern int IndexBitWidth(IntPtr handle);

    // ─── Mutation + Search ────────────────────────────────────────────────

    [DllImport(Lib, EntryPoint = "tvb_index_add", CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe int IndexAdd(
        IntPtr handle,
        float* vectors,
        int    count);

    [DllImport(Lib, EntryPoint = "tvb_index_search", CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe int IndexSearch(
        IntPtr handle,
        float* query,
        int    k,
        long*  outIndices,
        float* outScores);

    // ─── Persistence ──────────────────────────────────────────────────────

    [DllImport(Lib, EntryPoint = "tvb_index_save", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int IndexSave(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(Lib, EntryPoint = "tvb_index_load", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr IndexLoad([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    // ─── Version ──────────────────────────────────────────────────────────

    [DllImport(Lib, EntryPoint = "tvb_abi_version", CallingConvention = CallingConvention.Cdecl)]
    public static extern int AbiVersion();

    // ─── Helpers ──────────────────────────────────────────────────────────

    public static string DescribeStatus(int code) => code switch
    {
        TVB_OK                => "OK",
        TVB_ERR_NULL_HANDLE   => "null handle",
        TVB_ERR_INVALID_ARG   => "invalid argument",
        TVB_ERR_PANIC         => "native panic (caught at FFI boundary)",
        TVB_ERR_CONSTRUCT     => "construction failed (dim or bit_width out of range)",
        TVB_ERR_ADD           => "add failed",
        TVB_ERR_IO            => "I/O error",
        TVB_ERR_INVALID_UTF8  => "invalid UTF-8 in path",
        _                     => $"unknown status {code}",
    };
}
