// BrowserResidentAssistant.cs
//
// A tab cannot hold a microphone after you close it, and should not pretend to.
//
// Reported as Unsupported rather than Off: Off invites a person to tap a control
// that will never do anything, and a browser refusing this is a property of the
// platform, not a fault to be fixed.

namespace CircleAI.Samples.It.Web.Client.Services;

/// <inheritdoc />
public sealed class BrowserResidentAssistant : IResidentAssistant
{
    /// <inheritdoc />
    public bool IsListening => false;

    /// <inheritdoc />
    /// <remarks>Declared so the interface is satisfied; nothing ever raises it here.</remarks>
    public event EventHandler<string>? Woke { add { } remove { } }

    private static ResidentStatus No => new(
        ResidentState.Unsupported,
        "Not on the web",
        "A tab cannot listen once it is closed. The phone app can.");

    /// <inheritdoc />
    public Task<ResidentStatus> StartAsync(CancellationToken ct = default) => Task.FromResult(No);

    /// <inheritdoc />
    public Task<ResidentStatus> StopAsync(CancellationToken ct = default) => Task.FromResult(No);

    /// <inheritdoc />
    /// <remarks>There is nothing to resume: a tab never held the microphone.</remarks>
    public Task<ResidentStatus> ResumeAsync(CancellationToken ct = default) => Task.FromResult(No);

    /// <inheritdoc />
    /// <remarks>
    /// Nothing to catch up either. The phrase screen works on the web - somebody
    /// can choose what their phone will answer to from a browser - and it is
    /// written to the same store; there is simply no listener in this process for
    /// it to reach.
    /// </remarks>
    public Task<ResidentStatus> RefreshAsync(CancellationToken ct = default) => Task.FromResult(No);
}
