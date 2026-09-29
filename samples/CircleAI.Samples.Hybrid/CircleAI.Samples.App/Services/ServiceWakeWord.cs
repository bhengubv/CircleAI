// ServiceWakeWord.cs
//
// The wake word runs in CircleAIService, not here.
//
// DeviceWakeWord did it in this process: found the bundle, held the microphone,
// spotted the phrase. That is the always-on half, and it belongs to the app that has
// the foreground service and the notification disclosing the microphone.

using System;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Client;

namespace CircleAI.Samples.App.Services;

/// <inheritdoc />
public sealed class ServiceWakeWord(LinkedBrain brain) : IWakeWord
{
    /// <inheritdoc />
    /// <remarks>
    /// FALSE, and not a refusal to try: this app does not listen for the wake word,
    /// so asking a person for the microphone here would take a permission nothing
    /// then uses.
    /// </remarks>
    public Task<bool> RequestMicrophoneAsync() => Task.FromResult(false);

    /// <inheritdoc />
    public Task ListenAsync(IProgress<WakeStatus> updates, CancellationToken ct)
    {
        updates.Report(brain.ServiceInstalled
            ? new WakeStatus(WakeState.NotInstalled,
                "Waking runs in CircleAI",
                "Open CircleAI to turn it on — it keeps listening when this app is closed.")
            : new WakeStatus(WakeState.NotInstalled,
                "CircleAI is not installed",
                "This app asks CircleAI to listen and to think. Install it to use them."));
        return Task.CompletedTask;
    }
}
