namespace Circle.AI.Simulation;

/// <summary>
/// An in-memory entity–relationship graph extracted from episodic memory.
/// Nodes and edges are immutable once added; graphs are composable via
/// <see cref="Merge"/>.
/// </summary>
public sealed class KnowledgeGraph
{
    private readonly Dictionary<Guid, GraphNode> _nodes = new();
    private readonly Dictionary<Guid, GraphEdge> _edges = new();

    /// <summary>Gets all nodes in the graph, keyed by their ID.</summary>
    public IReadOnlyDictionary<Guid, GraphNode> Nodes => _nodes;

    /// <summary>Gets all edges in the graph, keyed by their ID.</summary>
    public IReadOnlyDictionary<Guid, GraphEdge> Edges => _edges;

    /// <summary>Adds or replaces a node (last-write wins on ID collision).</summary>
    /// <param name="node">The node to add.</param>
    public void AddNode(GraphNode node) { ArgumentNullException.ThrowIfNull(node); _nodes[node.Id] = node; }

    /// <summary>Adds or replaces an edge (last-write wins on ID collision).</summary>
    /// <param name="edge">The edge to add.</param>
    public void AddEdge(GraphEdge edge) { ArgumentNullException.ThrowIfNull(edge); _edges[edge.Id] = edge; }

    /// <summary>Returns all edges where <paramref name="nodeId"/> is the source or target.</summary>
    /// <param name="nodeId">The node ID to query edges for.</param>
    /// <returns>All incident edges for the given node.</returns>
    public IEnumerable<GraphEdge> EdgesFor(Guid nodeId) =>
        _edges.Values.Where(e => e.SourceId == nodeId || e.TargetId == nodeId);

    /// <summary>Returns all nodes reachable from <paramref name="startId"/> by BFS (including the start node itself).</summary>
    /// <param name="startId">The ID of the node to begin BFS from.</param>
    /// <returns>An ordered list of reachable nodes.</returns>
    public IReadOnlyList<GraphNode> ReachableFrom(Guid startId)
    {
        var visited = new HashSet<Guid>();
        var queue   = new Queue<Guid>();
        queue.Enqueue(startId);
        var result = new List<GraphNode>();

        while (queue.TryDequeue(out var current))
        {
            if (!visited.Add(current)) continue;
            if (_nodes.TryGetValue(current, out var node)) result.Add(node);
            foreach (var edge in EdgesFor(current))
            {
                var next = edge.SourceId == current ? edge.TargetId : edge.SourceId;
                if (!visited.Contains(next)) queue.Enqueue(next);
            }
        }
        return result;
    }

    /// <summary>Merges another graph's nodes and edges into this graph (last-write wins on ID collision).</summary>
    /// <param name="other">The graph whose nodes and edges will be merged in.</param>
    public void Merge(KnowledgeGraph other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var n in other._nodes.Values) _nodes[n.Id] = n;
        foreach (var e in other._edges.Values) _edges[e.Id] = e;
    }
}
