// FfnSurface.cs
//
// WHICH OF EACH LAYER'S FEED-FORWARD NEURON GROUPS A WORD COMPUTES (step 3 of the streaming engine). A group is as
// wide as the Hadamard block that turns ffn_down's input (512 neurons for the 2B, twelve groups a layer): its gate
// and up rows are whole tiles side by side, and its down columns whole blocks. A group left out gives exactly zero
// after the turn, so its gate and up rows are not read and its down blocks are skipped - the same arithmetic as
// computing it on a zero input, not an approximation of it. What is approximated is the choice: the groups a word
// would have used but the surface left out.
//
// Chosen by what fired: the engine adds up each group's activity as it answers (Qwen35Model.RecordFfn), and a
// surface keeps each layer's strongest groups.

namespace CircleAI.Inference.Managed.Model;

/// <summary>Each layer's feed-forward neuron groups a word computes; the rest are left out.</summary>
public sealed class FfnSurface
{
    /// <summary>One layer's groups kept: the gate and up tiles they are, and the down blocks they feed.</summary>
    /// <param name="Tiles">Tile ranges of the gate and up rows, [First, End), merged where groups sit side by side.</param>
    /// <param name="Blocks">One byte per block of a down row: nonzero for a block of a kept group.</param>
    /// <param name="Kept">Groups kept.</param>
    /// <param name="Mask">Which groups are kept.</param>
    public sealed record LayerKeep((int First, int End)[] Tiles, byte[] Blocks, int Kept, bool[] Mask);

    private readonly LayerKeep[] _layers;

    private FfnSurface(LayerKeep[] layers, int groupSize, int groups, double fraction)
    {
        _layers = layers;
        GroupSize = groupSize;
        Groups = groups;
        Fraction = fraction;
    }

    /// <summary>Neurons in a group.</summary>
    public int GroupSize { get; }

    /// <summary>Groups in a layer.</summary>
    public int Groups { get; }

    /// <summary>The share of groups kept, over all layers.</summary>
    public double Fraction { get; }

    /// <summary>Layer <paramref name="layer"/>'s groups kept.</summary>
    public LayerKeep Layer(int layer) => _layers[layer];

    /// <summary>The surface that keeps, for each layer, the groups <paramref name="keep"/> marks.</summary>
    public static FfnSurface Of(bool[][] keep, int groupSize, int feedForward)
    {
        ArgumentNullException.ThrowIfNull(keep);
        if (groupSize <= 0 || groupSize % 128 != 0 || feedForward % groupSize != 0)
            throw new ArgumentException($"A group of {groupSize} must be whole 128-wide blocks and divide {feedForward}.", nameof(groupSize));
        var groups = feedForward / groupSize;
        var tilesPerGroup = groupSize / Kernels.Ptq1PairMatrix.TileRows;
        var blocksPerGroup = groupSize / Kernels.Ptq1.BlockWeights;
        var layers = new LayerKeep[keep.Length];
        var kept = 0;
        for (var il = 0; il < keep.Length; il++)
        {
            if (keep[il].Length != groups) throw new ArgumentException($"Layer {il} marks {keep[il].Length} groups, not {groups}.", nameof(keep));
            var ranges = new List<(int, int)>();
            var blocks = new byte[feedForward / Kernels.Ptq1.BlockWeights];
            var count = 0;
            for (var g = 0; g < groups; g++)
            {
                if (!keep[il][g]) continue;
                count++;
                var first = g * tilesPerGroup;
                if (ranges.Count > 0 && ranges[^1].Item2 == first) ranges[^1] = (ranges[^1].Item1, first + tilesPerGroup);
                else ranges.Add((first, first + tilesPerGroup));
                blocks.AsSpan(g * blocksPerGroup, blocksPerGroup).Fill(1);
            }
            layers[il] = new LayerKeep([.. ranges], blocks, count, (bool[])keep[il].Clone());
            kept += count;
        }
        return new FfnSurface(layers, groupSize, groups, keep.Length == 0 ? 1 : (double)kept / (keep.Length * groups));
    }

    /// <summary>
    /// The surface that keeps, in every layer, the <paramref name="fraction"/> of groups that fired most in
    /// <paramref name="activity"/> (layer by layer, group by group) - at least one group a layer.
    /// </summary>
    public static FfnSurface Strongest(ReadOnlySpan<double> activity, double fraction, int layers, int groupSize, int feedForward)
    {
        var groups = feedForward / groupSize;
        if (activity.Length != layers * groups) throw new ArgumentException($"Activity holds {activity.Length} values, not {layers} layers of {groups}.", nameof(activity));
        var keep = new bool[layers][];
        var count = Math.Clamp((int)Math.Ceiling(groups * fraction), 1, groups);
        for (var il = 0; il < layers; il++)
        {
            var row = activity.Slice(il * groups, groups).ToArray();
            var order = Enumerable.Range(0, groups).OrderByDescending(g => row[g]).ThenBy(g => g).Take(count);
            keep[il] = new bool[groups];
            foreach (var g in order) keep[il][g] = true;
        }
        return Of(keep, groupSize, feedForward);
    }
}
