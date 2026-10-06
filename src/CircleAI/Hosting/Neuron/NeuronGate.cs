// NeuronGate.cs
//
// The concierge's guardrail checkpoint. Sits between the router's raw decision
// and the act of loading/answering. Guardrails, not limits: the default gate
// applies no veto — a guardrail is only enforced when the host configures one.

namespace CircleAI.Hosting.Neuron;

/// <summary>
/// The concierge's guardrail checkpoint. An optional content predicate can force
/// a turn back to the generalist (which always carries the persona + system
/// prompt) instead of spinning up a specialist. A <c>null</c> predicate applies
/// no veto — the honest default: no guardrail configured, none applied.
/// </summary>
public sealed class NeuronGate
{
    private readonly Func<string, bool>? _allowSpecialist;

    /// <param name="allowSpecialist">
    /// Optional predicate over the user query. Return <c>false</c> to force the
    /// turn onto the generalist even when the router picked a specialist.
    /// <c>null</c> (default) applies no veto.
    /// </param>
    public NeuronGate(Func<string, bool>? allowSpecialist = null)
        => _allowSpecialist = allowSpecialist;

    /// <summary>
    /// Apply guardrails to a raw router decision, returning the effective
    /// decision the concierge will act on.
    /// </summary>
    public RouteDecision Apply(RouteDecision decision, RouteContext context)
    {
        if (decision.Organ == Organ.Specialist
            && _allowSpecialist is not null
            && !_allowSpecialist(context.Query))
        {
            return RouteDecision.Generalist("gate: specialist vetoed → generalist");
        }

        return decision;
    }
}
