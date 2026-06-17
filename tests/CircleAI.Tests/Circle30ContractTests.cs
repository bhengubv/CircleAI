// Circle30ContractTests.cs
//
// (3.0.0) Contract tests for Research / Games / AutonomousBiz /
// CodeUnderstanding / DevTools.

using System;
using System.Threading.Tasks;
using CircleAI.AutonomousBiz;
using CircleAI.CodeUnderstanding;
using CircleAI.DevTools;
using CircleAI.Games;
using CircleAI.Research;
using Xunit;

namespace CircleAI.Tests;

public sealed class Circle30ContractTests
{
    // ── Research ─────────────────────────────────────────────────────

    [Fact]
    public async Task NullResearchCorpus_NoResults()
    {
        Assert.Null(await NullResearchCorpus.Instance.GetAsync("x"));
        Assert.Empty(await NullResearchCorpus.Instance.SearchAsync("transformer"));
    }

    [Fact]
    public async Task NullCitationGraph_NoEdges()
    {
        Assert.Empty(await NullCitationGraph.Instance.ForwardCitationsAsync("x"));
        Assert.Empty(await NullCitationGraph.Instance.BackwardCitationsAsync("x"));
    }

    // ── Games ────────────────────────────────────────────────────────

    [Fact]
    public async Task NullGameLoop_StartStopSafe()
    {
        var l = new NullGameLoop();
        await l.StartAsync();
        await l.StopAsync();
        await l.DisposeAsync();
    }

    [Fact]
    public async Task NullSceneGraph_EmptySnapshot()
        => Assert.Empty(await NullSceneGraph.Instance.SnapshotAsync());

    // ── AutonomousBiz ────────────────────────────────────────────────

    [Fact]
    public async Task NullTreasury_ReturnsZeroBalance()
    {
        var s = await NullTreasury.Instance.GetSnapshotAsync();
        Assert.Equal(0m, s.Balance);
    }

    [Fact]
    public async Task NullDecisionLog_AppendAndReadEmpty()
    {
        await NullDecisionLog.Instance.AppendAsync(new AutonomousDecision("d", "r", "act", DateTimeOffset.UtcNow));
        Assert.Empty(await NullDecisionLog.Instance.ReadAsync());
    }

    // ── CodeUnderstanding ────────────────────────────────────────────

    [Fact]
    public async Task NullCodeSearch_NoHits()
    {
        Assert.Empty(await NullCodeSearch.Instance.SearchAsync("foo"));
        Assert.Empty(await NullCodeSearch.Instance.SemanticSearchAsync("foo"));
    }

    [Fact]
    public async Task NullCodeIndexer_CountZero()
        => Assert.Equal(0, await NullCodeIndexer.Instance.CountSymbolsAsync("/"));

    [Fact]
    public async Task NullSymbolGraph_NoCallers()
    {
        var s = new CodeSymbol("f.cs", 1, "Foo", "method");
        Assert.Empty(await NullSymbolGraph.Instance.CallersOfAsync(s));
        Assert.Empty(await NullSymbolGraph.Instance.CalleesOfAsync(s));
    }

    // ── DevTools (the strategic cornerstone) ─────────────────────────

    [Fact]
    public async Task NullCodeEditor_ReadEmpty()
        => Assert.Equal("", await NullCodeEditor.Instance.ReadAsync("any"));

    [Fact]
    public async Task NullInlineSuggester_NoSuggestion()
        => Assert.Null(await NullInlineSuggester.Instance.SuggestAsync("f", 1, 1, "ctx"));

    [Fact]
    public async Task NullAgentShell_RunTurnReturnsEmptyResponse()
    {
        var t = await NullAgentShell.Instance.RunTurnAsync("hi");
        Assert.Equal("", t.Response);
        Assert.Empty(t.Edits);
    }

    [Fact]
    public async Task NullPatchPlanner_PlanEmptySteps()
    {
        var p = await NullPatchPlanner.Instance.PlanAsync("refactor");
        Assert.Empty(p.Steps);
        Assert.Empty(p.ProposedEdits);
    }

    [Fact]
    public async Task NullRefactorTool_NoEdits()
        => Assert.Empty(await NullRefactorTool.Instance.ProposeAsync(
            new RefactorRequest("rename", new[] { "f.cs" })));
}
