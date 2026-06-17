// NullImplementations.cs — (2.8.0)

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.CRM;

public sealed class NullContactStore : IContactStore
{
    public static readonly NullContactStore Instance = new();
    public string BackendId => "null";
    public ValueTask UpsertAsync(Contact c, CancellationToken ct = default)               => ValueTask.CompletedTask;
    public ValueTask<Contact?> GetAsync(string id, CancellationToken ct = default)        => ValueTask.FromResult<Contact?>(null);
    public ValueTask<IReadOnlyList<Contact>> SearchAsync(string q, int topK = 20, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<Contact>>(Array.Empty<Contact>());
}

public sealed class NullDealPipeline : IDealPipeline
{
    public static readonly NullDealPipeline Instance = new();
    public string BackendId => "null";
    public ValueTask UpsertAsync(Deal d, CancellationToken ct = default)             => ValueTask.CompletedTask;
    public ValueTask<Deal?> GetAsync(string id, CancellationToken ct = default)      => ValueTask.FromResult<Deal?>(null);
    public ValueTask<IReadOnlyList<Deal>> ListByStageAsync(string stage, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<Deal>>(Array.Empty<Deal>());
}

public sealed class NullActivityLog : IActivityLog
{
    public static readonly NullActivityLog Instance = new();
    public string BackendId => "null";
    public ValueTask AppendAsync(Activity a, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<IReadOnlyList<Activity>> ReadForContactAsync(string c, int limit = 100, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<Activity>>(Array.Empty<Activity>());
}
