using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Circle.AI.Simulation;
using Xunit;

namespace Circle.AI.Simulation.Tests;

// ============================================================================
// GraphNode
// ============================================================================

public sealed class GraphNodeTests
{
    [Fact]
    public void Create_ProducesNonEmptyId()
    {
        var node = GraphNode.Create("Alice", "person");
        Assert.NotEqual(Guid.Empty, node.Id);
    }

    [Fact]
    public void Create_SetsLabelCorrectly()
    {
        var node = GraphNode.Create("Alice", "person");
        Assert.Equal("Alice", node.Label);
    }

    [Fact]
    public void Create_SetsKindCorrectly()
    {
        var node = GraphNode.Create("DeployService", "app");
        Assert.Equal("app", node.Kind);
    }

    [Fact]
    public void Create_NullProperties_DefaultsToEmptyDictionary()
    {
        var node = GraphNode.Create("Topic", "topic");
        Assert.NotNull(node.Properties);
        Assert.Empty(node.Properties);
    }

    [Fact]
    public void Create_WithProperties_StoresProperties()
    {
        var props = new Dictionary<string, string> { ["key"] = "value" };
        var node  = GraphNode.Create("Event", "event", props);
        Assert.Equal("value", node.Properties["key"]);
    }

    [Fact]
    public void Create_ExtractedAtIsRecentUtc()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var node   = GraphNode.Create("X", "system");
        var after  = DateTimeOffset.UtcNow.AddSeconds(1);
        Assert.InRange(node.ExtractedAt, before, after);
    }

    [Fact]
    public void TwoCreatedNodes_HaveDifferentIds()
    {
        var a = GraphNode.Create("A", "topic");
        var b = GraphNode.Create("B", "topic");
        Assert.NotEqual(a.Id, b.Id);
    }
}

// ============================================================================
// GraphEdge
// ============================================================================

public sealed class GraphEdgeTests
{
    [Fact]
    public void Create_ProducesNonEmptyId()
    {
        var src = Guid.NewGuid();
        var tgt = Guid.NewGuid();
        var edge = GraphEdge.Create(src, tgt, "mentions");
        Assert.NotEqual(Guid.Empty, edge.Id);
    }

    [Fact]
    public void Create_DefaultWeight_IsOne()
    {
        var edge = GraphEdge.Create(Guid.NewGuid(), Guid.NewGuid(), "causes");
        Assert.Equal(1.0f, edge.Weight);
    }

    [Fact]
    public void Create_WeightAboveMax_ClampsToOne()
    {
        var edge = GraphEdge.Create(Guid.NewGuid(), Guid.NewGuid(), "resolves", weight: 1.5f);
        Assert.Equal(1.0f, edge.Weight);
    }

    [Fact]
    public void Create_WeightBelowMin_ClampsToZero()
    {
        var edge = GraphEdge.Create(Guid.NewGuid(), Guid.NewGuid(), "depends_on", weight: -0.3f);
        Assert.Equal(0.0f, edge.Weight);
    }

    [Fact]
    public void Create_NominalWeight_Preserved()
    {
        var edge = GraphEdge.Create(Guid.NewGuid(), Guid.NewGuid(), "tagged_with", weight: 0.7f);
        Assert.Equal(0.7f, edge.Weight, precision: 6);
    }

    [Fact]
    public void Create_StoresSourceAndTargetIds()
    {
        var src  = Guid.NewGuid();
        var tgt  = Guid.NewGuid();
        var edge = GraphEdge.Create(src, tgt, "occurred_in");
        Assert.Equal(src, edge.SourceId);
        Assert.Equal(tgt, edge.TargetId);
    }

    [Fact]
    public void Create_StoresRelation()
    {
        var edge = GraphEdge.Create(Guid.NewGuid(), Guid.NewGuid(), "followed_by", 0.5f);
        Assert.Equal("followed_by", edge.Relation);
    }

    [Fact]
    public void Create_CreatedAtIsRecentUtc()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var edge   = GraphEdge.Create(Guid.NewGuid(), Guid.NewGuid(), "mentions");
        var after  = DateTimeOffset.UtcNow.AddSeconds(1);
        Assert.InRange(edge.CreatedAt, before, after);
    }
}

// ============================================================================
// KnowledgeGraph
// ============================================================================

public sealed class KnowledgeGraphTests
{
    [Fact]
    public void AddNode_IncreasesNodeCount()
    {
        var g    = new KnowledgeGraph();
        var node = GraphNode.Create("X", "topic");
        g.AddNode(node);
        Assert.Single(g.Nodes);
    }

    [Fact]
    public void AddEdge_IncreasesEdgeCount()
    {
        var g    = new KnowledgeGraph();
        var src  = GraphNode.Create("A", "event");
        var tgt  = GraphNode.Create("B", "topic");
        g.AddNode(src);
        g.AddNode(tgt);
        g.AddEdge(GraphEdge.Create(src.Id, tgt.Id, "tagged_with"));
        Assert.Single(g.Edges);
    }

