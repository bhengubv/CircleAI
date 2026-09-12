// CompositeSkillStoreTests.cs
//
// The seam where the skill library meets the assistant's honesty about itself.
//
// AIOptions.SkillStore IS ONE REFERENCE. Wiring 1,405 community skills in means
// assigning over CapabilityManifestSkillStore - the store whose whole purpose is
// that the assistant does not claim features this build does not ship. Its tests
// would all have stayed green through that swap, because they test the store and
// not who is wired to the seam.
//
// So these test the seam. What must remain true after composing: the manifest
// still answers first, its read-only contract survives, a write still lands
// somewhere, and a delete does not claim success while leaving the skill behind.

using System.Threading;
using System.Threading.Tasks;
using CircleAI.Skills;
using Xunit;

namespace CircleAI.Tests;

public class CompositeSkillStoreTests
{
    private static SkillDraft Draft(string name, string description = "", params string[] tags)
        => new(name, description, "instructions for " + name, tags);

    /// <summary>A read-only store that refuses writes, like the manifest.</summary>
    private sealed class ReadOnlyStore : ISkillStore
    {
        private readonly InMemorySkillStore _inner = new();

        public ReadOnlyStore(params (string Id, SkillDraft Draft)[] seed)
        {
            foreach (var (id, draft) in seed)
                _inner.UpsertAsync(id, draft).GetAwaiter().GetResult();
        }

        public Task<IReadOnlyList<SkillSummary>> ListAsync(CancellationToken ct = default)
            => _inner.ListAsync(ct);

        public Task<SkillDetail?> GetAsync(string id, CancellationToken ct = default)
            => _inner.GetAsync(id, ct);

        public Task<IReadOnlyList<SkillSummary>> SearchAsync(string q, CancellationToken ct = default)
            => _inner.SearchAsync(q, ct);

        public Task<SkillDetail> UpsertAsync(string? id, SkillDraft d, CancellationToken ct = default)
            => throw new NotSupportedException("read-only, like the capability manifest");

        public Task DeleteAsync(string id, CancellationToken ct = default)
            => throw new NotSupportedException("read-only, like the capability manifest");
    }

    // ── order ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_first_store_answers_Get_before_the_second()
    {
        var manifest = new ReadOnlyStore(("voice", Draft("Voice", "not yet - planned")));
        var library = new InMemorySkillStore();
        await library.UpsertAsync("voice", Draft("Voice", "a community skill about voice"));

        var composite = new CompositeSkillStore(manifest, library);

        var hit = await composite.GetAsync("voice");

        // THE WHOLE REASON FOR THE ORDER. A community skill called "voice" must
        // never outrank what this build says about its own voice support.
        Assert.Equal("not yet - planned", hit!.Description);
    }

    [Fact]
    public async Task Search_puts_the_first_stores_hits_first()
    {
        var manifest = new ReadOnlyStore(("mem", Draft("Memory", "remembers across turns")));
        var library = new InMemorySkillStore();
        await library.UpsertAsync("note", Draft("Note taking", "remembers what you said"));

        var composite = new CompositeSkillStore(manifest, library);

        var hits = await composite.SearchAsync("remembers");

        Assert.Equal(2, hits.Count);
        Assert.Equal("mem", hits[0].Id);
    }

    [Fact]
    public async Task Search_still_reaches_the_library_when_the_manifest_also_matched()
    {
        // NOT FIRST-STORE-WINS. SkillContextBuilder takes the first few hits and
        // caps the block at 1500 chars, so stopping at the manifest would mean a
        // question it matched a single word of never reaches the library at all.
        var manifest = new ReadOnlyStore(("cap", Draft("Charts", "draws a chart")));
        var library = new InMemorySkillStore();
        await library.UpsertAsync("how", Draft("Chart recipes", "how to draw a chart well"));

        var composite = new CompositeSkillStore(manifest, library);

        var hits = await composite.SearchAsync("chart");

        Assert.Contains(hits, h => h.Id == "cap");
        Assert.Contains(hits, h => h.Id == "how");
    }

    [Fact]
    public async Task One_id_appears_once_however_many_stores_hold_it()
    {
        var manifest = new ReadOnlyStore(("dup", Draft("Dup", "manifest copy")));
        var library = new InMemorySkillStore();
        await library.UpsertAsync("dup", Draft("Dup", "library copy"));

        var composite = new CompositeSkillStore(manifest, library);

        Assert.Single(await composite.ListAsync());
        Assert.Single(await composite.SearchAsync("copy"));
    }

    [Fact]
    public async Task Everything_from_both_stores_is_listed()
    {
        var manifest = new ReadOnlyStore(("a", Draft("A")), ("b", Draft("B")));
        var library = new InMemorySkillStore();
        await library.UpsertAsync("c", Draft("C"));

        var composite = new CompositeSkillStore(manifest, library);

        Assert.Equal(3, (await composite.ListAsync()).Count);
    }

