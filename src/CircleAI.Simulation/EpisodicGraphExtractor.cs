using CircleAI.Memory;

namespace CircleAI.Simulation;

/// <summary>
/// Extracts a <see cref="KnowledgeGraph"/> from a list of <see cref="EpisodicMemoryEntry"/>
/// records using keyword and tag heuristics. Fully offline — no LLM dependency.
/// </summary>
/// <remarks>
/// Extraction rules applied, in order:
/// <list type="number">
///   <item>Each entry becomes an "event" node (Label = first 60 characters of UserText).</item>
///   <item>Each tag key becomes a "topic" node; an edge event → topic with relation "tagged_with" and weight 1.0 is added.</item>
///   <item>AppContext becomes an "app" node; an edge event → app with relation "occurred_in" and weight 1.0 is added.</item>
///   <item>Consecutive entries within 1 hour are connected via a "followed_by" edge with weight 0.5.</item>
/// </list>
/// </remarks>
public sealed class EpisodicGraphExtractor : IGraphBuilder
{
    /// <inheritdoc/>
    public KnowledgeGraph Build(IReadOnlyList<EpisodicMemoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var graph      = new KnowledgeGraph();
        var appNodes   = new Dictionary<string, GraphNode>(StringComparer.OrdinalIgnoreCase);
        var topicNodes = new Dictionary<string, GraphNode>(StringComparer.OrdinalIgnoreCase);
        GraphNode?     prev     = null;
        DateTimeOffset prevTime = DateTimeOffset.MinValue;

        foreach (var entry in entries.OrderBy(e => e.RecordedAtUtc))
        {
            var label  = entry.UserText.Length > 60 ? entry.UserText[..60] : entry.UserText;
            var evNode = GraphNode.Create(label, "event",
                new Dictionary<string, string> { ["episode_id"] = entry.Id.ToString() });
            graph.AddNode(evNode);

            // App context → node + edge
            if (!string.IsNullOrWhiteSpace(entry.AppContext))
            {
                if (!appNodes.TryGetValue(entry.AppContext, out var appNode))
                {
                    appNode = GraphNode.Create(entry.AppContext, "app");
                    appNodes[entry.AppContext] = appNode;
                    graph.AddNode(appNode);
                }
                graph.AddEdge(GraphEdge.Create(evNode.Id, appNode.Id, "occurred_in"));
            }

            // Tags → topic nodes + edges
            if (entry.Tags != null)
            {
                foreach (var tag in entry.Tags.Keys)
                {
                    if (!topicNodes.TryGetValue(tag, out var topicNode))
                    {
                        topicNode = GraphNode.Create(tag, "topic");
                        topicNodes[tag] = topicNode;
                        graph.AddNode(topicNode);
                    }
                    graph.AddEdge(GraphEdge.Create(evNode.Id, topicNode.Id, "tagged_with"));
                }
            }

            // Temporal sequence — connect to previous event if within 1 hour
            if (prev is not null && (entry.RecordedAtUtc - prevTime).TotalHours <= 1.0)
                graph.AddEdge(GraphEdge.Create(prev.Id, evNode.Id, "followed_by", 0.5f));

            prev     = evNode;
            prevTime = entry.RecordedAtUtc;
        }

        return graph;
    }
}
