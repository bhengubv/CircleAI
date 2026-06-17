// NullImplementations.cs — (2.9.0)

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.DocAnalytics;

public sealed class NullDocumentTracker : IDocumentTracker
{
    public static readonly NullDocumentTracker Instance = new();
    public string BackendId => "null";
    public ValueTask RecordViewAsync(DocumentView v, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<IReadOnlyList<DocumentView>> ListViewsAsync(string id, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<DocumentView>>(Array.Empty<DocumentView>());
}

public sealed class NullDocumentInsights : IDocumentInsights
{
    public static readonly NullDocumentInsights Instance = new();
    public string BackendId => "null";
    public ValueTask<DocumentInsight?> ComputeAsync(string id, CancellationToken ct = default) => ValueTask.FromResult<DocumentInsight?>(null);
}