    [Fact]
    public void ReachableFrom_StartOnly_ReturnsJustStartNode()
    {
        var g    = new KnowledgeGraph();
        var node = GraphNode.Create("Isolated", "event");
        g.AddNode(node);

        var reachable = g.ReachableFrom(node.Id);
        Assert.Single(reachable);
        Assert.Equal(node.Id, reachable[0].Id);
    }

    [Fact]
    public void ReachableFrom_ConnectedGraph_ReturnsBfsSet()
    {
        var g  = new KnowledgeGraph();
        var n1 = GraphNode.Create("N1", "event");
        var n2 = GraphNode.Create("N2", "topic");
        var n3 = GraphNode.Create("N3", "app");
        g.AddNode(n1);
        g.AddNode(n2);
        g.AddNode(n3);
        g.AddEdge(GraphEdge.Create(n1.Id, n2.Id, "tagged_with"));
        g.AddEdge(GraphEdge.Create(n2.Id, n3.Id, "occurred_in"));

        var reachable = g.ReachableFrom(n1.Id);
        var ids       = reachable.Select(n => n.Id).ToHashSet();
        Assert.Contains(n1.Id, ids);
        Assert.Contains(n2.Id, ids);
        Assert.Contains(n3.Id, ids);
    }

    [Fact]
    public void ReachableFrom_DisconnectedNode_NotReturned()
    {
        var g      = new KnowledgeGraph();
        var island = GraphNode.Create("Island", "system");
        var main   = GraphNode.Create("Main",   "event");
        g.AddNode(island);
        g.AddNode(main);
        // No edges

        var reachable = g.ReachableFrom(main.Id);
        Assert.DoesNotContain(reachable, n => n.Id == island.Id);
    }

    [Fact]
    public void Merge_CombinesNodesAndEdges()
    {
        var g1 = new KnowledgeGraph();
        var n1 = GraphNode.Create("A", "event");
        g1.AddNode(n1);

        var g2 = new KnowledgeGraph();
        var n2 = GraphNode.Create("B", "topic");
        g2.AddNode(n2);
        g2.AddEdge(GraphEdge.Create(n1.Id, n2.Id, "tagged_with"));

        g1.Merge(g2);

        Assert.Equal(2, g1.Nodes.Count);
        Assert.Single(g1.Edges);
    }

    [Fact]
    public void Merge_LastWriteWins_OnNodeIdCollision()
    {
        var sharedId = Guid.NewGuid();
        var g1 = new KnowledgeGraph();
        g1.AddNode(new GraphNode(sharedId, "Original", "event",
            new Dictionary<string, string>(), DateTimeOffset.UtcNow));

        var g2 = new KnowledgeGraph();
        g2.AddNode(new GraphNode(sharedId, "Overwritten", "event",
            new Dictionary<string, string>(), DateTimeOffset.UtcNow));

        g1.Merge(g2);

        Assert.Equal("Overwritten", g1.Nodes[sharedId].Label);
    }
}

// ============================================================================
// Fixture-contract validation — reads fixtures/graph_schema.json
// ============================================================================

public sealed class GraphSchemaFixtureTests
{
    private static readonly string FixturePath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "fixtures", "graph_schema.json"));

    [Fact]
    public void FixtureFile_Exists()
    {
        Assert.True(File.Exists(FixturePath), $"Fixture not found at: {FixturePath}");
    }

    [Fact]
    public void GraphEdge_WeightClamp_MatchesFixtureVectors()
    {
        var json  = File.ReadAllText(FixturePath);
        var doc   = JsonDocument.Parse(json);
        var vecs  = doc.RootElement.GetProperty("test_vectors");

        foreach (var vec in vecs.EnumerateArray())
        {
            var inputWeight    = vec.GetProperty("input_weight").GetSingle();
            var expectedWeight = vec.GetProperty("expected_weight").GetSingle();
            var edge           = GraphEdge.Create(Guid.NewGuid(), Guid.NewGuid(), "test", inputWeight);
            Assert.Equal(expectedWeight, edge.Weight, precision: 6);
        }
    }

    [Fact]
    public void GraphNode_Fields_MatchFixtureContract()
    {
        // Verify the C# record has all fixture-mandated fields accessible by name.
        var node = GraphNode.Create("test", "event");
        // These property accesses will fail at compile time if names diverge from the fixture.
        _ = node.Id;
        _ = node.Label;
        _ = node.Kind;
        _ = node.Properties;
        _ = node.ExtractedAt;
    }

    [Fact]
    public void GraphEdge_Fields_MatchFixtureContract()
    {
        var edge = GraphEdge.Create(Guid.NewGuid(), Guid.NewGuid(), "mentions", 0.5f);
        _ = edge.Id;
        _ = edge.SourceId;
        _ = edge.TargetId;
        _ = edge.Relation;
        _ = edge.Weight;
        _ = edge.CreatedAt;
    }
}
