// SetupItemStatus.cs
//
// WHERE ONE MODEL ON THE WAY IN HAS GOT TO, FOR AN APP THAT CANNOT BE PUSHED TO.
//
// The setup run said one thing at a time - "the brain, 40%" - and only for the run an app had
// started itself. The background fetch that brings every brain, voice and ear by itself (Step 4,
// "everything on") said nothing across the link at all, so an app could not show that the voice
// was here, the ears were being checked and the brain was still a third of the way down. A
// Binder link cannot push, so the service keeps one row per model and an app polls them.
//
// EIGHT STATES, AND EACH ONE IS SOMETHING A PERSON CAN ACT ON OR WAIT FOR: queued (its turn will
// come), waiting for network (connect to Wi-Fi), downloading (bytes, speed, time left), verifying
// (no bytes move; it is checking them), priming (loaded and warmed before it says ready), ready,
// failed (and why), healing (carrying on after a failure). Here in Core because the download
// path, the link and the screens all read it, and none of them may own it.

using System;
using System.Globalization;

namespace CircleAI.Core;

/// <summary>Where one model on the setup plan has got to. Appended, never renumbered.</summary>
public enum SetupItemState
{
    /// <summary>Planned; its turn has not come.</summary>
    Queued = 0,

    /// <summary>It cannot download now: no network, or only one the person said not to use.</summary>
    WaitingForNetwork = 1,

    /// <summary>Bytes are moving.</summary>
    Downloading = 2,

    /// <summary>Every byte is here and is being checked against its hash. No bytes move.</summary>
    Verifying = 3,

    /// <summary>On the device and being loaded and warmed, so its first use does not pay for it.</summary>
    Priming = 4,

    /// <summary>Here, checked and primed (or needing no priming).</summary>
    Ready = 5,

    /// <summary>It could not be fetched or primed; <see cref="SetupItemStatus.Detail"/> says why.</summary>
    Failed = 6,

    /// <summary>Carrying on after a failure: a retry, a fresh copy after a bad hash.</summary>
    Healing = 7,
}

/// <summary>One model on its way in, with every number an app needs to show it.</summary>
/// <param name="ModelId">The catalogue id.</param>
/// <param name="Title">What it gives the person - "the brain", "the ears" - or the id when nothing better is known.</param>
/// <param name="State">Where it has got to.</param>
/// <param name="BytesDone">Bytes on the device so far.</param>
/// <param name="BytesTotal">Bytes it needs; 0 when unknown.</param>
/// <param name="BytesPerSecond">The live download rate; 0 when nothing is moving.</param>
/// <param name="Remaining">Time left for this model's download; zero when not known.</param>
/// <param name="Detail">One plain sentence about the state - why it waits, why it failed, which step it is priming.</param>
/// <param name="Attempt">1 for the first try; higher once it has failed and carried on.</param>
/// <param name="Since">When it entered this state.</param>
public sealed record SetupItemStatus(
    string ModelId,
    string Title,
    SetupItemState State,
    long BytesDone,
    long BytesTotal,
    double BytesPerSecond,
    TimeSpan Remaining,
    string Detail,
    int Attempt,
    DateTimeOffset Since)
{
    /// <summary>0..1 of the download; 1 once it is ready.</summary>
    public double Fraction => State == SetupItemState.Ready
        ? 1
        : BytesTotal > 0 ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1) : 0;

    /// <summary>Whether nothing more will happen to it by itself: ready, or failed.</summary>
    public bool Settled => State is SetupItemState.Ready or SetupItemState.Failed;

    /// <summary>A line fit for a screen or a log: what, where, how far, how long.</summary>
    public string Describe()
    {
        var what = string.IsNullOrWhiteSpace(Title) ? ModelId : Title;
        var c = CultureInfo.InvariantCulture;
        var line = State switch
        {
            SetupItemState.Queued            => $"{what} - waiting its turn",
            SetupItemState.WaitingForNetwork => $"{what} - waiting for a network",
            SetupItemState.Downloading       => string.Format(c, "{0} - {1:0}%", what, Fraction * 100),
            SetupItemState.Verifying         => $"{what} - checking what arrived",
            SetupItemState.Priming           => $"{what} - getting it ready",
            SetupItemState.Ready             => $"{what} - ready",
            SetupItemState.Failed            => $"{what} - could not be set up",
            SetupItemState.Healing           => $"{what} - trying again",
            _                                => what,
        };

        if (State == SetupItemState.Downloading && BytesPerSecond > 0)
        {
            line += string.Format(c, " · {0:0.0} MB/s", BytesPerSecond / (1024 * 1024));
            if (Remaining > TimeSpan.Zero && Remaining <= TimeSpan.FromHours(12))
                line += Remaining.TotalMinutes >= 1
                    ? string.Format(c, " · {0:0} min left", Remaining.TotalMinutes)
                    : " · less than a minute left";
        }

        return string.IsNullOrWhiteSpace(Detail) ? line : $"{line} ({Detail})";
    }
}
