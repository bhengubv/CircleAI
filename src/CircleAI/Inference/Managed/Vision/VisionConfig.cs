// VisionConfig.cs
//
// The shape of a Bonsai 2 vision tower (an "mmproj" GGUF, projector qwen3vl_merger), read the way
// the pinned fork's clip_model_loader::load_hparams reads it. All four towers of the fleet are this
// one shape at different widths - a ViT over 16-pixel patches, a learned 48x48 position table,
// LayerNorm, GELU, a 2x2 merge into a 2-layer MLP - and none has deepstack layers or a pre-norm,
// so a file with either is refused rather than half-read.
//
// The image is sized the fork's way for this projector: between 8 and 4096 merged tokens
// (set_limit_image_tokens(8, 4096)), each merged token 32x32 pixels; a caller narrows the top end
// with its own budget.

using CircleAI.Inference.Managed.Gguf;

namespace CircleAI.Inference.Managed.Vision;

/// <summary>A qwen3vl_merger vision tower's shape.</summary>
/// <param name="Embedding">Width of the tower's hidden state (clip.vision.embedding_length).</param>
/// <param name="Heads">Attention heads.</param>
/// <param name="FeedForward">Width of each layer's feed-forward.</param>
/// <param name="Layers">Transformer layers.</param>
/// <param name="PatchSize">Pixels on a side of one patch.</param>
/// <param name="MergeSize">Patches on a side of one merged token.</param>
/// <param name="Projection">Width of an output token: the language model's embedding width.</param>
/// <param name="LayerNormEps">The LayerNorms' epsilon.</param>
/// <param name="ImageMean">Per channel, subtracted from each pixel scaled to [0, 1].</param>
/// <param name="ImageStd">Per channel, what the centred pixel is divided by.</param>
/// <param name="PositionGrid">Positions on a side of the learned position table.</param>
public sealed record VisionConfig(
    int Embedding,
    int Heads,
    int FeedForward,
    int Layers,
    int PatchSize,
    int MergeSize,
    int Projection,
    float LayerNormEps,
    float[] ImageMean,
    float[] ImageStd,
    int PositionGrid)
{
    /// <summary>The fewest merged tokens an image becomes.</summary>
    public const int MinTokens = 8;

    /// <summary>The most merged tokens an image becomes, before any caller's budget.</summary>
    public const int MaxTokens = 4096;

    /// <summary>Width of one attention head.</summary>
    public int HeadDim => Embedding / Heads;

    /// <summary>Pixels on a side of one merged token: what an image's sides are whole multiples of.</summary>
    public int TokenSide => PatchSize * MergeSize;

    /// <summary>Pixels in one merged token.</summary>
    public int TokenArea => TokenSide * TokenSide;

    /// <summary>The shape of <paramref name="file"/>'s vision tower; anything this engine cannot run exactly is refused.</summary>
    public static VisionConfig FromGguf(GgufFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var name = Path.GetFileName(file.FilePath);
        if (!file.TryGetValue("general.architecture", out var arch) || arch.AsString() != "clip")
            throw new NotSupportedException($"{name} is not a vision tower (general.architecture is not clip).");
        if (!file.TryGetValue("clip.has_vision_encoder", out var vision) || !vision.AsBool())
            throw new NotSupportedException($"{name} has no vision encoder.");
        var projector = file.TryGetValue("clip.projector_type", out var p) ? p.AsString()
            : file.TryGetValue("clip.vision.projector_type", out var vp) ? vp.AsString() : "";
        if (projector != "qwen3vl_merger")
            throw new NotSupportedException($"{name} is a '{projector}' projector; this engine runs qwen3vl_merger, the Bonsai 2 towers'.");

        // The fork's ffn_op: gelu when use_gelu, silu when use_silu, else gelu_quick. The towers say gelu.
        if (!file.TryGetValue("clip.use_gelu", out var gelu) || !gelu.AsBool())
            throw new NotSupportedException($"{name}'s feed-forward is not GELU.");
        if (file.TryGetValue("clip.use_silu", out var silu) && silu.AsBool())
            throw new NotSupportedException($"{name} names both GELU and SiLU.");

        int Int(string key) => checked((int)file[key].AsInt64());
        var heads = Int("clip.vision.attention.head_count");
        if (file.TryGetValue("clip.vision.attention.head_count_kv", out var kv) && kv.AsInt64() != heads)
            throw new NotSupportedException($"{name} groups its key/value heads; the Bonsai towers do not.");
        if (file.TryGetValue("clip.vision.attention.key_length", out _))
            throw new NotSupportedException($"{name} sets its own head width; the Bonsai towers split the hidden width evenly.");

        // Deepstack layers feed a language model that adds them into its first layers; qwen35 has no
        // such path in the fork, and no Bonsai 2 tower has them.
        if (file.TryGetValue("clip.vision.is_deepstack_layers", out var deep))
        {
            var flags = deep.AsArray();
            for (var i = 0; i < flags.Count; i++)
            {
                if (flags.GetBool(i)) throw new NotSupportedException($"{name} has deepstack layers, which qwen35 does not read.");
            }
        }
        foreach (var tensor in file.Tensors)
        {
            if (tensor.Name.StartsWith("v.deepstack.", StringComparison.Ordinal))
                throw new NotSupportedException($"{name} has deepstack tensors, which qwen35 does not read.");
            if (tensor.Name.StartsWith("v.pre_ln.", StringComparison.Ordinal))
                throw new NotSupportedException($"{name} has a pre-norm; the Bonsai towers do not, and this engine does not guess at one.");
        }

        var mean = Floats3(file, "clip.vision.image_mean");
        var std = Floats3(file, "clip.vision.image_std");
        var position = file.Tensor("v.position_embd.weight") ?? throw new InvalidDataException($"{name} has no learned position table.");
        var grid = (int)Math.Round(Math.Sqrt(position.Rows));
        if (grid * grid != position.Rows) throw new InvalidDataException($"{name}'s position table holds {position.Rows} positions, not a square grid.");

        var config = new VisionConfig(
            Embedding: Int("clip.vision.embedding_length"),
            Heads: heads,
            FeedForward: Int("clip.vision.feed_forward_length"),
            Layers: Int("clip.vision.block_count"),
            PatchSize: Int("clip.vision.patch_size"),
            MergeSize: file.TryGetValue("clip.vision.spatial_merge_size", out var merge) ? checked((int)merge.AsInt64()) : 2,
            Projection: Int("clip.vision.projection_dim"),
            LayerNormEps: (float)file["clip.vision.attention.layer_norm_epsilon"].AsDouble(),
            ImageMean: mean,
            ImageStd: std,
            PositionGrid: grid);

        if (config.Heads <= 0 || config.Embedding % config.Heads != 0) throw new InvalidDataException($"{name}: {config.Embedding} does not split into {config.Heads} heads.");
        if (config.HeadDim % 4 != 0) throw new NotSupportedException($"{name}: a head of {config.HeadDim} does not split into the four rotary sections.");
        if (config.MergeSize != 2) throw new NotSupportedException($"{name} merges {config.MergeSize}x{config.MergeSize} patches; the fork's qwen3vl graph merges 2x2.");
        if (config.PatchSize <= 0 || config.Layers <= 0) throw new InvalidDataException($"{name} has no patches or no layers.");
        if (position.RowLength != config.Embedding) throw new InvalidDataException($"{name}'s position table is {position.RowLength} wide, not {config.Embedding}.");
        return config;
    }

    // The fork's calc_size_preserved_ratio (mtmd-image.cpp) for this projector, with no longest
    // edge: the sides rounded to whole tokens, then scaled down under the most pixels or up over
    // the fewest, keeping the aspect - in fp32, as the fork computes it.
    /// <summary>
    /// The size, in pixels, an image of <paramref name="width"/> x <paramref name="height"/> is
    /// brought to before the tower reads it: whole merged tokens on each side, at least
    /// <see cref="MinTokens"/> of them and at most <paramref name="maxPixels"/> pixels' worth
    /// (never more than <see cref="MaxTokens"/>).
    /// </summary>
    public (int Width, int Height) Fit(int width, int height, int maxPixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var align = TokenSide;
        var min = MinTokens * TokenArea;
        var max = Math.Clamp(maxPixels, min, MaxTokens * TokenArea);

        int Round(float x) => (int)MathF.Round(x / align, MidpointRounding.AwayFromZero) * align;
        int Ceil(float x) => (int)MathF.Ceiling(x / align) * align;
        int Floor(float x) => (int)MathF.Floor(x / align) * align;

        var w = Math.Max(align, Round(width));
        var h = Math.Max(align, Round(height));
        if ((long)w * h > max)
        {
            var beta = MathF.Sqrt((float)height * width / max);
            h = Math.Max(align, Floor(height / beta));
            w = Math.Max(align, Floor(width / beta));
        }
        else if ((long)w * h < min)
        {
            var beta = MathF.Sqrt((float)min / ((float)height * width));
            h = Ceil(height * beta);
            w = Ceil(width * beta);
        }
        return (w, h);
    }

    private static float[] Floats3(GgufFile file, string key)
    {
        var values = file[key].AsArray();
        if (values.Count < 3) throw new InvalidDataException($"{key} holds {values.Count} values, not three.");
        return [(float)values.GetDouble(0), (float)values.GetDouble(1), (float)values.GetDouble(2)];
    }
}
