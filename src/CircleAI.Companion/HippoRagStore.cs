// HippoRagStore.cs
//
// (Phase D5) Real IHippoRagStore that walks the personal knowledge graph
// via Personalised PageRank for multi-hop recall.
//
// HippoRAG model (Wang et al. 2024):
//   1. Each memory item gets indexed as a node in the KG (with edges
//      derived from co-occurring entities — created by the
//      KnowledgeGraphExtractor on conversation turns).
//   2. At recall time, the query's entities seed a Personalised PageRank
//      walk over the KG. Nodes with high steady-state probability are
//      the multi-hop matches.
//   3. Top-K nodes get returned as MemoryHits with their PR mass as score.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Domain;

namespace CircleAI.Companion;

public sealed class SqliteHippoRagStore : IHippoRagStore
{
    private readonly SqliteKnowledgeGraph _kg;
    private readonly int   _walkIterations;
    private readonly float _damping;

    public SqliteHippoRagStore(SqliteKnowledgeGraph kg, int walkIterations = 32, float damping = 0.85f)
    {
        _kg             = kg ?? throw new ArgumentNullException(nameof(kg));
        _walkIterations = walkIterations;
        _damping        = damping;
    }

    public string BackendId => "sqlite-hippo-ppr";

    public ValueTask IndexAsync(MemoryItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        // The graph itself is populated by KnowledgeGraphExtractor — here we just
        // ensure the memory item exists as a node so the walker can land on it.
        _kg.AddTriple(item.Id, "memory_text", item.Text, source: item.Id, confidence: 1.0f);
        if (item.Metadata is not null)
        {
            foreach (var (k, v) in item.Metadata)
                _kg.AddTriple(item.Id, k, v, source: item.Id, confidence: 0.9f);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<MemoryHit>> MultiHopRecallAsync(string query, int topK = 5, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query required");
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK));

        // Build the adjacency list from all triples.
        var triples = _kg.AllTriples();
        if (triples.Count == 0) return ValueTask.FromResult<IReadOnlyList<MemoryHit>>(Array.Empty<MemoryHit>());

        var outgoing = new Dictionary<string, List<(string Nbr, float Conf)>>(StringComparer.Ordinal);
        var allNodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in triples)
        {
            allNodes.Add(t.Subject); allNodes.Add(t.Object);
            if (!outgoing.TryGetValue(t.Subject, out var list))
            {
                list = new List<(string, float)>();
                outgoing[t.Subject] = list;
            }
            list.Add((t.Object, t.Confidence));
        }

        // Seed personalisation vector from query terms that appear as nodes.
        var queryTerms = Regex.Split(query, "[^A-Za-z0-9]+")
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.ToLowerInvariant())
            .ToHashSet();
        var seedNodes = allNodes.Where(n => queryTerms.Contains(n.ToLowerInvariant())).ToList();
        // No query term touches the graph → there is no genuine association. Return
        // nothing rather than fabricating one from arbitrary nodes; the episodic path
        // still covers recency/similarity. Precision over noise.
        if (seedNodes.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<MemoryHit>>(Array.Empty<MemoryHit>());

        var rank = allNodes.ToDictionary(n => n, _ => 0.0, StringComparer.Ordinal);
        var seedMass = 1.0 / seedNodes.Count;
        foreach (var s in seedNodes) rank[s] = seedMass;

        // Power-iteration Personalised PageRank.
        for (var iter = 0; iter < _walkIterations; iter++)
        {
            ct.ThrowIfCancellationRequested();
            var next = allNodes.ToDictionary(n => n, _ => 0.0, StringComparer.Ordinal);
            // Random-jump component (personalisation).
            foreach (var seed in seedNodes) next[seed] += (1 - _damping) * seedMass;
            // Walk component.
            foreach (var (node, mass) in rank)
            {
                if (mass <= 0) continue;
                if (!outgoing.TryGetValue(node, out var nbrs) || nbrs.Count == 0)
                {
                    // Dangling node: redistribute via personalisation.
                    foreach (var seed in seedNodes) next[seed] += _damping * mass / seedNodes.Count;
                    continue;
                }
                // Confidence-weighted spread: a high-confidence edge carries more of the
                // walk's mass than a low-confidence (guessed) one, so a shaky belief does
                // not steer recall like a stated fact. With equal confidences this reduces
                // to the plain 1/count split.
                var totalConf = 0f;
                foreach (var (_, conf) in nbrs) totalConf += conf;
                foreach (var (nbr, conf) in nbrs)
                {
                    var weight = totalConf > 0f ? (double)conf / totalConf : 1.0 / nbrs.Count;
                    next[nbr] += _damping * mass * weight;
                }
            }
            rank = next;
        }

        // The seed nodes ARE the query's own terms — they are not recalled memories.
        // Exclude them so recall returns the associated nodes the walk reached, not
        // the query echoed back.
        var seedSet = new HashSet<string>(seedNodes, StringComparer.Ordinal);
        var hits = rank
            .Where(kv => kv.Value > 0 && !seedSet.Contains(kv.Key))
            .OrderByDescending(kv => kv.Value)
            .Take(topK)
            .Select(kv =>
            {
                var node = _kg.GetNode(kv.Key);
                var text = node?.Name ?? kv.Key;
                var item = new MemoryItem(
                    Id:       kv.Key,
                    Text:     text,
                    Metadata: node?.Properties);
                return new MemoryHit(item, (float)kv.Value);
            })
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<MemoryHit>>(hits);
    }
}
