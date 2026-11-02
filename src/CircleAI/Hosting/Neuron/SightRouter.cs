// SightRouter.cs
//
// A PICTURE GOES TO A BRAIN THAT CAN SEE; EVERYTHING ELSE STAYS WITH THE BRAIN.
//
// The service's inference process ran with no router, so every turn - a picture included - went to
// the one brain it had loaded, and on the P30 that brain is a text model: a question about a photo
// through the app's own path was answered about a picture it never got (S9, 2026-11-02), while the
// phone held SmolVLM as its eyes. The full concierge (HeuristicNeuronRouter) would also send long
// prompts and reasoning cues to specialists, which on a 3.6 GB phone means loading models nobody
// planned for; this routes on the picture alone. The specialist is best-fitted for Vision and
// admitted only if it fits beside the brain (ResidentSlotManager) - and when it is not, AIService
// says it cannot see rather than guess.

using CircleAI.Inference;

namespace CircleAI.Hosting.Neuron;

/// <summary>An <see cref="INeuronRouter"/> that sends a turn with a picture to a vision specialist, and nothing else anywhere.</summary>
public sealed class SightRouter : INeuronRouter
{
    /// <inheritdoc />
    public RouteDecision Route(RouteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.HasImage
            ? RouteDecision.Specialist(ChatCapability.Vision, "a picture -> a brain that can see")
            : RouteDecision.Generalist("no picture -> the brain");
    }
}
