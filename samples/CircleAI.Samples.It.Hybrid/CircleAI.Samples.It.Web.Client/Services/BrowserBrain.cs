// BrowserBrain.cs
//
// The answering model runs on the device, not here.

namespace CircleAI.Samples.It.Web.Client.Services;

/// <inheritdoc />
/// <remarks>
/// Routing to a server was considered and rejected for the same reason the voice
/// host rejects it: the sample's claim is that the conversation happens on your
/// device and does not leave it. Making the box work by posting what somebody
/// types to a server would break exactly the promise being demonstrated.
/// </remarks>
public sealed class BrowserBrain : IBrain
{
    /// <inheritdoc />
    public Task<BrainState> StateAsync(CancellationToken ct = default)
        => Task.FromResult(new BrainState(false,
            "Answering runs on the phone. Install the app to have a conversation."));

    /// <inheritdoc />
    public Task<string> AskAsync(
        string prompt, Action<string>? token = null, CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}
