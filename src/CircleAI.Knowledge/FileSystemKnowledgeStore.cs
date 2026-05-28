// FileSystemKnowledgeStore.cs
//
// Reference IKnowledgeStore: one .md file per note under a configured root.
// Writes are atomic (write-to-tmp + rename). Thread-safe via SemaphoreSlim
// per file. The Id.ToString("N") form is used as the filename stem so
// filenames are always safe.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace CircleAI.Knowledge;

/// <summary>
/// File-system <see cref="IKnowledgeStore"/>. Each note is stored as
/// <c>{rootDirectory}/{id-no-dashes}.md</c>.
/// </summary>
public sealed class FileSystemKnowledgeStore : IKnowledgeStore
{
    private readonly string _rootDirectory;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    /// <summary>
    /// Creates a new store rooted at <paramref name="rootDirectory"/>.
    /// The directory is created if it does not already exist.
    /// </summary>
    public FileSystemKnowledgeStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = rootDirectory;
        Directory.CreateDirectory(_rootDirectory);
    }

    /// <inheritdoc />
    public async Task<KnowledgeNote?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var path = NotePath(id);
        if (!File.Exists(path)) return null;

        var gate = LockFor(id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return KnowledgeNote.ParseFile(text);
        }
        finally { gate.Release(); }
    }

    /// <inheritdoc />
    public async Task<KnowledgeNote> SaveAsync(KnowledgeNote note, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(note);

        var refreshed = note with { UpdatedAt = DateTimeOffset.UtcNow };
        var target = NotePath(refreshed.Id);
        var tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";

        var gate = LockFor(refreshed.Id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Write to tmp first so a crash mid-write never corrupts the
            // canonical file.
            await File.WriteAllTextAsync(tmp, refreshed.ToFileText(), ct)
                .ConfigureAwait(false);
            File.Move(tmp, target, overwrite: true);
            return refreshed;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }
        finally { gate.Release(); }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var path = NotePath(id);
        var gate = LockFor(id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        finally { gate.Release(); }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<KnowledgeNote> SearchByTagAsync(
        string tag,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        await foreach (var note in EnumerateAllAsync(ct).ConfigureAwait(false))
        {
            if (note.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
                yield return note;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<KnowledgeNote> EnumerateAllAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!Directory.Exists(_rootDirectory)) yield break;

        foreach (var file in Directory.EnumerateFiles(_rootDirectory, "*.md"))
        {
            ct.ThrowIfCancellationRequested();

            KnowledgeNote? note;
            try
            {
                var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                note = KnowledgeNote.ParseFile(text);
            }
            catch
            {
                // Skip notes that are not in our format (e.g. user dropped a
                // README.md in the directory).
                continue;
            }
            yield return note;
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private SemaphoreSlim LockFor(Guid id) =>
        _locks.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));

    private string NotePath(Guid id) =>
        Path.Combine(_rootDirectory, id.ToString("N") + ".md");
}
