// HealingKnobs.cs
//
// THE C# ENGINE'S KNOBS, AS NOTCHES ON SELFHEAL'S SWITCHES (S6).
//
// Every knob Wolverine may turn is one of SelfHeal's switches, and SelfHeal's back-off is the
// loop guard: a trip holds the knob down for an hour, the next for six, then a day, then a week,
// and each trip is one notch further - three threads to two to one; working memory at 4 bits to
// 3 to 2; a 4096-token context to 2048 to 1024; no thinking budget to 1024 tokens to 512 to 256;
// vector kernels to scalar. When the window runs out the knob is back at full by the clock - the
// half-open try - and SelfHeal.Worked forgets the count once the brain has answered at full
// again. Nothing here decides that a knob is broken for good.
//
// A BRAIN STEPPED DOWN FROM is a switch of its own, "brain:<model id>", with the same back-off:
// the supervisor passes over it while it is off and the next brain this device holds answers.
//
// The values reach the brain through BrainTuning's file, written here whenever a switch moves -
// the inference process builds the brain and cannot see this process's switches.

using CircleAI.Core;
using CircleAI.Inference;
using CircleAI.SelfKnowledge;

namespace CircleAI.Hosting.SelfHealing;

/// <summary>Knob values by notch, and the projection of SelfHeal's switches onto BrainTuning.</summary>
public sealed class HealingKnobs
{
    /// <summary>The shortest context a knob turns down to: a persona and a question still fit.</summary>
    public const int MinContext = 1024;

    /// <summary>The prefix of a "stepped down from this brain" switch.</summary>
    public const string BrainPrefix = "brain:";

    /// <summary>The prefix of a "this model was fetched again" switch - one re-download per window.</summary>
    public const string RedownloadPrefix = "redownload:";

    /// <summary>The prefix of a "start this download from its other address" switch.</summary>
    public const string MirrorPrefix = "mirror:";

    private readonly TimeProvider _clock;

    /// <param name="fullThreads">The threads a brain runs at full: the device's fast cores, one left over.</param>
    /// <param name="fullContext">The context a brain holds at full.</param>
    /// <param name="clock">Injected for testable time.</param>
    public HealingKnobs(int fullThreads, int fullContext = 4096, TimeProvider? clock = null)
    {
        FullThreads = Math.Max(1, fullThreads);
        FullContext = Math.Max(MinContext, fullContext);
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Threads at full.</summary>
    public int FullThreads { get; }

    /// <summary>Context at full.</summary>
    public int FullContext { get; }

    // ── the ladders ──────────────────────────────────────────────────────────

    /// <summary>Threads at a notch: a third of them fewer each notch, never under one.</summary>
    public static int ThreadsAt(int full, int notch) =>
        notch <= 0 ? full : Math.Max(1, full - notch * Math.Max(1, full / 3));

    /// <summary>Working memory at a notch: 4 bits, then 3, then 2.</summary>
    public static KvCompressionMode MemoryAt(int notch) => notch switch
    {
        <= 0 => KvCompressionMode.TurboQuant4Bit,
        1 => KvCompressionMode.TurboQuant3Bit,
        _ => KvCompressionMode.TurboQuant2Bit,
    };

    /// <summary>Context at a notch: halved each notch, never under <see cref="MinContext"/>.</summary>
    public static int ContextAt(int full, int notch) =>
        notch <= 0 ? full : Math.Max(MinContext, full >> Math.Min(notch, 16));

    /// <summary>The thinking budget at a notch, in tokens; 0 is none.</summary>
    public static int ThinkingAt(int notch) => notch switch
    {
        <= 0 => 0,
        1 => 1024,
        2 => 512,
        _ => 256,
    };

    /// <summary>
    /// What a switch's knob is at <paramref name="notch"/>, as BrainTuning writes it; null at full
    /// (notch 0), and null for a switch that is not a knob.
    /// </summary>
    public string? ValueAt(string switchName, int notch)
    {
        if (notch <= 0) return null;
        return KnobOf(switchName) switch
        {
            // A NOTCH THAT CHANGES NOTHING IS NO NOTCH: one thread on a one-core device, the
            // shortest context on a device already holding it, read as "at full".
            BrainTuning.Knobs.Threads => ThreadsAt(FullThreads, notch) is var t && t < FullThreads ? Invariant(t) : null,
            BrainTuning.Knobs.MemoryBits => MemoryAt(notch).ToString(),
            BrainTuning.Knobs.Context => ContextAt(FullContext, notch) is var c && c < FullContext ? Invariant(c) : null,
            BrainTuning.Knobs.Thinking => Invariant(ThinkingAt(notch)),
            BrainTuning.Knobs.Kernels => "scalar",
            _ => null,
        };
    }

    /// <summary>What <paramref name="tuning"/> has in force for a switch's knob, in the same text as <see cref="ValueAt"/>.</summary>
    public static string? InForce(string switchName, BrainTuning tuning)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        return KnobOf(switchName) switch
        {
            BrainTuning.Knobs.Threads => tuning.MaxThreads is { } t ? Invariant(t) : null,
            BrainTuning.Knobs.MemoryBits => tuning.WorkingMemory?.ToString(),
            BrainTuning.Knobs.Context => tuning.MaxContext is { } c ? Invariant(c) : null,
            BrainTuning.Knobs.Thinking => tuning.ThinkingBudget is { } b ? Invariant(b) : null,
            BrainTuning.Knobs.Kernels => tuning.ScalarKernels ? "scalar" : null,
            _ => null,
        };
    }

