// Contracts.cs — (2.8.0)

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Visualization;

public sealed record DashboardDefinition(string DashboardId, string Title, string JsonSpec);
public sealed record ApiDoc(string DocId, string Title, string OpenApiJson);
public sealed record GeneratedSite(string SiteId, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Files);

public interface IDashboardDefinitionStore
{
    string BackendId { get; }
    ValueTask UpsertAsync(DashboardDefinition d, CancellationToken ct = default);
    ValueTask<DashboardDefinition?> GetAsync(string id, CancellationToken ct = default);
    ValueTask<IReadOnlyList<DashboardDefinition>> ListAsync(CancellationToken ct = default);
}

public interface IApiDocBuilder
{
    string BackendId { get; }
    ValueTask<ApiDoc> BuildAsync(string openApiSpec, CancellationToken ct = default);
}

public interface ISiteBuilder
{
    string BackendId { get; }
    ValueTask<GeneratedSite> BuildAsync(string siteSpec, CancellationToken ct = default);
}
