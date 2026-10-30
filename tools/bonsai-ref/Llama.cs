// Llama.cs
//
// The slice of llama.h this tool needs, declared against the pinned fork's own
// header (native/llama-bridge/build-android-v7a/_deps/llama_cpp-src/include/llama.h,
// tag prism-b10743-adfffbe). The structs are passed BY VALUE, so their field order
// and types must match that header exactly: this fork adds `dspark_head_source`
// first in llama_model_params and replaces use_mmap/use_mlock with `load_mode`,
// so a struct copied from upstream llama.cpp would silently shift every field.
// C `bool` is one byte, declared here as `byte`.

using System.Runtime.InteropServices;

namespace CircleAI.Tools.BonsaiRef;

[StructLayout(LayoutKind.Sequential)]
internal struct LlamaModelParams
{
    public nint DsparkHeadSource;
    public nint Devices;
    public nint TensorBuftOverrides;
    public int NGpuLayers;
    public int SplitMode;
    public int LoadMode;
    public int MainGpu;
    public nint TensorSplit;
    public nint ProgressCallback;
    public nint ProgressCallbackUserData;
    public nint KvOverrides;
    public byte VocabOnly;
    public byte CheckTensors;
    public byte UseExtraBufts;
    public byte NoHost;
    public byte NoAlloc;
    public byte LoadMtp;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LlamaContextParams
{
    public uint NCtx;
    public uint NBatch;
    public uint NUbatch;
    public uint NSeqMax;
    public uint NRsSeq;
    public uint NOutputsMax;
    public uint NOutputsMaxPerSeq;
    public int NThreads;
    public int NThreadsBatch;
    public int CtxType;
    public int RopeScalingType;
    public int PoolingType;
    public int AttentionType;
    public int FlashAttnType;
    public float RopeFreqBase;
    public float RopeFreqScale;
    public float YarnExtFactor;
    public float YarnAttnFactor;
    public float YarnBetaFast;
    public float YarnBetaSlow;
    public uint YarnOrigCtx;
    public float DefragThold;
    public nint CbEval;
    public nint CbEvalUserData;
    public int TypeK;
    public int TypeV;
    public nint PathKvMeanCenter;
    public nint AbortCallback;
    public nint AbortCallbackData;
    public byte Embeddings;
    public byte OffloadKqv;
    public byte NoPerf;
    public byte OpOffload;
    public byte SwaFull;
    public byte KvUnified;
    public nint Samplers;
    public nuint NSamplers;
    public nint CtxOther;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LlamaBatch
{
    public int NTokens;
    public nint Token;
    public nint Embd;
    public nint Pos;
    public nint NSeqId;
    public nint SeqId;
    public nint Logits;
}

internal static unsafe class Llama
{
    private const string Lib = "llama";

    [DllImport(Lib)] public static extern void llama_backend_init();
    [DllImport(Lib)] public static extern void llama_backend_free();
    [DllImport(Lib)] public static extern LlamaModelParams llama_model_default_params();
    [DllImport(Lib)] public static extern LlamaContextParams llama_context_default_params();
    [DllImport(Lib)] public static extern nint llama_model_load_from_file(byte* path, LlamaModelParams p);
    [DllImport(Lib)] public static extern void llama_model_free(nint model);
    [DllImport(Lib)] public static extern nint llama_init_from_model(nint model, LlamaContextParams p);
    [DllImport(Lib)] public static extern void llama_free(nint ctx);
    [DllImport(Lib)] public static extern nint llama_model_get_vocab(nint model);
    [DllImport(Lib)] public static extern int llama_vocab_n_tokens(nint vocab);
    [DllImport(Lib)] public static extern byte llama_vocab_is_eog(nint vocab, int token);
    [DllImport(Lib)] public static extern int llama_model_meta_count(nint model);
    [DllImport(Lib)] public static extern int llama_model_meta_key_by_index(nint model, int i, byte* buf, nuint size);
    [DllImport(Lib)] public static extern int llama_model_meta_val_str_by_index(nint model, int i, byte* buf, nuint size);
    [DllImport(Lib)] public static extern int llama_model_meta_val_str(nint model, byte* key, byte* buf, nuint size);
    [DllImport(Lib)] public static extern int llama_tokenize(nint vocab, byte* text, int textLen, int* tokens, int nTokensMax, byte addSpecial, byte parseSpecial);
    [DllImport(Lib)] public static extern int llama_token_to_piece(nint vocab, int token, byte* buf, int length, int lstrip, byte special);
    [DllImport(Lib)] public static extern LlamaBatch llama_batch_get_one(int* tokens, int nTokens);
    [DllImport(Lib)] public static extern int llama_decode(nint ctx, LlamaBatch batch);
    [DllImport(Lib)] public static extern float* llama_get_logits_ith(nint ctx, int i);
    [DllImport(Lib)] public static extern nint llama_get_memory(nint ctx);
    [DllImport(Lib)] public static extern void llama_memory_clear(nint mem, byte data);
}
