// OnnxTextEmbedder.cs
//
// Text into a vector through ONNX Runtime, for an XLM-RoBERTa encoder that publishes a
// SentencePiece .model beside its graph - Granite-Embedding-107M-Multilingual today
// (OPEN-GAPS A7).
//
// WHY NOT TextEmbedder BESIDE IT. Its MNN backend P/Invokes mnn_embed_text, and the native
// bridge exports only mnn_embed_get_dim, so it cannot embed one sentence on any device. ONNX
// Runtime already ships for the voices: this needs no native change at all.
//
// THE MODEL WAS CHOSEN FOR ITS DATA. Most open embedders are fine-tuned on MS-MARCO, whose
// licence is non-commercial; Granite's card says it was left out for exactly that reason, and
// the rest of its training data is permissively licensed or IBM's own.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Embeddings.Local;
using CircleAI.Voice;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CircleAI.Embeddings;

/// <summary>An XLM-RoBERTa sentence encoder over ONNX Runtime: CLS pooling, unit length.</summary>
public sealed class OnnxTextEmbedder : ITextEmbedder, IEmbeddingEncoder, IDisposable
{
    /// <summary>The longest input the encoder's position table holds, specials included.</summary>
    public const int MaxTokens = 512;

    // XLM-R's own ids for its specials, ahead of the SentencePiece vocabulary.
    private const long Start = 0, End = 2, Unknown = 3;

    private readonly string _modelPath;
    private readonly SentencePieceUnigram _pieces;
    private readonly object _gate = new();
    private InferenceSession? _session;
    private int _dimension;
    private bool _disposed;

    /// <param name="modelPath">The encoder graph (<c>model.onnx</c>).</param>
    /// <param name="sentencePieceModelPath">Its <c>sentencepiece.bpe.model</c>.</param>
    public OnnxTextEmbedder(string modelPath, string sentencePieceModelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        _modelPath = modelPath;
        _pieces = SentencePieceUnigram.FromModelProto(sentencePieceModelPath);
    }

    /// <summary>The vector width, read from the graph (opens it on first use).</summary>
    public int Dimension
    {
        get
        {
            if (_dimension > 0) return _dimension;
            var output = EnsureSession().OutputMetadata.First().Value.Dimensions;
            return _dimension = output[^1];
        }
    }

    /// <inheritdoc />
    public Task<float[]> GenerateAsync(string text, CancellationToken ct = default)
        => Task.Run(() => Embed(text), ct);

    /// <inheritdoc />
    public ValueTask<float[]> EncodeAsync(string text, CancellationToken ct = default)
        => new(Task.Run(() => Embed(text), ct));

    /// <summary>One text's vector, unit length.</summary>
    public float[] Embed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var ids = Ids(_pieces.Encode(Squash(text)));
        var n = ids.Length;
        var ones = new long[n];
        Array.Fill(ones, 1L);

        lock (_gate)
        {
            var session = EnsureSession();
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, new[] { 1, n })),
                NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(ones, new[] { 1, n })),
            };
            // XLM-R carries a token-type table it never uses; some exports still ask for it.
            if (session.InputMetadata.ContainsKey("token_type_ids"))
                inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(new long[n], new[] { 1, n })));

            using var results = session.Run(inputs);
            var hidden = results.First().AsTensor<float>();

            // CLS POOLING, AS THE MODEL'S OWN POOLING CONFIG SAYS: the first position's state.
            // A graph that already pools hands back [1, d].
            var d = hidden.Dimensions[^1];
            var vector = new float[d];
            for (var i = 0; i < d; i++)
                vector[i] = hidden.Dimensions.Length == 3 ? hidden[0, 0, i] : hidden[0, i];
            _dimension = d;
            return Normalised(vector);
        }
    }

    /// <summary>
    /// SentencePiece ids as XLM-R numbers them: the fairseq offset of one, the unknown piece as
    /// id 3, wrapped in its start and end tokens and cut to <see cref="MaxTokens"/>.
    /// </summary>
    internal static long[] Ids(IReadOnlyList<int> pieces)
    {
        var n = Math.Min(pieces.Count, MaxTokens - 2);
        var ids = new long[n + 2];
        ids[0] = Start;
        for (var i = 0; i < n; i++) ids[i + 1] = pieces[i] <= 0 ? Unknown : pieces[i] + 1L;
        ids[^1] = End;
        return ids;
    }

    /// <summary>SentencePiece's <c>remove_extra_whitespaces</c>: trimmed, runs of space as one.</summary>
    internal static string Squash(string text) => Regex.Replace(text.Trim(), @"\s+", " ");

    private static float[] Normalised(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += x * x;
        var norm = Math.Sqrt(sum);
        if (norm > 1e-12)
            for (var i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
        return v;
    }

    private InferenceSession EnsureSession()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _session ??= OnnxSessionFactory.Open(_modelPath);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _session?.Dispose();
            _session = null;
        }
    }
}
