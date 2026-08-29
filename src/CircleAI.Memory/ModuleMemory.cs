// ModuleMemory.cs
//
// A module's own view of the memory the device holds.
//
// MEMORY IS A SERVICE EVERY MODULE CONSUMES, not a feature one app has. There
// is one memory on a device and a hundred and fifty things that might want it,
// and each of them needs the same two answers: what do we already know, and how
// do I record something without pretending it came from somewhere else.
//
// AND THEY ALL NEED IT - INCLUDING THE ONES THAT MUST NOT KEEP ANYTHING. That
// is the part that is easy to get backwards. A live interpreter must never
// retain what passes through it, because those are two other people's words;
// a safety gate must never remember that something was allowed, because being
// talked past once would then buy you past it forever. But "never keep this"
// is itself a thing that has to be remembered. A module with no continuity
// cannot remember its own prohibition.
//
// So the line is not which modules have memory. It is what they hold: the
// interpreter remembers "never keep what passes through me", never the words.
//
// THE GUARANTEE IS IN THE REGISTRATION, NOT IN THE MEMORY. The retention a
// module was built with is declared where it is registered, so it holds even
// on a device whose memory was wiped, edited or has not been written to yet.
// The memory records it as well - so a person can see it, and so it syncs and
// can be argued with - but a rule that could be forgotten is not a rule, and a
// prohibition that fails open is worse than none at all.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory;

/// <summary>What a module is allowed to keep.</summary>
public enum MemoryRetention
{
    /// <summary>
    /// It remembers what happened, which is the ordinary case.
    /// </summary>
    Everything = 0,

    /// <summary>
    /// It remembers rules and nothing else.
    /// </summary>
    /// <remarks>
    /// For anything handling words that are not the owner's, and anything whose
    /// answer must be re-decided every time. A live interpreter carries two
    /// other people's conversation; a content or safety gate that remembered
    /// "this was fine last time" turns one successful argument into permanent
    /// permission. Both still need continuity - for their own rules.
    /// </remarks>
    RulesOnly = 1,
}

/// <summary>One module's use of the device's memory.</summary>
public interface IModuleMemory
{
    /// <summary>Which module this is.</summary>
    string Module { get; }

    /// <summary>What it is allowed to keep.</summary>
    MemoryRetention Retention { get; }

    /// <summary>
    /// What bears on what is about to happen.
    /// </summary>
    /// <remarks>
    /// READING IS NEVER RESTRICTED. Retention is about what a module may write.
    /// A safety gate that could not read the owner's standing rules would be
    /// worse at its job, not safer at it.
    /// </remarks>
    Task<RecallResult> RecallAsync(
        Situation situation, RecallBudget? budget = null, CancellationToken ct = default);

    /// <summary>
    /// Record something, attributed to this module.
    /// </summary>
    /// <returns>
    /// Whether it was kept. False means this module's retention does not allow
    /// it - which is an answer, not a failure, and the caller is told rather
    /// than left to assume.
    /// </returns>
    Task<bool> RememberAsync(
        MemoryAtom atom, Guid? supersedes = null, CancellationToken ct = default);

    /// <summary>
    /// The owner said this - read it for anything worth remembering.
    /// </summary>
    /// <remarks>
    /// A module that may keep only rules does nothing here at all. Extraction
    /// reads whatever it is given, and what passes through an interpreter is
    /// precisely what must never be read.
    /// </remarks>
    Task<LearnReport> HeardAsync(
        string said, string? subject = null, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ModuleMemory : IModuleMemory
{
    private readonly IMemoryService _memory;

    /// <param name="memory">The device's memory.</param>
    /// <param name="module">
    /// What this module is called - "interpret", "career", "banking". It
    /// becomes the atom's subject prefix, so what a module recorded can be
    /// found, read and argued with rather than melting into one pile.
    /// </param>
    /// <param name="retention">What it is allowed to keep.</param>
    public ModuleMemory(
        IMemoryService memory, string module,
        MemoryRetention retention = MemoryRetention.Everything)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));

        if (string.IsNullOrWhiteSpace(module))
            throw new ArgumentException("A module has to say what it is.", nameof(module));

        Module = module.Trim().ToLowerInvariant();
        Retention = retention;
    }

    /// <inheritdoc />
    public string Module { get; }

    /// <inheritdoc />
    public MemoryRetention Retention { get; }

    /// <inheritdoc />
    public Task<RecallResult> RecallAsync(
        Situation situation, RecallBudget? budget = null, CancellationToken ct = default) =>
        _memory.RecallAsync(situation, budget, ct);

    /// <inheritdoc />
    public async Task<bool> RememberAsync(
        MemoryAtom atom, Guid? supersedes = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(atom);

        if (!MayKeep(atom.Kind)) return false;

        await _memory.RememberAsync(Owned(atom), supersedes, ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public Task<LearnReport> HeardAsync(
        string said, string? subject = null, CancellationToken ct = default)
    {
        if (Retention == MemoryRetention.RulesOnly)
            return Task.FromResult(Nothing);

        return _memory.LearnAsync(said, subject ?? Module, ct);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Whether this module may keep a thing of this sort.
    /// </summary>
    /// <remarks>
    /// A rule, a leaning and how somebody wants to be worked with are how a
    /// module knows its own job. What happened, and what turned out to be true,
    /// are the record of other people's business.
    /// </remarks>
    private bool MayKeep(AtomKind kind) =>
        Retention == MemoryRetention.Everything ||
        kind is AtomKind.Ruling or AtomKind.Preference or AtomKind.Relationship;

    /// <summary>The atom, said to have come from here.</summary>
    private MemoryAtom Owned(MemoryAtom atom) => new()
    {
        Id = atom.Id,
        Kind = atom.Kind,
        Text = atom.Text,

        // Prefixed rather than replaced, so "interpret:languages" still rolls
        // up to "interpret" and a module's whole memory can be read at once.
        Subject = atom.Subject is { Length: > 0 } subject
            ? (subject.StartsWith(Module + ":", StringComparison.Ordinal) ? subject : $"{Module}:{subject}")
            : Module,

        Challenge = atom.Challenge,
        Outcome = atom.Outcome,
        SourceEpisode = atom.SourceEpisode,
        RecordedAtUtc = atom.RecordedAtUtc,
        Machine = atom.Machine,
        Verify = atom.Verify,
        Corrections = atom.Corrections,
        LastCorrectedUtc = atom.LastCorrectedUtc,
    };

    private static readonly LearnReport Nothing = new(
        0, Array.Empty<AtomCandidate>(), Array.Empty<AtomCandidate>(), Array.Empty<AtomCandidate>());
}
