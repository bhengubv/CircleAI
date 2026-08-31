// BrowserConversation.cs
//
// A browser tab does not hold a conversation.

namespace CircleAI.Samples.It.Web.Client.Services;

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
}
