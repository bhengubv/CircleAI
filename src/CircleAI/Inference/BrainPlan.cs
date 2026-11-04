// BrainPlan.cs
//
// STEP 1'S ANSWER, SAID OUT LOUD: THIS IS THE BRAIN FOR THIS DEVICE NOW, ON WHAT, AND HOW FAST.
//
// The choice itself is made where it always was - the selector, by the one rule every pick uses
// (ModelChoice.Best: the Bonsai 2 fleet first, then rank) - and the knobs are set where the brain
// is built (BrainFactory, from DeviceContext). This is the sentence: which brain, on which engine,
// with how many threads, and how fast it answers here. Speed is said, never a reason: this once
// named the brain a phone passed over for being below four tokens a second, and the owner ruled
// that gate out on 2026-11-04.

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
    /// <summary>One sentence a person can be shown: which brain, on what, and how fast it answers here.</summary>
    /// <param name="context">The device, as Step 1 read it.</param>
    /// <param name="chosen">The brain chosen, or null when none can run here.</param>
    /// <param name="catalogue">Every model known, for estimates.</param>
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

        return string.Join(", ", parts);
    }

    private static string EngineName(ModelEngine engine) => engine switch
    {
        ModelEngine.Managed  => "the C# engine",
        ModelEngine.LlamaCpp => "llama.cpp",
        ModelEngine.Mnn      => "MNN",
        _                    => engine.ToString(),
    };
}
