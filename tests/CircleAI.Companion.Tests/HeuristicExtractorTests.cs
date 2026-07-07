// HeuristicExtractorTests.cs
//
// (M1) The model-free connector: links content words to their memory (both ways)
// and drops function words, so associations form on meaningful words.

using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace CircleAI.Companion.Tests;

public class HeuristicExtractorTests
{
    [Fact]
    public async Task LinksContentWordsToMemory_BothDirections()
    {
        var ex = new HeuristicKnowledgeGraphExtractor();
        var triples = await ex.ExtractFromTurnAsync("my father had a heart attack", "", "memA");

        Assert.Contains(triples, t => t.Subject == "memA" && t.Predicate == "mentions" && t.Object == "heart");
        Assert.Contains(triples, t => t.Subject == "heart" && t.Predicate == "seenin" && t.Object == "memA");
        Assert.Contains(triples, t => t.Object == "father");
    }

    [Fact]
    public async Task DropsFunctionWordsAndShortTokens()
    {
        var ex = new HeuristicKnowledgeGraphExtractor();
        var triples = await ex.ExtractFromTurnAsync("i am at the shop", "", "memB");

        var words = triples.Select(t => t.Object).Concat(triples.Select(t => t.Subject)).ToHashSet();
        Assert.DoesNotContain("the", words);
        Assert.DoesNotContain("at", words);
        Assert.DoesNotContain("am", words);
        Assert.Contains("shop", words);
    }
}
