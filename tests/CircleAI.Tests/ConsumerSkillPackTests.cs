// ConsumerSkillPackTests.cs
//
// The consumer pack is what makes the everyday-life Services REAL. A Service
// tile is a promise; if a tap on it selects no skill, it is a label and a 0.6B
// improvises. These tests pin the promise: the pack loads, each flagship tile's
// actual opener (read from Capabilities, so a renamed tile fails here) selects
// its domain's skill through the SAME identifying (name+tags) match the shipped
// SQLite library uses, the real SkillContextBuilder puts it in the prompt, and a
// consumer skill is offered ahead of the community library.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CircleAI.Assistant;   // Capabilities — the generated Services catalogue
using CircleAI.Skills;
using Xunit;

namespace CircleAI.Tests;

public sealed class ConsumerSkillPackTests
{
    /// <summary>The current opener for a Services tile, by its title.</summary>
    private static string OpenerFor(string title)
    {
        var cap = Capabilities.All.SelectMany(g => g.Items)
            .FirstOrDefault(c => c.Title == title);
        Assert.True(cap is not null, $"No Services tile titled \"{title}\" — was it renamed?");
        return cap!.Opener;
    }

    [Fact]
    public async Task Pack_loads_and_every_skill_is_stamped()
    {
        var store = ConsumerSkillPack.Load();   // a fresh load, not the singleton
        var all = await store.ListAsync();

        Assert.True(all.Count >= 15, $"expected the authored consumer skills, got {all.Count}");
        Assert.All(all, s => Assert.Contains($"pack:{ConsumerSkillPack.PackName}", s.Tags));
    }

    [Theory]
    // Ids are the slug of each skill's frontmatter `name:`, not the folder name.
    [InlineData("Your CV",      "build-your-cv")]
    [InlineData("Work rights",  "your-rights-at-work")]
    [InlineData("Banking",      "understand-your-bank-account")]
    [InlineData("Money",        "plan-your-money-this-month")]
    public async Task A_flagship_tile_opener_selects_its_consumer_skill(string tileTitle, string expectedId)
    {
        var opener = OpenerFor(tileTitle);

        var hits = await ConsumerSkillPack.Shared.SearchAsync(opener);

        Assert.Contains(hits, h => h.Id == expectedId);
    }

    [Theory]
    [InlineData("Housing",      "renting-a-home-your-rights")]
    [InlineData("Health",       "using-the-clinic-and-when-to-get-help")]
    [InlineData("Legal",        "getting-legal-help-for-free")]
    [InlineData("Staying safe", "emergencies-and-staying-safe")]
    [InlineData("Electricity",  "electricity-and-load-shedding")]
    [InlineData("Food",         "feeding-your-family-well")]
    [InlineData("Farming",      "growing-food-at-home")]
    public async Task A_wave1_tile_opener_selects_its_consumer_skill(string tileTitle, string expectedId)
    {
        var opener = OpenerFor(tileTitle);
        var hits = await ConsumerSkillPack.Shared.SearchAsync(opener);
        Assert.Contains(hits, h => h.Id == expectedId);
    }

    [Fact]
    public async Task The_government_tile_opener_selects_the_grant_skill()
    {
        // The "Government" tile (Civic) is how a person reaches SASSA grants.
        var hits = await ConsumerSkillPack.Shared.SearchAsync(OpenerFor("Government"));
        Assert.Contains(hits, h => h.Id == "apply-for-a-social-grant");
    }

    [Fact]
    public async Task The_engine_puts_a_consumer_skill_in_the_prompt_block()
    {
        // The production path: SkillContextBuilder is what AIService calls to
        // enrich the system prompt. Prove the skill actually reaches the block.
        var builder = new SkillContextBuilder(ConsumerSkillPack.Shared);

        var block = await builder.BuildContextAsync(OpenerFor("Work rights"));

        Assert.Contains("## Available Skills", block);
        // Either work skill leading is fine — both are domain-grounded; the 1500-
        // char budget usually fits only the first. The point is a consumer skill
        // for work reached the prompt at all.
        Assert.True(
            block.Contains("your-rights-at-work") || block.Contains("if-you-are-dismissed-unfairly"),
            $"no work-rights consumer skill in the block:\n{block}");
    }

    [Fact]
    public async Task Match_is_identifying_only_a_body_word_does_not_hit()
    {
        var store = ConsumerSkillPack.Shared;

        // "conciliation" is in the CCMA skill's BODY, not its name or tags — so
        // it must NOT select it (same rule as the library's identifyingMatchOnly).
        var body = await store.SearchAsync("conciliation");
        Assert.DoesNotContain(body, h => h.Id == "if-you-are-dismissed-unfairly");

        // "ccma" IS a tag — so it must.
        var tag = await store.SearchAsync("ccma");
        Assert.Contains(tag, h => h.Id == "if-you-are-dismissed-unfairly");
    }

    [Fact]
    public async Task No_significant_terms_means_no_match_not_everything()
    {
        // A query of only function words matches nothing, deliberately — the
        // caller's no-match path is better than a match on "do you".
        var hits = await ConsumerSkillPack.Shared.SearchAsync("what can you do");
        Assert.Empty(hits);
    }

    [Fact]
    public async Task Composed_ahead_of_the_library_for_a_shared_opener()
    {
        // A community-library skill that also matches "money" must not outrank
        // ours. The stand-in library term-matches the way the SQLite one does.
        var library = new ConsumerSkillStore();
        library.Add("money-market-python", "money market python",
            "a developer skill", "body", new[] { "money", "python" });

        var composite = new CompositeSkillStore(ConsumerSkillPack.Shared, library);

        var hits = (await composite.SearchAsync(OpenerFor("Money"))).ToList();

        var ours = hits.FindIndex(h => h.Id == "plan-your-money-this-month");
        var lib  = hits.FindIndex(h => h.Id == "money-market-python");

        Assert.True(ours >= 0, "our money skill was not selected");
        Assert.True(lib < 0 || ours < lib, "the library skill outranked ours");
    }

    /// <summary>The tiles that need NO grounded skill — the model and its tools
    /// answer them directly (write, translate, draw, look up, chat). Everything
    /// else is a grounded domain and MUST have a skill, or the tile is a label.</summary>
    private static readonly HashSet<string> Generative = new(StringComparer.Ordinal)
    {
        "Relationships", "Languages", "Interpret", "Research", "Code",
        "Write something", "A presentation", "A chart", "Show me",
        "Read a picture", "Look it up", "Make something up",
        "Faith", "People", "Places to go",
        "Music", "Sport", "Watch and read", "Video", "Games", "Gaming",
    };

    [Fact]
    public async Task Every_grounded_services_tile_has_a_skill_behind_it()
    {
        // "A service with no skill behind it is a label." This is the whole-job
        // guarantee: tap any grounded tile and its OWN opener must select at least
        // one consumer skill. If a new tile is added with no skill, this fails and
        // names it - you cannot ship half the grid.
        var store = ConsumerSkillPack.Shared;
        var missing = new List<string>();

        foreach (var group in Capabilities.All)
            foreach (var tile in group.Items)
            {
                if (Generative.Contains(tile.Title)) continue;
                var hits = await store.SearchAsync(tile.Opener);
                if (hits.Count == 0) missing.Add($"{tile.Title} -> \"{tile.Opener}\"");
            }

        Assert.True(missing.Count == 0,
            "Grounded Services tiles with no consumer skill behind them:\n  " +
            string.Join("\n  ", missing));
    }
}
