// MarkdownEpisodicMemoryStoreTests.cs
//
// Tests for MarkdownEpisodicMemoryStore — verifies it satisfies the
// IEpisodicMemoryStore contract and that the on-disk markdown shape is
// the documented "## User / ## Assistant" body.

using Circle.AI.Knowledge;
using Circle.AI.Memory;
using Xunit;

namespace Circle.AI.Knowledge.Tests;

public sealed class MarkdownEpisodicMemoryStoreTests : IDisposable
{
    private readonly string _root;

    public MarkdownEpisodicMemoryStoreTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "circle-ai-knowledge-episodic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private (MarkdownEpisodicMemoryStore Store, FileSystemKnowledgeStore Backing)
        BuildStore()
    {
        var backing = new FileSystemKnowledgeStore(_root);
        var store = new MarkdownEpisodicMemoryStore(backing);
        return (store, backing);
    }

    [Fact]
    public async Task AddAsync_WritesMarkdownFileWithFrontmatter()
    {
        var (store, backing) = BuildStore();

        var entry = new EpisodicMemoryEntry
        {
            UserText = "Hello, B!",
            AssistantText = "Hello. What's the plan?",
            AppContext = "tgn.bidbaas",
            Tags = new Dictionary<string, string> { ["locale"] = "en-ZA" },
            Embedding = new[] { 0.1f, 0.2f, 0.3f },
        };
        await store.AddAsync(entry);

        // One .md file is on disk.
        var files = Directory.GetFiles(_root, "*.md");
        Assert.Single(files);
        var raw = await File.ReadAllTextAsync(files[0]);

        // Sanity check the on-disk shape.
        Assert.Contains("---\n", raw);
        Assert.Contains("episode_id:", raw);
        Assert.Contains("app_context: tgn.bidbaas", raw);
        Assert.Contains("## User\n\nHello, B!", raw);
        Assert.Contains("## Assistant\n\nHello. What's the plan?", raw);

        // The backing knowledge store can also read it.
        var notes = new List<KnowledgeNote>();
        await foreach (var n in backing.EnumerateAllAsync()) notes.Add(n);
        Assert.Single(notes);
    }

    [Fact]
    public async Task GetRecentAsync_ReturnsReverseChronological()
    {
        var (store, _) = BuildStore();

        var now = DateTimeOffset.UtcNow;
        // Save deliberately out of order so we know the sort step is doing work.
        await store.AddAsync(new EpisodicMemoryEntry
        {
            UserText = "middle", AssistantText = "m",
            RecordedAtUtc = now.AddMinutes(-10),
        });
        await store.AddAsync(new EpisodicMemoryEntry
        {
            UserText = "oldest", AssistantText = "o",
            RecordedAtUtc = now.AddMinutes(-100),
        });
        await store.AddAsync(new EpisodicMemoryEntry
        {
            UserText = "newest", AssistantText = "n",
            RecordedAtUtc = now,
        });

        var recent = await store.GetRecentAsync(10);

        Assert.Equal(3, recent.Count);
        Assert.Equal("newest", recent[0].UserText);
        Assert.Equal("middle", recent[1].UserText);
        Assert.Equal("oldest", recent[2].UserText);
    }

    [Fact]
    public async Task CountAsync_TracksWritesAndDeletes()
    {
        var (store, backing) = BuildStore();

        Assert.Equal(0, await store.CountAsync());

        var e1 = new EpisodicMemoryEntry { UserText = "a", AssistantText = "a" };
        await store.AddAsync(e1);
        var e2 = new EpisodicMemoryEntry { UserText = "b", AssistantText = "b" };
        await store.AddAsync(e2);

        Assert.Equal(2, await store.CountAsync());

        await backing.DeleteAsync(e1.Id);
        Assert.Equal(1, await store.CountAsync());
    }

    [Fact]
    public async Task SearchAsync_FallsBackToRecencyWithoutEmbedding()
    {
        var (store, _) = BuildStore();
        var now = DateTimeOffset.UtcNow;
        await store.AddAsync(new EpisodicMemoryEntry
        {
            UserText = "old", AssistantText = "o", RecordedAtUtc = now.AddHours(-2),
        });
        await store.AddAsync(new EpisodicMemoryEntry
        {
            UserText = "new", AssistantText = "n", RecordedAtUtc = now,
        });

        var result = await store.SearchAsync(queryEmbedding: null, topK: 1);
        Assert.Single(result);
        Assert.Equal("new", result[0].UserText);
    }

    [Fact]
    public async Task SearchAsync_ReturnsClosestEmbedding()
    {
        var (store, _) = BuildStore();

        await store.AddAsync(new EpisodicMemoryEntry
        {
            UserText = "match", AssistantText = "m",
            Embedding = new[] { 1f, 0f, 0f },
        });
        await store.AddAsync(new EpisodicMemoryEntry
        {
            UserText = "miss", AssistantText = "x",
            Embedding = new[] { 0f, 1f, 0f },
        });

        var hits = await store.SearchAsync(new[] { 1f, 0f, 0f }, topK: 1);
        Assert.Single(hits);
        Assert.Equal("match", hits[0].UserText);
    }

    [Fact]
    public async Task PruneOlderThanAsync_RemovesAndReportsCount()
    {
        var (store, _) = BuildStore();
        var now = DateTimeOffset.UtcNow;

        await store.AddAsync(new EpisodicMemoryEntry
        {
            UserText = "ancient", AssistantText = "x",
            RecordedAtUtc = now.AddDays(-30),
        });
        await store.AddAsync(new EpisodicMemoryEntry
        {
            UserText = "recent", AssistantText = "x",
            RecordedAtUtc = now,
        });

        int pruned = await store.PruneOlderThanAsync(now.AddDays(-1));
        Assert.Equal(1, pruned);

        var remaining = await store.GetRecentAsync(10);
        Assert.Single(remaining);
        Assert.Equal("recent", remaining[0].UserText);
    }

    [Fact]
    public async Task RoundTrip_PreservesEmbeddingBytes()
    {
        var (store, _) = BuildStore();
        var emb = new[] { 0.5f, -0.25f, 0.125f, 1.0f, -1.0f };
        var entry = new EpisodicMemoryEntry
        {
            UserText = "u", AssistantText = "a", Embedding = emb,
        };
        await store.AddAsync(entry);

        var hits = await store.GetRecentAsync(1);
        Assert.Single(hits);
        Assert.NotNull(hits[0].Embedding);
        Assert.Equal(emb, hits[0].Embedding!);
    }

    [Fact]
    public async Task RoundTrip_PreservesTagsAndAppContext()
    {
        var (store, _) = BuildStore();
        var entry = new EpisodicMemoryEntry
        {
            UserText = "u", AssistantText = "a",
            AppContext = "tgn.txtme",
            Tags = new Dictionary<string, string>
            {
                ["sentiment"] = "positive",
                ["locale"] = "zu-ZA",
            },
        };
        await store.AddAsync(entry);

        var hits = await store.GetRecentAsync(1);
        Assert.Single(hits);
        Assert.Equal("tgn.txtme", hits[0].AppContext);
        Assert.NotNull(hits[0].Tags);
        Assert.Equal("positive", hits[0].Tags!["sentiment"]);
        Assert.Equal("zu-ZA", hits[0].Tags!["locale"]);
    }
}
