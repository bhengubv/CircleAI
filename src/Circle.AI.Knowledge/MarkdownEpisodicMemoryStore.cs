// MarkdownEpisodicMemoryStore.cs
//
// IEpisodicMemoryStore implementation backed by IKnowledgeStore. Maps each
// episodic exchange to a markdown note with structured frontmatter and a
// "## User\n\n... ## Assistant\n\n..." body.

using System.Globalization;
using Circle.AI.Memory;

namespace Circle.AI.Knowledge;

/// <summary>
/// Markdown-on-disk implementation of
/// <see cref="Circle.AI.Memory.IEpisodicMemoryStore"/>. Backed by an
/// <see cref="IKnowledgeStore"/>; each <see cref="EpisodicMemoryEntry"/> is
/// persisted as one <see cref="KnowledgeNote"/>. The note format is
/// human-readable and Git-diffable.
/// </summary>
public sealed class MarkdownEpisodicMemoryStore : IEpisodicMemoryStore
{
    // Frontmatter keys used to round-trip an EpisodicMemoryEntry.
    private const string EpisodeIdKey = "episode_id";
    private const string RecordedAtKey = "recorded_at";
    private const string AppContextKey = "app_context";
    private const string EmbeddingKey = "embedding";
    private const string EmbeddingDimsKey = "embedding_dims";
    private const string TagPrefix = "tag_";

    private readonly IKnowledgeStore _store;

