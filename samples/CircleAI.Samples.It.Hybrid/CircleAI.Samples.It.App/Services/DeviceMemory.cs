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

namespace CircleAI.Samples.It.App.Services;

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

            return result.Atoms
                .Where(a => a.IsCurrent && !a.IsStale)
                .Take(limit)
                .Select(a => new Remembered(a.Text))
                .ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // A store that could not answer is not a reason to fail the turn in
            // front of it. Nothing remembered, and the answer still happens.
            return [];
        }
    }
}
