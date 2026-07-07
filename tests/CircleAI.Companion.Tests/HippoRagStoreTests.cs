// HippoRagStoreTests.cs
//
// (M1) Locks in the two precision fixes to SqliteHippoRagStore.MultiHopRecallAsync
// that the recall eval exposed: (1) no fabricated association when the query
// touches no graph node, and (2) the query's own seed terms are not returned as
// recalled memories.

using System;
using System.Linq;
using System.Threading.Tasks;
using CircleAI.Domain;
using Xunit;

namespace CircleAI.Companion.Tests;

public class HippoRagStoreTests
{
    private static SqliteHippoRagStore Build(params (string s, string p, string o)[] triples)
    {
        var kg = new SqliteKnowledgeGraph("Data Source=:memory:");
        foreach (var (s, p, o) in triples) kg.AddTriple(s, p, o, source: "test", confidence: 1f);
        return new SqliteHippoRagStore(kg);
    }

    [Fact]
    public async Task NoQueryTermMatchesGraph_ReturnsEmpty()
    {
        var hippo = Build(("chest", "symptom", "cardiacnode"), ("cardiacnode", "about", "chest"));
        var hits = await hippo.MultiHopRecallAsync("completely unrelated words here", topK: 5);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task SeedTerm_IsExcludedFromResults()
    {
        var hippo = Build(("chest", "symptom", "cardiacnode"), ("cardiacnode", "about", "chest"));
        var hits = await hippo.MultiHopRecallAsync("chest", topK: 5);
        Assert.DoesNotContain(hits, h => h.Item.Text.Equals("chest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AssociatedNode_IsRecalled()
    {
        var hippo = Build(("chest", "symptom", "cardiacnode"), ("cardiacnode", "about", "chest"));
        var hits = await hippo.MultiHopRecallAsync("chest", topK: 5);
        Assert.Contains(hits, h => h.Item.Text.Contains("cardiacnode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConfidenceWeighted_HighConfidenceAssociationRanksAboveLow()
    {
        using var kg = new SqliteKnowledgeGraph("Data Source=:memory:");
        kg.AddTriple("worry", "leadsto", "solidfact", source: "s", confidence: 1.0f);
        kg.AddTriple("solidfact", "backto", "worry", source: "s", confidence: 1.0f);
        kg.AddTriple("worry", "leadsto", "shakyguess", source: "s", confidence: 0.1f);
        kg.AddTriple("shakyguess", "backto", "worry", source: "s", confidence: 0.1f);
        var hippo = new SqliteHippoRagStore(kg);

        var hits = (await hippo.MultiHopRecallAsync("worry", topK: 5)).ToList();
        var solid = hits.FindIndex(h => h.Item.Text == "solidfact");
        var shaky = hits.FindIndex(h => h.Item.Text == "shakyguess");

        Assert.True(solid >= 0, "the high-confidence association should surface");
        Assert.True(shaky < 0 || solid < shaky, "high-confidence association must rank above the low-confidence one");
    }
}
