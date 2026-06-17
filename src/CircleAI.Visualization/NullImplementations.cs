// NullImplementations.cs — (2.8.0)

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Visualization;

public sealed class NullDashboardDefinitionStore : IDashboardDefinitionStore
{
    public static readonly NullDashboardDefinitionStore Instance = new();
    public string BackendId => "null";
    public ValueTask UpsertAsync(DashboardDefinition d, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<DashboardDefinition?> GetAsync(string id, CancellationToken ct = default) => ValueTask.FromResult<DashboardDefinition?>(null);
    public ValueTask<IReadOnlyList<DashboardDefinition>> ListAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<DashboardDefinition>>(Array.Empty<DashboardDefinition>());
}

public sealed class NullApiDocBuilder : IApiDocBuilder
{
    public static readonly NullApiDocBuilder Instance = new();
    public string BackendId => "null";
    public ValueTask<ApiDoc> BuildAsync(string s, CancellationToken ct = default)
        => ValueTask.FromResult(new ApiDoc(Guid.Empty.ToString(), "", "{}"));
}

public sealed class NullSiteBuilder : ISiteBuilder
{
    public static readonly NullSiteBuilder Instance = new();
    public string BackendId => "null";
    public ValueTask<GeneratedSite> BuildAsync(string spec, CancellationToken ct = default)
        => ValueTask.FromResult(new GeneratedSite(Guid.Empty.ToString(), new Dictionary<string, ReadOnlyMemory<byte>>()));
}
