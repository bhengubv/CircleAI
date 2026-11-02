// BrainTuning.cs
//
// WHAT WOLVERINE HAS TURNED DOWN ON THIS DEVICE, WHERE EVERY BRAIN IS BUILT (S6).
//
// The healing loop runs in the service's main process; the brain is built in the ":inference"
// process (BrainFactory, through AIService) and chosen in the main one (the supervisor). Static
// state does not cross that line - SelfHeal's switches in one process are invisible to the
// other, which is why a knob decided by Wolverine has to be written down where both can read it.
//
// ONE OWNER, AND THIS IS NOT IT. SelfHeal decides how long a knob stays down and how many
// notches (its back-off is the loop guard); HealingKnobs turns that into values; this file is
// only their projection onto disk - a list of knobs, each with the time it comes back up. It is
// rewritten whenever a switch moves, and an entry past its time is simply not in force: the
// clock puts the brain back at full, which is SelfHeal's half-open try, with nobody having to
// delete anything.
//
// IT NEVER THROWS AND NEVER BLOCKS A BRAIN. A file that cannot be read is no tuning at all,
// which is exactly how the brain was built before any of this.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircleAI.Core;

namespace CircleAI.Inference;

/// <summary>One knob Wolverine turned down, and when it comes back up.</summary>
/// <param name="Name">The knob: threads, memory-bits, context, thinking, kernels, or avoid:&lt;model&gt;.</param>
/// <param name="Value">Its value while it is down, invariant text.</param>
/// <param name="UntilUtc">When it is back at full.</param>
public sealed record TunedKnob(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("untilUtc")] DateTimeOffset UntilUtc);

/// <summary>The knobs every brain on this device is built with right now.</summary>
public sealed record BrainTuning
{
    /// <summary>The knob names, one owner for the two processes that read them.</summary>
    public static class Knobs
    {
        /// <summary>The most threads a brain may run.</summary>
        public const string Threads = "threads";

        /// <summary>The working memory's form - a <see cref="KvCompressionMode"/> name.</summary>
        public const string MemoryBits = "memory-bits";

        /// <summary>The longest context a brain may hold.</summary>
        public const string Context = "context";

        /// <summary>The most tokens a brain may think before it answers.</summary>
        public const string Thinking = "thinking";

        /// <summary>"scalar": no vector instructions in the kernels.</summary>
        public const string Kernels = "kernels";

        /// <summary>The prefix of a brain stepped down from: avoid:&lt;model id&gt;.</summary>
        public const string AvoidPrefix = "avoid:";
    }

    /// <summary>Nothing turned down: every brain is built as it always was.</summary>
    public static BrainTuning None { get; } = new();

    /// <summary>The most threads a brain may run; null leaves it to the device.</summary>
    public int? MaxThreads { get; init; }

    /// <summary>The working memory's form; null keeps the engine's usual one.</summary>
    public KvCompressionMode? WorkingMemory { get; init; }

    /// <summary>The longest context a brain may hold; null leaves it.</summary>
    public int? MaxContext { get; init; }

    /// <summary>The most tokens a brain may think before it must answer; null is no budget.</summary>
    public int? ThinkingBudget { get; init; }

    /// <summary>Kernels without vector instructions, after an illegal instruction.</summary>
    public bool ScalarKernels { get; init; }

    /// <summary>Brains stepped down from for now: the next one this device holds answers instead.</summary>
    public IReadOnlyList<string> AvoidedBrains { get; init; } = [];

    /// <summary>Whether nothing is turned down.</summary>
    public bool IsNone => MaxThreads is null && WorkingMemory is null && MaxContext is null && ThinkingBudget is null
                          && !ScalarKernels && AvoidedBrains.Count == 0;

