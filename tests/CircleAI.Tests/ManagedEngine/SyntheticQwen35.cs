// SyntheticQwen35.cs
//
// A tiny qwen35 model written as GGUF bytes, field by field, in either ternary packing - the same
// trits and scales in both - so the model's wiring can be tested without the real Bonsai files:
// two layers (one Gated DeltaNet, one full attention), widths of whole Q8_K blocks except the
// feed-forward's 384, which is three PQ2_0 blocks and so takes the fork's Q8_0 fallback.

using System.Runtime.InteropServices;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Kernels;

namespace CircleAI.Tests;

internal static class SyntheticQwen35
{
    public const int Embedding = 256;
    public const int FeedForward = 384;
    public const int Heads = 2;
    public const int KvHeads = 1;
    public const int HeadDim = 128;
    public const int SsmInner = 256;
    public const int SsmState = 128;
    public const int SsmGroups = 1;
    public const int SsmHeads = 2;
    public const int ConvKernel = 4;
    public const int Vocabulary = 32;
    public const int Layers = 2;
    public const int ConvChannels = SsmInner + 2 * SsmGroups * SsmState;

    /// <summary>The model's GGUF bytes with its ternary weights in <paramref name="packing"/>.</summary>
    /// <param name="packing">PTQ1_0 or PQ2_0.</param>
    /// <param name="seed">The weights: the same seed gives the same trits and scales in either packing.</param>
    /// <param name="mixedPacking">Writes one weight in the other packing, which the model must refuse.</param>
    public static byte[] Build(GgmlType packing, int seed = 7, bool mixedPacking = false)
    {
        var rng = new Random(seed);
        var b = new GgufBuilder()
            .String("general.architecture", "qwen35")
            .Key("qwen35.block_count", GgufType.UInt32, w => w.Write((uint)Layers))
            .Key("qwen35.embedding_length", GgufType.UInt32, w => w.Write((uint)Embedding))
            .Key("qwen35.feed_forward_length", GgufType.UInt32, w => w.Write((uint)FeedForward))
            .Key("qwen35.attention.head_count", GgufType.UInt32, w => w.Write((uint)Heads))
            .Key("qwen35.attention.head_count_kv", GgufType.UInt32, w => w.Write((uint)KvHeads))
            .Key("qwen35.attention.key_length", GgufType.UInt32, w => w.Write((uint)HeadDim))
            .Key("qwen35.attention.value_length", GgufType.UInt32, w => w.Write((uint)HeadDim))
            .Key("qwen35.rope.dimension_count", GgufType.UInt32, w => w.Write(64u))
            .Key("qwen35.rope.freq_base", GgufType.Float32, w => w.Write(10000f))
            .Key("qwen35.attention.layer_norm_rms_epsilon", GgufType.Float32, w => w.Write(1e-6f))
            .Key("qwen35.full_attention_interval", GgufType.UInt32, w => w.Write(2u))
            .Key("qwen35.context_length", GgufType.UInt32, w => w.Write(64u))
            .Key("qwen35.ssm.conv_kernel", GgufType.UInt32, w => w.Write((uint)ConvKernel))
            .Key("qwen35.ssm.inner_size", GgufType.UInt32, w => w.Write((uint)SsmInner))
            .Key("qwen35.ssm.state_size", GgufType.UInt32, w => w.Write((uint)SsmState))
            .Key("qwen35.ssm.group_count", GgufType.UInt32, w => w.Write((uint)SsmGroups))
            .Key("qwen35.ssm.time_step_rank", GgufType.UInt32, w => w.Write((uint)SsmHeads))
            .Strings("tokenizer.ggml.tokens", Enumerable.Range(0, Vocabulary).Select(i => $"t{i}").ToArray());

        var mixedDone = false;
        void Ternary(string name, int columns, int rows, float scale)
        {
            var type = packing;
            if (mixedPacking && !mixedDone && name.StartsWith("blk.1.", StringComparison.Ordinal))
            {
                type = packing == GgmlType.PQ2_0 ? GgmlType.PTQ1_0 : GgmlType.PQ2_0;
                mixedDone = true;
            }
            b.Tensor(name, [columns, rows], (uint)type, Pack(rng, columns, rows, scale, type));
        }

        void Floats(string name, long[] shape, Func<int, float> value)
        {
            var count = (int)shape.Aggregate(1L, (a, d) => a * d);
            var data = new float[count];
            for (var i = 0; i < count; i++) data[i] = value(i);
            b.Tensor(name, shape, (uint)GgmlType.F32, MemoryMarshal.AsBytes(data.AsSpan()).ToArray());
        }

        float Near(float centre, float spread) => centre + (float)(rng.NextDouble() * 2 - 1) * spread;

        Ternary("token_embd.weight", Embedding, Vocabulary, 0.25f);
        Floats("output_norm.weight", [Embedding], _ => Near(1f, 0.1f));
        for (var il = 0; il < Layers; il++)
        {
            var p = $"blk.{il}.";
            Floats(p + "attn_norm.weight", [Embedding], _ => Near(1f, 0.1f));
            Floats(p + "post_attention_norm.weight", [Embedding], _ => Near(1f, 0.1f));
            Ternary(p + "ffn_gate.weight", Embedding, FeedForward, 0.05f);
            Ternary(p + "ffn_up.weight", Embedding, FeedForward, 0.05f);
            Ternary(p + "ffn_down.weight", FeedForward, Embedding, 0.05f);
            if (il % 2 == 0)
            {
                Ternary(p + "attn_qkv.weight", Embedding, ConvChannels, 0.05f);
                Ternary(p + "attn_gate.weight", Embedding, SsmInner, 0.05f);
                Ternary(p + "ssm_out.weight", SsmInner, Embedding, 0.05f);
                Floats(p + "ssm_beta.weight", [Embedding, SsmHeads], _ => Near(0f, 0.05f));
                Floats(p + "ssm_alpha.weight", [Embedding, SsmHeads], _ => Near(0f, 0.05f));
                Floats(p + "ssm_conv1d.weight", [ConvKernel, ConvChannels], _ => Near(0f, 0.5f));
                Floats(p + "ssm_a", [SsmHeads], _ => Near(-1f, 0.2f));
                Floats(p + "ssm_dt.bias", [SsmHeads], _ => Near(0.5f, 0.1f));
                Floats(p + "ssm_norm.weight", [SsmState], _ => Near(1f, 0.1f));
            }
            else
            {
                Ternary(p + "attn_q.weight", Embedding, 2 * Heads * HeadDim, 0.05f);
                Ternary(p + "attn_k.weight", Embedding, KvHeads * HeadDim, 0.05f);
                Ternary(p + "attn_v.weight", Embedding, KvHeads * HeadDim, 0.05f);
                Ternary(p + "attn_output.weight", Heads * HeadDim, Embedding, 0.05f);
                Floats(p + "attn_q_norm.weight", [HeadDim], _ => Near(1f, 0.1f));
                Floats(p + "attn_k_norm.weight", [HeadDim], _ => Near(1f, 0.1f));
            }
        }
        return b.Build();
    }

