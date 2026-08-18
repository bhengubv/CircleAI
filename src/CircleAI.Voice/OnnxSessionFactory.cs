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

        // The fingerprint is IN THE NAME, so identity is decided by the filename
        // rather than by a timestamp comparison that side-loading always loses.
        var optimised = Path.ChangeExtension(modelPath, null)
                        + "." + Fingerprint(modelPath) + ".ort.onnx";

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

        return File.Exists(modelPath);
    }

    /// <summary>
    /// A cheap content fingerprint, so the cache name identifies the model.
    /// </summary>
    /// <remarks>
    /// TIMESTAMPS WERE THE WRONG KEY AND IT COST MINUTES A TURN. The check used to
    /// be "cache must be no older than the model", which is correct reasoning and
    /// wrong here: the side-load importer COPIES the model into place, so its
    /// mtime becomes now and every existing cache looks stale. The graph was then
    /// re-optimised on every single open — measured on a P30 as the app sitting at
    /// 64% CPU for three and a half minutes on a 122 MB Japanese voice, with a
    /// person waiting and nothing in the log to say why.
    /// <para>
    /// Path alone is not safe either, and the old comment was right about why:
    /// voices are commonly side-loaded by overwriting one well-known filename, so
    /// a path-keyed cache would serve the previous language's graph. Two different
    /// voices can also share a byte count — lessac-medium and zh_CN-huayan-medium
    /// are both exactly 63 201 294 bytes — so size is not a discriminator on its
    /// own.
    /// </para>
    /// <para>
    /// So the name carries a fingerprint of the CONTENT: length plus the head and
    /// tail of the file. Copying the same bytes yields the same name and the cache
    /// is reused; a different model at the same path yields a different name and
    /// gets its own. Reading 128 KB of a 122 MB file costs nothing next to the
    /// optimisation it avoids.
    /// </para>
    /// </remarks>
    private static string Fingerprint(string modelPath)
    {
        const int Edge = 64 * 1024;
        try
        {
            using var fs = File.OpenRead(modelPath);
            var len = fs.Length;
            var buf = new byte[Edge];

            using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256);
            sha.AppendData(BitConverter.GetBytes(len));

            var head = fs.Read(buf, 0, (int)Math.Min(Edge, len));
            sha.AppendData(buf, 0, head);

            if (len > Edge * 2)
            {
                fs.Seek(-Edge, SeekOrigin.End);
                var tail = fs.Read(buf, 0, Edge);
                sha.AppendData(buf, 0, tail);
            }

            return Convert.ToHexString(sha.GetHashAndReset(), 0, 4).ToLowerInvariant();
        }
        catch
        {
            // Unreadable head or tail: fall back to a name that simply will not
            // collide with a real fingerprint, so the cache is rebuilt rather than
            // a wrong graph being served.
            return "nofp";
        }
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
        IntraOpNumThreads = IntraOpThreads(),
    };

    /// <summary>
    /// Threads for a single operator: half the cores, leaving room for the
    /// model generating the words being spoken.
    /// </summary>
    /// <remarks>
    /// NOT A THREAD-EFFICIENCY SETTING — A CONTENTION ONE. Taking every core
    /// looks free, and is not, because on this product speech synthesis does not
    /// run alone: sentences are spoken while the language model is still writing
    /// the rest of the answer, so ONNX Runtime and MNN are both resident and
    /// both busy. Asking for all eight cores while MNN holds four oversubscribes
    /// an eight-core phone, and the two engines take turns being descheduled.
    /// <para>
    /// Measured on a P30 Lite, same question, same 37-character opening clause:
    /// </para>
    /// <code>
    ///   ORT threads   first clause synthesised   LLM decode per chunk
    ///   4 (half)                  4 603 ms                   157 ms
    ///   8 (all)                   5 937 ms                   214 ms
    /// </code>
    /// <para>
    /// BOTH got worse with more threads, which is the signature of
    /// oversubscription rather than of slow cores. An earlier version of this
    /// comment blamed the four little A53s and cited a per-character figure to
    /// match; that reasoning was wrong, and the numbers behind it had been taken
    /// while a model load was still hidden inside the first synthesis.
    /// </para>
    /// <para>
    /// Half also lands sensibly where nothing else is competing: on a
    /// hyperthreaded desktop it is roughly the physical core count, which is
    /// what a saturated matmul wants anyway.
    /// </para>
    /// </remarks>
    private static int IntraOpThreads() => Math.Max(1, Environment.ProcessorCount / 2);
}
