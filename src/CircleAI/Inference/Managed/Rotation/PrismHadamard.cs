// PrismHadamard.cs
//
// The rotation Bonsai 2's weights are stored in (PrismML's prism.hadamard metadata). Every
// listed weight was folded with a normalised Sylvester-Walsh-Hadamard matrix H - applied
// block by block along its input - and a +-1 sign vector s, so its input must be turned the
// same way before the product:
//
//   x' = H(s * x)                      before each folded weight (ssm_out may first regroup
//                                      its heads: gdn_v_grouped)
//   h  = s * (H z)                     after a token-embedding lookup, the table being stored
//                                      turned; H is its own inverse
//
// In version 2 the output head is the embedding table read forward (tied), so its input is
// turned like any folded weight's. An activation feeding several weights of one width is
// turned once: those weights share one InputTransform.
//
// Ported from the pinned fork (prism-b10743-adfffbe): the loader's checks from
// llama-model.cpp, the per-weight transform from llama-graph.cpp (build_lora_mm,
// build_embd_rows), and the transform itself from ggml-cpu's ops.cpp
// (ggml_compute_forward_fwht) - which scales by 1/sqrt(block) first and then runs the
// butterflies in widening order, so this gives the fork's floats to the last bit.

using CircleAI.Inference.Managed.Gguf;

namespace CircleAI.Inference.Managed.Rotation;

/// <summary>What one folded weight needs done to its input: an optional head regrouping, signs, then H per block.</summary>
public sealed class InputTransform
{
    private readonly float[]? _signs;
    private readonly (int Hd, int Nk, int Rep)? _grouping;

    internal InputTransform(int width, int blockSize, float[]? signs, (int Hd, int Nk, int Rep)? grouping)
    {
        Width = width;
        BlockSize = blockSize;
        _signs = signs;
        _grouping = grouping;
    }

    /// <summary>The input width this transform turns.</summary>
    public int Width { get; }

    /// <summary>The Hadamard block size.</summary>
    public int BlockSize { get; }

    /// <summary>Whether this transform regroups DeltaNet heads first, and so cannot work in place.</summary>
    public bool Regroups => _grouping is not null;

    /// <summary>
    /// Writes the turned <paramref name="x"/> to <paramref name="output"/>, the same length. They may
    /// be the same memory unless <see cref="Regroups"/>.
    /// </summary>
    public void Apply(ReadOnlySpan<float> x, Span<float> output)
    {
        if (x.Length != Width || output.Length != Width)
            throw new ArgumentException($"This transform turns {Width} values, not {x.Length} into {output.Length}.");

        if (_grouping is { } grouping)
        {
            if (x.Overlaps(output)) throw new ArgumentException("A regrouping transform needs its output apart from its input.");
            var (hd, nk, rep) = grouping;
            // tiled [hd, nk, rep] -> grouped [hd, rep, nk] feature order
            for (var r = 0; r < rep; r++)
            for (var k = 0; k < nk; k++)
                x.Slice(hd * (k + nk * r), hd).CopyTo(output.Slice(hd * (r + rep * k), hd));
        }
        else
        {
            x.CopyTo(output);
        }

        if (_signs is not null)
        {
            for (var i = 0; i < output.Length; i++) output[i] *= _signs[i];
        }
        for (var b = 0; b < output.Length; b += BlockSize) PrismHadamard.Transform(output.Slice(b, BlockSize));
    }
}

/// <summary>A Bonsai 2 file's Hadamard folding: which weights take a turned input, and the turn itself.</summary>
public sealed class PrismHadamard
{
    private const string EmbeddingName = "token_embd.weight";

    private static readonly string[] FoldableKinds =
    [
        "attn_q", "attn_k", "attn_v", "attn_qkv", "attn_gate", "attn_output",
        "ffn_gate", "ffn_up", "ffn_down",
        "ffn_gate_exps", "ffn_up_exps", "ffn_down_exps", "ffn_gate_up_exps",
        "ffn_gate_shexp", "ffn_up_shexp", "ffn_down_shexp",
        "ssm_out",
    ];

    private readonly Dictionary<string, InputTransform> _inputs;
    private readonly Dictionary<int, float[]> _signs;

    private PrismHadamard(int version, int blockSize, bool tiedOutput, Dictionary<string, InputTransform> inputs,
        Dictionary<int, float[]> signs, bool invertsEmbedding)
    {
        Version = version;
        BlockSize = blockSize;
        TiedOutput = tiedOutput;
        _inputs = inputs;
        _signs = signs;
        InvertsEmbedding = invertsEmbedding;
    }

