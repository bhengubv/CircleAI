// ThoughtIndex.cs
//
// THE CORE INDEX, ON FLASH (the indexed mind, plan step 5). Built from what Watch saw: per layer, the thoughts grouped by
// likeness - k-means on their sketches - and each group's posting list, the neurons its words fired most, pointing into
// the one copy of the weights (the model's own file: a neuron is a row of ffn_gate and of ffn_up). A word's thought finds
// its group by the nearest centre, and its feed-forward needs only that list's neurons. The cached index (CachedIndex)
// holds a portion of the lists' pieces side by side; the warm index holds the centres and the hottest pieces in RAM.
//
// Lists are whole tiles of 16 neurons - the vector kernel's - sorted by neuron.
//
// Layout, little-endian:
//   header   magic "CAIINDEX", version, layers, embedding, feed-forward, groups a layer, neurons a list, sketch width,
//            and the watch's records it was built from
//   layers   per layer: the centres (groups x sketch width floats), how many words each group held (ints), then each
//            group's list (neurons a list ushorts)

using System.Buffers.Binary;

namespace CircleAI.Inference.Managed.Store;

/// <summary>The core index: per layer, thought groups and each group's posting list of strong neurons (<see cref="ThoughtIndex"/>'s layout).</summary>
public sealed class ThoughtIndex
{
    /// <summary>"CAIINDEX", little-endian.</summary>
    public const ulong Magic = 0x5845_444E_4949_4143;

    /// <summary>The layout's version.</summary>
    public const int Version = 1;

    private const int HeaderBytes = 64;
    private const int Rounds = 12;   // k-means rounds
    private readonly float[][] _centres;  // by layer: groups x sketch width
    private readonly int[][] _sizes;      // by layer: words each group held
    private readonly ushort[][][] _lists; // by layer, by group: the neurons, sorted

    private ThoughtIndex(string path, int layers, int embedding, int feedForward, int groups, int listNeurons, long records,
        float[][] centres, int[][] sizes, ushort[][][] lists)
    {
        FilePath = path;
        Layers = layers;
        Embedding = embedding;
        FeedForward = feedForward;
        Groups = groups;
        ListNeurons = listNeurons;
        Records = records;
        _centres = centres;
        _sizes = sizes;
        _lists = lists;
    }

    /// <summary>The file.</summary>
    public string FilePath { get; }

    /// <summary>Layers indexed.</summary>
    public int Layers { get; }

    /// <summary>The thoughts' width.</summary>
    public int Embedding { get; }

    /// <summary>Neurons in a layer's feed-forward.</summary>
    public int FeedForward { get; }

    /// <summary>Thought groups a layer.</summary>
    public int Groups { get; }

    /// <summary>Neurons in each group's posting list: whole tiles.</summary>
    public int ListNeurons { get; }

    /// <summary>The watch's records it was built from.</summary>
    public long Records { get; }

    /// <summary>Layer <paramref name="layer"/>'s group <paramref name="group"/>'s posting list: its neurons, sorted.</summary>
    public ReadOnlySpan<ushort> List(int layer, int group) => _lists[layer][group];

    // The list itself, for a piece that names its neurons without a copy.
    internal ushort[] ListArray(int layer, int group) => _lists[layer][group];

    /// <summary>How many of Watch's words layer <paramref name="layer"/>'s group <paramref name="group"/> held.</summary>
    public int Size(int layer, int group) => _sizes[layer][group];

    /// <summary>The group at layer <paramref name="layer"/> whose centre is nearest <paramref name="sketch"/> (a unit sketch).</summary>
    public int Nearest(int layer, ReadOnlySpan<float> sketch) => Nearest(_centres[layer], Groups, sketch);