    /// <summary>
    /// Creates a new episodic store backed by <paramref name="store"/>.
    /// </summary>
    public MarkdownEpisodicMemoryStore(IKnowledgeStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public async Task AddAsync(EpisodicMemoryEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var note = ToNote(entry);
        await _store.SaveAsync(note, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EpisodicMemoryEntry>> SearchAsync(
        float[]? queryEmbedding,
        int topK = 5,
        CancellationToken ct = default)
    {
        var snapshot = new List<EpisodicMemoryEntry>();
        await foreach (var note in _store.EnumerateAllAsync(ct).ConfigureAwait(false))
        {
            snapshot.Add(FromNote(note));
        }

        if (queryEmbedding is null || queryEmbedding.Length == 0)
        {
            return snapshot
                .OrderByDescending(e => e.RecordedAtUtc)
                .Take(topK)
                .ToList();
        }

        return snapshot
            .Where(e => e.Embedding is not null && e.Embedding.Length == queryEmbedding.Length)
            .Select(e => (Entry: e, Score: CosineSimilarity(queryEmbedding, e.Embedding!)))
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.Entry)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EpisodicMemoryEntry>> GetRecentAsync(
        int count = 10,
        CancellationToken ct = default)
    {
        var snapshot = new List<EpisodicMemoryEntry>();
        await foreach (var note in _store.EnumerateAllAsync(ct).ConfigureAwait(false))
        {
            snapshot.Add(FromNote(note));
        }

        return snapshot
            .OrderByDescending(e => e.RecordedAtUtc)
            .Take(count)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        int n = 0;
        await foreach (var _ in _store.EnumerateAllAsync(ct).ConfigureAwait(false)) n++;
        return n;
    }

    /// <inheritdoc />
    public async Task<int> PruneOlderThanAsync(
        DateTimeOffset cutoff, CancellationToken ct = default)
    {
        var doomed = new List<Guid>();
        await foreach (var note in _store.EnumerateAllAsync(ct).ConfigureAwait(false))
        {
            var entry = FromNote(note);
            if (entry.RecordedAtUtc < cutoff) doomed.Add(note.Id);
        }
        foreach (var id in doomed)
        {
            await _store.DeleteAsync(id, ct).ConfigureAwait(false);
        }
        return doomed.Count;
    }

    // ------------------------------------------------------------------
    // EpisodicMemoryEntry <-> KnowledgeNote
    // ------------------------------------------------------------------

    /// <summary>
    /// Maps an <see cref="EpisodicMemoryEntry"/> to its <see cref="KnowledgeNote"/>
    /// representation.
    /// </summary>
    internal static KnowledgeNote ToNote(EpisodicMemoryEntry entry)
    {
        var frontmatter = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EpisodeIdKey] = entry.Id.ToString("D"),
            [RecordedAtKey] = entry.RecordedAtUtc.ToString("O"),
        };
        if (!string.IsNullOrWhiteSpace(entry.AppContext))
            frontmatter[AppContextKey] = entry.AppContext;

        if (entry.Embedding is { Length: > 0 } emb)
        {
            // Encode the embedding as base64 of the raw float[] bytes.
            var bytes = new byte[emb.Length * sizeof(float)];
            Buffer.BlockCopy(emb, 0, bytes, 0, bytes.Length);
            frontmatter[EmbeddingKey] = Convert.ToBase64String(bytes);
            frontmatter[EmbeddingDimsKey] = emb.Length.ToString(CultureInfo.InvariantCulture);
        }

        var tags = new List<string>();
        if (entry.Tags is not null)
        {
            foreach (var kvp in entry.Tags)
            {
                frontmatter[TagPrefix + kvp.Key] = kvp.Value;
                tags.Add(kvp.Key);
            }
        }

        var body =
            "## User\n\n" + entry.UserText + "\n\n" +
            "## Assistant\n\n" + entry.AssistantText;

        return new KnowledgeNote(
            Id: entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id,
            Title: TruncateForTitle(entry.UserText),
            BodyMarkdown: body,
            Frontmatter: frontmatter,
            Tags: tags,
            CreatedAt: entry.RecordedAtUtc,
            UpdatedAt: entry.RecordedAtUtc);
    }

    /// <summary>
    /// Inverse of <see cref="ToNote(EpisodicMemoryEntry)"/>.
    /// </summary>
    internal static EpisodicMemoryEntry FromNote(KnowledgeNote note)
    {
        Guid episodeId = note.Id;
        if (note.Frontmatter.TryGetValue(EpisodeIdKey, out var raw)
            && Guid.TryParse(raw, out var parsed))
        {
            episodeId = parsed;
        }

        DateTimeOffset recordedAt = note.CreatedAt;
        if (note.Frontmatter.TryGetValue(RecordedAtKey, out var rawWhen)
            && DateTimeOffset.TryParse(rawWhen, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var when))
        {
            recordedAt = when;
        }

        string? appContext = note.Frontmatter.TryGetValue(AppContextKey, out var ctx)
            ? ctx
            : null;

        float[]? embedding = null;
        if (note.Frontmatter.TryGetValue(EmbeddingKey, out var b64)
            && !string.IsNullOrWhiteSpace(b64))
        {
            try
            {
                var bytes = Convert.FromBase64String(b64);
                embedding = new float[bytes.Length / sizeof(float)];
                Buffer.BlockCopy(bytes, 0, embedding, 0, bytes.Length);
            }
            catch
            {
                embedding = null;
            }
        }

        var (userText, assistantText) = SplitBody(note.BodyMarkdown);

        Dictionary<string, string>? tagsOut = null;
        foreach (var kvp in note.Frontmatter)
        {
            if (!kvp.Key.StartsWith(TagPrefix, StringComparison.Ordinal)) continue;
            tagsOut ??= new Dictionary<string, string>(StringComparer.Ordinal);
            tagsOut[kvp.Key[TagPrefix.Length..]] = kvp.Value;
        }

        return new EpisodicMemoryEntry
        {
            Id = episodeId,
            RecordedAtUtc = recordedAt,
            UserText = userText,
            AssistantText = assistantText,
            AppContext = appContext,
            Embedding = embedding,
            Tags = tagsOut,
        };
    }

    private static (string user, string assistant) SplitBody(string body)
    {
        if (string.IsNullOrEmpty(body)) return (string.Empty, string.Empty);

        var normal = body.Replace("\r\n", "\n");
        const string userMarker = "## User\n\n";
        const string assistantMarker = "\n\n## Assistant\n\n";

        int userIdx = normal.IndexOf(userMarker, StringComparison.Ordinal);
        int assistantIdx = normal.IndexOf(assistantMarker, StringComparison.Ordinal);

        if (userIdx < 0 || assistantIdx <= userIdx) return (normal, string.Empty);

        string userText = normal.Substring(
            userIdx + userMarker.Length,
            assistantIdx - (userIdx + userMarker.Length));

        string assistantText = normal.Substring(
            assistantIdx + assistantMarker.Length);

        return (userText, assistantText);
    }

    private static string TruncateForTitle(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "(untitled)";
        var single = source.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return single.Length <= 64 ? single : single[..64];
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0f;
        for (int i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot;
    }
}
