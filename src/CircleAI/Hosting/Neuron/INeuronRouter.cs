// INeuronRouter.cs
//
// The concierge's decision layer. Per turn it decides which organ answers:
// the always-warm generalist, or a capability-matched specialist the concierge
// hot-loads beside it. Cheap and synchronous — this runs before every
// generation, so implementations must not do model inference.

using CircleAI.Inference;

namespace CircleAI.Hosting.Neuron;

/// <summary>Which organ answers a given turn.</summary>
public enum Organ
{
    /// <summary>The always-warm generalist model (the floor — never evicted).</summary>
    Generalist = 0,

    /// <summary>A capability-matched specialist, hot-loaded into the second slot.</summary>
    Specialist = 1,
}

/// <summary>
/// The inputs the concierge classifies for a single turn. Kept tiny — the
/// router runs on the hot path.
/// </summary>
/// <param name="Query">The user's latest message text.</param>
/// <param name="HasImage">Whether the turn carries an image attachment.</param>
public sealed record RouteContext(string Query, bool HasImage = false);

/// <summary>
/// The concierge's per-turn decision. When <see cref="Organ"/> is
/// <see cref="Organ.Specialist"/>, <see cref="Capability"/> names the capability
/// the specialist must satisfy (fed straight to <c>IModelSelector.BestFit</c>);
/// for the generalist it is <see cref="ChatCapability.Default"/>.
/// </summary>
/// <param name="Organ">Which organ should answer.</param>
/// <param name="Capability">Capability the specialist must satisfy.</param>
/// <param name="Reason">Human-readable rationale (telemetry / debugging).</param>
public sealed record RouteDecision(Organ Organ, ChatCapability Capability, string Reason)
{
    /// <summary>Route to the always-warm generalist.</summary>
    public static RouteDecision Generalist(string reason = "generalist")
        => new(Organ.Generalist, ChatCapability.Default, reason);

    /// <summary>Route to a capability-matched specialist.</summary>
    public static RouteDecision Specialist(ChatCapability capability, string reason)
        => new(Organ.Specialist, capability, reason);
}

/// <summary>
/// The concierge's brain: decide, per turn, whether the always-warm generalist
/// answers or a specialist should. Implementations must be cheap and
/// synchronous — no model inference — because this runs before every
/// generation. The default heuristic lives in <see cref="HeuristicNeuronRouter"/>.
/// </summary>
public interface INeuronRouter
{
    /// <summary>Classify a turn into a routing decision.</summary>
    RouteDecision Route(RouteContext context);
}