    /// <summary>
    /// Builds the core index from <paramref name="watch"/>'s records and writes it to <paramref name="path"/>: per layer,
    /// <paramref name="groups"/> thought groups and posting lists of <paramref name="listNeurons"/> neurons (rounded up to
    /// whole tiles) - the neurons the group's words marked most often.
    /// </summary>
    public static ThoughtIndex Build(WatchRecords watch, string path, int groups, int listNeurons)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentOutOfRangeException.ThrowIfLessThan(groups, 1);
        const int tile = Kernels.Ptq1PairMatrix.TileRows;
        listNeurons = Math.Min(watch.FeedForward, (listNeurons + tile - 1) / tile * tile);
        var width = ThoughtSketch.Width;

        // THE SKETCHES, every layer's, in one pass: the groups come from them.
        var sketches = new List<float[]>[watch.Layers];
        for (var il = 0; il < watch.Layers; il++) sketches[il] = [];
        foreach (var (layer, sketch, _) in watch.Each())
            if ((uint)layer < (uint)watch.Layers) sketches[layer].Add((float[])sketch.Clone());

        var centres = new float[watch.Layers][];
        var sizes = new int[watch.Layers][];
        for (var il = 0; il < watch.Layers; il++)
        {
            var (c, assigned) = KMeans(sketches[il], Math.Max(1, Math.Min(groups, sketches[il].Count)), groups, width, seed: il);
            centres[il] = c;
            sizes[il] = new int[groups];
            foreach (var g in assigned) sizes[il][g]++;
        }

        // THE LISTS: each group's neurons counted over its words, a few layers at a time so the counts stay small.
        var lists = new ushort[watch.Layers][][];
        const int layersAPass = 8;
        for (var first = 0; first < watch.Layers; first += layersAPass)
        {
            var end = Math.Min(watch.Layers, first + layersAPass);
            var counts = new int[end - first][];
            for (var il = first; il < end; il++) counts[il - first] = new int[groups * watch.FeedForward];
            foreach (var (layer, sketch, strong) in watch.Each())
            {
                if (layer < first || layer >= end) continue;
                var g = Nearest(centres[layer], groups, sketch);
                var row = counts[layer - first].AsSpan(g * watch.FeedForward, watch.FeedForward);
                for (var n = 0; n < watch.FeedForward; n++)
                    if ((strong[n >> 3] & (1 << (n & 7))) != 0) row[n]++;
            }
            for (var il = first; il < end; il++)
            {
                lists[il] = new ushort[groups][];
                for (var g = 0; g < groups; g++)
                    lists[il][g] = Top(counts[il - first].AsSpan(g * watch.FeedForward, watch.FeedForward), listNeurons);
            }
        }

