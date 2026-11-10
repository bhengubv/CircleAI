// ModelConfig.cs
//
// The shape of a qwen35 model - every Bonsai 2 size is one - read from its GGUF the way the
// pinned fork's llama_model_qwen35::load_arch_hparams reads it. Nothing is assumed about the
// 2B: the 4B, 9B and 27B differ only in these numbers.

using CircleAI.Inference.Managed.Gguf;

namespace CircleAI.Inference.Managed.Model;

/// <summary>A qwen35 model's hyperparameters.</summary>
public sealed record ModelConfig(
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
    int Vocabulary,
    int MtpLayers = 0)
{
    /// <summary>Width of one DeltaNet value head.</summary>
    public int SsmHeadDim => SsmInner / SsmHeads;

    /// <summary>Channels the DeltaNet convolution runs over: q, k (SsmGroups heads each) and v.</summary>
    public int ConvChannels => SsmInner + 2 * SsmGroups * SsmState;

    /// <summary>Whether <paramref name="layer"/> is a Gated DeltaNet layer (three of every four) rather than full attention.</summary>
    public bool IsRecurrent(int layer) => (layer + 1) % FullAttentionInterval != 0;

    /// <summary>
    /// The interleaved multi-section RoPE's sections (t, h, w, e), counted in rotary pairs, as
    /// rope.dimension_sections gives them; empty when the file gives none, and then every pair
    /// takes the token's one position.
    /// </summary>
    public int[] RopeSections { get; init; } = [];

    /// <summary>
    /// The position component that turns rotary pair <paramref name="pair"/> under IMROPE
    /// (ggml_mrope_cache_init with is_imrope): 0 the token's position t, 1 an image token's row
    /// h, 2 its column w, 3 the fourth component e. Text has t = h = w; an image token's are
    /// where the fork places it.
    /// </summary>
    public int RopeSection(int pair)
    {
        var s = RopeSections;
        if (s.Length < 3) return 0;
        var total = s.Sum();
        if (total <= 0) return 0;
        var sector = pair % total;
        if (sector % 3 == 1 && sector < 3 * s[1]) return 1;
        if (sector % 3 == 2 && sector < 3 * s[2]) return 2;
        if (sector % 3 == 0 && sector < 3 * s[0]) return 0;
        return 3;
    }

    /// <summary>
    /// The architectures whose layers this engine computes - Gated DeltaNet layers with full attention every few - by the
    /// name a model's file gives them (general.architecture). A model of one of these needs no code; a new architecture adds
    /// its layers to this engine and its name here.
    /// </summary>
    public static IReadOnlySet<string> Architectures { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "qwen35" };

    /// <summary>Whether <paramref name="file"/> carries a multi-token-prediction head, read under its architecture's own name.</summary>
    public static bool HasMtpHead(GgufFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return file.TryGetValue(file["general.architecture"].AsString() + ".nextn_predict_layers", out _);
    }

    /// <summary>The shape of <paramref name="file"/>'s model, read from its own keys; an architecture the engine has no layers for is refused.</summary>
    /// <remarks>
    /// THE SHAPE IS THE FILE'S, under the architecture's own name: every key is read as "{architecture}.{key}", so the engine
    /// is named for what it computes, not for one model family.
    /// <para>
    /// AN MTP FILE COUNTS ITS HEAD AS A BLOCK. Bonsai 2's PQ2_0-MTP files say block_count 33 and
    /// nextn_predict_layers 1: the trunk is the first 32 (the fork's n_layer() is n_layer_all minus
    /// the nextn layers) and blk.32 is the multi-token-prediction head. One head is read, as the
    /// fork's MTP graph reads one.
    /// </para>
    /// </remarks>
    public static ModelConfig FromGguf(GgufFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var arch = file["general.architecture"].AsString();
        if (!Architectures.Contains(arch))
            throw new NotSupportedException($"Architecture '{arch}' has no layers in this engine; it computes {string.Join(", ", Architectures)}.");
        if (file.TryGetValue(arch + ".attention.recurrent_layers", out _))
            throw new NotSupportedException("An explicit recurrent-layer list is not supported; the fleet sets full_attention_interval.");
        var mtp = file.TryGetValue(arch + ".nextn_predict_layers", out var nextn) ? checked((int)nextn.AsInt64()) : 0;
        if (mtp is not (0 or 1)) throw new NotSupportedException($"{mtp} MTP heads; the MTP head is one block.");

        int Int(string key) => checked((int)file[arch + "." + key].AsInt64());
        var headDim = Int("attention.key_length");
        if (Int("attention.value_length") != headDim) throw new NotSupportedException("Key and value heads of different widths are not supported.");

        var config = new ModelConfig(
            Layers: Int("block_count") - mtp,
            Embedding: Int("embedding_length"),
            FeedForward: Int("feed_forward_length"),
            Heads: Int("attention.head_count"),
            KvHeads: Int("attention.head_count_kv"),
            HeadDim: headDim,
            RopeDims: Int("rope.dimension_count"),
            RopeBase: (float)file[arch + ".rope.freq_base"].AsDouble(),
            RmsEps: (float)file[arch + ".attention.layer_norm_rms_epsilon"].AsDouble(),
            FullAttentionInterval: file.TryGetValue(arch + ".full_attention_interval", out var interval) ? checked((int)interval.AsInt64()) : 4,
            ContextLength: Int("context_length"),
            ConvKernel: Int("ssm.conv_kernel"),
            SsmInner: Int("ssm.inner_size"),
            SsmState: Int("ssm.state_size"),
            SsmGroups: Int("ssm.group_count"),
            SsmHeads: Int("ssm.time_step_rank"),
            Vocabulary: file["tokenizer.ggml.tokens"].AsArray().Count,
            MtpLayers: mtp)
        {
            RopeSections = file.TryGetValue(arch + ".rope.dimension_sections", out var sections) ? sections.AsArray().ToInt32Array() : [],
        };

        if (config.Heads % config.KvHeads != 0) throw new NotSupportedException("Query heads must be whole groups of key/value heads.");
        if (config.SsmInner % config.SsmHeads != 0 || config.SsmHeadDim != config.SsmState)
            throw new NotSupportedException("DeltaNet heads whose value width differs from the state width are not supported.");
        if (config.RopeDims % 2 != 0 || config.RopeDims > config.HeadDim) throw new NotSupportedException("Rotary dims must be even and fit a head.");
        CheckRope(config);
        return config;
    }

    // qwen35 uses interleaved multi-section RoPE (IMROPE). For text every position component is
    // the token's position, so it is plain rotate-half RoPE; an image token's row and column turn
    // the h and w pairs. Every rotary pair must fall in the t, h or w sections: a pair falling to
    // the fourth (extra) component would take a position neither text nor an image is given, so
    // such a file is refused, not guessed at.
    private static void CheckRope(ModelConfig config)
    {
        if (config.RopeSections.Length == 0) return;
        if (config.RopeSections.Length < 3) throw new NotSupportedException("rope.dimension_sections needs at least three sections.");
        for (var pair = 0; pair < config.RopeDims / 2; pair++)
        {
            if (config.RopeSection(pair) == 3) throw new NotSupportedException($"Rotary pair {pair} falls outside the t, h and w sections.");
        }
    }
}
