// IMakesMusic.cs
//
// Music, behind a contract, because the shared UI cannot reach the library that
// makes it.
//
// ProceduralMusicBedGenerator has been in CircleAI.Music for a long time and the
// native sample was its only consumer. That sample is being retired, so without
// this the capability goes with it.
//
// WHY AN INTERFACE AND NOT A DIRECT CALL. CircleAI.Assistant has ZERO project
// references on purpose - a browser loads it - so the shared Razor library
// cannot reference CircleAI.Music any more than it can reference
// CircleAI.Inference. Every device capability in this app already crosses that
// line the same way: IBrain, IVoiceHost, IPlaysMedia, IDeviceFacts. This is one
// more of those, not a new pattern.
//
// IT NEEDS NOTHING INSTALLED, which is most of the reason it is worth offering.
// The generator is arithmetic: no model, no download, no network. On a phone
// that has just been unboxed, or has no signal, this is one of the few things
// the app can do immediately - which makes it the right thing to have on screen
// while a 500 MB model is still arriving.

namespace CircleAI.Assistant;

/// <summary>The feel of a piece, in the words a person would choose.</summary>
/// <remarks>
/// MIRRORED RATHER THAN REFERENCED. CircleAI.Music has its own Mood enum and
/// this is deliberately a separate one: importing that enum here would drag the
/// whole library into the assembly a browser loads, which is the exact thing
/// this interface exists to avoid. The head maps one to the other, in one place.
/// <para>
/// The names match CircleAI.Music.Mood exactly so the mapping is a parse rather
/// than a table somebody has to keep in step.
/// </para>
/// </remarks>
public enum MusicMood
{
    Neutral = 0,
    Calm,
    Warm,
    Reflective,
    Uplifting,
    Corporate,
    Focus,
    Energetic,
    Playful,
    Cinematic,
}

/// <summary>Makes a short piece of music on this device.</summary>
public interface IMakesMusic
{
    /// <summary>Whether this head can make music at all.</summary>
    /// <remarks>
    /// A BROWSER TAB CANNOT, and a screen that offers a button which does
    /// nothing is worse than a screen that says so. Same shape as every other
    /// capability seam here.
    /// </remarks>
    bool Available { get; }

    /// <summary>The moods this head offers, in the order to show them.</summary>
    IReadOnlyList<MusicMood> Moods { get; }

    /// <summary>
    /// Make a piece and return where it was written.
    /// </summary>
    /// <param name="mood">The feel of it.</param>
    /// <param name="length">How long. Thirty seconds is the sample's default.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>
    /// A path to a WAV on this device, or null when nothing could be made.
    /// </returns>
    /// <remarks>
    /// A PATH RATHER THAN BYTES, because the only thing anybody does with it is
    /// hand it to IPlaysMedia, and passing megabytes through a Razor component to
    /// write them straight back out is work nobody asked for.
    /// <para>
    /// Synthesis is CPU-bound and synchronous inside the generator - its own
    /// remarks say so - so an implementation must get off the UI thread itself
    /// rather than leaving that to every caller.
    /// </para>
    /// </remarks>
    Task<string?> MakeAsync(
        MusicMood mood, TimeSpan length, CancellationToken ct = default);

    /// <summary>Hand a finished piece to whatever the person wants to do with it.</summary>
    /// <remarks>
    /// THE SCREEN COULD MAKE MUSIC AND NOT GET IT OFF THE PHONE. It printed the
    /// app-private path and stopped there - a real WAV, in a folder nothing on
    /// the phone can browse to, which is the same as not having made it. The
    /// other head offered "Save as WAV" through a document picker.
    /// <para>
    /// A page in a WebView has no document picker, so this is the share sheet
    /// instead: every phone has one, it reaches the file manager, WhatsApp and
    /// the downloads folder alike, and it is the same route the CV takes.
    /// </para>
    /// <para>
    /// Returns false when nothing could be opened, which is a real outcome on a
    /// phone with no app that takes audio. The file still exists either way, so
    /// the screen says where it is rather than claiming a failure.
    /// </para>
    /// </remarks>
    Task<bool> ShareAsync(string path, CancellationToken ct = default);
}

/// <summary>A head that cannot make music, and says so.</summary>
/// <remarks>
/// BESIDE THE CONTRACT, NOT IN A SAMPLE, matching NoMediaPlayer next to
/// IPlaysMedia. Both web heads already import this namespace, and a no-op that
/// lives in one sample is a no-op the other sample cannot reach.
/// <para>
/// Available is FALSE rather than a MakeAsync that quietly returns null: the
/// screen can then say "this needs the app on a phone" instead of spinning and
/// reporting that nothing came out, which reads as broken rather than absent.
/// </para>
/// </remarks>
public sealed class NoMusic : IMakesMusic
{
    /// <inheritdoc />
    public bool Available => false;

    /// <inheritdoc />
    public IReadOnlyList<MusicMood> Moods { get; } = [];

    /// <inheritdoc />
    /// <remarks>Nothing was made, so there is nothing to hand anybody.</remarks>
    public Task<bool> ShareAsync(string path, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <inheritdoc />
    public Task<string?> MakeAsync(
        MusicMood mood, TimeSpan length, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
