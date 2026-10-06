// IKnowledgeStore.cs
//
// Contract for storing and querying KnowledgeNote markdown documents.

namespace CircleAI.Knowledge;

/// <summary>
/// Persistent store for <see cref="KnowledgeNote"/> documents.
/// Implementations may persist to disk (markdown files), to Git, or to a
/// remote sync server.
/// </summary>
public interface IKnowledgeStore
{
    /// <summary>
    /// Loads the note with the given identifier. Returns <c>null</c> when
    /// no note exists for <paramref name="id"/>.
    /// </summary>
    Task<KnowledgeNote?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Persists <paramref name="note"/>. The returned record may differ from
    /// the input (e.g. updated <see cref="KnowledgeNote.UpdatedAt"/>).
    /// </summary>
    Task<KnowledgeNote> SaveAsync(KnowledgeNote note, CancellationToken ct = default);

    /// <summary>
    /// Deletes the note with the given identifier. No-op if it does not
    /// exist.
    /// </summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Streams notes carrying <paramref name="tag"/> in their
    /// <see cref="KnowledgeNote.Tags"/> collection.
    /// </summary>
    IAsyncEnumerable<KnowledgeNote> SearchByTagAsync(
        string tag, CancellationToken ct = default);

    /// <summary>
    /// Streams every note currently stored.
    /// </summary>
    IAsyncEnumerable<KnowledgeNote> EnumerateAllAsync(CancellationToken ct = default);
}
