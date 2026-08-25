// IVoiceHost.cs
//
// What a head must provide for the shared UI to speak.
//
// THE UI IS WRITTEN ONCE AND THE CAPABILITY IS NOT THE SAME EVERYWHERE, and
// pretending otherwise is the trap. On the phone, synthesis is a 144 MB ONNX
// graph, a 103 MB morphological dictionary and a native espeak provider. In a
// browser tab none of that exists.
//
// So the contract makes the difference VISIBLE rather than hiding it: a head
// says what it can do, and the UI says so too. The alternative - a browser
// implementation that quietly returns silence - produces a "Hear it" button that
// looks identical to the working one and does nothing, which is worse than a
// button that explains itself.

namespace CircleAI.Samples.It;

/// <summary>Why a head cannot speak, when it cannot.</summary>
public enum VoiceAvailability
{
    /// <summary>Synthesis runs here, on this device, offline.</summary>
    OnDevice,

    /// <summary>
    /// This head has no synthesiser at all - a browser tab, typically.
    /// </summary>
    /// <remarks>
    /// NOT an error and NOT a missing feature. The sample's whole claim is that
    /// the work happens on the phone with nothing leaving it; a web page
    /// truthfully cannot make that claim, and saying so is more useful than
    /// shipping a button that appears to work.
    /// </remarks>
    Unavailable,
}

/// <summary>The outcome of asking a head to speak.</summary>
/// <param name="Spoke">True when audio was actually produced and played.</param>
/// <param name="Detail">
/// What happened, in words a person can read. Carries the reason on failure and
/// the measurement on success, because "it spoke" with no number behind it is
/// the claim this sample exists to stop making.
/// </param>
/// <param name="Milliseconds">Wall-clock time to produce the audio, or null.</param>
/// <param name="AudioMilliseconds">Length of the audio produced, or null.</param>
public sealed record SpeakOutcome(
    bool Spoke,
    string Detail,
    long? Milliseconds = null,
    long? AudioMilliseconds = null);

/// <summary>One row of the language list: a tag and the download it implies.</summary>
/// <param name="Tag">Registry tag.</param>
/// <param name="Bytes">
/// Size of the voice that will ACTUALLY PLAY, or null when this head cannot say.
/// </param>
/// <remarks>
/// THE SIZE MUST COME FROM THE SAME SELECTOR THAT SPEAKS. It is device-aware, so
/// it cannot be baked into a table. It was once picked independently - smallest
/// voice for the label, selector's choice for the audio - and the row then
/// described a voice you would never hear: Japanese read 122 MB while "Hear it"
/// played the 137.6 MB one. A size belonging to a different voice is worse than no
/// size, because it looks checked.
/// </remarks>
public sealed record VoiceRow(string Tag, long? Bytes);

/// <summary>Speaks a language on whichever head is hosting the shared UI.</summary>
public interface IVoiceHost
{
    /// <summary>
    /// Every language this head can offer, with the size of the voice that would
    /// play. Ordered by the caller, not here.
    /// </summary>
    /// <remarks>
    /// Comes from the head rather than from <see cref="SampleLanguages"/> so the
    /// phone lists exactly what its own catalogue holds - including any tag the
    /// name table has no row for, which the native screen renders as the bare tag.
    /// Reproducing that faithfully keeps the two apps the same screen; it also
    /// keeps the gap visible instead of hiding it behind a shorter list.
    /// </remarks>
    Task<IReadOnlyList<VoiceRow>> CatalogueAsync(CancellationToken ct = default);

    /// <summary>Whether this head can synthesise at all.</summary>
    VoiceAvailability Availability { get; }

    /// <summary>
    /// A short, honest sentence about where the work happens. Shown in the UI, so
    /// it is written for a reader rather than for a log.
    /// </summary>
    string Provenance { get; }

    /// <summary>
    /// Speak one language's checked greeting, downloading the voice if needed.
    /// </summary>
    /// <param name="tag">Registry tag, as <see cref="SampleLanguages"/> spells it.</param>
    /// <param name="progress">
    /// Called with human-readable progress. A voice can be 247 MB, and a screen
    /// that sits on "preparing" for that long is indistinguishable from a hang.
    /// </param>
    Task<SpeakOutcome> SpeakAsync(
        string tag,
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    /// <summary>Speak a given sentence in a given language.</summary>
    /// <remarks>
    /// SEPARATE FROM <see cref="SpeakAsync"/> ON PURPOSE. That one speaks the
    /// table's CHECKED greeting, which is the right thing for a language demo:
    /// every phrase there was verified by somebody, and a demo that mispronounces
    /// an invented sentence at a native speaker is worse than one that says less.
    /// This one speaks whatever the assistant actually replied, which is the point
    /// of a voice assistant and cannot be a fixed phrase.
    /// </remarks>
    Task<SpeakOutcome> SayAsync(
        string tag,
        string text,
        IProgress<string>? progress = null,
        CancellationToken ct = default);
}
