// BrowserBrain.cs
//
// The answering model runs on the device, not here.

namespace CircleAI.Samples.Web.Client.Services;

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
    /// <remarks>
    /// NO TOOLS IN A TAB. The empty string tells the caller to keep whatever the
    /// streamed pass produced, rather than replacing a real answer with nothing.
    /// </remarks>
    public Task<string> AskWithToolsAsync(string prompt, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    /// <inheritdoc />
    /// <remarks>
    /// ZERO MEANS DO NOT RESIZE, because there is nothing to resize FOR: a tab
    /// has no vision model. Returning 512 here would have the screen shrink a
    /// picture to fit a model that is not there.
    /// </remarks>
    public int MaxImageEdge => 0;

    /// <inheritdoc />
    public Task<BrainState> StateAsync(CancellationToken ct = default)
        => Task.FromResult(new BrainState(false,
            "Answering runs on the phone. Install the app to have a conversation."));

    /// <inheritdoc />
    public Task<string> AskAsync(
        string prompt, Action<string>? token = null, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    /// <inheritdoc />
    public Task<string> SeeAsync(
        string question, byte[] image,
        Action<string>? token = null, CancellationToken ct = default)
        => Task.FromResult("Reading an image runs on the phone.");
}
