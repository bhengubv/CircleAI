// Qwen35Config.cs
//
// The shape of a qwen35 model - every Bonsai 2 size is one - read from its GGUF the way the
// pinned fork's llama_model_qwen35::load_arch_hparams reads it. Nothing is assumed about the
// 2B: the 4B, 9B and 27B differ only in these numbers.

using CircleAI.Inference.Managed.Gguf;

namespace CircleAI.Inference.Managed.Model;

/// <summary>A qwen35 model's hyperparameters.</summary>
public sealed record Qwen35Config(
    int Layers,
    int Embedding,
    int FeedForward,
    int Heads,
    int KvHeads,
    int HeadDim,
    int RopeDims,
    float RopeBase,
    float RmsEps,
    int FullAttentionInterval,
    int ContextLength,
    int ConvKernel,
    int SsmInner,
    int SsmState,
    int SsmGroups,
    int SsmHeads,
    int Vocabulary)
{
    /// <summary>Width of one DeltaNet value head.</summary>
    public int SsmHeadDim => SsmInner / SsmHeads;

    /// <summary>Channels the DeltaNet convolution runs over: q, k (SsmGroups heads each) and v.</summary>
    public int ConvChannels => SsmInner + 2 * SsmGroups * SsmState;

    /// <summary>Whether <paramref name="layer"/> is a Gated DeltaNet layer (three of every four) rather than full attention.</summary>
    public bool IsRecurrent(int layer) => (layer + 1) % FullAttentionInterval != 0;

    /// <summary>The shape of <paramref name="file"/>'s qwen35 model; anything else is refused.</summary>
    public static Qwen35Config FromGguf(GgufFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var arch = file["general.architecture"].AsString();
        if (arch != "qwen35") throw new NotSupportedException($"Architecture '{arch}' is not qwen35.");
        if (file.TryGetValue("qwen35.attention.recurrent_layers", out _))
            throw new NotSupportedException("An explicit recurrent-layer list is not supported; the fleet sets full_attention_interval.");
        if (file.TryGetValue("qwen35.nextn_predict_layers", out var nextn) && nextn.AsInt64() != 0)
            throw new NotSupportedException("A file with an MTP head is read in S5, not here.");

        int Int(string key) => checked((int)file["qwen35." + key].AsInt64());
        var headDim = Int("attention.key_length");
        if (Int("attention.value_length") != headDim) throw new NotSupportedException("Key and value heads of different widths are not supported.");

        var config = new Qwen35Config(
            Layers: Int("block_count"),
            Embedding: Int("embedding_length"),
            FeedForward: Int("feed_forward_length"),
            Heads: Int("attention.head_count"),
            KvHeads: Int("attention.head_count_kv"),
            HeadDim: headDim,
            RopeDims: Int("rope.dimension_count"),
            RopeBase: (float)file["qwen35.rope.freq_base"].AsDouble(),
            RmsEps: (float)file["qwen35.attention.layer_norm_rms_epsilon"].AsDouble(),
            FullAttentionInterval: file.TryGetValue("qwen35.full_attention_interval", out var interval) ? checked((int)interval.AsInt64()) : 4,
            ContextLength: Int("context_length"),
            ConvKernel: Int("ssm.conv_kernel"),
            SsmInner: Int("ssm.inner_size"),
            SsmState: Int("ssm.state_size"),
            SsmGroups: Int("ssm.group_count"),
            SsmHeads: Int("ssm.time_step_rank"),
            Vocabulary: file["tokenizer.ggml.tokens"].AsArray().Count);

        if (config.Heads % config.KvHeads != 0) throw new NotSupportedException("Query heads must be whole groups of key/value heads.");
        if (config.SsmInner % config.SsmHeads != 0 || config.SsmHeadDim != config.SsmState)
            throw new NotSupportedException("DeltaNet heads whose value width differs from the state width are not supported.");
        if (config.RopeDims % 2 != 0 || config.RopeDims > config.HeadDim) throw new NotSupportedException("Rotary dims must be even and fit a head.");
        CheckTextRope(file, config.RopeDims);
        return config;
    }

    // qwen35 uses interleaved multi-section RoPE (IMROPE). For text every position component
    // is the token's position, so it is plain rotate-half RoPE - provided every rotary pair
    // falls in the t, h or w sections. A pair falling to the fourth (extra) component would
    // take a position text does not have, so such a file is refused, not guessed at.
    private static void CheckTextRope(GgufFile file, int ropeDims)
    {
        if (!file.TryGetValue("qwen35.rope.dimension_sections", out var value)) return;
        var sections = value.AsArray().ToInt32Array();
        if (sections.Length < 3) throw new NotSupportedException("rope.dimension_sections needs at least three sections.");
        var total = sections.Sum();
        if (total <= 0) return;
        for (var pair = 0; pair < ropeDims / 2; pair++)
        {
            var sector = pair % total;
            var covered = (sector % 3 == 1 && sector < 3 * sections[1])
                || (sector % 3 == 2 && sector < 3 * sections[2])
                || (sector % 3 == 0 && sector < 3 * sections[0]);
            if (!covered) throw new NotSupportedException($"Rotary pair {pair} falls outside the t, h and w sections.");
        }
    }
}
