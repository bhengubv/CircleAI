// HeadSearch.cs
//
// THE OUTPUT STEP AS A TOP-MATCHES SEARCH (the indexed mind, "Index every part of the model": "Output step. Scoring the
// whole vocabulary is a top-matches search, so it need not touch every row."). Scoring every row of the head is a full
// scan; this is the head's index. It keeps a small sketch of every token's head row in RAM - a random projection to a few
// dimensions, which preserves inner products well enough to rank - and, for a word, ranks the head's chunks by the best
// sketch score in each and reads only the likeliest, so the scoring reads fewer rows than the whole surface. The argmax
// over what it reads is the full scan's argmax almost always; a miss is an acceptable risk, as the design allows.
//
// Known limit, recorded: the sketch is of the head rows as they are stored, and the query as it arrives. Where the model
// folds a Hadamard into the head (the real Ternary-Bonsai-2), the true score rotates the query first, so the sketch must
// rotate with it to rank right. Built here for a head with no such rotation; the rotation-aware sketch is the next step.

using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Kernels;

namespace CircleAI.Inference.Managed.Store;

/// <summary>A head's index for a top-matches search: a sketch of every token's row in RAM, and each token's chunk.</summary>
public sealed class HeadSearch
{
    private readonly float[] _projection; // _dim rows of _embedding: the random projection
    private readonly float[][] _sketch;    // each token's row projected to _dim
    private readonly int[] _chunkOf;       // each token's chunk in the head's rows, or -1 when it is not in them
    private readonly int _dim, _embedding, _chunkCount;

    private HeadSearch(float[] projection, float[][] sketch, int[] chunkOf, int dim, int embedding, int chunkCount)
    {
        _projection = projection;
        _sketch = sketch;
        _chunkOf = chunkOf;
        _dim = dim;
        _embedding = embedding;
        _chunkCount = chunkCount;
    }

    /// <summary>The head's chunks, the search's whole span.</summary>
    public int ChunkCount => _chunkCount;

    /// <summary>
    /// Builds the head search of <paramref name="model"/>'s output head, with <paramref name="rows"/> for which chunk each
    /// token is in (read once, apart from the scoring's own reads). The head's rows are read from the model's file and each
    /// projected to <paramref name="dim"/> dimensions. PTQ1_0 heads only for now.
    /// </summary>
    public static HeadSearch Build(GgufFile model, IHeadRows rows, int dim = 24, int seed = 1234)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(rows);
        var tensor = model.Tensor("output.weight") ?? model.Tensor("token_embd.weight")
            ?? throw new InvalidDataException("The model has no output head to search.");
        if (tensor.Type != GgmlType.PTQ1_0) throw new NotSupportedException($"The head search reads PTQ1_0 heads; this one is {tensor.Type}.");
        var embedding = (int)tensor.RowLength;
        var vocab = (int)tensor.Rows;
        var rowBytes = embedding / Ptq1.BlockWeights * Ptq1.BlockBytes;
        var blocks = embedding / Ptq1.BlockWeights;
        var data = model.Bytes(tensor);

        var rng = new Random(seed);
        var projection = new float[dim * embedding];
        for (var i = 0; i < projection.Length; i++) projection[i] = (float)(rng.NextDouble() * 2 - 1);

        var sketch = new float[vocab][];
        var trits = new sbyte[Ptq1.BlockWeights];
        var row = new float[embedding];
        for (var t = 0; t < vocab; t++)
        {
            var at = (long)t * rowBytes;
            for (var b = 0; b < blocks; b++)
            {
                var block = data.Slice((int)(at + (long)b * Ptq1.BlockBytes), Ptq1.BlockBytes);
                var scale = Ptq1.Scale(block);
                Ptq1.DecodeScalar(block, trits);
                for (var j = 0; j < Ptq1.BlockWeights; j++) row[b * Ptq1.BlockWeights + j] = trits[j] * scale;
            }
            var s = new float[dim];
            Project(projection, dim, embedding, row, s);
            sketch[t] = s;
        }

        var chunkOf = new int[vocab];
        Array.Fill(chunkOf, -1);
        for (var ci = 0; ci < rows.ChunkCount; ci++)
        {
            rows.Chunk(ci, out _, out _, out var tokens);
            foreach (var tk in tokens)
                if (tk >= 0 && tk < vocab) chunkOf[tk] = ci;
        }
        return new HeadSearch(projection, sketch, chunkOf, dim, embedding, rows.ChunkCount);
    }

    /// <summary>
    /// The chunks to read for a word of <paramref name="query"/> (the thought entering the head): every chunk but the one
    /// whose best token sketches lowest, so a scoring reads fewer rows than the whole surface and still holds the likeliest
    /// tokens. <paramref name="chunks"/> is cleared and filled.
    /// </summary>
    public void Candidates(ReadOnlySpan<float> query, HashSet<int> chunks)
    {
        chunks.Clear();
        if (_chunkCount <= 1)
        {
            if (_chunkCount == 1) chunks.Add(0);
            return;
        }
        Span<float> q = stackalloc float[_dim];
        Project(_projection, _dim, _embedding, query, q);
        var best = new float[_chunkCount];
        Array.Fill(best, float.NegativeInfinity);
        for (var t = 0; t < _sketch.Length; t++)
        {
            var ci = _chunkOf[t];
            if (ci < 0) continue;
            var s = _sketch[t];
            float dot = 0;
            for (var d = 0; d < _dim; d++) dot += s[d] * q[d];
            if (dot > best[ci]) best[ci] = dot;
        }
        var worst = 0;
        for (var ci = 1; ci < _chunkCount; ci++)
            if (best[ci] < best[worst]) worst = ci;
        for (var ci = 0; ci < _chunkCount; ci++)
            if (ci != worst) chunks.Add(ci);
    }

    private static void Project(float[] projection, int dim, int embedding, ReadOnlySpan<float> x, Span<float> into)
    {
        for (var d = 0; d < dim; d++)
        {
            float sum = 0;
            var at = d * embedding;
            for (var e = 0; e < embedding; e++) sum += projection[at + e] * x[e];
            into[d] = sum;
        }
    }
}
