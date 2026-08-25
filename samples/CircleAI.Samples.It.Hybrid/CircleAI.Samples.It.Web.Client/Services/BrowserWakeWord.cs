// BrowserWakeWord.cs
//
// A web page does not sit listening for a wake phrase.

namespace CircleAI.Samples.It.Web.Client.Services;

/// <inheritdoc />
/// <remarks>
/// NOT A NO-OP THAT LOOKS LIKE LISTENING. The whole promise of this screen is that
/// the microphone is open, on the device, and nothing leaves it. A browser
/// implementation that showed the same animation while doing nothing would be
/// claiming precisely the thing it is not doing.
/// </remarks>
public sealed class BrowserWakeWord : IWakeWord
{
    /// <inheritdoc />
    public Task<bool> RequestMicrophoneAsync() => Task.FromResult(false);

    /// <inheritdoc />
    public Task ListenAsync(IProgress<WakeStatus> updates, CancellationToken ct)
    {
        updates.Report(new WakeStatus(WakeState.NotInstalled,
            "Waking runs on the phone",
            "Install the app and turn on Waking under “What it can do”."));
        return Task.CompletedTask;
    }
}
