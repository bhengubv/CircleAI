// BrowserSetup.cs
//
// There is nothing to set up in a browser.

namespace CircleAI.Samples.It.Web.Client.Services;

/// <inheritdoc />
/// <remarks>
/// Reports Ready with nothing to fetch, which is true rather than evasive: this
/// head downloads no models because it runs none. Showing a setup wizard here
/// would invite somebody to spend their data on files the page cannot open.
/// </remarks>
public sealed class BrowserSetup : ISetup
{
    /// <inheritdoc />
    public Task<Readiness> ReadinessAsync(CancellationToken ct = default)
        => Task.FromResult(new Readiness(ReadyStage.Ready,
            "Pick a language to hear",
            "The app runs on the phone. This page shows you what it does.",
            CanTalk: false));

    /// <inheritdoc />
    public Task<IReadOnlyList<SetupItem>> PlanAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SetupItem>>([]);

    /// <inheritdoc />
    /// <remarks>
    /// A BROWSER HAS NO MODELS AND SHOULD NOT PRETEND OTHERWISE. The census is
    /// about what is on a device; here the honest answer is one row saying where
    /// the work actually happens, rather than a list of absent things that were
    /// never going to be present.
    /// </remarks>
    public Task<Census> CensusAsync(CancellationToken ct = default)
        => Task.FromResult(new Census(
            [new CensusRow("the phone app", false, 0, "everything runs there, not here")],
            Present: 0, Total: 1, Summary: "nothing runs in a browser"));

    /// <inheritdoc />
    /// <remarks>Nothing is ever fetched here, so nothing is ever running.</remarks>
    public bool IsRunning => false;

    /// <inheritdoc />
    public Task RunAsync(IProgress<SetupProgressReport> progress, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<IReadOnlyList<TourStep>> TourAsync(TimeSpan remaining, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TourStep>>([]);

    /// <inheritdoc />
    /// <remarks>
    /// A browser tab has no microphone to grant on this app's behalf and no
    /// battery policy to be exempt from. Both say no rather than pretending,
    /// which is why they return a result instead of being no-ops.
    /// </remarks>
    public Task<bool> AllowMicrophoneAsync(CancellationToken ct = default)
        => Task.FromResult(false);

    /// <inheritdoc />
    public Task<bool> AllowBackgroundAsync(CancellationToken ct = default)
        => Task.FromResult(false);
}
