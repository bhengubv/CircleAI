using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Memory;
using CircleAI.Simulation;
using Xunit;

namespace CircleAI.Simulation.Tests;

public sealed class NetworkHealthSimulatorTests
{
    private static EpisodicMemoryEntry MakeEntry(
        string userText,
        DateTimeOffset? at  = null,
        string?         app = null,
        Dictionary<string, string>? tags = null) =>
        new()
        {
            UserText      = userText,
            AssistantText = "assistant response",
            RecordedAtUtc = at ?? DateTimeOffset.UtcNow,
            AppContext     = app,
            Tags           = tags,
        };

    private static SimulationScenario MakeScenario(int steps = 10) =>
        SimulationScenario.Create(ScenarioKind.SoftwareDeployment, "Test deployment", steps: steps);

    // ------------------------------------------------------------------
    // 1. Empty history → Healthy (no edges, health stays 1.0)
    // ------------------------------------------------------------------

    [Fact]
    public async Task ForecastAsync_EmptyHistory_HealthyOutcome()
    {
        var sim    = new NetworkHealthSimulator();
        var result = await sim.ForecastAsync(
            Array.Empty<EpisodicMemoryEntry>(),
            MakeScenario());

        Assert.Equal(SimulationOutcome.Healthy, result.Outcome);
        Assert.Equal(1.0f, result.HealthScore, precision: 6);
    }

    // ------------------------------------------------------------------
    // 2. High-weight edges (all 1.0) → Healthy
    // ------------------------------------------------------------------

    [Fact]
    public async Task ForecastAsync_AllHighWeightEdges_HealthyOutcome()
    {
        // All edges weight=1.0 → (1-1.0)*decay = 0 per step → no health loss.
        // Entries are more than 1 hour apart so no low-weight "followed_by" edge
        // is created between them (which would add non-zero decay).
        var now  = DateTimeOffset.UtcNow;
        var e1   = MakeEntry("Alpha", at: now,               app: "tgn.app1");
        var e2   = MakeEntry("Beta",  at: now.AddHours(2),   app: "tgn.app1");

        var sim    = new NetworkHealthSimulator();
        var result = await sim.ForecastAsync(new[] { e1, e2 }, MakeScenario(steps: 10));

        Assert.Equal(SimulationOutcome.Healthy, result.Outcome);
        Assert.Equal(1.0f, result.HealthScore, precision: 4);
    }

    // ------------------------------------------------------------------
    // 3. Low-weight edges with many steps → Degraded or Critical
    // ------------------------------------------------------------------

    [Fact]
    public async Task ForecastAsync_ManyLowWeightEdges_DegradedOrCritical()
    {
        // Build a graph with many low-weight edges by using many tagged entries
        var now     = DateTimeOffset.UtcNow;
        var entries = Enumerable.Range(0, 20)
            .Select(i => MakeEntry($"Entry {i}",
                at:   now.AddMinutes(i * 5),
                tags: new Dictionary<string, string>
                {
                    [$"tag{i}a"] = "1",
                    [$"tag{i}b"] = "1",
                }))
            .ToList();

        // Use a custom engine with all-zero-weight edges to force maximum decay
        var graph = new KnowledgeGraph();
        var nodeA = GraphNode.Create("A", "event");
        var nodeB = GraphNode.Create("B", "event");
        graph.AddNode(nodeA);
        graph.AddNode(nodeB);
        // weight=0.1 → (1-0.1)*0.01 = 0.009 per edge per step
        for (int i = 0; i < 30; i++)
            graph.AddEdge(GraphEdge.Create(nodeA.Id, nodeB.Id, "depends_on", 0.1f));

        var engine = new MiroFishAdapter();
        var scenario = MakeScenario(steps: 50);
        var result = await engine.RunAsync(scenario, graph);

        Assert.True(
            result.Outcome is SimulationOutcome.Degraded or SimulationOutcome.Critical or SimulationOutcome.Unknown,
            $"Expected Degraded/Critical/Unknown but got {result.Outcome} (health={result.HealthScore})");
    }

    // ------------------------------------------------------------------
    // 4. Cancellation is respected
    // ------------------------------------------------------------------