    /// <summary>prism.hadamard.version: 1, or 2 with the output head tied to the embedding table.</summary>
    public int Version { get; }

    /// <summary>The Hadamard block size (512 for the 2B and 4B).</summary>
    public int BlockSize { get; }

    /// <summary>Whether the output head is the embedding table, its input turned like a folded weight's.</summary>
    public bool TiedOutput { get; }

    /// <summary>Whether a token-embedding lookup must be turned back (the table is stored turned).</summary>
    public bool InvertsEmbedding { get; }

    /// <summary>Folded weights, by name; with a tied output, token_embd.weight stands for the head.</summary>
    public IReadOnlyCollection<string> FoldedWeights => _inputs.Keys;

    /// <summary>The transform the input of <paramref name="weightName"/> needs, or null when it is not folded.</summary>
    public InputTransform? InputOf(string weightName) => _inputs.GetValueOrDefault(weightName);

    /// <summary>Turns an embedding row back to the model's basis, in place: h = s * (H z).</summary>
    public void InvertLookup(Span<float> row)
    {
        if (!InvertsEmbedding) throw new InvalidOperationException("This model's embedding table is not stored turned.");
        if (row.Length % BlockSize != 0) throw new ArgumentException($"A row of {row.Length} is not whole blocks of {BlockSize}.", nameof(row));
        for (var b = 0; b < row.Length; b += BlockSize) Transform(row.Slice(b, BlockSize));
        if (_signs.Count == 0) return; // sign_mode identity
        if (!_signs.TryGetValue(row.Length, out var signs)) throw new InvalidOperationException($"No sign vector for width {row.Length}.");
        for (var i = 0; i < row.Length; i++) row[i] *= signs[i];
    }

