// DeviceMemory.cs
//
// The phone's half of IRemembers: the real store, behind the shared contract.
//
// CircleAI.Memory holds episodes and atoms in an append-only log on disk. The
// shared UI cannot reference it - a WebAssembly client would have to load it -
// so this head wraps it and the store's effects talk to the interface.
//
// WHAT WAS ALREADY HERE AND WHAT WAS NOT. LearnAsync was already called on every
// utterance from DeviceConversation.HeardAsync, so the writing half has worked
// for a long time. RecallAsync was called from exactly one place: a diagnostic
// screen. This is the first time anything reads it back into an answer.

using CircleAI.Memory;

namespace CircleAI.Assistant.Device;

/// <inheritdoc />
public sealed class DeviceMemory : IRemembers
{
    private readonly IMemoryService _memory;

    public DeviceMemory(IMemoryService memory) => _memory = memory;

    /// <inheritdoc />
    /// <remarks>
    /// The store decides what is worth keeping - LearnAsync reads what was said
    /// and records only what survives its own judgement, which is why a turn can
    /// hand it everything without filtering first.
    /// </remarks>
    public async Task LearnAsync(string said, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(said)) return;
        await _memory.LearnAsync(said, ct: ct).ConfigureAwait(false);

        // BOTH HALVES OF THE LOOP LEAVE A MARK. This is the one seam the write
        // and the read both cross, and until now neither said anything: "does
        // this phone actually remember me" was a question nobody could answer
        // from outside without speaking to it twice a day apart. One line each
        // way turns the claim into something a logcat settles.
        Android.Util.Log.Info("CircleAI.Memory", $"learn: {Excerpt(said)}");
    }

    /// <inheritdoc />
    /// <remarks>
    /// A SITUATION, NOT A SEARCH STRING. CircleAI.Memory recalls against what is
    /// about to happen rather than by keyword, and a spoken question has no verb
    /// or target this code can honestly extract - so it goes in whole as Text and
    /// the store decides what bears on it.
    /// <para>
    /// SUPERSEDED AND STALE ATOMS ARE DROPPED. An atom that has been corrected
    /// carries SupersededBy, and a fact that failed its own verification is
    /// IsStale. Feeding either into a prompt is worse than remembering nothing:
    /// it is telling the model something the store already knows to be wrong.
    /// </para>
    /// <para>
    /// Confidence is 1 because MemoryAtom does not carry one - the score belongs
    /// to AtomCandidate, before recording, and inventing a spread here would be a
    /// number with nothing behind it.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Remembered>> RecallAsync(
        string about, int limit = 4, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(about)) return [];

        try
        {
            var result = await _memory
                .RecallAsync(new Situation(Text: about), ct: ct)
                .ConfigureAwait(false);

            var kept = result.Atoms
                .Where(a => a.IsCurrent && !a.IsStale)
                .Take(limit)
                .Select(a => new Remembered(a.Text))
                .ToList();

            // OFFERED AND KEPT, NOT JUST KEPT. A store that returned nine atoms
            // and had eight of them superseded reads identically to a store with
            // one atom in it unless both numbers are here - and those are
            // different problems with different fixes.
            Android.Util.Log.Info("CircleAI.Memory",
                $"recall: {result.Atoms.Count} offered, {kept.Count} kept for \"{Excerpt(about)}\"");

            return kept;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A store that could not answer is not a reason to fail the turn in
            // front of it. Nothing remembered, and the answer still happens -
            // but SAID so, because a swallowed failure and an empty store looked
            // the same from outside and only one of them needs fixing.
            Android.Util.Log.Warn("CircleAI.Memory", "recall failed: " + ex.Message);
            return [];
        }
    }

    /// <summary>Enough of an utterance to recognise it in a log, and no more.</summary>
    /// <remarks>
    /// Truncated because this is somebody's conversation. A log that is useful
    /// for a week is worth having; a verbatim transcript of everything anybody
    /// ever said to their phone, sitting in logcat, is not.
    /// </remarks>
    private static string Excerpt(string text)
    {
        var tidy = text.Trim();
        return tidy.Length <= 48 ? tidy : tidy[..48] + "...";
    }
}
