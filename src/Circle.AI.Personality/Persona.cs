// Persona.cs
//
// The user-DECLARED persona artefact. Distinct from Circle.AI.Memory.PersonaState
// (which is the AI's LEARNED model of the user). Persona is the user's structured,
// editable, exportable identity declaration — a document the user owns.

namespace Circle.AI.Personality;

/// <summary>
/// User-declared persona artefact. Captures the structured identity the user
/// has chosen to share with the assistant — distinct from the AI's
/// <see cref="Circle.AI.Memory.PersonaState"/>, which is what the AI has
/// inferred about the user over time.
/// </summary>
/// <param name="Id">Stable identifier for the persona document.</param>
/// <param name="DisplayName">User's preferred display name.</param>
/// <param name="Pronouns">Free-form pronouns (e.g. "she/her", "they/them"). May be <c>null</c>.</param>
/// <param name="IdentityTags">Free-form identity tags (e.g. "parent", "vegan", "isiZulu learner", "type-1 diabetic").</param>
/// <param name="Values">Stated values the assistant should respect (e.g. "privacy", "family", "faith").</param>
/// <param name="Taboos">Topics the assistant must refuse or avoid.</param>
/// <param name="PreferredLocale">IETF BCP-47 locale.</param>
/// <param name="VoicePreference">Optional preferred voice tag (e.g. "warm-female", "neutral").</param>
/// <param name="Formality">Declared formality range — the AI's learned PersonaState may ride inside these bounds.</param>
/// <param name="Privacy">Declared privacy posture.</param>
/// <param name="CreatedAt">UTC time of initial creation.</param>
/// <param name="UpdatedAt">UTC time of the last modification.</param>
public sealed record Persona(
    Guid Id,
    string DisplayName,
    string? Pronouns,
    IReadOnlyList<string> IdentityTags,
    IReadOnlyList<string> Values,
    IReadOnlyList<string> Taboos,
    string PreferredLocale,
    string? VoicePreference,
    FormalityRange Formality,
    PrivacyLevel Privacy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
)
{
    /// <summary>
    /// Creates a new <see cref="Persona"/> with sensible defaults: balanced privacy,
    /// no taboos or values, formality range "casual..formal" (effectively unconstrained),
    /// and timestamps stamped to now.
    /// </summary>
    /// <param name="displayName">User's preferred display name. Required.</param>
    /// <param name="locale">IETF BCP-47 locale. Required.</param>
    /// <returns>A fresh persona with a new <see cref="Guid"/> identifier.</returns>
    public static Persona Create(string displayName, string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);

        var now = DateTimeOffset.UtcNow;
        return new Persona(
            Id: Guid.NewGuid(),
            DisplayName: displayName,
            Pronouns: null,
            IdentityTags: Array.Empty<string>(),
            Values: Array.Empty<string>(),
            Taboos: Array.Empty<string>(),
            PreferredLocale: locale,
            VoicePreference: null,
            Formality: new FormalityRange("casual", "formal"),
            Privacy: PrivacyLevel.Balanced,
            CreatedAt: now,
            UpdatedAt: now);
    }
}

/// <summary>
/// Declared bounds on conversational formality. The AI's learned
/// <see cref="Circle.AI.Memory.PersonaState.Formality"/> can drift within
/// these bounds but is clamped to <see cref="Floor"/>/<see cref="Ceiling"/>
/// by an <see cref="IPersonaConflictResolver"/>.
/// </summary>
/// <param name="Floor">Lowest acceptable formality. Allowed values: <c>"casual"</c>, <c>"neutral"</c>, <c>"formal"</c>.</param>
/// <param name="Ceiling">Highest acceptable formality. Allowed values: <c>"casual"</c>, <c>"neutral"</c>, <c>"formal"</c>.</param>
public sealed record FormalityRange(string Floor, string Ceiling);

/// <summary>
/// Declared privacy posture controlling how aggressively the assistant
/// minimises stored signals and how visibly it surfaces personal context.
/// </summary>
public enum PrivacyLevel
{
    /// <summary>Minimum retention, no proactive surfacing, no third-party calls without prompt.</summary>
    Strict,

    /// <summary>Default. Reasonable retention, helpful proactive prompts.</summary>
    Balanced,

    /// <summary>Maximum retention, willing to share personal context across surfaces.</summary>
    Open,
}
