// DomainPacksTests.cs
//
// OPEN-GAPS A9: forty-four domain packs - a framing paragraph each, and an adapter that puts it
// in front of every message - with nothing naming either, while the capability tiles sent bare
// openers. These hold the table that makes them reachable to the packs that exist, and every
// tile that names a pack to one that does.

using System;
using System.Linq;
using System.Reflection;
using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public sealed class DomainPacksTests
{
    [Fact]
    public void Every_pack_in_the_library_is_in_the_table_with_its_own_framing()
    {
        var contexts = typeof(DomainPacks).Assembly.GetTypes()
            .Where(t => t.IsAbstract && t.IsSealed && t.Name.EndsWith("DomainContext", StringComparison.Ordinal))
            .Select(t => t.GetProperty("SystemPromptSnippet", BindingFlags.Public | BindingFlags.Static))
            .Where(p => p is not null)
            .Select(p => (string)p!.GetValue(null)!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(44, contexts.Count);
        Assert.Equal(contexts.Count, DomainPacks.All.Count);
        Assert.All(DomainPacks.All.Values, framing => Assert.Contains(framing, contexts));
    }

    [Fact]
    public void Every_tile_that_names_a_pack_names_one_that_exists()
    {
        var framed = Capabilities.All.SelectMany(g => g.Items).Where(c => c.Domain is not null).ToList();

        Assert.True(framed.Count >= 39, $"only {framed.Count} tiles name a pack");
        Assert.All(framed, c => Assert.NotNull(DomainPacks.Framing(c.Domain)));
        Assert.Equal("Pets", Capabilities.All.SelectMany(g => g.Items).Single(c => c.Title == "Pets").Domain);
    }

    [Fact]
    public void A_question_is_framed_the_way_the_packs_own_adapter_frames_it()
    {
        var framing = CircleAI.Pets.PetsDomainContext.SystemPromptSnippet;

        Assert.Equal($"{framing}\n\nmy dog will not eat", DomainPacks.Frame("Pets", "my dog will not eat"));
        Assert.Equal($"{framing}\n\nx", DomainPacks.Frame("pets", "x"));              // a name, not a spelling
        Assert.Equal("my dog will not eat", DomainPacks.Frame(null, "my dog will not eat"));
        Assert.Equal("my dog will not eat", DomainPacks.Frame("Astrology", "my dog will not eat"));
    }
}
