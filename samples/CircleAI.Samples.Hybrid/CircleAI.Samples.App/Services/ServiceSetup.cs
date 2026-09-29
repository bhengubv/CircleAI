// ServiceSetup.cs
//
// Setting up means installing CircleAI, not downloading models here.
//
// DeviceSetup planned and ran model downloads into THIS app's storage. A thin app
// downloads nothing: the service holds the models, so "is it ready" collapses to "is
// CircleAI installed and linked", and the work a person has to do is one install
// rather than a queue of gigabytes.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Client;

namespace CircleAI.Samples.App.Services;

/// <inheritdoc />
public sealed class ServiceSetup(LinkedBrain brain) : ISetup
{
    /// <inheritdoc />
    /// <remarks>Nothing downloads here, so nothing is ever running.</remarks>
    public bool IsRunning => false;

    /// <inheritdoc />
    public async Task<Readiness> ReadinessAsync(CancellationToken ct = default)
    {
        if (!brain.ServiceInstalled)
            return new Readiness(ReadyStage.NeedsSetup, "Install CircleAI",
                "This app asks CircleAI to think, speak and listen.", CanTalk: false);

        var state = await brain.StateAsync(ct).ConfigureAwait(false);
        return state.Ready
            ? new Readiness(ReadyStage.Ready, "Ready", state.Detail, CanTalk: true)
            : new Readiness(ReadyStage.NeedsSetup, "Approve the link", state.Detail, CanTalk: false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// EMPTY, because there is nothing to download. A plan with rows in it would
    /// offer somebody work this app cannot do and does not need done.
    /// </remarks>
    public Task<IReadOnlyList<SetupItem>> PlanAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SetupItem>>(Array.Empty<SetupItem>());

    /// <inheritdoc />
    /// <remarks>
    /// ONE ROW, AND IT IS THE TRUE ONE. A census counts what is present on the
    /// device; for this app that is exactly one thing — CircleAI itself.
    /// </remarks>
    public Task<Census> CensusAsync(CancellationToken ct = default)
    {
        var there = brain.ServiceInstalled;
        var row = new CensusRow("CircleAI", there, 0,
            there ? "Installed — it holds the models." : "Not installed.");
        return Task.FromResult(new Census(
            new[] { row }, there ? 1 : 0, 1,
            there ? "CircleAI is installed." : "CircleAI is not installed."));
    }

    /// <inheritdoc />
    /// <remarks>Nothing to run: the service downloads its own models.</remarks>
    public Task RunAsync(IProgress<SetupProgressReport> progress, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<IReadOnlyList<TourStep>> TourAsync(TimeSpan remaining, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TourStep>>(new[]
        {
            new TourStep("Install CircleAI",
                "It holds the models and does the thinking, so this app stays small.",
                "Open CircleAI", null),
        });

    /// <inheritdoc />
    /// <remarks>
    /// FALSE: this app does not hold the microphone for listening, so asking for it
    /// would take a permission nothing uses. Recording for a single spoken question
    /// is asked for at the point it happens.
    /// </remarks>
    public Task<bool> AllowMicrophoneAsync(CancellationToken ct = default) => Task.FromResult(false);

    /// <inheritdoc />
    /// <remarks>Background work is the service's; this app has none to exempt.</remarks>
    public Task<bool> AllowBackgroundAsync(CancellationToken ct = default) => Task.FromResult(false);

    /// <inheritdoc />
    public Task<bool> BackgroundAllowedAsync(CancellationToken ct = default) => Task.FromResult(false);
}
