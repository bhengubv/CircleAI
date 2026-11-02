// CanDoNow.cs
//
// "WHAT CAN YOU DO RIGHT NOW?" - ANSWERED LIVE, NOT FROM THE BROCHURE (Step 4).
//
// Two answers already existed and neither is this one. The Capabilities verb reads the honest
// manifest - what the product can do on any device - and the Abilities verb reads this device's
// disk - what is downloaded. Neither knows that the brain is half-way through loading, that the
// eyes are 40% down and will land in four minutes, that the person switched listening off, or that
// the balance loop has put seeing to rest while the phone is hot. Right now is all of those at once.
//
// PURE, SO EVERY RULE IS TESTED OFF A PHONE: the service gathers the facts (abilities, the brain's
// phase from the readiness tracker, the person's switches, the balance's resting list, the setup
// board) and this turns them into one row per ability and one sentence.

using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Core;

namespace CircleAI.Assistant;

/// <summary>What an ability can do at this moment. Appended, never renumbered.</summary>
public enum NowState
{
    /// <summary>It works now.</summary>
    Now = 0,

    /// <summary>It will work by itself shortly: arriving, loading, or about to retry.</summary>
    Soon = 1,

    /// <summary>The person switched it off.</summary>
    Off = 2,

    /// <summary>The service put it to rest to spare the phone - heat, battery, memory - and wakes it when calm.</summary>
    Resting = 3,

    /// <summary>Not on this device, and nothing is fetching it.</summary>
    Missing = 4,

    /// <summary>This device cannot do it: too big, or nothing serves it.</summary>
    Cannot = 5,

    /// <summary>Everything it needs is here and it is not switched on (the wake word, not listening).</summary>
    NotOn = 6,
}

/// <summary>One ability, now.</summary>
/// <param name="Title">The ability, as the Abilities verb names it; empty for the summary row.</param>
/// <param name="State">What it can do at this moment.</param>
/// <param name="Detail">One plain sentence: why, or how long.</param>
/// <param name="Progress">0..1 while what it needs is arriving; null otherwise.</param>
public sealed record CanDoRow(string Title, NowState State, string Detail, double? Progress);

/// <summary>Composes the live answer to "what can you do right now".</summary>
public static class CanDoNow
{
    /// <summary>One row per ability, in the abilities' own order.</summary>
    /// <param name="abilities">This device's abilities (the Abilities verb's rows).</param>
    /// <param name="brain">The brain's state, from the readiness tracker.</param>
    /// <param name="needsBrain">Which abilities only work when the brain answers.</param>
    /// <param name="switchedOff">Abilities the person switched off, by title.</param>
    /// <param name="resting">Abilities the balance put to rest, with why.</param>
    /// <param name="arriving">What the setup board says of the model an ability needs, or null.</param>
    public static IReadOnlyList<CanDoRow> Answer(
        IReadOnlyList<AbilityRow> abilities,
        ComponentState? brain,
        Func<string, bool> needsBrain,
        IReadOnlySet<string> switchedOff,
        IReadOnlyDictionary<string, string> resting,
        Func<string, SetupItemStatus?> arriving)
    {
        ArgumentNullException.ThrowIfNull(abilities);
        ArgumentNullException.ThrowIfNull(needsBrain);
        ArgumentNullException.ThrowIfNull(switchedOff);
        ArgumentNullException.ThrowIfNull(resting);
        ArgumentNullException.ThrowIfNull(arriving);

        var rows = new List<CanDoRow>(abilities.Count);
        foreach (var ability in abilities)
        {
            var title = ability.Title;
            if (switchedOff.Contains(title))
            {
                rows.Add(new(title, NowState.Off, "Switched off. Turn it on to use it again.", null));
                continue;
            }

            if (resting.TryGetValue(title, out var why))
            {
                rows.Add(new(title, NowState.Resting, why, null));
                continue;
            }

            var coming = Safe(arriving, title);
            if (needsBrain(title))
            {
                rows.Add(Brain(title, brain, coming));
                continue;
            }

            rows.Add(ability.State switch
            {
                AbilityState.On => new(title, NowState.Now, "Works now.", null),
                AbilityState.Ready => new(title, NowState.NotOn, "Everything it needs is here; it is not switched on.", null),
                AbilityState.Available => coming is { Settled: false } item
                    ? new(title, NowState.Soon, Arriving(item), item.Fraction)
                    : new(title, NowState.Missing, "Not on this phone yet.", null),
                AbilityState.TooBig => new(title, NowState.Cannot, "This phone cannot hold what it needs.", null),
                _ => new(title, NowState.Cannot, "Nothing on this phone can do this yet.", null),
            });
        }
        return rows;
    }

    /// <summary>The sentence to say: what works now, and what is coming.</summary>
    public static string Sentence(IReadOnlyList<CanDoRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var now = rows.Where(r => r.State == NowState.Now && r.Title.Length > 0).Select(r => r.Title.ToLowerInvariant()).ToList();
        var soon = rows.Where(r => r.State == NowState.Soon && r.Title.Length > 0).Select(r => r.Title.ToLowerInvariant()).ToList();
        var resting = rows.Where(r => r.State == NowState.Resting && r.Title.Length > 0).Select(r => r.Title.ToLowerInvariant()).ToList();

        var parts = new List<string>();
        parts.Add(now.Count == 0 ? "Nothing works yet" : "Right now: " + Join(now));
        if (soon.Count > 0) parts.Add("on its way: " + Join(soon));
        if (resting.Count > 0) parts.Add("resting to spare the phone: " + Join(resting));
        return string.Join("; ", parts) + ".";
    }

    /// <summary>The summary row: an empty title, the overall state, and the sentence.</summary>
    public static CanDoRow Summary(IReadOnlyList<CanDoRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var state = rows.Any(r => r.State == NowState.Now) ? NowState.Now
                  : rows.Any(r => r.State == NowState.Soon) ? NowState.Soon
                  : NowState.Missing;
        return new CanDoRow(string.Empty, state, Sentence(rows), null);
    }

    private static CanDoRow Brain(string title, ComponentState? brain, SetupItemStatus? coming)
    {
        if (brain is not null)
        {
            switch (brain.Phase)
            {
                case BrainPhase.Ready:
                    return new(title, NowState.Now, "Works now.", null);
                case BrainPhase.Loading:
                    return new(title, NowState.Soon, ReadinessSentences.For(brain), brain.Progress);
                case BrainPhase.Installed:
                    return new(title, NowState.Soon, "Its brain loads when you ask; the first answer takes a moment.", null);
                case BrainPhase.Failed or BrainPhase.Paused when brain.RetryAfter is not null:
                    return new(title, NowState.Soon, ReadinessSentences.For(brain), null);
                case BrainPhase.Failed or BrainPhase.Paused:
                    return new(title, NowState.Cannot, ReadinessSentences.For(brain), null);
            }
        }

        // NO BRAIN, OR NOTHING SAID YET: a brain on its way is "soon", with how far it has got.
        return coming is { Settled: false } item
            ? new(title, NowState.Soon, Arriving(item), item.Fraction)
            : new(title, NowState.Missing, ReadinessSentences.NoModel, null);
    }

    private static string Arriving(SetupItemStatus item) => item.Describe();

    private static SetupItemStatus? Safe(Func<string, SetupItemStatus?> arriving, string title)
    {
        try { return arriving(title); }
        catch { return null; }
    }

    private static string Join(IReadOnlyList<string> words) => words.Count switch
    {
        0 => string.Empty,
        1 => words[0],
        _ => string.Join(", ", words.Take(words.Count - 1)) + " and " + words[^1],
    };
}
