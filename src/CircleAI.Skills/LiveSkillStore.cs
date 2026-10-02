// LiveSkillStore.cs
//
// A store that is asked for, rather than captured.
//
// THE LIBRARY ARRIVES AFTER THE BRAIN DOES. Opening it unpacks 20 MB out of the APK,
// so the service starts it on a background task and carries on - while the brain is
// built eagerly, with WarmOnStart, so a question never waits on a cold model. Both
// are right, and they race: whichever composite the brain was handed at build time
// would hold whatever existed in that instant, and a library that finished a second
// later would never reach the model at all.
//
// A COMPOSITE IS A FIXED ARRAY, which is the whole reason this exists: it is built
// once, and AIOptions.SkillStore is init-only by design. So one member of that array
// forwards to wherever the library actually lives, and answers nothing until it is
// there.
//
// EMPTY, NOT NULL, WHEN IT IS NOT READY. A store that throws or returns null during
// the first few seconds would turn "the library has not unpacked yet" into a failed
// turn; answering with nothing is true and costs the person only the skills they did
// not have yet.

namespace CircleAI.Skills;

/// <summary>Forwards to a store that may not exist yet.</summary>
/// <param name="store">Asked on every call. Null means "nothing yet".</param>
public sealed class LiveSkillStore(Func<ISkillStore?> store) : ISkillStore
{
    private static readonly IReadOnlyList<SkillSummary> None = [];

    /// <inheritdoc />
    public Task<IReadOnlyList<SkillSummary>> ListAsync(CancellationToken cancellationToken = default)
        => Now()?.ListAsync(cancellationToken) ?? Task.FromResult(None);

    /// <inheritdoc />
    public Task<SkillDetail?> GetAsync(string id, CancellationToken cancellationToken = default)
        => Now()?.GetAsync(id, cancellationToken) ?? Task.FromResult<SkillDetail?>(null);

    /// <inheritdoc />
    public Task<IReadOnlyList<SkillSummary>> SearchAsync(string query, CancellationToken cancellationToken = default)
        => Now()?.SearchAsync(query, cancellationToken) ?? Task.FromResult(None);

    /// <inheritdoc />
    /// <remarks>
    /// WRITING NEEDS A REAL STORE. There is nowhere to put a skill until the thing
    /// behind this exists, and inventing a buffer that might never be flushed would
    /// lose somebody's work quietly. The exception says which of the two it is.
    /// </remarks>
    public Task<SkillDetail> UpsertAsync(string? id, SkillDraft draft, CancellationToken cancellationToken = default)
        => Now() is { } live
            ? live.UpsertAsync(id, draft, cancellationToken)
            : throw new InvalidOperationException("The skill library is not open yet.");

    /// <inheritdoc />
    /// <remarks>
    /// A NO-OP RATHER THAN A THROW: there is nothing to delete from a store that is
    /// not open, which is both true and survivable - unlike a write, where
    /// swallowing the failure would lose what somebody had just added.
    /// </remarks>
    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
        => Now()?.DeleteAsync(id, cancellationToken) ?? Task.CompletedTask;

    private ISkillStore? Now()
    {
        try { return store(); }
        catch { return null; }
    }
}
