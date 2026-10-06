// NullImplementations.cs — (3.0.0)

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Research;

public sealed class NullResearchCorpus : IResearchCorpus
{
    public static readonly NullResearchCorpus Instance = new();
    public string BackendId => "null";
    public ValueTask<ResearchPaper?> GetAsync(string id, CancellationToken ct = default) => ValueTask.FromResult<ResearchPaper?>(null);
    public ValueTask<IReadOnlyList<ResearchPaper>> SearchAsync(string q, int topK = 10, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<ResearchPaper>>(Array.Empty<ResearchPaper>());
}

public sealed class NullPaperRetrieval : IPaperRetrieval
{
    public static readonly NullPaperRetrieval Instance = new();
    public string BackendId => "null";
    public ValueTask<ReadOnlyMemory<byte>?> FetchFullTextAsync(string id, CancellationToken ct = default) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
}

public sealed class NullCitationGraph : ICitationGraph
{
    public static readonly NullCitationGraph Instance = new();
    public string BackendId => "null";
    public ValueTask<IReadOnlyList<Citation>> ForwardCitationsAsync(string id, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<Citation>>(Array.Empty<Citation>());
    public ValueTask<IReadOnlyList<Citation>> BackwardCitationsAsync(string id, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<Citation>>(Array.Empty<Citation>());
}