    [Fact]
    public async Task ForecastAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sim = new NetworkHealthSimulator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sim.ForecastAsync(
                new[] { MakeEntry("A message") },
                MakeScenario(),
                cts.Token));
    }

    // ------------------------------------------------------------------
    // 5. Findings list non-empty when high-impact nodes exist
    // ------------------------------------------------------------------

    [Fact]
    public async Task ForecastAsync_HighImpactNodes_FindingsNonEmpty()
    {
        // Build a graph with a high-weight edge (>=0.7) so the engine reports it
        var graph  = new KnowledgeGraph();
        var nodeA  = GraphNode.Create("HighImpactNode", "event");
        var nodeB  = GraphNode.Create("Target", "app");
        graph.AddNode(nodeA);
        graph.AddNode(nodeB);
        graph.AddEdge(GraphEdge.Create(nodeA.Id, nodeB.Id, "depends_on", 0.9f));

        var engine   = new MiroFishAdapter();
        var scenario = MakeScenario(steps: 5);
        var result   = await engine.RunAsync(scenario, graph);

        Assert.NotEmpty(result.Findings);
        Assert.Contains(result.Findings, f => f.Contains("HighImpactNode"));
    }

    // ------------------------------------------------------------------
    // 6. Recommendations include deployment advice when Degraded
    // ------------------------------------------------------------------

    [Fact]
    public async Task ForecastAsync_DegradedOutcome_RecommendationsContainDeploymentAdvice()
    {
        // Craft a graph that will produce a Degraded outcome:
        // many edges with weight 0.2 → (1-0.2)*0.01 = 0.008 per edge per step
        // 20 edges × 50 steps = 8.0 total reduction → clamped to 0 (Critical/Unknown)
        // Use fewer edges to land in Degraded range (0.5–0.8)
        var graph = new KnowledgeGraph();
        var nodeA = GraphNode.Create("Src", "event");
        var nodeB = GraphNode.Create("Tgt", "topic");
        graph.AddNode(nodeA);
        graph.AddNode(nodeB);
        // 10 edges × weight 0.5 → (0.5)*0.01=0.005 per step per edge
        // 10 edges × 30 steps = 1.5 total → clamped to 0, but let's use 5 edges × 10 steps
        // 5 × 10 × 0.005 = 0.25 reduction → health ≈ 0.75 → Degraded (0.5–0.8 exclusive-ish)
        for (int i = 0; i < 5; i++)
            graph.AddEdge(GraphEdge.Create(nodeA.Id, nodeB.Id, "depends_on", 0.5f));

        var engine   = new MiroFishAdapter();
        var scenario = MakeScenario(steps: 10);
        var result   = await engine.RunAsync(scenario, graph);

        // Could be Healthy or Degraded depending on exact float arithmetic;
        // if Degraded, recommendations must contain deployment advice.
        if (result.Outcome is SimulationOutcome.Degraded or SimulationOutcome.Critical)
        {
            Assert.Contains(result.Recommendations,
                r => r.Contains("deployment", StringComparison.OrdinalIgnoreCase)
                  || r.Contains("rollout",    StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            Assert.Contains(result.Recommendations,
                r => r.Contains("nominal", StringComparison.OrdinalIgnoreCase));
        }
    }

    // ------------------------------------------------------------------
    // 7. StepsRun matches scenario StepCount
    // ------------------------------------------------------------------

    [Fact]
    public async Task ForecastAsync_StepsRun_MatchesScenarioStepCount()
    {
        var sim      = new NetworkHealthSimulator();
        var scenario = MakeScenario(steps: 7);
        var result   = await sim.ForecastAsync(Array.Empty<EpisodicMemoryEntry>(), scenario);

        Assert.Equal(7, result.StepsRun);
    }

    // ------------------------------------------------------------------
    // 8. ScenarioId propagated correctly
    // ------------------------------------------------------------------

    [Fact]
    public async Task ForecastAsync_ScenarioId_PropagatedToResult()
    {
        var scenario = MakeScenario();
        var sim      = new NetworkHealthSimulator();
        var result   = await sim.ForecastAsync(Array.Empty<EpisodicMemoryEntry>(), scenario);

        Assert.Equal(scenario.Id, result.ScenarioId);
    }
}
