// MeaningModel.cs
//
// The embedding model setup fetched, opened once where it lies (OPEN-GAPS A7).
//
// WHAT IS ON DISK IS WHAT IS USED, and nothing is downloaded from here. Setup fetches the
// model as "search by meaning" when the phone has room for it; until then this answers null
// and every search goes on by its words.

using System;
using System.IO;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;

namespace CircleAI.Embeddings;

/// <summary>Finds and opens the installed text-embedding model.</summary>
public static class MeaningModel
{
    private static readonly object Gate = new();
    private static OnnxTextEmbedder? _open;
    private static string? _openPath;

    /// <summary>
    /// The encoder for the installed embedding model in <paramref name="storageDir"/>, or null
    /// when none is there or the one there is not an ONNX graph with its SentencePiece model.
    /// </summary>
    public static OnnxTextEmbedder? Installed(string? storageDir)
    {
        lock (Gate)
        {
            if (_open is not null && File.Exists(_openPath)) return _open;

            using var registry = new ModelRegistryService();
            using var loader = new BundleModelLoader(storageDir, registry);
            var entry = registry.AllModels
                .Where(m => m.Modality == ModelModality.Embedding && !m.Retired)
                .FirstOrDefault(m => loader.ModelPresent(m.Name));
            if (entry is null) return null;

            var graph = loader.GetModelPath(entry.Name);
            if (!graph.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)) return null;   // an MNN bundle

            var pieces = Directory
                .EnumerateFiles(Path.GetDirectoryName(graph)!, "*.model", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (pieces is null) return null;

            _open?.Dispose();
            _open = new OnnxTextEmbedder(graph, pieces);
            _openPath = graph;
            return _open;
        }
    }
}