    /// <summary>The BrainTuning knob a SelfHeal switch turns, or null.</summary>
    public static string? KnobOf(string? switchName) => (switchName ?? string.Empty).ToLowerInvariant() switch
    {
        SelfHeal.Switch.BrainThreads => BrainTuning.Knobs.Threads,
        SelfHeal.Switch.WorkingMemoryBits => BrainTuning.Knobs.MemoryBits,
        SelfHeal.Switch.LongContext => BrainTuning.Knobs.Context,
        SelfHeal.Switch.ThinkingBudget => BrainTuning.Knobs.Thinking,
        SelfHeal.Switch.VectorKernels => BrainTuning.Knobs.Kernels,
        _ => null,
    };

    // ── where each switch stands ─────────────────────────────────────────────

    /// <summary>How many times a switch has tripped on this device, as SelfHeal holds it; 0 when never.</summary>
    public static int Trips(string switchName) =>
        SelfHeal.All().FirstOrDefault(h => string.Equals(h.Name, switchName, StringComparison.OrdinalIgnoreCase))?.Trips ?? 0;

    /// <summary>The notch a switch stands at: its trips while it is off; 0 once its window ran out.</summary>
    public int Notch(string switchName, DateTimeOffset? now = null) =>
        SelfHeal.IsOff(switchName, now ?? _clock.GetUtcNow()) ? Trips(switchName) : 0;

    /// <summary>Whether one more trip would change nothing: the knob is at its floor.</summary>
    public bool AtFloor(string switchName, DateTimeOffset? now = null) =>
        string.Equals(ValueAt(switchName, Trips(switchName) + 1), ValueAt(switchName, Notch(switchName, now)), StringComparison.Ordinal);

    // ── onto the brain ───────────────────────────────────────────────────────

    /// <summary>
    /// Writes every knob SelfHeal holds down now - with the time its window runs out - to
    /// BrainTuning's file, and returns the tuning that is in force.
    /// </summary>
    /// <remarks>
    /// Called whenever a switch moves and once at start-up, after SelfModel has put back what an
    /// earlier run switched off: a knob a previous run turned down is still down in this one,
    /// for the rest of its window, in the process that builds the brain.
    /// </remarks>
    public BrainTuning Project(DateTimeOffset? now = null)
    {
        var at = now ?? _clock.GetUtcNow();
        var knobs = new List<TunedKnob>();
        foreach (var healed in SelfHeal.All())
        {
            if (!healed.IsOff(at)) continue;
            if (KnobOf(healed.Name) is { } knob)
            {
                if (ValueAt(healed.Name, healed.Trips) is { } value) knobs.Add(new TunedKnob(knob, value, healed.RetryAfterUtc));
            }
            else if (healed.Name.StartsWith(BrainPrefix, StringComparison.OrdinalIgnoreCase) && healed.Name.Length > BrainPrefix.Length)
            {
                knobs.Add(new TunedKnob(BrainTuning.Knobs.AvoidPrefix + healed.Name[BrainPrefix.Length..], "1", healed.RetryAfterUtc));
            }
        }
        BrainTuning.Write(knobs);
        return BrainTuning.From(knobs, at);
    }

    /// <summary>
    /// Whether <paramref name="modelId"/>'s download starts from its other address now: its mirror
    /// switch is off and has tripped an odd number of times, so each resume alternates the two.
    /// </summary>
    public static bool StartsFromAlternate(string modelId, DateTimeOffset? now = null)
    {
        var name = MirrorPrefix + modelId;
        return SelfHeal.IsOff(name, now) && Trips(name) % 2 == 1;
    }

    private static string Invariant(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
