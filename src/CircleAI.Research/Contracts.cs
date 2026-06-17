// Contracts.cs — (3.0.0) Research corpora contracts.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Research;

public sealed record ResearchPaper(string PaperId, string Title, IReadOnlyList<string> Authors, string Abstract, DateTimeOffset PublishedAtUtc, string? Doi);
public sealed record Citation(string FromPaperId, string ToPaperId, string Context);

public interface IResearchCorpus
{
    string BackendId { get; }
    ValueTask<ResearchPaper?> GetAsync(string paperId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<ResearchPaper>> SearchAsync(string query, int topK = 10, CancellationToken ct = default);
}

public interface IPaperRetrieval
{
    string BackendId { get; }
    ValueTask<ReadOnlyMemory<byte>?> FetchFullTextAsync(string paperId, CancellationToken ct = default);
}

public interface ICitationGraph
{
    string BackendId { get; }
    ValueTask<IReadOnlyList<Citation>> ForwardCitationsAsync(string paperId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<Citation>> BackwardCitationsAsync(string paperId, CancellationToken ct = default);
}
