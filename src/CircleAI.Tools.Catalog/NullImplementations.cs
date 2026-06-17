// NullImplementations.cs
//
// (2.5.0) Fail-closed defaults for every Tools.Catalog contract.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Tools.Catalog;

public sealed class NullProviderCatalog : IProviderCatalog
{
    public static readonly NullProviderCatalog Instance = new();
    public string BackendId => "null";
    public ValueTask<IReadOnlyList<ProviderDescriptor>> ListProvidersAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<ProviderDescriptor>>(Array.Empty<ProviderDescriptor>());
    public ValueTask<ProviderDescriptor?> GetProviderAsync(string p, CancellationToken ct = default)
        => ValueTask.FromResult<ProviderDescriptor?>(null);
    public ValueTask<IReadOnlyList<ProviderDescriptor>> SearchProvidersAsync(string q, int topK = 8, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<ProviderDescriptor>>(Array.Empty<ProviderDescriptor>());
}

public sealed class NullCredentialStore : ICredentialStore
{
    public static readonly NullCredentialStore Instance = new();
    public string BackendId => "null";
    public ValueTask UpsertAsync(CredentialBundle b, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<CredentialBundle?> GetAsync(string p, string u, CancellationToken ct = default)
        => ValueTask.FromResult<CredentialBundle?>(null);
    public ValueTask DeleteAsync(string p, string u, CancellationToken ct = default) => ValueTask.CompletedTask;
}

public sealed class NullOAuth2FlowDriver : IOAuth2FlowDriver
{
    public static readonly NullOAuth2FlowDriver Instance = new();
    public string BackendId => "null";
    public ValueTask<string> StartAsync(string p, string u, string r, CancellationToken ct = default)
        => ValueTask.FromResult("about:blank");
    public ValueTask<CredentialBundle> CompleteAsync(string p, string u, string code, string redirect, CancellationToken ct = default)
        => throw new InvalidOperationException("NullOAuth2FlowDriver: no real provider wired.");
}

public sealed class NullQuotaGuard : IQuotaGuard
{
    public static readonly NullQuotaGuard Instance = new();
    public string BackendId => "null";
    public ValueTask<bool> TryAcquireAsync(string p, string u, CancellationToken ct = default) => ValueTask.FromResult(false);
    public ValueTask SetPolicyAsync(QuotaPolicy policy, CancellationToken ct = default)        => ValueTask.CompletedTask;
    public ValueTask<QuotaPolicy?> GetPolicyAsync(string p, string u, CancellationToken ct = default)
        => ValueTask.FromResult<QuotaPolicy?>(null);
}

public sealed class NullToolNamespaceStore : IToolNamespaceStore
{
    public static readonly NullToolNamespaceStore Instance = new();
    public string BackendId => "null";
    public ValueTask UpsertAsync(ToolNamespace ns, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<ToolNamespace?> GetAsync(string nsId, CancellationToken ct = default)
        => ValueTask.FromResult<ToolNamespace?>(null);
    public ValueTask<IReadOnlyList<ToolNamespace>> ListForUserAsync(string userId, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<ToolNamespace>>(Array.Empty<ToolNamespace>());
}
