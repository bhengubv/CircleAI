// InMemoryResearch.cs
//
// (3.3.0) Real in-memory research corpus + citation graph. Search uses
// substring scoring on title + abstract; citations are a plain
// adjacency list. Hosts that need real arXiv / Semantic Scholar swap
// in a remote impl behind the same contract.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Research;

public sealed class InMemoryResearchCorpus : IResearchCorpus
{
    private readonly ConcurrentDictionary<string, ResearchPaper> _papers = new(StringComparer.Ordinal);

    public string BackendId => "in-memory";

    public void Add(ResearchPaper paper) { ArgumentNullException.ThrowIfNull(paper); _papers[paper.PaperId] = paper; }

    public ValueTask<ResearchPaper?> GetAsync(string paperId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(paperId)) throw new ArgumentException("paperId required", nameof(paperId));
        _papers.TryGetValue(paperId, out var p);
        return ValueTask.FromResult(p);
    }

    public ValueTask<IReadOnlyList<ResearchPaper>> SearchAsync(string query, int topK = 10, CancellationToken ct = default)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK));
        var hits = _papers.Values
            .Select(p => new { p, Score = Score(p, query) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.p)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<ResearchPaper>>(hits);
    }

    private static int Score(ResearchPaper p, string q)
    {
        var s = 0;
        if (p.Title?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)    s += 3;
        if (p.Abstract?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) s += 1;
        if (p.Authors is not null && p.Authors.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase))) s += 1;
        return s;
    }
}

public sealed class InMemoryPaperRetrieval : IPaperRetrieval
{
    private readonly ConcurrentDictionary<string, ReadOnlyMemory<byte>> _texts = new(StringComparer.Ordinal);

    public string BackendId => "in-memory";

    public void Add(string paperId, ReadOnlyMemory<byte> fullText)
    {
        if (string.IsNullOrWhiteSpace(paperId)) throw new ArgumentException("paperId required", nameof(paperId));
        _texts[paperId] = fullText;
    }

    public ValueTask<ReadOnlyMemory<byte>?> FetchFullTextAsync(string paperId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(paperId)) throw new ArgumentException("paperId required", nameof(paperId));
        if (!_texts.TryGetValue(paperId, out var bytes)) return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(bytes);
    }
}

public sealed class InMemoryCitationGraph : ICitationGraph
{
    private readonly ConcurrentDictionary<string, List<Citation>> _forward = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<Citation>> _backward = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public string BackendId => "in-memory";

    public void Link(Citation c)
    {
        ArgumentNullException.ThrowIfNull(c);
        lock (_lock)
        {
            _forward.GetOrAdd(c.FromPaperId, _ => new List<Citation>()).Add(c);
            _backward.GetOrAdd(c.ToPaperId,  _ => new List<Citation>()).Add(c);
        }
    }

    public ValueTask<IReadOnlyList<Citation>> ForwardCitationsAsync(string paperId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(paperId)) throw new ArgumentException("paperId required", nameof(paperId));
        lock (_lock)
        {
            if (!_forward.TryGetValue(paperId, out var l)) return ValueTask.FromResult<IReadOnlyList<Citation>>(Array.Empty<Citation>());
            return ValueTask.FromResult<IReadOnlyList<Citation>>(l.ToArray());
        }
    }

    public ValueTask<IReadOnlyList<Citation>> BackwardCitationsAsync(string paperId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(paperId)) throw new ArgumentException("paperId required", nameof(paperId));
        lock (_lock)
        {
            if (!_backward.TryGetValue(paperId, out var l)) return ValueTask.FromResult<IReadOnlyList<Citation>>(Array.Empty<Citation>());
            return ValueTask.FromResult<IReadOnlyList<Citation>>(l.ToArray());
        }
    }
}
