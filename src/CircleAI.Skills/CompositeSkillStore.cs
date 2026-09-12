// CompositeSkillStore.cs
//
// Two stores answering two different questions, in one seam.
//
// THE MISTAKE THIS EXISTS TO AVOID IS REPLACEMENT. AIOptions.SkillStore is a
// single reference, so wiring the 1,405-skill library in means assigning over
// CapabilityManifestSkillStore - and that store is not a set of skills, it is
// the assistant's honesty about itself. Its own header:
//
//     "A capability catalogue that let the assistant claim planned features
//      would be a machine for confident lying - precisely the failure this file
//      exists to end. 'Can you do voice?' must produce 'not yet', not an
//      enthusiastic yes."
//
// Every entry carries a [status], CapabilityManifestSkillStoreTests pins the
// "Do NOT claim" wording for non-shipping features, and a straight swap would
// have deleted all of that while every one of those tests stayed green - they
// test the store, not who is wired to the seam.
//
// The two answer different questions and both are wanted:
//
//     "what can you do?"      -> the manifest. What THIS BUILD actually ships.
//     "how do I do X?"        -> the library. 1,405 SKILL.md files.
//
// MANIFEST FIRST, ALWAYS. A community skill called "voice" or "memory" must
// never outrank what this build says about its own voice or memory.

namespace CircleAI.Skills;

/// <summary>Queries several stores in order, first answer winning.</summary>
public sealed class CompositeSkillStore : ISkillStore, IDisposable
{
    private readonly IReadOnlyList<ISkillStore> _stores;

    /// <param name="stores">
    /// In priority order. The first is authoritative: its ids win, its search
    /// hits come first, and writes go to it.
    /// </param>
    public CompositeSkillStore(params ISkillStore[] stores)
    {
        ArgumentNullException.ThrowIfNull(stores);
        if (stores.Length == 0)
            throw new ArgumentException("At least one store is required.", nameof(stores));
        _stores = [.. stores];
    }

    /// <summary>Everything, nearest store first, ids de-duplicated.</summary>
    public async Task<IReadOnlyList<SkillSummary>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all = new List<SkillSummary>();

        foreach (var store in _stores)
            foreach (var s in await store.ListAsync(cancellationToken).ConfigureAwait(false))
                if (seen.Add(s.Id)) all.Add(s);

        return all;
    }

    /// <inheritdoc />
    public async Task<SkillDetail?> GetAsync(
        string id, CancellationToken cancellationToken = default)
    {
        foreach (var store in _stores)
        {
            var hit = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>Hits from every store, nearest first, ids de-duplicated.</summary>
    /// <remarks>
    /// NOT FIRST-STORE-WINS, AND THE DIFFERENCE MATTERS ON THE TURN PATH.
    /// SkillContextBuilder takes the first few hits and caps the block at 1500
    /// characters, so stopping at the manifest would mean a question the manifest
    /// happens to match a single word of never reaches the library at all. Both
    /// are asked; the manifest simply gets to go first.
    /// </remarks>
    public async Task<IReadOnlyList<SkillSummary>> SearchAsync(
        string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hits = new List<SkillSummary>();

        foreach (var store in _stores)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var s in await store.SearchAsync(query, cancellationToken).ConfigureAwait(false))
                if (seen.Add(s.Id)) hits.Add(s);
        }

        return hits;
    }

    /// <summary>Writes go to the first store that accepts them.</summary>
    /// <remarks>
    /// THE MANIFEST IS READ-ONLY AND THROWS, WHICH IS THE POINT OF IT. Its
    /// UpsertAsync refuses because the capability list is generated from
    /// capabilities.json and a skill written over it would be a claim about the
    /// product that nothing checks. So a write falls through to the next store
    /// rather than failing - the library is where a new skill belongs.
    /// </remarks>
    public async Task<SkillDetail> UpsertAsync(
        string? id, SkillDraft draft, CancellationToken cancellationToken = default)
    {
        Exception? first = null;

        foreach (var store in _stores)
        {
            try
            {
                return await store.UpsertAsync(id, draft, cancellationToken).ConfigureAwait(false);
            }
            catch (NotSupportedException ex) { first ??= ex; }
            catch (InvalidOperationException ex) { first ??= ex; }
        }

        throw first ?? new NotSupportedException("No store in this composite accepts writes.");
    }

    /// <summary>Deletes from every store that will take it.</summary>
    /// <remarks>
    /// EVERY STORE, NOT THE FIRST. A delete that stopped at the first read-only
    /// store would silently leave the skill in the library, and the caller would
    /// have been told it was gone.
    /// </remarks>
    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        foreach (var store in _stores)
        {
            try { await store.DeleteAsync(id, cancellationToken).ConfigureAwait(false); }
            catch (NotSupportedException) { /* read-only: nothing of ours to remove */ }
            catch (InvalidOperationException) { /* same */ }
        }
    }

    /// <summary>Disposes any store that owns something.</summary>
    public void Dispose()
    {
        foreach (var store in _stores)
            (store as IDisposable)?.Dispose();
    }
}
