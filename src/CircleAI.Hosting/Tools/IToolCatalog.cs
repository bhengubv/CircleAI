// IToolCatalog.cs
//
// (2.0.3 skeleton) The searchable registry of every tool the host knows
// about. Providers register their descriptors here at startup.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Hosting.Tools;

/// <summary>
/// (2.0.3) The CircleAI tool catalog. Searchable by name, tag, and
/// natural-language query.
/// </summary>
public interface IToolCatalog
{
    /// <summary>How many tools are currently registered.</summary>
    int Count { get; }

    /// <summary>Register or replace one tool. Idempotent for same Name.</summary>
    ValueTask UpsertAsync(ToolDescriptor descriptor, CancellationToken ct = default);

    /// <summary>Remove a tool by name. Idempotent.</summary>
    ValueTask<bool> RemoveAsync(string name, CancellationToken ct = default);

    /// <summary>Get exactly one descriptor by name, or <c>null</c> when unknown.</summary>
    ValueTask<ToolDescriptor?> GetAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Enumerate every registered descriptor. Order is implementation-defined
    /// but stable within one process lifetime.
    /// </summary>
    IReadOnlyList<ToolDescriptor> List();

    /// <summary>
    /// Free-form search. v1 (this skeleton) is keyword-substring over name +
    /// description + tags. Semantic / embedding-based search ships in 2.5.0.
    /// </summary>
    IReadOnlyList<ToolDescriptor> Search(string query, int topK = 10);

    /// <summary>Filter by provider id (exact match, case-insensitive).</summary>
    IReadOnlyList<ToolDescriptor> ListByProvider(string provider);
}

/// <summary>
/// A source of tools — vendored integrations, MCP server, AetherNet peer,
/// or the optional Composio wrapper. The provider registers its tool
/// descriptors against an <see cref="IToolCatalog"/> at startup and routes
/// executions through <see cref="IToolExecutor"/>.
/// </summary>
public interface IToolProvider
{
    /// <summary>Stable provider id, e.g. <c>"local"</c> / <c>"composio"</c> / <c>"mcp"</c>.</summary>
    string ProviderId { get; }

    /// <summary>
    /// Discover every tool this provider exposes. The catalog calls this
    /// during initialisation and after refresh hints.
    /// </summary>
    ValueTask<IReadOnlyList<ToolDescriptor>> DiscoverAsync(CancellationToken ct = default);

    /// <summary>
    /// Cheap availability probe — used by the catalog to skip dead providers
    /// during search and to surface health on diagnostics endpoints.
    /// </summary>
    ValueTask<bool> IsAvailableAsync(CancellationToken ct = default);
}

/// <summary>
/// Sandboxed execution surface. Implementations route the call to the
/// owning provider, enforce arg-schema validation, and wrap the
/// underlying call in whatever isolation policy the host wants.
/// </summary>
public interface IToolExecutor
{
    /// <summary>
    /// Execute one tool call. <paramref name="argumentsJson"/> is the
    /// model-emitted JSON object; the executor validates against
    /// <see cref="ToolDescriptor.JsonSchema"/> before dispatch.
    /// </summary>
    ValueTask<ToolExecutionResult> ExecuteAsync(
        ToolDescriptor   tool,
        string           argumentsJson,
        CancellationToken ct = default);
}