    /// <summary>
    /// The normalised Sylvester-Walsh-Hadamard transform of one block, in place, as the fork's
    /// ggml_compute_forward_fwht computes it: scale by 1/sqrt(n) first, then the butterflies
    /// (u + v, u - v) at widths 1, 2, 4 ... n/2. The block length must be a power of two.
    /// </summary>
    public static unsafe void Transform(Span<float> block)
    {
        var n = block.Length;
        if (n == 0 || (n & (n - 1)) != 0) throw new ArgumentException($"A Hadamard block is a power of two, not {n}.", nameof(block));
        var scale = 1.0f / MathF.Sqrt(n);
        fixed (float* p = block)
        {
            for (var j = 0; j < n; j++) p[j] *= scale;
            for (var len = 1; len < n; len <<= 1)
            {
                for (var i = 0; i < n; i += 2 * len)
                {
                    var a = p + i;
                    var c = a + len;
                    for (var j = 0; j < len; j++)
                    {
                        var u = a[j];
                        var v = c[j];
                        a[j] = u + v;
                        c[j] = u - v;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The folding <paramref name="file"/> declares, checked as the fork checks it; null when
    /// the file has none (no prism.hadamard.version). Anything the fork would refuse is refused.
    /// </summary>
    public static PrismHadamard? FromGguf(GgufFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!file.TryGetValue("prism.hadamard.version", out var versionValue)) return null;

        var architecture = file["general.architecture"].AsString();
        if (architecture is not ("llama" or "qwen3" or "qwen3moe" or "qwen35" or "qwen35moe" or "qwen3next"))
            throw Refused($"architecture '{architecture}' is not verified to turn every folded weight's input");

        var version = (int)versionValue.AsInt64();
        if (version is not (1 or 2)) throw Refused($"version {version} is not supported");
        var tied = file.TryGetValue("prism.hadamard.tied_output", out var tiedValue) && tiedValue.AsBool();
        if ((version == 2) != tied) throw Refused("version 2 requires tied_output=true; version 1 forbids it");
        if (tied && file.Tensor("output.weight") is not null) throw Refused("tied_output requires output.weight to be absent");

        var blockSize = file["prism.hadamard.block_size"].AsInt64();
        if (blockSize <= 0 || blockSize > 1 << 20 || (blockSize & (blockSize - 1)) != 0) throw Refused($"block_size {blockSize} is not a power of two");
        var transform = file["prism.hadamard.transform"].AsString();
        if (transform != "normalized-sylvester-walsh-hadamard") throw Refused($"transform '{transform}' is not supported");
        var axis = file["prism.hadamard.axis"].AsString();
        if (axis != "input-last-dimension") throw Refused($"axis '{axis}' is not supported");
        var signMode = file["prism.hadamard.sign_mode"].AsString();
        if (signMode is not ("identity" or "explicit")) throw Refused($"sign_mode '{signMode}' is not supported");

        var weightNames = file["prism.hadamard.weight_names"].AsArray().ToStringArray();
        if (weightNames.Length == 0) throw Refused("weight_names is empty");

        // Explicit signs: one +-1 vector per input width, laid end to end.
        var signs = new Dictionary<int, float[]>();
        if (signMode == "explicit")
        {
            var widths = file["prism.hadamard.sign_widths"].AsArray().ToInt32Array();
            var values = file["prism.hadamard.sign_values"].AsArray().ToInt32Array();
            if (widths.Length == 0) throw Refused("sign_mode is explicit but sign_widths is empty");
            var at = 0;
            foreach (var width in widths)
            {
                if (width <= 0 || width % blockSize != 0 || at + width > values.Length) throw Refused($"sign width {width} is invalid");
                var vector = new float[width];
                for (var i = 0; i < width; i++)
                {
                    vector[i] = values[at + i] switch
                    {
                        1 => 1f,
                        -1 => -1f,
                        _ => throw Refused("sign values must be +1 or -1"),
                    };
                }
                signs[width] = vector; // a repeated width: the later wins, as in the fork
                at += width;
            }
            if (at != values.Length) throw Refused("sign_values length does not match sign_widths");
        }

        var grouped = file.TryGetValue("prism.hadamard.gdn_v_grouped", out var groupedValue) && groupedValue.AsBool();

        var folded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in weightNames)
        {
            if (!IsFoldable(name)) throw Refused($"weight '{name}' is not on a verified Hadamard-aware product");
            if (!folded.Add(name)) throw Refused($"weight '{name}' is listed twice");
        }

        var inverses = file.TryGetValue("prism.hadamard.inverse_weight_names", out var inverseValue)
            ? inverseValue.AsArray().ToStringArray()
            : [];
        var invertsEmbedding = false;
        foreach (var name in inverses)
        {
            if (name != EmbeddingName) throw Refused($"'{name}' is not a verified inverse-after-lookup table");
            if (folded.Contains(name) || invertsEmbedding) throw Refused($"inverse weight '{name}' is listed twice");
            invertsEmbedding = true;
        }

        if (tied)
        {
            if (!invertsEmbedding) throw Refused("tied_output requires a turned token embedding");
            folded.Add(EmbeddingName); // the head reads the embedding table forward
        }
        else if (invertsEmbedding && file.Tensor("output.weight") is null)
        {
            throw Refused("a tied output requires version 2 and tied_output=true");
        }

        // One transform per (width, regrouping), shared by every weight that reads it.
        var shared = new Dictionary<(int Width, bool Grouped), InputTransform>();
        var inputs = new Dictionary<string, InputTransform>(StringComparer.Ordinal);
        foreach (var name in folded)
        {
            var tensor = file.Tensor(name) ?? throw Refused($"weight '{name}' is not in the file");
            var width = checked((int)tensor.Dimensions[0]);
            if (width % blockSize != 0) throw Refused($"block size {blockSize} does not divide the input width {width} of {name}");
            float[]? vector = null;
            if (signs.Count > 0 && !signs.TryGetValue(width, out vector)) throw Refused($"there is no sign vector for width {width} ({name})");

            var regroups = grouped && name.Contains(".ssm_out.", StringComparison.Ordinal);
            if (!shared.TryGetValue((width, regroups), out var input))
            {
                (int, int, int)? grouping = null;
                if (regroups)
                {
                    var nv = (int)file[architecture + ".ssm.time_step_rank"].AsInt64();
                    var nk = (int)file[architecture + ".ssm.group_count"].AsInt64();
                    if (nk <= 0 || nv <= 0 || nv % nk != 0 || width % nv != 0) throw Refused($"bad DeltaNet head geometry for {name}");
                    grouping = (width / nv, nk, nv / nk);
                }
                input = new InputTransform(width, (int)blockSize, vector, grouping);
                shared[(width, regroups)] = input;
            }
            inputs[name] = input;
        }

        return new PrismHadamard(version, (int)blockSize, tied, inputs, signs, invertsEmbedding);
    }

    // The fork's is_foldable_weight: output.weight, or blk.<n>.<kind>.weight for a verified kind.
    private static bool IsFoldable(string name)
    {
        if (name == "output.weight") return true;
        if (!name.StartsWith("blk.", StringComparison.Ordinal)) return false;
        var pos = 4;
        while (pos < name.Length && char.IsAsciiDigit(name[pos])) pos++;
        if (pos == 4 || pos >= name.Length || name[pos] != '.') return false;
        var rest = name[(pos + 1)..];
        return FoldableKinds.Any(kind => rest == kind + ".weight");
    }

    private static InvalidDataException Refused(string why) => new($"prism.hadamard: {why}.");
}
