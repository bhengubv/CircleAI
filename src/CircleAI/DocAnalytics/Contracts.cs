// Contracts.cs — (2.9.0) Document-analytics contracts.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.DocAnalytics;

public sealed record DocumentView(string DocumentId, string ViewerId, DateTimeOffset AtUtc, TimeSpan Duration, int PagesViewed);
public sealed record DocumentInsight(string DocumentId, int TotalViews, int UniqueViewers, double AvgDurationSeconds);

public interface IDocumentTracker
{
    string BackendId { get; }
    ValueTask RecordViewAsync(DocumentView view, CancellationToken ct = default);
    ValueTask<IReadOnlyList<DocumentView>> ListViewsAsync(string documentId, CancellationToken ct = default);
}

public interface IDocumentInsights
{
    string BackendId { get; }
    ValueTask<DocumentInsight?> ComputeAsync(string documentId, CancellationToken ct = default);
}
