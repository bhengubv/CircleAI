// AndroidShareTarget.cs
//
// The share sheet's half of IShareTarget.
//
// MainActivity catches ACTION_SEND and parks the text here; the job screen takes
// it. A static, because the activity and the Blazor component that wants the
// text do not otherwise meet: the activity exists before the web view does, and
// an intent can arrive while the app is already running.

namespace CircleAI.Samples.It.App.Services;

/// <inheritdoc />
public sealed class AndroidShareTarget : IShareTarget
{
    /// <summary>What arrived, until somebody takes it.</summary>
    /// <remarks>
    /// Set from the UI thread in OnCreate/OnNewIntent and read from the UI
    /// thread by a Blazor component, so it needs no lock - but volatile says so
    /// out loud rather than leaving it to be assumed.
    /// </remarks>
    internal static volatile string? Parked;

    /// <inheritdoc />
    public bool HasPending => !string.IsNullOrWhiteSpace(Parked);

    /// <inheritdoc />
    public string? Take()
    {
        var text = Parked;
        Parked = null;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
