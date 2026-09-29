// ServiceDeviceFacts.cs
//
// What this phone can do is the service's answer, not this app's.
//
// DeviceFacts read the model catalogue and the device probe in THIS process to work
// out which abilities were on. A thin app has neither: it holds no models and makes
// no fit decisions. So it asks the one process that does, and where the link cannot
// carry the question it says so rather than composing a plausible list.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Client;

namespace CircleAI.Samples.App.Services;

/// <inheritdoc />
public sealed class ServiceDeviceFacts(LinkedBrain brain) : IDeviceFacts
{
    /// <inheritdoc />
    /// <remarks>
    /// THE STATE COMES FROM WHETHER THE LINK IS UP, which is the honest granularity
    /// available. The service knows per-ability detail and there is no verb to ask it
    /// for that, so every row shares one answer rather than four invented ones.
    /// <para>
    /// Seeing is listed as NotCatalogued even when the link is up, because the wire
    /// carries text and audio and has no transaction for an image — so this app
    /// genuinely cannot show CircleAI a picture, whatever the service can do.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<AbilityRow>> AbilitiesAsync(CancellationToken ct = default)
    {
        var state = await brain.StateAsync(ct).ConfigureAwait(false);
        var on = state.Ready ? AbilityState.On : AbilityState.NotCatalogued;
        var via = state.Ready ? "through CircleAI" : state.Detail;

        return new[]
        {
            new AbilityRow("Answering", $"Answers questions and helps you write — {via}", on),
            new AbilityRow("Talking",   $"Reads things out loud — {via}", on),
            new AbilityRow("Listening", $"Understands you when you speak — {via}", on),
            new AbilityRow("Seeing",    "Looking at a photo is not available in this app yet.",
                           AbilityState.NotCatalogued),
        };
    }

    /// <inheritdoc />
    public async Task<PhoneFacts> PhoneAsync(CancellationToken ct = default)
    {
        var state = await brain.StateAsync(ct).ConfigureAwait(false);
        return new PhoneFacts(
            new[]
            {
                new PhoneFact("Where it runs",
                    "In CircleAI, on this phone. This app holds no models of its own."),
                new PhoneFact("Right now", state.Detail),
            },
            Array.Empty<string>());
    }

    /// <inheritdoc />
    /// <remarks>
    /// Turning an ability on means installing or linking CircleAI, and both are the
    /// person's actions in another app — this one cannot do either on their behalf.
    /// </remarks>
    public Task<string> TurnOnAsync(string title, IProgress<string>? progress = null,
                                    CancellationToken ct = default)
        => Task.FromResult(brain.ServiceInstalled
            ? "Open CircleAI and approve the link to use this."
            : "Install CircleAI to use this.");
}