    /// <summary>Whether <paramref name="modelId"/> is stepped down from right now.</summary>
    public bool Avoids(string? modelId) =>
        !string.IsNullOrWhiteSpace(modelId) && AvoidedBrains.Contains(modelId!.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>The threads to run: the request, held under the cap when there is one.</summary>
    /// <param name="requested">What the host or device asked for; null or 0 lets the engine pick.</param>
    /// <returns>Null when nothing was asked and nothing is capped, so the engine still picks.</returns>
    public int? Threads(int? requested) =>
        MaxThreads is { } cap
            ? Math.Max(1, requested is > 0 ? Math.Min(requested.Value, cap) : cap)
            : requested;

    /// <summary>The context to hold: the request, held under the cap when there is one.</summary>
    public int Context(int requested) => MaxContext is { } cap ? Math.Max(1, Math.Min(requested, cap)) : requested;

    /// <summary>One line for a log: what is turned down, or "nothing".</summary>
    public string Describe()
    {
        if (IsNone) return "nothing turned down";
        var parts = new List<string>();
        if (MaxThreads is { } t) parts.Add(Invariant($"threads <= {t}"));
        if (WorkingMemory is { } m) parts.Add($"working memory {m}");
        if (MaxContext is { } c) parts.Add(Invariant($"context <= {c}"));
        if (ThinkingBudget is { } b) parts.Add(Invariant($"thinking <= {b}"));
        if (ScalarKernels) parts.Add("scalar kernels");
        if (AvoidedBrains.Count > 0) parts.Add("stepped down from " + string.Join(", ", AvoidedBrains));
        return string.Join("; ", parts);
    }

    // ── on disk ──────────────────────────────────────────────────────────────

    /// <summary>The file's name in <see cref="Directory"/>.</summary>
    public const string FileName = "brain-tuning.json";

    private static readonly object FileGate = new();

    /// <summary>
    /// Where the projection lives, set by the host in every process that builds or chooses a
    /// brain. Null - every desktop test, and any host that has no healing - is no tuning.
    /// </summary>
    public static string? Directory { get; set; }

    /// <summary>The knobs in force now, from <see cref="Directory"/>. Never throws.</summary>
    public static BrainTuning Current(DateTimeOffset? now = null) => From(ReadKnobs(), now ?? DateTimeOffset.UtcNow);

    /// <summary>Every knob in the file, in force or not. Never throws; empty when there is none.</summary>
    public static IReadOnlyList<TunedKnob> ReadKnobs()
    {
        var dir = Directory;
        if (string.IsNullOrWhiteSpace(dir)) return [];
        try
        {
            var path = Path.Combine(dir, FileName);
            if (!File.Exists(path)) return [];
            string json;
            lock (FileGate) json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<TuningFile>(json);
            return file?.Knobs?.Where(k => k is not null && !string.IsNullOrWhiteSpace(k.Name)).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Replaces the projection with <paramref name="knobs"/>; false when it could not be written.</summary>
    /// <remarks>
    /// WRITTEN WHOLE, THEN MOVED INTO PLACE, so the inference process - which may read it at any
    /// moment a brain is built - never sees half a file.
    /// </remarks>
    public static bool Write(IEnumerable<TunedKnob> knobs)
    {
        ArgumentNullException.ThrowIfNull(knobs);
        var dir = Directory;
        if (string.IsNullOrWhiteSpace(dir)) return false;
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, FileName);
            var json = JsonSerializer.Serialize(new TuningFile { Version = 1, Knobs = knobs.ToList() },
                new JsonSerializerOptions { WriteIndented = true });
            lock (FileGate)
            {
                var writing = path + ".writing";
                File.WriteAllText(writing, json);
                File.Move(writing, path, overwrite: true);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The tuning <paramref name="knobs"/> describe at <paramref name="now"/>: entries past their time are not in force.</summary>
    public static BrainTuning From(IEnumerable<TunedKnob> knobs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(knobs);
        int? threads = null, context = null, thinking = null;
        KvCompressionMode? memory = null;
        var scalar = false;
        var avoided = new List<string>();

        foreach (var knob in knobs)
        {
            if (knob is null || knob.UntilUtc <= now) continue;
            switch (knob.Name)
            {
                case Knobs.Threads when Number(knob.Value) is int t && t > 0:
                    threads = threads is { } a ? Math.Min(a, t) : t;
                    break;
                case Knobs.Context when Number(knob.Value) is int c && c > 0:
                    context = context is { } b ? Math.Min(b, c) : c;
                    break;
                case Knobs.Thinking when Number(knob.Value) is int n && n > 0:
                    thinking = thinking is { } d ? Math.Min(d, n) : n;
                    break;
                case Knobs.MemoryBits when Enum.TryParse<KvCompressionMode>(knob.Value, ignoreCase: true, out var mode)
                                           && mode != KvCompressionMode.Off:
                    memory = memory is { } was && (int)was > (int)mode ? was : mode;   // the fewer bits win
                    break;
                case Knobs.Kernels:
                    scalar |= string.Equals(knob.Value, "scalar", StringComparison.OrdinalIgnoreCase);
                    break;
                default:
                    if (knob.Name.StartsWith(Knobs.AvoidPrefix, StringComparison.Ordinal)
                        && knob.Name.Length > Knobs.AvoidPrefix.Length)
                        avoided.Add(knob.Name[Knobs.AvoidPrefix.Length..]);
                    break;
            }
        }

        return new BrainTuning
        {
            MaxThreads = threads,
            WorkingMemory = memory,
            MaxContext = context,
            ThinkingBudget = thinking,
            ScalarKernels = scalar,
            AvoidedBrains = avoided.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    private static int? Number(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private sealed class TuningFile
    {
        [JsonPropertyName("version")] public int Version { get; set; }

        [JsonPropertyName("knobs")] public List<TunedKnob>? Knobs { get; set; }
    }
}
