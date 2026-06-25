// InMemoryDocumentTracker.cs
//
// (3.3.0) Real in-memory IDocumentTracker + IDocumentInsights. Records
// every view in a thread-safe list and computes insights on demand.
// Hosts that need durability swap in a database-backed implementation
// behind the same contract.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.DocAnalytics;

/// <summary>(3.3.0) Thread-safe in-memory document tracker.</summary>
public sealed class InMemoryDocumentTracker : IDocumentTracker, IDocumentInsights
{
    private readonly ConcurrentDictionary<string, List<DocumentView>> _byDoc = new(StringComparer.Ordinal);
    private readonly object _writeLock = new();

    public string BackendId => "in-memory";

    public ValueTask RecordViewAsync(DocumentView view, CancellationToken ct = default)
    {
        if (view is null) throw new ArgumentNullException(nameof(view));
        if (string.IsNullOrWhiteSpace(view.DocumentId)) throw new ArgumentException("DocumentId required", nameof(view));
        lock (_writeLock)
        {
            var list = _byDoc.GetOrAdd(view.DocumentId, _ => new List<DocumentView>());
            list.Add(view);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<DocumentView>> ListViewsAsync(string documentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(documentId)) throw new ArgumentException("documentId required", nameof(documentId));
        lock (_writeLock)
        {
            if (!_byDoc.TryGetValue(documentId, out var views))
                return ValueTask.FromResult<IReadOnlyList<DocumentView>>(Array.Empty<DocumentView>());
            return ValueTask.FromResult<IReadOnlyList<DocumentView>>(views.ToArray());
        }
    }

    public ValueTask<DocumentInsight?> ComputeAsync(string documentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(documentId)) throw new ArgumentException("documentId required", nameof(documentId));
        lock (_writeLock)
        {
            if (!_byDoc.TryGetValue(documentId, out var views) || views.Count == 0)
                return ValueTask.FromResult<DocumentInsight?>(null);

            var total      = views.Count;
            var unique     = views.Select(v => v.ViewerId).Distinct(StringComparer.Ordinal).Count();
            var avgSeconds = views.Average(v => v.Duration.TotalSeconds);

            return ValueTask.FromResult<DocumentInsight?>(new DocumentInsight(
                DocumentId:         documentId,
                TotalViews:         total,
                UniqueViewers:      unique,
                AvgDurationSeconds: avgSeconds));
        }
    }
}
