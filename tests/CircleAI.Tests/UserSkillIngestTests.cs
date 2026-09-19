// UserSkillIngestTests.cs
//
// "The user may upload skills, or from any other source." That funnels to one
// place — SkillPackLoader.ImportMarkdownAsync — which parses the SKILL.md, stamps
// where it came from, and writes it to a WRITABLE store (the person's SQLite
// skills). These pin that an upload becomes searchable and tagged, that junk is
// refused, and that a user's own skill composes alongside the shipped packs.

using System;
using System.IO;
using System.Threading.Tasks;
using CircleAI.Skills;
using Xunit;

namespace CircleAI.Tests;

public sealed class UserSkillIngestTests
{
    private const string Sample =
        "---\n" +
        "name: Fixing a Primus stove\n" +
        "description: How to clean and relight a paraffin Primus stove safely.\n" +
        "tags: [primus, stove, paraffin, repair]\n" +
        "---\n" +
        "# Fixing a Primus stove\n\n" +
        "Let it cool. Clean the jet with the pricker. Pump slowly. Light with care.";

    [Fact]
    public async Task An_upload_is_stored_searchable_and_tagged_by_source()
    {
        var path = Path.Combine(Path.GetTempPath(), $"circleai-user-{Guid.NewGuid():N}.db");
        try
        {
            using var user = new SqliteSkillStore(path, identifyingMatchOnly: true);

            var stored = await SkillPackLoader.ImportMarkdownAsync(user, Sample, source: "user");

            Assert.NotNull(stored);
            Assert.Equal("fixing-a-primus-stove", stored!.Id);
            Assert.Contains("pack:user", stored.Tags);
            Assert.Contains("source:user", stored.Tags);

            // Reachable through the same identifying (name+tags) match the library uses.
            var hits = await user.SearchAsync("my primus stove is broken");
            Assert.Contains(hits, h => h.Id == "fixing-a-primus-stove");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task It_persists_across_reopening_the_store()
    {
        var path = Path.Combine(Path.GetTempPath(), $"circleai-user-{Guid.NewGuid():N}.db");
        try
        {
            using (var user = new SqliteSkillStore(path, identifyingMatchOnly: true))
                await SkillPackLoader.ImportMarkdownAsync(user, Sample, source: "share");

            using var reopened = new SqliteSkillStore(path, identifyingMatchOnly: true);
            Assert.NotNull(await reopened.GetAsync("fixing-a-primus-stove"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    public async Task Junk_is_refused(string markdown)
    {
        var store = new InMemorySkillStore();
        Assert.Null(await SkillPackLoader.ImportMarkdownAsync(store, markdown));
    }

    [Fact]
    public async Task A_user_skill_composes_alongside_the_consumer_pack()
    {
        var path = Path.Combine(Path.GetTempPath(), $"circleai-user-{Guid.NewGuid():N}.db");
        try
        {
            using var user = new SqliteSkillStore(path, identifyingMatchOnly: true);
            await SkillPackLoader.ImportMarkdownAsync(user, Sample, source: "user");

            // manifest is omitted here; just prove the user store composes and is asked.
            using var composite = new CompositeSkillStore(ConsumerSkillPack.Shared, user);
            var hits = await composite.SearchAsync("primus stove");
            Assert.Contains(hits, h => h.Id == "fixing-a-primus-stove");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
