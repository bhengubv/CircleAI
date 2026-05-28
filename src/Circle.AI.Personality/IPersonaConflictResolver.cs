// IPersonaConflictResolver.cs
//
// Bridges the declared Persona (from this package) and the learned
// PersonaState (from Circle.AI.Memory). Decides which wins when they
// disagree — e.g. user declared "casual" but AI learned the user actually
// behaves more formally.

using Circle.AI.Memory;

namespace Circle.AI.Personality;

/// <summary>
/// Reconciles a user-declared <see cref="Persona"/> with the AI's learned
/// <see cref="PersonaState"/>. The output is the persona that should be
/// applied to the active session — either the declared one with bounds
/// enforced, or the learned one overriding declaration.
/// </summary>
public interface IPersonaConflictResolver
{
    /// <summary>
    /// Resolves any disagreement between <paramref name="declared"/> and
    /// <paramref name="learned"/>. Implementations must be deterministic and
    /// must NEVER mutate either input.
    /// </summary>
    /// <param name="declared">The user-declared persona document.</param>
    /// <param name="learned">The AI's learned persona snapshot.</param>
    /// <returns>The reconciled persona to apply to the session.</returns>
    Persona Resolve(Persona declared, PersonaState learned);
}

/// <summary>
/// Default resolver: the declared persona's bounds are hard limits. The
/// learned formality is clamped to the declared <see cref="FormalityRange"/>.
/// Everything else from the declared persona passes through unchanged. This
/// is the privacy-respecting default — the user's stated preference wins.
/// </summary>
public sealed class DeclaredWinsResolver : IPersonaConflictResolver
{
    /// <inheritdoc />
    public Persona Resolve(Persona declared, PersonaState learned)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(learned);

        // Clamp the learned formality into the declared range and record the
        // resolved formality on the floor/ceiling of the range. The declared
        // record is otherwise the source of truth.
        var clamped = ClampFormality(learned.Formality, declared.Formality);
        if (string.Equals(clamped, learned.Formality, StringComparison.Ordinal))
        {
            // Learned was within bounds — no adjustment to surface.
            return declared;
        }

        // Learned drifted outside declared bounds — surface the clamped value
        // by replacing the floor or ceiling so future projections respect it.
        var range = clamped switch
        {
            "casual" => new FormalityRange("casual", declared.Formality.Ceiling),
            "formal" => new FormalityRange(declared.Formality.Floor, "formal"),
            _ => declared.Formality,
        };

        return declared with { Formality = range };
    }

    private static string ClampFormality(string learned, FormalityRange range)
    {
        int learnedRank = Rank(learned);
        int floorRank = Rank(range.Floor);
        int ceilingRank = Rank(range.Ceiling);

        // If declared range is inverted, treat declared as fixed at floor.
        if (floorRank > ceilingRank) return range.Floor;

        if (learnedRank < floorRank) return range.Floor;
        if (learnedRank > ceilingRank) return range.Ceiling;
        return learned;
    }

    private static int Rank(string formality) => formality switch
    {
        "casual" => 0,
        "neutral" => 1,
        "formal" => 2,
        _ => 1, // unknown values rank as neutral
    };
}

/// <summary>
/// Alternative resolver: the learned <see cref="PersonaState"/> overrides
/// the declared <see cref="Persona"/>. Intended for "privacy mode off"
/// scenarios where the user has opted in to letting the AI follow what it
/// has observed rather than what was declared.
/// </summary>
public sealed class LearnedWinsResolver : IPersonaConflictResolver
{
    /// <inheritdoc />
    public Persona Resolve(Persona declared, PersonaState learned)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(learned);

        // Pass through — the caller will mostly use the learned state, but we
        // still return the declared persona so identity, taboos, and values
        // stay intact. The learned formality/locale/verbosity should be
        // applied separately by the prompt builder.
        return declared;
    }
}
