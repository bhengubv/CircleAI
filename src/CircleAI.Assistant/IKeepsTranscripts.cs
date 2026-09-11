// IKeepsTranscripts.cs
//
// Somewhere for a transcript to live.
//
// THE REASON "SEARCH ACROSS EVERYTHING" HAD ALMOST NOTHING TO SEARCH. A
// transcript was shown on a screen, optionally written out as an .srt to a
// location the person chose, and then discarded. Nothing in the app kept it, so
// a search across memory, transcripts and documents was a search across memory -
// which already has a better search of its own.
//
// The blocker was never the ranking. It was that two of the three sources did
// not exist.
//
// ON THE DEVICE, AND ONLY THERE. A transcript is the most private thing this app
// produces: a recording of a meeting, a clinic appointment, somebody's
// interview. It is written to the app's own storage, it is not synced, it is not
// backed up anywhere, and forgetting one removes it. That is not a feature
// decision to be revisited - it is the posture the whole product is built on,
// and a transcript store that phoned anywhere would contradict every other part
// of it.
//
// A FOLDER OF FILES RATHER THAN A DATABASE, deliberately. A transcript is a
// whole document read whole, not rows queried by column; the corpus is a
// person's own recordings, not millions; and a file each means a store that a
// person could copy off the phone, inspect, or delete with a file manager. A
// database would be a second schema to migrate for no benefit any caller asked
// for.

namespace CircleAI.Assistant;

/// <summary>A transcript that was kept, and what it was called.</summary>
/// <param name="Id">Stable identifier, assigned when it was kept.</param>
/// <param name="Title">
/// What it is, in the person's words - usually the recording's filename.
/// </param>
/// <param name="When">When it was kept.</param>
/// <param name="Transcript">What was said, and when, and by whom where known.</param>
public sealed record SavedTranscript(
    string Id, string Title, DateTimeOffset When, Transcript Transcript)
{
    /// <summary>A line a person could read in a list.</summary>
    public string Summary
    {
        get
        {
            var words = Transcript.Text.Length;
            var length = Transcript.Length;

            return length > TimeSpan.Zero
                ? $"{Title} · {length:hh\\:mm\\:ss} · {words} characters"
                : $"{Title} · {words} characters";
        }
    }
}

/// <summary>Keeps transcripts on this device.</summary>
public interface IKeepsTranscripts
{
    /// <summary>Keep one. Returns its id.</summary>
    /// <param name="title">What to call it.</param>
    /// <param name="transcript">What was said.</param>
    /// <param name="ct">Cancels the write.</param>
    Task<string> KeepAsync(string title, Transcript transcript, CancellationToken ct = default);

    /// <summary>Everything kept, newest first.</summary>
    /// <remarks>
    /// THE WHOLE LIST, because the corpus is a person's own recordings and the
    /// caller that wants to search them needs all of them. A paging API here
    /// would be borrowed from a server and would cost every caller a loop for a
    /// list that fits in memory several times over.
    /// </remarks>
    Task<IReadOnlyList<SavedTranscript>> AllAsync(CancellationToken ct = default);

    /// <summary>One by id, or <c>null</c> when it is not there.</summary>
    Task<SavedTranscript?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Remove one. Returns whether it was there to remove.</summary>
    /// <remarks>
    /// ACTUALLY GONE, not flagged. Somebody deleting a recording of a clinic
    /// appointment means it, and a store that kept a tombstone with the text
    /// still in it would be lying to them.
    /// </remarks>
    Task<bool> ForgetAsync(string id, CancellationToken ct = default);
}

/// <summary>A store that keeps nothing, for a head that cannot write files.</summary>
/// <remarks>
/// A browser tab has no app-private storage to put somebody's meeting in. It
/// declines rather than pretending - the same way it declines the microphone and
/// the camera - so a screen can say "this happens on the phone" instead of
/// appearing to save and losing it.
/// </remarks>
public sealed class KeepsNoTranscripts : IKeepsTranscripts
{
    /// <summary>The shared instance.</summary>
    public static readonly KeepsNoTranscripts Instance = new();

    /// <inheritdoc />
    public Task<string> KeepAsync(string title, Transcript transcript, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    /// <inheritdoc />
    public Task<IReadOnlyList<SavedTranscript>> AllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SavedTranscript>>([]);

    /// <inheritdoc />
    public Task<SavedTranscript?> GetAsync(string id, CancellationToken ct = default)
        => Task.FromResult<SavedTranscript?>(null);

    /// <inheritdoc />
    public Task<bool> ForgetAsync(string id, CancellationToken ct = default)
        => Task.FromResult(false);
}

/// <summary>Something found by a search, and where it came from.</summary>
/// <param name="Kind">"memory" or "transcript".</param>
/// <param name="Id">
/// The transcript\'s id, so a screen can open it. Empty for a memory, which has
/// no screen of its own.
/// </param>
/// <param name="Title">What to show as the heading.</param>
/// <param name="Text">The line that matched.</param>
/// <param name="When">When it happened, where that is known.</param>
public sealed record Found(
    string Kind, string Id, string Title, string Text, DateTimeOffset? When = null);
