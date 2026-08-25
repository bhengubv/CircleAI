// IConversation.cs
//
// The app as a conversation rather than as a demo.
//
// THIS IS THE GAP THE PARITY AUDIT FOUND. SpokenReply, VoiceTurn, HandsFree,
// Earcon, the greeting cycle and the language read-out are not six missing
// features - they are one: the phone listening and answering out loud. A chat box
// that types back is a demo of a language model; this is what makes it an
// assistant somebody can use without reading.

namespace CircleAI.Samples.It;

/// <summary>What a voice turn is doing right now.</summary>
public enum TurnPhase
{
    /// <summary>Nothing happening.</summary>
    Idle,

    /// <summary>Microphone open, waiting for speech.</summary>
    Listening,

    /// <summary>Heard something, working on it.</summary>
    Thinking,

    /// <summary>Answering out loud.</summary>
    Speaking,
}

/// <summary>Where a voice turn has got to.</summary>
/// <param name="Phase">Which part of the exchange.</param>
/// <param name="Level">
/// Microphone level, 0 to 1, while listening. A REAL level or zero - never an
/// invented one, because the mark scales its arcs with this and a fake meter lies
/// about the one thing the person is trying to find out.
/// </param>
/// <param name="Heard">What it understood, once it has.</param>
/// <param name="Reply">The answer so far, as it streams.</param>
/// <param name="Language">
/// The language the turn was spoken in, for the read-out. NOT a setting: it
/// reports what happened, and stays empty until a turn has actually been heard -
/// announcing a language before anybody has spoken is a claim the phone has not
/// earned.
/// </param>
/// <param name="Detail">Anything worth saying, including why it stopped.</param>
public sealed record TurnState(
    TurnPhase Phase,
    double Level = 0,
    string? Heard = null,
    string? Reply = null,
    string? Language = null,
    string? Detail = null);

/// <summary>Runs a spoken exchange: listen, think, answer aloud.</summary>
public interface IConversation
{
    /// <summary>Whether a spoken turn can run at all on this head.</summary>
    Task<BrainState> StateAsync(CancellationToken ct = default);

    /// <summary>
    /// One turn: open the microphone, hear a question, answer it out loud.
    /// </summary>
    /// <remarks>
    /// Reports every phase, because the mark on the home screen IS the progress
    /// indicator and it has a different motion for each: listening scales with
    /// your voice, thinking travels outward, speaking fires in sequence.
    /// </remarks>
    Task TurnAsync(IProgress<TurnState> updates, CancellationToken ct = default);

    /// <summary>Say something aloud, without listening first.</summary>
    /// <remarks>
    /// What the chat screen's speaker control uses, and the greeting the circle
    /// gives on a phone that cannot converse yet: pressing it must DO something,
    /// because "nothing happens" is indistinguishable from "broken".
    /// </remarks>
    Task SayAsync(string text, string? languageTag = null, CancellationToken ct = default);

    /// <summary>Answer a question about an image.</summary>
    Task<string> SeeAsync(
        string question, byte[] image, Action<string>? token = null, CancellationToken ct = default);
}
