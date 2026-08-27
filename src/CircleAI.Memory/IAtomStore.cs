// IAtomStore.cs
//
// Reading and writing the layer between raw turns and a persona.
//
// ONE SEAM, MANY ENGINES. SQLite is the default and the only one that matters
// first: it needs no server, ships inside the app, and is the only option on a
// phone. PostgreSQL, SQL Server, MySQL and Oracle are the shared case - a team
// or a machine somebody already runs - and they are a dialect problem behind
// this interface rather than a second design.
//
// NOTHING HERE REQUIRES AN EMBEDDING. Vector search improves recall; it must
// never be what enables it. A store that stops working without a 100 MB
// embedding model is a store that does not work on the phone this is for.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory;

/// <summary>The atoms: what is known, in kinds, traceable to where it was said.</summary>
public interface IAtomStore
{
    /// <summary>Record something worth remembering.</summary>
    Task AddAsync(MemoryAtom atom, CancellationToken ct = default);

    /// <summary>
    /// Replace an atom with a newer one, keeping the old one readable.
    /// </summary>
    /// <remarks>
    /// The correction count carries forward and increments: this is how "you
    /// have had to say this four times" survives, and that count is what pushes
    /// an atom to the top of a recall.
    /// </remarks>
    Task<MemoryAtom> SupersedeAsync(Guid oldAtomId, MemoryAtom replacement, CancellationToken ct = default);

    /// <summary>
    /// What is known that bears on this situation, best first.
    /// </summary>
    /// <remarks>
    /// Subject match first, then keyword. Superseded atoms are never returned -
    /// they remain readable through <see cref="GetAsync"/> so a decision can be
    /// traced, but they are not answers.
    /// </remarks>
    Task<IReadOnlyList<MemoryAtom>> MatchAsync(
        Situation situation,
        int limit = 20,
        CancellationToken ct = default);

    /// <summary>Every current atom of a kind, newest first.</summary>
    Task<IReadOnlyList<MemoryAtom>> ByKindAsync(
        AtomKind kind,
        int limit = 50,
        CancellationToken ct = default);

    /// <summary>
    /// Every atom, newest first.
    /// </summary>
    /// <remarks>
    /// For reading the memory rather than querying it - listing it for a
    /// person, or finding an atom from the front of its id. Superseded ones are
    /// off by default because they are not answers; they are still here, and a
    /// caller auditing a decision needs them.
    /// </remarks>
    Task<IReadOnlyList<MemoryAtom>> AllAsync(
        bool includeSuperseded = false,
        int limit = 500,
        CancellationToken ct = default);

    /// <summary>One atom by id, superseded or not.</summary>
    Task<MemoryAtom?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Record what a fact's re-check found.</summary>
    /// <remarks>
    /// A failed check does not delete the fact. It marks it, and recall shows it
    /// with the doubt attached - which is more use than silence.
    /// </remarks>
    Task MarkVerifiedAsync(Guid id, bool ok, DateTimeOffset whenUtc, CancellationToken ct = default);

    /// <summary>How many atoms are current.</summary>
    Task<int> CountAsync(CancellationToken ct = default);
}
