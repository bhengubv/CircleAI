// CompanionMemoryEncoderTests.cs
//
// (M1) Proves the "fill as you talk" piece: a turn handed to the encoder ends up
// as connected memory in the graph, and two turns that share a word bridge across
// each other — the same cross-turn link the recall eval depends on, now built by
// the live path instead of the test.

using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace CircleAI.Companion.Tests;

public class CompanionMemoryEncoderTests
{
    [Fact]
    public async Task Enqueue_FillsGraphFromTurn()
    {
        using var kg = new SqliteKnowledgeGraph("Data Source=:memory:");
        var encoder = new CompanionMemoryEncoder(new HeuristicKnowledgeGraphExtractor(), kg);
        encoder.Enqueue("my father had a heart attack", string.Empty, "ep1");
        await encoder.DisposeAsync(); // drains the queue

        var tripleCount = kg.AllTriples().Count;
        Assert.True(tripleCount > 0, $"graph empty after drain; drain error: {encoder.LastError}");
        var hippo = new SqliteHippoRagStore(kg);
        var hits = await hippo.MultiHopRecallAsync("father", topK: 5);
        var texts = string.Join(" | ", hits.Select(h => h.Item.Text));
        Assert.True(hits.Any(h => h.Item.Text.Contains("heart", StringComparison.OrdinalIgnoreCase)),
            $"triples={tripleCount}, hits=[{texts}]");
    }

    [Fact]
    public async Task TwoTurnsSharingAWord_BridgeAcrossThem()
    {
        using var kg = new SqliteKnowledgeGraph("Data Source=:memory:");
        await using (var encoder = new CompanionMemoryEncoder(new HeuristicKnowledgeGraphExtractor(), kg))
        {
            encoder.Enqueue("my fathers heart failed from cardiac arrest", string.Empty, "epA");
            encoder.Enqueue("the doctor checked my heart and my chest", string.Empty, "epB");
        }

        var hippo = new SqliteHippoRagStore(kg);
        // "chest" only appears in epB; the bridge word "heart" reaches epA (cardiac).
        var hits = await hippo.MultiHopRecallAsync("chest", topK: 5);
        Assert.Contains(hits, h => h.Item.Text.Contains("cardiac", StringComparison.OrdinalIgnoreCase));
    }
}
