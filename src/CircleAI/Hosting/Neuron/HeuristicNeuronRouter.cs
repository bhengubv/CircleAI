// HeuristicNeuronRouter.cs
//
// The default concierge router: cheap, deterministic keyword + length +
// modality heuristics — no model inference. The "gear system": most turns ride
// the always-warm generalist; a turn that clearly wants a different capability
// (vision, long-context, hard reasoning) is routed to a capability-matched
// specialist the concierge hot-loads beside the warm generalist.

using CircleAI.Inference;

namespace CircleAI.Hosting.Neuron;

/// <summary>
/// Default <see cref="INeuronRouter"/>. Routes on modality (image → vision),
/// prompt length (very long → long-context), and reasoning/coding cues
/// (→ reasoning); everything else stays on the generalist. Applies the
/// supplied <see cref="NeuronGate"/> to its raw decision.
/// </summary>
public sealed class HeuristicNeuronRouter : INeuronRouter
{
    private readonly NeuronGate _gate;
    private readonly int _longContextChars;

    // Lowercase substrings that signal a turn wants an explicit reasoning model.
    private static readonly string[] ReasoningCues =
    {
        "prove", "solve", "calculate", "derive", "algorithm", "complexity",
        "debug", "stack trace", "refactor", "regex", "step by step",
        "step-by-step", "theorem", "equation", "big-o", "big o",
    };

    /// <param name="gate">Guardrail checkpoint. <c>null</c> uses a no-veto gate.</param>
    /// <param name="longContextChars">
    /// Prompt length (chars) at or above which the turn routes to a long-context
    /// specialist. Default 4000.
    /// </param>
    public HeuristicNeuronRouter(NeuronGate? gate = null, int longContextChars = 4000)
    {
        _gate = gate ?? new NeuronGate();
        _longContextChars = longContextChars > 0 ? longContextChars : 4000;
    }

    /// <inheritdoc />
    public RouteDecision Route(RouteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var decision = Classify(context);
        return _gate.Apply(decision, context);
    }

    private RouteDecision Classify(RouteContext context)
    {
        // 1. An image attachment needs a vision model.
        if (context.HasImage)
            return RouteDecision.Specialist(ChatCapability.Vision, "image attached -> vision specialist");

        var query = context.Query ?? string.Empty;

        // 2. A very long prompt needs a long-context model.
        if (query.Length >= _longContextChars)
            return RouteDecision.Specialist(
                ChatCapability.LongContext,
                $"prompt length {query.Length} >= {_longContextChars} -> long-context specialist");

        // 3. Reasoning / coding cues want an explicit reasoning model.
        var lower = query.ToLowerInvariant();
        foreach (var cue in ReasoningCues)
        {
            if (lower.Contains(cue, StringComparison.Ordinal))
                return RouteDecision.Specialist(ChatCapability.Reasoning, $"reasoning cue '{cue}' -> reasoning specialist");
        }

        // 4. Everything else: the always-warm generalist.
        return RouteDecision.Generalist("no specialist cue -> generalist");
    }
}
