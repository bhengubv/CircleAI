// FileSystemKnowledgeStoreTests.cs
//
// Tests for FileSystemKnowledgeStore — save/get round-trip, delete, search,
// enumeration, and atomic-write semantics on failure.

using CircleAI.Knowledge;
using Xunit;

namespace CircleAI.Knowledge.Tests;

public sealed class FileSystemKnowledgeStoreTests : IDisposable
{
    private readonly string _root;

    public FileSystemKnowledgeStoreTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "circle-ai-knowledge-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private static KnowledgeNote MakeNote(string title, params string[] tags) =>
        new(
            Id: Guid.NewGuid(),
            Title: title,
            BodyMarkdown: "# " + title + "\n\nbody",
            Frontmatter: new Dictionary<string, string> { ["mood"] = "neutral" },
            Tags: tags,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public async Task SaveAndGet_RoundTripsNote()
    {
        var store = new FileSystemKnowledgeStore(_root);
        var note = MakeNote("First", "alpha", "beta");

        var saved = await store.SaveAsync(note);
        var loaded = await store.GetAsync(note.Id);

        Assert.NotNull(loaded);
        Assert.Equal(note.Id, loaded!.Id);
        Assert.Equal(note.Title, loaded.Title);
        Assert.Equal(note.BodyMarkdown, loaded.BodyMarkdown);
        Assert.Equal(note.Tags, loaded.Tags);
        Assert.Equal("neutral", loaded.Frontmatter["mood"]);
        Assert.True(saved.UpdatedAt >= note.UpdatedAt);
    }

    [Fact]
    public async Task GetAsync_ReturnsNullForMissingId()
    {
        var store = new FileSystemKnowledgeStore(_root);
        Assert.Null(await store.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheNote()
    {
        var store = new FileSystemKnowledgeStore(_root);
        var note = MakeNote("ToDelete");
        await store.SaveAsync(note);

        Assert.NotNull(await store.GetAsync(note.Id));

        await store.DeleteAsync(note.Id);
        Assert.Null(await store.GetAsync(note.Id));
    }

    [Fact]
    public async Task DeleteAsync_IsIdempotent()
    {
        var store = new FileSystemKnowledgeStore(_root);
        // Calling delete on a non-existent ID should not throw.
        await store.DeleteAsync(Guid.NewGuid());
    }

    [Fact]
    public async Task SearchByTagAsync_ReturnsOnlyMatchingNotes()
    {
        var store = new FileSystemKnowledgeStore(_root);
        await store.SaveAsync(MakeNote("One", "red", "blue"));
        await store.SaveAsync(MakeNote("Two", "blue", "green"));
        await store.SaveAsync(MakeNote("Three", "yellow"));

        var blues = new List<string>();
        await foreach (var n in store.SearchByTagAsync("blue")) blues.Add(n.Title);

        Assert.Equal(2, blues.Count);
        Assert.Contains("One", blues);
        Assert.Contains("Two", blues);
    }

    [Fact]
    public async Task SearchByTagAsync_IsCaseInsensitive()
    {
        var store = new FileSystemKnowledgeStore(_root);
        await store.SaveAsync(MakeNote("Mixed", "Red"));

        var hits = new List<KnowledgeNote>();
        await foreach (var n in store.SearchByTagAsync("red")) hits.Add(n);
        Assert.Single(hits);
    }

    [Fact]
    public async Task EnumerateAllAsync_YieldsAllSavedNotes()
    {
        var store = new FileSystemKnowledgeStore(_root);
        await store.SaveAsync(MakeNote("A"));
        await store.SaveAsync(MakeNote("B"));
        await store.SaveAsync(MakeNote("C"));

        var titles = new List<string>();
        await foreach (var n in store.EnumerateAllAsync()) titles.Add(n.Title);

        Assert.Equal(3, titles.Count);
        Assert.Contains("A", titles);
        Assert.Contains("B", titles);
        Assert.Contains("C", titles);
    }

    [Fact]
    public async Task EnumerateAllAsync_SkipsNonKnowledgeMarkdownFiles()
    {
        // A stray README.md in the directory must not break enumeration.
        await File.WriteAllTextAsync(
            Path.Combine(_root, "README.md"),
            "Just a README, no frontmatter.");

        var store = new FileSystemKnowledgeStore(_root);
        await store.SaveAsync(MakeNote("real"));

        int n = 0;
        await foreach (var _ in store.EnumerateAllAsync()) n++;
        Assert.Equal(1, n);
    }

    [Fact]
    public async Task SaveAsync_LeavesNoTempFilesOnSuccess()
    {
        var store = new FileSystemKnowledgeStore(_root);
        await store.SaveAsync(MakeNote("Atomic"));

        // Any .tmp file means we leaked an intermediate write.
        var temps = Directory.GetFiles(_root, "*.tmp");
        Assert.Empty(temps);
    }

    [Fact]
    public async Task SaveAsync_DoesNotCorruptTargetOnWriteFailure()
    {
        // Simulate a "write failed" by holding the target file open exclusively
        // before triggering File.Move via SaveAsync. Save should throw and the
        // pre-existing file content must remain intact (atomic-or-error).
        var store = new FileSystemKnowledgeStore(_root);
        var note = MakeNote("Original");
        await store.SaveAsync(note);

        var path = Path.Combine(_root, note.Id.ToString("N") + ".md");
        var originalText = await File.ReadAllTextAsync(path);

        // Open the target with no-sharing on Windows — File.Move(overwrite:true)
        // will fail. On non-Windows this lock is advisory; the test still
        // verifies "either succeeds or leaves original intact".
        using (var hold = new FileStream(
                   path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try
            {
                await store.SaveAsync(note with { Title = "Changed" });
            }
            catch
            {
                // Expected on Windows.
            }
        }

        var afterText = await File.ReadAllTextAsync(path);
        // Either save succeeded (file changed) or failed (file unchanged) —
        // but the file must remain valid in both cases.
        var parsed = KnowledgeNote.ParseFile(afterText);
        Assert.Equal(note.Id, parsed.Id);

        // No leaked temp files in either branch.
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        // And the original byte content matches whichever winning version
        // was committed.
        Assert.True(afterText == originalText || parsed.Title == "Changed");
    }
}
