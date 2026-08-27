// MemoryAtom.cs
//
// One remembered thing, of one kind, traceable to where it was said.
//
// THE LAYER THE STORE DID NOT HAVE. Episodes (L0) hold what was said and a
// persona (L3) holds a summary of who somebody is, with nothing in between —
// so anything specific enough to act on had to be re-derived from raw turns
// every time, which in practice means it was not recalled at all.
//
// AN ATOM IS NOT A RULE. Nothing here blocks anything. The kind decides how an
// atom is WEIGHTED when several match the moment, and whether it decays; a
// fact carries how to re-check itself, and a stale fact is reported rather
// than obeyed. Recall, not handcuffs.
//
// NEVER DELETED, ONLY SUPERSEDED. The correction history is the signal that
// makes one atom outrank another - "you have said this four times" is a
// different fact from "you said this once" - and overwriting throws away
// exactly the thing that makes a memory feel load-bearing.

using System;

namespace CircleAI.Memory;

/// <summary>What kind of thing is being remembered.</summary>
/// <remarks>
/// The four behave differently enough that one undifferentiated pile
/// guarantees mishandling: a preference treated as a ruling makes the assistant
/// rigid, and a ruling treated as a preference makes it useless.
/// </remarks>
public enum AtomKind
{
    /// <summary>A decision that was made. Never decays; surfaces first.</summary>
    Ruling,

    /// <summary>Something true about the world. Re-checked before it is relied on.</summary>
    Fact,

    /// <summary>How somebody likes things done. Applied by default, easy to override.</summary>
    Preference,

    /// <summary>
    /// How to work with this person. Never quoted back at them - it shapes tone
    /// and how much to ask, which is not the same as being repeated.
    /// </summary>
    Relationship,
}

/// <summary>One fact, one kind, one source.</summary>
public sealed class MemoryAtom
{
    /// <summary>Stable identity, so a later atom can point at the one it replaces.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Which of the four this is.</summary>
    public AtomKind Kind { get; init; } = AtomKind.Fact;

    /// <summary>The thing itself, in the words a person would recognise.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// What this is about, as a situation key - "deploy:android", "language:count".
    /// </summary>
    /// <remarks>
    /// THIS IS WHAT MAKES RECALL-AT-THE-MOMENT POSSIBLE. Searching prose for
    /// relevance is a guess; matching the subject of the action against the
    /// subject of the atom is not. Free text still gets searched, but the
    /// subject is what makes the right atom arrive before the wrong one.
    /// </remarks>
    public string? Subject { get; init; }

    /// <summary>The episode this came out of, so the claim can be walked back.</summary>
    /// <remarks>
    /// A memory that cannot be audited is a rumour. Every layer above the raw
    /// turns keeps a path down to them.
    /// </remarks>
    public Guid? SourceEpisode { get; init; }

    /// <summary>When it was first recorded.</summary>
    public DateTimeOffset RecordedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// How many times this had to be corrected into place.
    /// </summary>
    /// <remarks>
    /// THE COUNT IS THE POINT, and it is a relevance signal rather than a
    /// punishment ledger: something corrected three times in a week is what
    /// somebody most needs put in front of them, whatever else also matches.
    /// </remarks>
    public int Corrections { get; init; }

    /// <summary>When it was last corrected, or null if it never was.</summary>
    public DateTimeOffset? LastCorrectedUtc { get; init; }

    /// <summary>The atom that replaced this one, or null while it still stands.</summary>
    public Guid? SupersededBy { get; init; }

    /// <summary>Whether this atom is still the current answer.</summary>
    public bool IsCurrent => SupersededBy is null;

    /// <summary>
    /// For a fact: how to re-check it against reality.
    /// </summary>
    /// <remarks>
    /// A command, a path, a query - whatever the caller can act on. This is the
    /// ONE piece of executable memory in the design and it is deliberately a
    /// verification rather than an assertion: it decides whether a fact is
    /// trustworthy, never whether an action is allowed.
    /// </remarks>
    public string? Verify { get; init; }

    /// <summary>When the fact was last checked.</summary>
    public DateTimeOffset? VerifiedAtUtc { get; init; }

    /// <summary>What the last check found, or null if it has never run.</summary>
    public bool? VerifiedOk { get; init; }

    /// <summary>
    /// Whether this should be shown with a warning rather than relied on.
    /// </summary>
    /// <remarks>
    /// A fact that failed its check still surfaces - WITH the fact that it
    /// failed. Hiding it would leave somebody acting on the stale belief they
    /// already had, which is worse than showing a doubt.
    /// </remarks>
    public bool IsStale => Kind == AtomKind.Fact && VerifiedOk == false;
}
