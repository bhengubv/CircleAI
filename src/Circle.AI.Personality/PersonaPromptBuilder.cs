// PersonaPromptBuilder.cs
//
// Renders a Persona into a compact natural-language system-prompt hint.
// Defensive against prompt-injection: every user-controlled string is
// emitted as a JSON string literal so any embedded quotes or newlines are
// escaped before reaching the model.

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Circle.AI.Personality;

/// <summary>
/// Builds the natural-language system-prompt block describing a
/// <see cref="Persona"/>. Returns an empty string when the persona is in
/// its default/unedited state so the prompt is not bloated with no-op
/// instructions.
/// </summary>
public static class PersonaPromptBuilder
{
    private static readonly JsonSerializerOptions s_stringQuote = new()
    {
        // Hardened escaping — keep control characters and quotes inside the
        // JSON-encoded literal so a malicious taboo entry cannot break out
        // of the [Persona] block.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Renders <paramref name="persona"/> into a compact system-prompt hint.
    /// </summary>
    /// <param name="persona">The persona to render. Required.</param>
    /// <returns>
    /// A persona-block string, or an empty string when the persona is
    /// effectively default (display name only).
    /// </returns>
    public static string BuildSystemHint(Persona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);

        if (IsEffectivelyDefault(persona)) return string.Empty;

        var sb = new StringBuilder();
        sb.Append("[Persona]");

        sb.Append("\nYou are speaking with ");
        sb.Append(Quote(persona.DisplayName));
        sb.Append('.');

        if (!string.IsNullOrWhiteSpace(persona.Pronouns))
        {
            sb.Append(" They identify as ");
            sb.Append(Quote(persona.Pronouns));
            sb.Append('.');
        }

        sb.Append("\nThey prefer responses in ");
        sb.Append(Quote(persona.PreferredLocale));
        sb.Append(", tone between ");
        sb.Append(Quote(persona.Formality.Floor));
        sb.Append(" and ");
        sb.Append(Quote(persona.Formality.Ceiling));
        sb.Append('.');

        if (persona.IdentityTags.Count > 0)
        {
            sb.Append("\nIdentity tags: ");
            sb.Append(QuoteList(persona.IdentityTags));
            sb.Append('.');
        }

        if (persona.Values.Count > 0)
        {
            sb.Append("\nTheir declared values: ");
            sb.Append(QuoteList(persona.Values));
            sb.Append('.');
        }

        if (persona.Taboos.Count > 0)
        {
            sb.Append("\nAvoid: ");
            sb.Append(QuoteList(persona.Taboos));
            sb.Append('.');
        }

        if (!string.IsNullOrWhiteSpace(persona.VoicePreference))
        {
            sb.Append("\nPreferred voice tag: ");
            sb.Append(Quote(persona.VoicePreference));
            sb.Append('.');
        }

        if (persona.Privacy == PrivacyLevel.Strict)
        {
            sb.Append("\nPrivacy: strict — minimize stored signals, do not surface personal context proactively, and never share personal context across surfaces without explicit prompt.");
        }
        else if (persona.Privacy == PrivacyLevel.Open)
        {
            sb.Append("\nPrivacy: open — the user has authorised broader retention and proactive surfacing.");
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// True when the persona contains no information beyond the
    /// <see cref="Persona.Create(string, string)"/> defaults.
    /// </summary>
    private static bool IsEffectivelyDefault(Persona p) =>
        string.IsNullOrWhiteSpace(p.Pronouns)
        && p.IdentityTags.Count == 0
        && p.Values.Count == 0
        && p.Taboos.Count == 0
        && string.IsNullOrWhiteSpace(p.VoicePreference)
        && p.Privacy == PrivacyLevel.Balanced
        && string.Equals(p.Formality.Floor, "casual", StringComparison.Ordinal)
        && string.Equals(p.Formality.Ceiling, "formal", StringComparison.Ordinal);

    /// <summary>
    /// JSON-encodes <paramref name="value"/> into a quoted literal. This is
    /// the prompt-injection defence: any embedded quote, newline, or
    /// directive ("ignore previous instructions") is rendered as inert text
    /// inside a quoted string.
    /// </summary>
    private static string Quote(string value) =>
        JsonSerializer.Serialize(value, s_stringQuote);

    private static string QuoteList(IReadOnlyList<string> items)
    {
        if (items.Count == 0) return string.Empty;
        var parts = new string[items.Count];
        for (int i = 0; i < items.Count; i++) parts[i] = Quote(items[i]);
        return string.Join(", ", parts);
    }
}
