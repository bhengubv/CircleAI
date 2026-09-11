// BrowserConversation.cs
//
// A browser tab does not hold a conversation.

namespace CircleAI.Samples.Web.Client.Services;

/// <inheritdoc />
/// <remarks>
/// The microphone, the recogniser, the model and the synthesiser are all on the
/// phone. Asking a server to stand in for them would make the button work by
/// sending somebody's voice off their device, which is the promise this sample
/// exists to demonstrate keeping.
/// </remarks>
public sealed class BrowserConversation : IConversation
{
    private const string OnPhone =
        "Talking to it happens on the phone. Install the app to have a conversation.";

    /// <inheritdoc />

    /// <inheritdoc />
    /// <remarks>The browser's own engines are the platform's; there is nothing to open.</remarks>
    public Task<string> PrepareAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.FromResult("nothing to warm in a tab");

    public Task<BrainState> StateAsync(CancellationToken ct = default)
        => Task.FromResult(new BrainState(false, OnPhone));

    /// <inheritdoc />
    public Task TurnAsync(IProgress<TurnState> updates, CancellationToken ct = default)
    {
        updates.Report(new TurnState(TurnPhase.Idle, Detail: OnPhone));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>No microphone here, and it says which part is missing rather
    /// than borrowing the conversation's excuse.</remarks>
    public Task<string?> DictateAsync(
        IProgress<TurnState> updates, CancellationToken ct = default, string? language = null)
    {
        updates.Report(new TurnState(TurnPhase.Idle,
            Detail: "Listening runs on the phone. Install the app to speak to it."));
        return Task.FromResult<string?>(null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// SAME REFUSAL, SAID THE SAME WAY. A tab may not hold a microphone for the
    /// length of a meeting, and a session that quietly transcribed nothing would
    /// look exactly like one that heard nothing.
    /// </remarks>
    public Task<string> SessionAsync(
        IProgress<TurnState> updates, CancellationToken ct = default,
        string? language = null, double silenceMs = 5000)
    {
        updates.Report(new TurnState(TurnPhase.Idle,
            Detail: "Taking down a meeting runs on the phone. Install the app to record one."));
        return Task.FromResult("");
    }

    /// <inheritdoc />
    /// <remarks>
    /// The browser has nowhere to keep this. The memory is on the device, and
    /// a web page that quietly kept a copy somewhere else would be the one
    /// thing this whole design refuses to do.
    /// </remarks>
    public Task HeardAsync(string said, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task SayAsync(string text, string? languageTag = null, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<string> SeeAsync(
        string question, byte[] image, Action<string>? token = null, CancellationToken ct = default)
        => Task.FromResult("Reading an image happens on the phone.");

    /// <inheritdoc />
    /// <remarks>
    /// A browser tab has no file path to be handed. It has a File object from an
    /// input element, which is a different thing entirely - so this declines
    /// the same way the microphone and the camera do rather than pretending a
    /// path means anything here.
    /// </remarks>
    public Task<Transcript> TranscribeFileAsync(
        string path,
        string? language = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
        => Task.FromResult(Transcript.Nothing);

    /// <inheritdoc />
    /// <remarks>
    /// Nothing to translate with. A WASM head has no model, which is the same
    /// reason it cannot chat - and saying so beats returning the original text,
    /// which would look like a translation into the language it was already in.
    /// </remarks>
    public Task<string> TranslateAsync(
        string text, string fromTag, string toTag, CancellationToken ct = default)
        => Task.FromResult("Translating happens on the phone.");

    /// <inheritdoc />
    /// <remarks>
    /// PURE STRING WORK, SO THE BROWSER CAN DO IT. Unlike everything else on
    /// this class this needs no microphone, no model and no native library -
    /// it is a transcript turned into text - so declining would be declining
    /// something that works. The format lives in CircleAI.Voice, which a WASM
    /// head cannot load, so the two dialects are spelled out here; the tests
    /// pin both against the library's own output.
    /// </remarks>
    public string AsSubtitles(Transcript transcript, SubtitleFormat format = SubtitleFormat.SubRip)
        => BrowserSubtitles.Render(transcript, format);
}