        var index = new ThoughtIndex(path, watch.Layers, watch.Embedding, watch.FeedForward, groups, listNeurons, watch.Count, centres, sizes, lists);
        index.Write();
        return index;
    }

    /// <summary>The core index at <paramref name="path"/>; null when there is none or it is another layout's.</summary>
    public static ThoughtIndex? Open(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt64() != Magic || reader.ReadInt32() != Version) return null;
            var layers = reader.ReadInt32();
            var embedding = reader.ReadInt32();
            var feedForward = reader.ReadInt32();
            var groups = reader.ReadInt32();
            var listNeurons = reader.ReadInt32();
            if (reader.ReadInt32() != ThoughtSketch.Width) return null;
            var records = reader.ReadInt64();
            stream.Position = HeaderBytes;
            var centres = new float[layers][];
            var sizes = new int[layers][];
            var lists = new ushort[layers][][];
            for (var il = 0; il < layers; il++)
            {
                centres[il] = new float[groups * ThoughtSketch.Width];
                for (var i = 0; i < centres[il].Length; i++) centres[il][i] = reader.ReadSingle();
                sizes[il] = new int[groups];
                for (var g = 0; g < groups; g++) sizes[il][g] = reader.ReadInt32();
                lists[il] = new ushort[groups][];
                for (var g = 0; g < groups; g++)
                {
                    lists[il][g] = new ushort[listNeurons];
                    for (var n = 0; n < listNeurons; n++) lists[il][g][n] = reader.ReadUInt16();
                }
            }
            return new ThoughtIndex(path, layers, embedding, feedForward, groups, listNeurons, records, centres, sizes, lists);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException)
        {
            return null;
        }
    }

    private void Write()
    {
        var partial = FilePath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        using (var writer = new BinaryWriter(stream))
        {
            var header = new byte[HeaderBytes];
            BinaryPrimitives.WriteUInt64LittleEndian(header, Magic);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), Version);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), Layers);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), Embedding);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), FeedForward);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), Groups);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), ListNeurons);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32), ThoughtSketch.Width);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(36), Records);
            writer.Write(header);
            for (var il = 0; il < Layers; il++)
            {
                foreach (var v in _centres[il]) writer.Write(v);
                foreach (var s in _sizes[il]) writer.Write(s);
                foreach (var list in _lists[il])
                    foreach (var n in list) writer.Write(n);
            }
            writer.Flush();
            FlashFile.Settle(stream, 0, stream.Length);
        }
        File.Move(partial, FilePath, overwrite: true);
    }

    // The nearest of `groups` centres to a unit sketch: the largest inner product.
    private static int Nearest(float[] centres, int groups, ReadOnlySpan<float> sketch)
    {
        var width = ThoughtSketch.Width;
        var best = 0;
        var bestScore = float.NegativeInfinity;
        for (var g = 0; g < groups; g++)
        {
            var c = centres.AsSpan(g * width, width);
            float score = 0;
            for (var j = 0; j < width; j++) score += c[j] * sketch[j];
            if (score > bestScore) (best, bestScore) = (g, score);
        }
        return best;
    }

    // K-means on unit sketches by inner product (spherical): `used` centres seeded from spread-out sketches, `groups` slots
    // (any beyond the sketches' count stay zero and are never nearest). Returns the centres and each sketch's group.
    private static (float[] Centres, int[] Assigned) KMeans(List<float[]> sketches, int used, int groups, int width, int seed)
    {
        var centres = new float[groups * width];
        var assigned = new int[sketches.Count];
        if (sketches.Count == 0) return (centres, assigned);
        var rng = new Random(seed);
        for (var g = 0; g < used; g++) sketches[(int)((long)g * sketches.Count / used + rng.Next(Math.Max(1, sketches.Count / used))) % sketches.Count].CopyTo(centres, g * width);
        for (var round = 0; round < Rounds; round++)
        {
            for (var s = 0; s < sketches.Count; s++) assigned[s] = Nearest(centres, used, sketches[s]);
            var sums = new float[groups * width];
            for (var s = 0; s < sketches.Count; s++)
            {
                var into = sums.AsSpan(assigned[s] * width, width);
                for (var j = 0; j < width; j++) into[j] += sketches[s][j];
            }
            for (var g = 0; g < used; g++)
            {
                var sum = sums.AsSpan(g * width, width);
                double norm = 0;
                foreach (var v in sum) norm += (double)v * v;
                if (norm <= 0) continue; // an empty group keeps its centre
                var scale = (float)(1 / Math.Sqrt(norm));
                for (var j = 0; j < width; j++) centres[g * width + j] = sum[j] * scale;
            }
        }
        for (var s = 0; s < sketches.Count; s++) assigned[s] = Nearest(centres, used, sketches[s]);
        centres.AsSpan(used * width).Fill(float.NaN); // a slot no words filled is never nearest
        return (centres, assigned);
    }

    // The `count` neurons counted most often (ties to the lower neuron), sorted by neuron.
    private static ushort[] Top(ReadOnlySpan<int> counts, int count)
    {
        var keys = new long[counts.Length];
        for (var n = 0; n < keys.Length; n++) keys[n] = ((long)(int.MaxValue - counts[n]) << 32) | (uint)n;
        Array.Sort(keys);
        var top = new ushort[count];
        for (var i = 0; i < count; i++) top[i] = (ushort)(keys[i] & 0xFFFF_FFFF);
        Array.Sort(top);
        return top;
    }
}
