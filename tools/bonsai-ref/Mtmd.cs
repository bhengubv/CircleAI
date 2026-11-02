// Mtmd.cs
//
// The slice of the pinned fork's tools/mtmd/mtmd.h and mtmd-helper.h this tool needs, for
// mtmd.dll built from that same source by tools/bonsai-ref/mtmd (the shipped llama.dll was
// built without it). Field order and types follow the fork's header exactly; C `bool` is one
// byte, declared here as `byte`, and the structs keep C's natural alignment (x64):
//
//   mtmd_context_params  use_gpu@0 device@8 print_timings@16 n_threads@20 image_marker@24
//                        media_marker@32 flash_attn_type@40 warmup@44 image_min_tokens@48
//                        image_max_tokens@52 cb_eval@56 cb_eval_user_data@64
//                        batch_max_tokens@72 progress_callback@80 progress_user_data@88 (96 bytes)

using System.Runtime.InteropServices;

namespace CircleAI.Tools.BonsaiRef;

[StructLayout(LayoutKind.Sequential)]
internal struct MtmdContextParams
{
    public byte UseGpu;
    public nint Device;
    public byte PrintTimings;
    public int NThreads;
    public nint ImageMarker;
    public nint MediaMarker;
    public int FlashAttnType;
    public byte Warmup;
    public int ImageMinTokens;
    public int ImageMaxTokens;
    public nint CbEval;
    public nint CbEvalUserData;
    public int BatchMaxTokens;
    public nint ProgressCallback;
    public nint ProgressCallbackUserData;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MtmdInputText
{
    public nint Text;
    public nuint TextLen;
    public byte AddSpecial;
    public byte ParseSpecial;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MtmdBitmapWrapper
{
    public nint Bitmap;
    public nint VideoCtx;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MtmdDecoderPos
{
    public uint T;
    public uint X;
    public uint Y;
    public uint Z;
}

internal static unsafe class Mtmd
{
    private const string Lib = "mtmd";

    public const int ChunkText = 0;
    public const int ChunkImage = 1;

    [DllImport(Lib)] public static extern MtmdContextParams mtmd_context_params_default();
    [DllImport(Lib)] public static extern nint mtmd_init_from_file(byte* mmprojPath, nint textModel, MtmdContextParams p);
    [DllImport(Lib)] public static extern void mtmd_free(nint ctx);
    [DllImport(Lib)] public static extern byte* mtmd_default_marker();
    [DllImport(Lib)] public static extern byte mtmd_decode_use_mrope(nint ctx);

    [DllImport(Lib)] public static extern MtmdBitmapWrapper mtmd_helper_bitmap_init_from_file(nint ctx, byte* path, byte placeholder);
    [DllImport(Lib)] public static extern uint mtmd_bitmap_get_nx(nint bitmap);
    [DllImport(Lib)] public static extern uint mtmd_bitmap_get_ny(nint bitmap);
    [DllImport(Lib)] public static extern void mtmd_bitmap_free(nint bitmap);

    [DllImport(Lib)] public static extern nint mtmd_input_chunks_init();
    [DllImport(Lib)] public static extern void mtmd_input_chunks_free(nint chunks);
    [DllImport(Lib)] public static extern nuint mtmd_input_chunks_size(nint chunks);
    [DllImport(Lib)] public static extern nint mtmd_input_chunks_get(nint chunks, nuint index);
    [DllImport(Lib)] public static extern int mtmd_input_chunk_get_type(nint chunk);
    [DllImport(Lib)] public static extern int* mtmd_input_chunk_get_tokens_text(nint chunk, nuint* count);
    [DllImport(Lib)] public static extern nint mtmd_input_chunk_get_tokens_image(nint chunk);
    [DllImport(Lib)] public static extern nuint mtmd_input_chunk_get_n_tokens(nint chunk);
    [DllImport(Lib)] public static extern int mtmd_input_chunk_get_n_pos(nint chunk);
    [DllImport(Lib)] public static extern nuint mtmd_image_tokens_get_nx(nint imageTokens);
    [DllImport(Lib)] public static extern nuint mtmd_image_tokens_get_ny(nint imageTokens);
    [DllImport(Lib)] public static extern MtmdDecoderPos mtmd_image_tokens_get_decoder_pos(nint imageTokens, int pos0, nuint index);

    [DllImport(Lib)] public static extern int mtmd_tokenize(nint ctx, nint chunks, MtmdInputText* text, nint* bitmaps, nuint count);
    [DllImport(Lib)] public static extern int mtmd_encode_chunk(nint ctx, nint chunk);
    [DllImport(Lib)] public static extern float* mtmd_get_output_embd(nint ctx);

    [DllImport(Lib)] public static extern int mtmd_helper_eval_chunk_single(nint ctx, nint lctx, nint chunk, int nPast, int seqId, int nBatch, byte logitsLast, int* newNPast);
    [DllImport(Lib)] public static extern int mtmd_helper_decode_image_chunk(nint ctx, nint lctx, nint chunk, float* embeddings, int nPast, int seqId, int nBatch, int* newNPast, nint callback, nint userData);
}
