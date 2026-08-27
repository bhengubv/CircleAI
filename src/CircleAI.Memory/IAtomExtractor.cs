// IAtomExtractor.cs
//
// The seam between "what was said" and "what is remembered".
//
// ONE SEAM, TWO MECHANISMS. CueExtractor needs no model and therefore works on
// a phone with the radios off, which makes it the floor rather than the
// fallback. A model reads a conversation better than any list of phrases will,
// and when one is loaded it should do this job - but it must never be what
// makes the memory able to fill itself at all.
//
// EXTRACTION PROPOSES. Nothing here writes to a store; that is AtomLearner's
// job, and it is separate precisely so that "what did you spot" and "what did
// you keep" are two questions with two answers.

using System.Collections.Generic;

namespace CircleAI.Memory;

/// <summary>Finds things worth remembering in what was said.</summary>
public interface IAtomExtractor
{
    /// <summary>What this extractor is, for a diagnostic.</summary>
    string Name { get; }

    /// <summary>
    /// What is worth remembering out of one exchange.
    /// </summary>
    /// <param name="episode">The exchange.</param>
    /// <param name="subject">
    /// The situation key to file candidates under. AN EXTRACTOR MUST NOT INVENT
    /// ONE: a wrong subject makes an atom findable in the wrong situation and
    /// invisible in the right one, which is worse than having no key at all.
    /// The caller knows what it is doing and says so.
    /// </param>
    IReadOnlyList<AtomCandidate> Extract(EpisodicMemoryEntry episode, string? subject = null);
}
