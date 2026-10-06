// NeuronVoice.cs
//
// Wires the existing voice pipeline to a Neuron brain. The Neuron lives in
// CircleAI.Hosting as an IAIService; voice lives here (Companion) and in
// CircleAI.Voice. This helper is the composition seam: build a CompanionSession
// over the brain — so the Neuron's concierge routing, two-slot residency,
// memory, and persona all apply — and hand it to the existing
// VoiceCompanionListener. "Hey B" -> transcribe -> brain -> reply -> the host
// plays it back via VoiceCompanionListener.ResponseReady. No new voice logic.

using CircleAI.Hosting;
using CircleAI.Voice;

namespace CircleAI.Companion;

/// <summary>
/// Composition helpers that put a Neuron brain behind the voice pipeline.
/// </summary>
public static class NeuronVoice
{
    /// <summary>
    /// Create a voice listener that drives <paramref name="brain"/> (the Neuron).
    /// The host owns the <paramref name="pipeline"/> (wake-word detector +
    /// transcriber + TTS) and the returned listener's lifetime — dispose it to
    /// tear down the pipeline and session.
    /// </summary>
    /// <param name="pipeline">The platform voice pipeline (wake + STT + TTS).</param>
    /// <param name="brain">The Neuron brain (an <c>AIService</c> / <c>NeuronNode.Brain</c>).</param>
    /// <param name="identityId">Per-user identity for the Companion session.</param>
    /// <param name="displayName">Display name for the session.</param>
    public static VoiceCompanionListener CreateListener(
        VoicePipeline pipeline,
        IAIService brain,
        string identityId = "default",
        string displayName = "You")
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(brain);

        var session = new CompanionSession(
            identityId:        identityId,
            displayName:       displayName,
            @interface:        InterfaceKind.Ambient,
            preferredLanguage: null,
            ai:                brain);

        return new VoiceCompanionListener(pipeline, session);
    }
}