    // ── writing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_write_falls_past_the_read_only_store_rather_than_failing()
    {
        // The manifest throws NotSupportedException on purpose - "editing them at
        // runtime would let the assistant claim things the repo cannot back up".
        // A new skill belongs in the library, so the write must land there, not
        // blow up.
        var composite = new CompositeSkillStore(
            new ReadOnlyStore(), new InMemorySkillStore());

        var saved = await composite.UpsertAsync("new", Draft("New skill"));

        Assert.Equal("new", saved.Id);
        Assert.NotNull(await composite.GetAsync("new"));
    }

    [Fact]
    public async Task A_write_with_nowhere_to_go_still_throws()
    {
        // Falling through silently would tell a caller their skill was saved when
        // nothing had been.
        var composite = new CompositeSkillStore(new ReadOnlyStore(), new ReadOnlyStore());

        await Assert.ThrowsAsync<NotSupportedException>(
            () => composite.UpsertAsync("x", Draft("X")));
    }

    [Fact]
    public async Task A_delete_reaches_every_store_that_will_take_it()
    {
        // STOPPING AT THE FIRST READ-ONLY STORE WOULD BE A LIE. The caller is told
        // the skill is gone; it is still in the library, and it comes back in the
        // next search.
        var library = new InMemorySkillStore();
        await library.UpsertAsync("gone", Draft("Gone", "remove me"));

        var composite = new CompositeSkillStore(new ReadOnlyStore(), library);

        await composite.DeleteAsync("gone");

        Assert.Null(await composite.GetAsync("gone"));
        Assert.Empty(await composite.SearchAsync("remove"));
    }

    // ── the guards ───────────────────────────────────────────────────────────

    [Fact]
    public void A_composite_over_nothing_is_refused()
        => Assert.Throws<ArgumentException>(() => new CompositeSkillStore());

    [Fact]
    public async Task An_empty_query_returns_nothing()
    {
        var composite = new CompositeSkillStore(
            new ReadOnlyStore(("a", Draft("A", "something"))));

        Assert.Empty(await composite.SearchAsync(""));
    }

    // ── the regression the phone found ───────────────────────────────────────

    [Fact]
    public async Task Asking_what_it_can_do_still_reaches_the_capability_manifest()
    {
        // MEASURED ON A P30, 2026-09-12. Asked "what can you do", the phone said
        // "my primary capabilities include assisting with various queries related
        // to user needs" - a model with nothing to go on.
        //
        // THE CHAIN, AND NOT ONE LINK OF IT IS OBVIOUS FROM ANY SINGLE FILE:
        //
        //   CapabilityManifestSkillStore matches the WHOLE QUERY as a substring,
        //   so "what can you do" hits nothing in it - by design, and harmless for
        //   as long as it was the only store.
        //
        //   SkillContextBuilder switches to COMPACT mode on zero matches and
        //   lists capability names. That is how the model used to learn what it
        //   is.
        //
        //   Add a library that ORs every term, and "do" and "you" match hundreds
        //   of community skills. Now the search is NOT empty, the builder goes to
        //   FULL mode over five arbitrary skills, and the manifest is gone.
        //
        // Every existing test stayed green: the manifest's tests test the
        // manifest, the library's test the library, and the defect lives between
        // them. This one asserts the seam.
        var manifest = new ReadOnlyStore(
            ("offline", Draft("Offline", "runs entirely on this phone")),
            ("memory", Draft("Memory", "remembers across turns")));

        var library = new InMemorySkillStore();
        foreach (var name in new[] { "Do the dishes", "You and your team", "How to do X" })
            await library.UpsertAsync(null, Draft(name, "a community skill about how you do things"));

        var composite = new CompositeSkillStore(manifest, library);

        // The library must not answer a question made only of stopwords. Here the
        // in-memory store is a plain substring matcher, so it WOULD match - which
        // is exactly why SqliteSkillStore filters stopwords before it searches and
        // why SqliteSkillStoreTests pins that separately.
        var builder = new SkillContextBuilder(composite, maxSkills: 5, maxChars: 1500);
        var block = await builder.BuildContextAsync("what can you do");

        // Whatever mode it lands in, the assistant's own capabilities must be in
        // the block it hands the model.
        Assert.Contains("offline", block, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_real_question_still_reaches_the_library()
    {
        // The other half. Fixing the above by never letting the library answer
        // would be a library nothing can reach.
        var manifest = new ReadOnlyStore(("offline", Draft("Offline", "runs on this phone")));
        var library = new InMemorySkillStore();
        await library.UpsertAsync("commit", Draft(
            "Commit messages", "write a good commit message", "imperative mood"));

        var composite = new CompositeSkillStore(manifest, library);
        var builder = new SkillContextBuilder(composite, maxSkills: 5, maxChars: 1500);

        var block = await builder.BuildContextAsync("how do I write a commit message");

        Assert.Contains("commit", block, StringComparison.OrdinalIgnoreCase);
    }
}
