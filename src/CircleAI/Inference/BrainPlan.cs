// BrainPlan.cs
//
// STEP 1'S ANSWER, SAID OUT LOUD: THIS IS THE BRAIN FOR THIS DEVICE NOW, AND WHY.
//
// The choice itself is made where it always was - the selector, which now passes over a
// brain too slow to talk with (BrainSpeed) - and the knobs are set where the brain is built
// (BrainFactory, from DeviceContext). What was missing is the sentence: a phone that keeps a
// smaller brain while a bigger one sits in the catalogue should be able to say it measured
// the bigger one and found it too slow, not leave a person to guess.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;

namespace CircleAI.Inference;

/// <summary>Why this device runs the brain it runs.</summary>
public static class BrainPlan
{
    /// <summary>One sentence a person can be shown: which brain, on what, and why not a higher-ranked one.</summary>
    /// <param name="context">The device, as Step 1 read it.</param>
    /// <param name="chosen">The brain chosen, or null when none can run here.</param>
    /// <param name="catalogue">Every model known, for what was passed over and for estimates.</param>
    public static string Explain(DeviceContext context, ModelEntry? chosen, IReadOnlyList<ModelEntry> catalogue)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(catalogue);
        if (chosen is null) return "no brain this device can run is on it or catalogued for it";

        var engine = EngineChoice.Run(chosen) ?? chosen.Engine;
        var parts = new List<string> { $"{chosen.Name} on {EngineName(engine)}" };
        if (engine == ModelEngine.Managed) parts.Add($"{context.BrainThreads} threads");
        if (BrainSpeed.Of(chosen, catalogue) is { } speed)
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"about {speed.TokensPerSecond:F1} tokens a second{(speed.From == chosen.Name ? string.Empty : $" (estimated from {speed.From})")}"));

        // THE ONE IT DID NOT CHOOSE, when speed is the reason - the sentence that makes keeping a
        // smaller brain a decision rather than a mystery.
        var passedOver = catalogue
            .Where(e => e.Modality == ModelModality.Chat && !e.Retired && e.QualityRank > chosen.QualityRank
                        && EngineChoice.CanRun(e) && ModelFit.CouldEverHold(e, context.Probe)
                        && BrainSpeed.TooSlow(e, catalogue))
            .OrderByDescending(e => e.QualityRank)
            .FirstOrDefault();

        var sentence = string.Join(", ", parts);
        return passedOver is null
            ? sentence
            : $"{sentence} - not {passedOver.Name}: {BrainSpeed.WhyTooSlow(passedOver, catalogue)}";
    }

    private static string EngineName(ModelEngine engine) => engine switch
    {
        ModelEngine.Managed  => "the C# engine",
        ModelEngine.LlamaCpp => "llama.cpp",
        ModelEngine.Mnn      => "MNN",
        _                    => engine.ToString(),
    };
}
