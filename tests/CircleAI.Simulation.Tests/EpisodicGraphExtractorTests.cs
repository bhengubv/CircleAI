using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Memory;
using CircleAI.Simulation;
using Xunit;

namespace CircleAI.Simulation.Tests;

public sealed class EpisodicGraphExtractorTests
{
    private static EpisodicMemoryEntry MakeEntry(
        string userText,
        DateTimeOffset? at      = null,
        string?         app     = null,
        Dictionary<string, string>? tags = null) =>
        new()
        {
            UserText      = userText,
            AssistantText = "response",
            RecordedAtUtc = at ?? DateTimeOffset.UtcNow,
            AppContext     = app,
            Tags           = tags,
        };

    // ------------------------------------------------------------------
    // 1. Empty input
    // ------------------------------------------------------------------

    [Fact]
    public void Build_EmptyInput_ReturnsEmptyGraph()
    {
        var extractor = new EpisodicGraphExtractor();
        var graph     = extractor.Build(Array.Empty<EpisodicMemoryEntry>());

        Assert.Empty(graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    // ------------------------------------------------------------------
    // 2. Single entry, no tags, no app context
    // ------------------------------------------------------------------

    [Fact]
    public void Build_SingleEntryNoTagsNoApp_OneEventNodeNoEdges()
    {
        var extractor = new EpisodicGraphExtractor();
        var graph     = extractor.Build(new[] { MakeEntry("Hello world") });

        Assert.Single(graph.Nodes);
        Assert.Empty(graph.Edges);

        var node = graph.Nodes.Values.Single();
        Assert.Equal("event", node.Kind);
    }

    // ------------------------------------------------------------------
    // 3. Entry with AppContext
    // ------------------------------------------------------------------

    [Fact]
    public void Build_EntryWithApp_EventNodeAppNodeAndOccurredInEdge()
    {
        var extractor = new EpisodicGraphExtractor();
        var graph     = extractor.Build(new[]
        {
            MakeEntry("Searched for a route", app: "tgn.tagme")
        });

        // event node + app node
        Assert.Equal(2, graph.Nodes.Count);

        var eventNode = graph.Nodes.Values.Single(n => n.Kind == "event");
        var appNode   = graph.Nodes.Values.Single(n => n.Kind == "app");
        Assert.Equal("tgn.tagme", appNode.Label);

        // One edge: event → app, relation = occurred_in
        Assert.Single(graph.Edges);
        var edge = graph.Edges.Values.Single();
        Assert.Equal("occurred_in", edge.Relation);
        Assert.Equal(eventNode.Id, edge.SourceId);
        Assert.Equal(appNode.Id,   edge.TargetId);
    }

    // ------------------------------------------------------------------
    // 4. Entry with Tags
    // ------------------------------------------------------------------

    [Fact]
    public void Build_EntryWithTags_TopicNodesAndTaggedWithEdges()
    {
        var extractor = new EpisodicGraphExtractor();
        var tags      = new Dictionary<string, string>
        {
            ["navigation"] = "true",
            ["urgent"]     = "true",
        };
        var graph = extractor.Build(new[] { MakeEntry("Find nearest shelter", tags: tags) });

        // event + 2 topic nodes
        Assert.Equal(3, graph.Nodes.Count);

        var topicNodes = graph.Nodes.Values.Where(n => n.Kind == "topic").ToList();
        Assert.Equal(2, topicNodes.Count);
        Assert.Contains(topicNodes, n => n.Label == "navigation");
        Assert.Contains(topicNodes, n => n.Label == "urgent");

        // 2 tagged_with edges
        Assert.Equal(2, graph.Edges.Count);
        Assert.All(graph.Edges.Values, e => Assert.Equal("tagged_with", e.Relation));
        Assert.All(graph.Edges.Values, e => Assert.Equal(1.0f, e.Weight));
    }

    // ------------------------------------------------------------------
    // 5. Two entries within 1 hour → followed_by edge
    // ------------------------------------------------------------------

    [Fact]
    public void Build_TwoEntriesWithin1Hour_FollowedByEdge()
    {
        var extractor = new EpisodicGraphExtractor();
        var now       = DateTimeOffset.UtcNow;
        var entry1    = MakeEntry("First message",  at: now);
        var entry2    = MakeEntry("Second message", at: now.AddMinutes(30));

        var graph = extractor.Build(new[] { entry1, entry2 });

        // 2 event nodes only (no tags, no app)
        Assert.Equal(2, graph.Nodes.Count);

        // 1 followed_by edge
        Assert.Single(graph.Edges);
        var edge = graph.Edges.Values.Single();
        Assert.Equal("followed_by", edge.Relation);
        Assert.Equal(0.5f, edge.Weight);
    }

    // ------------------------------------------------------------------
    // 6. Two entries more than 1 hour apart → no followed_by edge
    // ------------------------------------------------------------------

    [Fact]
    public void Build_TwoEntriesMoreThan1HourApart_NoFollowedByEdge()
    {
        var extractor = new EpisodicGraphExtractor();
        var now       = DateTimeOffset.UtcNow;
        var entry1    = MakeEntry("Morning message", at: now);
        var entry2    = MakeEntry("Evening message", at: now.AddHours(2));

        var graph = extractor.Build(new[] { entry1, entry2 });

        Assert.Equal(2, graph.Nodes.Count);
        Assert.Empty(graph.Edges);
    }

    // ------------------------------------------------------------------
    // 7. Long UserText is truncated to 60 chars in label
    // ------------------------------------------------------------------

    [Fact]
    public void Build_LongUserText_TruncatesLabelTo60Chars()
    {
        var extractor = new EpisodicGraphExtractor();
        var longText  = new string('A', 100);
        var graph     = extractor.Build(new[] { MakeEntry(longText) });

        var node = graph.Nodes.Values.Single();
        Assert.Equal(60, node.Label.Length);
    }

    // ------------------------------------------------------------------
    // 8. Repeated AppContext reuses the same app node
    // ------------------------------------------------------------------

    [Fact]
    public void Build_RepeatedAppContext_ReusesSingleAppNode()
    {
        var extractor = new EpisodicGraphExtractor();
        var now       = DateTimeOffset.UtcNow;
        var e1 = MakeEntry("First",  at: now,                app: "tgn.bruh");
        var e2 = MakeEntry("Second", at: now.AddHours(2),    app: "tgn.bruh");

        var graph = extractor.Build(new[] { e1, e2 });

        // 2 event nodes + 1 shared app node
        Assert.Equal(3, graph.Nodes.Count);
        Assert.Single(graph.Nodes.Values, n => n.Kind == "app");
    }
}
