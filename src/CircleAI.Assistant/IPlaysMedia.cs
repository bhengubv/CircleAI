// IPlaysMedia.cs
//
// Asking the phone to play something, without knowing what plays it.
//
// THE FIRST CAPABILITY THAT ACTS ON THE DEVICE RATHER THAN ON THE APP. Every
// capability so far opens one of this app's own screens; "play Coldplay" is the
// first that has to reach outside it. The seam is here for the same reason
// IRemembers is: the shared UI cannot reference Android, so the contract lives
// in Contracts and the phone's head satisfies it.
//
// NO PLAYER IS NAMED, ANYWHERE. Android has a standard "play this" intent that
// every music app answers - the phone's own chooser decides who gets it, and
// whatever somebody already uses is what opens. Naming Spotify in code would
// make the feature depend on one company's app being installed, and on this
// product the degoogled rule makes hard-coding a store-delivered app worse than
// useless.

using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Samples.It;

/// <summary>What happened when the phone was asked to play something.</summary>
public enum PlayResult
{
    /// <summary>A player took it and started.</summary>
    Playing,

    /// <summary>Nothing on this phone can play music.</summary>
    NoPlayer,

    /// <summary>This head cannot reach the device at all — a browser.</summary>
    NotHere,
}

/// <summary>Plays music through whatever this device already has.</summary>
public interface IPlaysMedia
{
    /// <summary>
    /// Hands <paramref name="what"/> to whatever plays music here.
    /// </summary>
    /// <param name="what">
    /// What was asked for, in the person's words — "Coldplay", "something
    /// quiet", "the album Parachutes". Passed through UNPARSED: the player's own
    /// search understands its own catalogue far better than anything here could,
    /// and splitting artist from album is exactly the guess that turns "play
    /// Adele 30" into a search for the number thirty.
    /// </param>
    Task<PlayResult> PlayAsync(string what, CancellationToken ct = default);
}

/// <summary>A head with no device to reach.</summary>
/// <remarks>
/// The browser. Says so rather than throwing, so a shared screen can offer the
/// capability everywhere and have it decline honestly in one place.
/// </remarks>
public sealed class NoMediaPlayer : IPlaysMedia
{
    public Task<PlayResult> PlayAsync(string what, CancellationToken ct = default)
        => Task.FromResult(PlayResult.NotHere);
}
