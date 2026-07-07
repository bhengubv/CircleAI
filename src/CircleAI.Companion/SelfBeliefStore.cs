// SelfBeliefStore.cs
//
// (M2) Memory integrity, part two: what the system is allowed to believe about YOU.
// Only Self-attributed beliefs become facts about the user; Other/World beliefs are
// kept for audit but never surface as user facts — so "my mother is diabetic" can
// never turn into "you are diabetic". A newer self-belief on the same predicate
// supersedes the older one (a functional fact has one current value), and a
// correction retracts a belief outright.
//
// Thread-safe: the encoder writes from its background drain while the session reads
// facts for the prompt.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Companion;

/// <summary>(M2) The user's own facts, with attribution filtering, revision, and correction.</summary>
public sealed class SelfBeliefStore
{
    private readonly object _gate = new();
    private readonly List<PersonalBelief> _self = new();
    private readonly List<PersonalBelief> _audit = new();   // other/world — remembered, never a user fact

    /// <summary>Record a belief. Only Self beliefs become user facts; the rest are audited.</summary>
    public void Record(PersonalBelief belief)
    {
        ArgumentNullException.ThrowIfNull(belief);
        lock (_gate)
        {
            if (belief.Attribution != Attribution.Self)
            {
                _audit.Add(belief);
                return;
            }
            // Supersede an existing self-belief on the same (subject, predicate): a functional
            // fact holds one current value. The prior value drops out of the user's facts.
            _self.RemoveAll(b =>
                string.Equals(b.Subject, belief.Subject, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(b.Predicate, belief.Predicate, StringComparison.OrdinalIgnoreCase));
            _self.Add(belief);
        }
    }

    /// <summary>The user's own current facts.</summary>
    public IReadOnlyList<PersonalBelief> SelfFacts()
    {
        lock (_gate) return _self.ToList();
    }

    /// <summary>Beliefs remembered but never treated as user facts (audit trail).</summary>
    public IReadOnlyList<PersonalBelief> NonSelf()
    {
        lock (_gate) return _audit.ToList();
    }

    /// <summary>Correction ("no, that's my mother"): drop any user fact mentioning the text.</summary>
    public int Retract(string objectContains)
    {
        if (string.IsNullOrWhiteSpace(objectContains)) return 0;
        lock (_gate)
            return _self.RemoveAll(b => b.Object.Contains(objectContains, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Introspection ("why do you think that?"): the source turns behind the user's facts.</summary>
    public IReadOnlyList<string> Provenance()
    {
        lock (_gate)
            return _self.Where(b => b.Source is not null).Select(b => b.Source!).Distinct(StringComparer.Ordinal).ToList();
    }
}
