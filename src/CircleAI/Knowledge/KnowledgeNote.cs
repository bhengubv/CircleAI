// KnowledgeNote.cs
//
// A single markdown-on-disk knowledge entry: YAML frontmatter + markdown body.
// Inspired by Obsidian/CircleUp note formats — Git-diffable and user-editable.

namespace CircleAI.Knowledge;

/// <summary>
/// A markdown knowledge note: arbitrary frontmatter metadata combined with a
/// markdown body. Serialised on disk as
/// <c>---\nkey: value\n---\n(body)</c>.
/// </summary>
/// <param name="Id">Stable identifier — also the file stem on disk.</param>
/// <param name="Title">Display title of the note.</param>
/// <param name="BodyMarkdown">Markdown body content.</param>
/// <param name="Frontmatter">Arbitrary flat key-value metadata. Stored as YAML.</param>
/// <param name="Tags">Free-form tags for retrieval.</param>
/// <param name="CreatedAt">UTC creation time.</param>
/// <param name="UpdatedAt">UTC modification time.</param>
public sealed record KnowledgeNote(
    Guid Id,
    string Title,
    string BodyMarkdown,
    IReadOnlyDictionary<string, string> Frontmatter,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
)
{
    private const string TitleKey = "title";
    private const string CreatedKey = "created_at";
    private const string UpdatedKey = "updated_at";
    private const string IdKey = "id";
    private const string TagsKey = "tags";

    /// <summary>
    /// Serialises this note to its on-disk text form.
    /// </summary>
    public string ToFileText()
    {
        // Merge well-known fields with the user-supplied frontmatter. The
        // well-known fields win — they are the canonical metadata.
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kvp in Frontmatter) merged[kvp.Key] = kvp.Value;
        merged[IdKey] = Id.ToString("D");
        merged[TitleKey] = Title;
        merged[CreatedKey] = CreatedAt.ToString("O");
        merged[UpdatedKey] = UpdatedAt.ToString("O");
        merged[TagsKey] = string.Join(",", Tags);

        return YamlFrontmatter.Write(merged, BodyMarkdown);
    }

    /// <summary>
    /// Parses the on-disk text form back into a <see cref="KnowledgeNote"/>.
    /// </summary>
    public static KnowledgeNote ParseFile(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var (frontmatter, body) = YamlFrontmatter.Read(text);

        if (!frontmatter.TryGetValue(IdKey, out var idRaw) || !Guid.TryParse(idRaw, out var id))
            throw new FormatException("Knowledge note frontmatter missing or invalid 'id'.");

        string title = frontmatter.TryGetValue(TitleKey, out var t) ? t : string.Empty;

        var created = ParseTimestamp(frontmatter, CreatedKey);
        var updated = ParseTimestamp(frontmatter, UpdatedKey);

        var tags = frontmatter.TryGetValue(TagsKey, out var rawTags) && !string.IsNullOrWhiteSpace(rawTags)
            ? rawTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .ToList()
            : new List<string>();

        // Strip the well-known keys from the user-visible frontmatter view.
        var userFrontmatter = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kvp in frontmatter)
        {
            if (kvp.Key is IdKey or TitleKey or CreatedKey or UpdatedKey or TagsKey) continue;
            userFrontmatter[kvp.Key] = kvp.Value;
        }

        return new KnowledgeNote(
            Id: id,
            Title: title,
            BodyMarkdown: body,
            Frontmatter: userFrontmatter,
            Tags: tags,
            CreatedAt: created,
            UpdatedAt: updated);
    }

    private static DateTimeOffset ParseTimestamp(
        IReadOnlyDictionary<string, string> map, string key)
    {
        if (!map.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return DateTimeOffset.UtcNow;

        return DateTimeOffset.TryParse(
                   raw, System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.RoundtripKind, out var dto)
            ? dto
            : DateTimeOffset.UtcNow;
    }
}
