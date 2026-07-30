using Microsoft.ML.OnnxRuntime;

namespace CircleAI.Voice;

/// <summary>
/// Opens ONNX sessions the way a phone needs them opened.
/// </summary>
/// <remarks>
/// <para>
/// On a phone these models cost far more to LOAD than to run — measured on a
/// Kirin 710, a single utterance was 620 s of session construction against 14 s
/// of actual inference. Two things fix that, and both belong to every engine
/// rather than to whichever one was written last:
/// </para>
/// <list type="bullet">
///   <item>pick the cheapest-to-load form of the model that is present, and</item>
///   <item>let ONNX Runtime optimise the graph ONCE, then reload the optimised
///         copy with optimisation disabled.</item>
/// </list>
/// <para>
/// This lived only in <see cref="ToucanOnnxTtsEngine"/> while
/// <see cref="OnnxTtsEngine"/> — the engine that actually serves most voices —
/// still paid full optimisation on every session. Sharing it removes that split.
/// </para>
/// </remarks>
public static class OnnxSessionFactory
{
    /// <summary>
    /// The cheapest-to-load variant of <paramref name="stem"/> in
    /// <paramref name="directory"/>: <c>.ort</c> (pre-optimised flatbuffer, no
    /// protobuf parse), else <c>_int8.onnx</c> (fewer bytes), else plain
    /// <c>.onnx</c>.
    /// </summary>
    public static string PickModelFile(string directory, string stem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(stem);

        foreach (var candidate in new[]
                 {
                     Path.Combine(directory, stem + ".ort"),
                     Path.Combine(directory, stem + "_int8.onnx"),
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }
        return Path.Combine(directory, stem + ".onnx");
    }

    /// <summary>
    /// Open <paramref name="modelPath"/>, reusing an ORT-optimised copy when one
    /// exists and writing one when it does not. Falls back from a quantised model
    /// to its full-precision twin if the runtime lacks an int8 kernel.
    /// </summary>
    public static InferenceSession Open(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        try
        {
            return OpenCore(modelPath);
        }
        catch (OnnxRuntimeException) when (modelPath.Contains("_int8", StringComparison.Ordinal))
        {
            // A quantised model can load on a desktop and be unrunnable on the
            // phone — Android's ONNX Runtime ships without some int8 kernels
            // (ConvInteger, for one). Losing the size win beats losing the voice.
            var full = modelPath.Replace("_int8", "", StringComparison.Ordinal);
            if (!File.Exists(full)) throw;
            return OpenCore(full);
        }
    }

    private static InferenceSession OpenCore(string modelPath)
    {
        // .ort is already optimised; re-optimising it discards the reason to use it.
        if (modelPath.EndsWith(".ort", StringComparison.OrdinalIgnoreCase))
            return new InferenceSession(modelPath, Options(GraphOptimizationLevel.ORT_DISABLE_ALL));

        var optimised = Path.ChangeExtension(modelPath, null) + ".ort.onnx";

        // Only reuse the optimised copy if it was built from THIS model. Voices are
        // commonly sideloaded by overwriting one well-known filename, so a cache
        // keyed on the path alone would serve the previous language's graph — and
        // it survives app restarts, so the wrong voice would persist silently.
        if (IsUsableOptimisedCopy(modelPath, optimised))
            return new InferenceSession(optimised, Options(GraphOptimizationLevel.ORT_DISABLE_ALL));

        var opts = Options(GraphOptimizationLevel.ORT_ENABLE_ALL);

        // Emit the optimised graph so the NEXT open skips this entirely.
        // Best-effort: a read-only location must not stop us synthesising.
        try { opts.OptimizedModelFilePath = optimised; }
        catch { }

        return new InferenceSession(modelPath, opts);
    }

    /// <summary>
    /// True when <paramref name="optimised"/> was built from the current
    /// <paramref name="modelPath"/> — i.e. it exists, is non-trivial, and is not
    /// older than the model it claims to optimise.
    /// </summary>
    private static bool IsUsableOptimisedCopy(string modelPath, string optimised)
    {
        if (!File.Exists(optimised)) return false;

        var cache = new FileInfo(optimised);
        if (cache.Length <= 1024) return false;

        var source = new FileInfo(modelPath);
        if (!source.Exists) return false;

        // Overwriting the model updates its timestamp; anything not newer than the
        // model was built from something else.
        return cache.LastWriteTimeUtc >= source.LastWriteTimeUtc;
    }

    /// <summary>
    /// An identity for a model file — path plus size plus last-write time. Callers
    /// that cache engines must key on THIS, not on the path: sideloading a voice
    /// usually means overwriting one filename, and a path-only key hands back the
    /// previous voice.
    /// </summary>
    public static string ModelIdentity(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        var f = new FileInfo(modelPath);
        return f.Exists
            ? $"{f.FullName}|{f.Length}|{f.LastWriteTimeUtc.Ticks}"
            : modelPath;
    }

    private static SessionOptions Options(GraphOptimizationLevel level) => new()
    {
        GraphOptimizationLevel = level,
        InterOpNumThreads = 1,
        IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount)
    };
}
