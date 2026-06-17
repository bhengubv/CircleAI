// Contracts.cs
//
// (2.5.0) The full Tools.Catalog contract surface — composio pattern-port.
// Complements (does not replace) the lightweight IToolCatalog shipped in
// 2.0.3 inside CircleAI.Hosting. Real providers (Gmail / Slack / Linear /
// Stripe / Notion) land in 2.5.1 when the connectors are vendored.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Tools.Catalog;

/// <summary>How the provider authenticates.</summary>
public enum AuthKind { None, ApiKey, BearerToken, OAuth2, Basic, Custom }

/// <summary>One provider in the catalog (Gmail, Slack, Linear, …).</summary>
public sealed record ProviderDescriptor(
    string                      ProviderId,
    string                      DisplayName,
    string                      Description,
    string?                     Homepage,
    AuthKind                    Auth,
    IReadOnlyList<string>       Tags,
    IReadOnlyList<string>       Capabilities,
    OAuth2Descriptor?           OAuth2 = null);

/// <summary>OAuth2 configuration when <see cref="ProviderDescriptor.Auth"/> = OAuth2.</summary>
public sealed record OAuth2Descriptor(
    string                AuthorizeUrl,
    string                TokenUrl,
    IReadOnlyList<string> Scopes,
    string?               UserInfoUrl = null);

/// <summary>One stored credential for one user / one provider.</summary>
public sealed record CredentialBundle(
    string                              ProviderId,
    string                              UserId,
    IReadOnlyDictionary<string, string> Fields,
    DateTimeOffset?                     ExpiresAtUtc = null);

/// <summary>A quota / rate-limit policy on one (provider, user) pair.</summary>
public sealed record QuotaPolicy(
    string ProviderId,
    string UserId,
    int    DailyCallBudget,
    int    MaxConcurrent,
    int    PerMinuteCap);

/// <summary>(2.5.0) The provider directory.</summary>
public interface IProviderCatalog
{
    string BackendId { get; }

    ValueTask<IReadOnlyList<ProviderDescriptor>> ListProvidersAsync(CancellationToken ct = default);
    ValueTask<ProviderDescriptor?> GetProviderAsync(string providerId, CancellationToken ct = default);

    /// <summary>Semantic search over the registered providers.</summary>
    ValueTask<IReadOnlyList<ProviderDescriptor>> SearchProvidersAsync(
        string            query,
        int               topK = 8,
        CancellationToken ct   = default);
}

/// <summary>(2.5.0) Credential storage. Implementations must encrypt at rest.</summary>
public interface ICredentialStore
{
    string BackendId { get; }

    ValueTask UpsertAsync(CredentialBundle bundle, CancellationToken ct = default);
    ValueTask<CredentialBundle?> GetAsync(string providerId, string userId, CancellationToken ct = default);
    ValueTask DeleteAsync(string providerId, string userId, CancellationToken ct = default);
}

/// <summary>(2.5.0) OAuth2 flow driver. Lets the catalog initiate + complete a 3-legged flow.</summary>
public interface IOAuth2FlowDriver
{
    string BackendId { get; }

    /// <summary>Build the redirect URL for the user's browser.</summary>
    ValueTask<string> StartAsync(string providerId, string userId, string redirectUri, CancellationToken ct = default);

    /// <summary>Exchange the authorisation code returned to the redirect URI for a credential bundle.</summary>
    ValueTask<CredentialBundle> CompleteAsync(
        string            providerId,
        string            userId,
        string            authorizationCode,
        string            redirectUri,
        CancellationToken ct = default);
}

/// <summary>(2.5.0) Per-(provider,user) quota enforcement.</summary>
public interface IQuotaGuard
{
    string BackendId { get; }

    ValueTask<bool> TryAcquireAsync(string providerId, string userId, CancellationToken ct = default);
    ValueTask SetPolicyAsync(QuotaPolicy policy, CancellationToken ct = default);
    ValueTask<QuotaPolicy?> GetPolicyAsync(string providerId, string userId, CancellationToken ct = default);
}

/// <summary>(2.5.0) Namespace partition — keep one user's tool list separate from the next.</summary>
public sealed record ToolNamespace(string NamespaceId, string OwnerUserId, IReadOnlyList<string> ProviderIds);

public interface IToolNamespaceStore
{
    string BackendId { get; }

    ValueTask UpsertAsync(ToolNamespace ns, CancellationToken ct = default);
    ValueTask<ToolNamespace?> GetAsync(string namespaceId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<ToolNamespace>> ListForUserAsync(string userId, CancellationToken ct = default);
}
