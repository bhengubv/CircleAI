// BeliefIntegrityTests.cs
//
// (M2) The dangerous-scenario tests: a third party's fact never becomes the user's,
// a first-person fact does, a contradicting fact supersedes, and a correction retracts.

using System;
using System.Threading.Tasks;
using Xunit;

namespace CircleAI.Companion.Tests;

public class BeliefIntegrityTests
{
    [Fact]
    public async Task ThirdPartyCondition_NeverBecomesASelfFact()
    {
        var ex = new HeuristicBeliefExtractor();
        var store = new SelfBeliefStore();
        foreach (var b in await ex.ExtractAsync("my mother is diabetic", "turn-1"))
            store.Record(b);

        // The user is NOT diabetic — that fact is the mother's.
        Assert.DoesNotContain(store.SelfFacts(),
            b => b.Object.Contains("diabetic", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(store.NonSelf(),
            b => b.Attribution == Attribution.Other &&
                 b.Object.Contains("diabetic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FirstPersonCondition_IsASelfFact()
    {
        var ex = new HeuristicBeliefExtractor();
        var store = new SelfBeliefStore();
        foreach (var b in await ex.ExtractAsync("i am diabetic", "turn-1"))
            store.Record(b);

        Assert.Contains(store.SelfFacts(),
            b => b.Attribution == Attribution.Self &&
                 b.Object.Contains("diabetic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ContradictingSelfBelief_Supersedes()
    {
        var store = new SelfBeliefStore();
        store.Record(new PersonalBelief(Attribution.Self, "user", "residence", "durban", 0.9f, "t1", DateTimeOffset.UtcNow));
        store.Record(new PersonalBelief(Attribution.Self, "user", "residence", "johannesburg", 0.9f, "t2", DateTimeOffset.UtcNow));

        var facts = store.SelfFacts();
        Assert.Single(facts);
        Assert.Equal("johannesburg", facts[0].Object);
    }

    [Fact]
    public async Task Correction_RetractsSelfFact()
    {
        var ex = new HeuristicBeliefExtractor();
        var store = new SelfBeliefStore();
        foreach (var b in await ex.ExtractAsync("i am diabetic", "turn-1"))
            store.Record(b);

        var removed = store.Retract("diabetic");

        Assert.True(removed > 0);
        Assert.Empty(store.SelfFacts());
    }

    [Fact]
    public async Task LivePath_ThirdPartyFact_NeverBecomesSelfBelief()
    {
        using var kg = new SqliteKnowledgeGraph("Data Source=:memory:");
        var beliefs = new SelfBeliefStore();
        var encoder = new CompanionMemoryEncoder(
            new HeuristicKnowledgeGraphExtractor(), kg, new HeuristicBeliefExtractor(), beliefs);

        encoder.Enqueue("my mother is diabetic", string.Empty, "ep1");
        await encoder.DisposeAsync();   // drains

        Assert.DoesNotContain(beliefs.SelfFacts(),
            b => b.Object.Contains("diabetic", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(beliefs.NonSelf(),
            b => b.Object.Contains("diabetic", StringComparison.OrdinalIgnoreCase));
    }
}
