// ServiceResidentAssistant.cs
//
// Listening happens in CircleAIService, not in this app.
//
// DeviceResidentAssistant used to live here and did the whole job in-process: found
// the wake-word bundle, installed ResidentWakeWord, started CircleNeuronService and
// held the microphone. That is the always-on half, and it belongs to the service —
// which is the app with the foreground service, the notification that discloses the
// microphone, and the models behind it.
//
// SO THIS REPORTS RATHER THAN CONTROLS, in the shape BrowserResidentAssistant
// already set: a head that cannot do a thing says so and says where it can be done,
// instead of offering a control that does nothing. The difference from the browser
// is that here the answer is not "impossible" but "somewhere else on this phone".

using System;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Client;

namespace CircleAI.Samples.App.Services;

/// <inheritdoc />
public sealed class ServiceResidentAssistant(LinkedBrain brain) : IResidentAssistant
{
    /// <inheritdoc />
    /// <remarks>
    /// FALSE, because THIS app is not listening — and that is the honest reading of
    /// the question. Whether the service is listening is the service's own state and
    /// there is no verb on the link to ask it; answering with a guess would put a
    /// "listening" indicator on a screen with nothing behind it.
    /// </remarks>
    public bool IsListening => false;

    /// <inheritdoc />
    /// <remarks>
    /// Declared to satisfy the interface, never raised. The wake word fires in the
    /// service's process; delivering that here needs a verb the link does not have.
    /// </remarks>
    public event EventHandler<string>? Woke { add { } remove { } }

    private ResidentStatus Where => brain.ServiceInstalled
        ? new ResidentStatus(
            ResidentState.Unsupported,
            "Handled by CircleAI",
            "Listening for your wake word runs in the CircleAI app, so it keeps working "
          + "when this one is closed. Open CircleAI to turn it on or off.")
        : new ResidentStatus(
            ResidentState.NotInstalled,
            "CircleAI is not installed",
            "This app asks CircleAI to listen and to think. Install it to use them.");

    /// <inheritdoc />
    public Task<ResidentStatus> StartAsync(CancellationToken ct = default) => Task.FromResult(Where);

    /// <inheritdoc />
    public Task<ResidentStatus> StopAsync(CancellationToken ct = default) => Task.FromResult(Where);

    /// <inheritdoc />
    public Task<ResidentStatus> ResumeAsync(CancellationToken ct = default) => Task.FromResult(Where);

    /// <inheritdoc />
    public Task<ResidentStatus> RefreshAsync(CancellationToken ct = default) => Task.FromResult(Where);
}