    // Random trits and an fp16 scale per 128-weight block, packed by the fork's rule for the packing.
    private static byte[] Pack(Random rng, int columns, int rows, float scale, GgmlType packing)
    {
        var blocks = columns / 128 * rows;
        var blockBytes = packing == GgmlType.PQ2_0 ? Pq2.BlockBytes : Ptq1.BlockBytes;
        var data = new byte[blocks * blockBytes];
        var trits = new sbyte[128];
        for (var i = 0; i < blocks; i++)
        {
            for (var j = 0; j < 128; j++) trits[j] = (sbyte)(rng.Next(3) - 1);
            var d = (float)(Half)(scale * (0.5f + (float)rng.NextDouble()));
            var block = packing == GgmlType.PQ2_0 ? PackPq2(trits, d) : Ptq1KernelTests.PackReference(trits, d);
            block.CopyTo(data, i * blockBytes);
        }
        return data;
    }

    // PQ2_0 straight from trits: code = trit + 1, lowest pair first, the scale first.
    private static byte[] PackPq2(ReadOnlySpan<sbyte> trits, float scale)
    {
        var block = new byte[Pq2.BlockBytes];
        BitConverter.TryWriteBytes(block.AsSpan(0, 2), (Half)scale);
        for (var j = 0; j < 128; j++) block[Pq2.CodesOffset + j / 4] |= (byte)((trits[j] + 1) << (j % 4 * 2));
        return block;
    }
}
