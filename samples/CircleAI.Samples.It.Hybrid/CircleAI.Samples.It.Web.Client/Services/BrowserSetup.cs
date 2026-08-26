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
    /// <remarks>Nothing is ever fetched here, so nothing is ever running.</remarks>
    public bool IsRunning => false;

    /// <inheritdoc />
    public Task RunAsync(IProgress<SetupProgressReport> progress, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<IReadOnlyList<TourStep>> TourAsync(TimeSpan remaining, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TourStep>>([]);
}
