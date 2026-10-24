// ModelCosts.cs
//
// WHERE THE MEASUREMENTS GO, SO THE SECOND ONE IS WORTH MORE THAN THE FIRST.
//
// One reading says what a model cost once. Two readings at different context lengths
// say how fast the conversation's cache grows, which is the term the old rule folded
// into a 15% fraction and the term that decides whether a model that fits at 2K still
// fits at 32K. So readings are KEPT, not replaced.
//
// A FILE, NOT THE CATALOGUE DB, AND THAT IS NOT LAZINESS. The brain runs in its own
// process (:inference); the catalogue is opened by the service process. Two processes
// writing one SQLite file over a model load is a lock contention bug waiting for a
// slow phone. A small append-mostly JSON document that either side can read, and only
// the measuring side writes, has no such failure.
//
// NEVER AUTHORITATIVE OVER A CATALOGUED FIGURE. ModelEntry.MmapResidentGb is a number
// somebody put in the catalogue deliberately; this is a number a phone observed. When
// both exist the catalogued one wins, because it was reviewed. When only this exists
// it is still a fact, and a fact beats max(MinRamGb, file size), which is an
// assumption that the file must sit in memory whole.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircleAI.Inference;

/// <summary>What models have actually cost on this device.</summary>
public static class ModelCosts
{
    /// <summary>The file, in whatever directory the host set. Null disables all of it.</summary>
    public static string? Directory { get; set; }

    /// <summary>Where a line is reported, so a measurement is never silent.</summary>
    /// <remarks>
    /// A measurement nobody can see is how MmapResidentGb stayed empty for the life of
    /// the product while the field documented exactly how to fill it.
    /// </remarks>
    public static Action<string>? Say { get; set; }

    private const string FileName = "model-costs.json";
    private static readonly object Gate = new();

    /// <summary>How many readings are kept per model.</summary>
    /// <remarks>
    /// Enough to see a slope and to notice a device whose numbers have moved; few
    /// enough that the file stays a page of text. The oldest go first.
    /// </remarks>
    public const int KeepPerModel = 8;

    private static string? Path() =>
        string.IsNullOrWhiteSpace(Directory) ? null : System.IO.Path.Combine(Directory!, FileName);

    /// <summary>Writes down what a model cost. Never throws.</summary>
    public static void Record(ModelCost cost)
    {
        if (cost is null) return;

        try
        {
            var path = Path();
            if (path is null) return;

            lock (Gate)
            {
                var all = ReadAll();
                all.RemoveAll(c => c.ModelId == cost.ModelId && c.AtContextTokens == cost.AtContextTokens);
                all.Add(cost);

                // Oldest first out, per model.
                foreach (var group in all.GroupBy(c => c.ModelId).ToList())
                {
                    var extra = group.OrderByDescending(c => c.When).Skip(KeepPerModel).ToList();
                    foreach (var old in extra) all.Remove(old);
                }

                System.IO.Directory.CreateDirectory(Directory!);
                System.IO.File.WriteAllText(path, JsonSerializer.Serialize(all, Options));
            }

            Say?.Invoke(Invariant(
                $"measured {cost.ModelId}: {cost.ResidentGb:F2} GB resident over a {cost.FloorGb:F2} GB floor, at {cost.AtContextTokens} tokens"));
        }
        catch (Exception ex)
        {
            Say?.Invoke("could not record what the model cost: " + ex.Message);
        }
    }

    /// <summary>
    /// What is known about this model's cost here, with a cache slope when two
    /// readings at different contexts allow one. Null when nothing was measured.
    /// </summary>
    /// <remarks>
    /// THE HIGHEST RESIDENT READING WINS, not the average. A model that peaked at
    /// 1.4 GB once needs 1.4 GB, and averaging that with a quieter run produces a
    /// number that fits on paper and OOMs on the phone.
    /// </remarks>
    public static ModelCost? For(string modelId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(modelId)) return null;

            List<ModelCost> mine;
            lock (Gate) mine = ReadAll().Where(c => c.ModelId == modelId).ToList();
            if (mine.Count == 0) return null;

            var worst = mine.OrderByDescending(c => c.ResidentGb).First();

            // TWO CONTEXTS MAKE A SLOPE. The cache is linear in context - the only
            // published end-to-end breakdown shows the same 66.7 kB/token between 4K
            // and 10K as between 10K and 100K - so two honest readings give the term
            // that the 15% headroom was standing in for.
            var low  = mine.Where(c => c.AtContextTokens > 0).OrderBy(c => c.AtContextTokens).FirstOrDefault();
            var high = mine.Where(c => c.AtContextTokens > 0).OrderByDescending(c => c.AtContextTokens).FirstOrDefault();

            double? slope = null;
            if (low is not null && high is not null && high.AtContextTokens > low.AtContextTokens)
            {
                var grew = high.ResidentGb - low.ResidentGb;
                var over = high.AtContextTokens - low.AtContextTokens;

                // A cache cannot shrink as the conversation grows. A negative reading
                // is noise - another app freed memory between the two - and reporting
                // it as a slope would make long contexts look cheaper than short ones.
                if (grew > 0) slope = grew / over * 1000.0;
            }

            return worst with { CacheGbPerThousandTokens = slope };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Everything measured here, newest first. For a screen or a log.</summary>
    public static IReadOnlyList<ModelCost> All()
    {
        try
        {
            lock (Gate) return ReadAll().OrderByDescending(c => c.When).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static List<ModelCost> ReadAll()
    {
        var path = Path();
        if (path is null || !System.IO.File.Exists(path)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<ModelCost>>(
                System.IO.File.ReadAllText(path), Options) ?? [];
        }
        catch
        {
            // A corrupt file is a file nobody can learn from, and refusing to start
            // because of one would be worse. It is overwritten by the next reading.
            return [];
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string Invariant(FormattableString s) =>
        s.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
