// LocalDevTunnel.cs
//
// (3.3.0) Local-dev tunnel resolver. A voice loop needs an
// internet-reachable webhook URL even when running locally. This
// abstraction lets dev configurations route through Cloudflare
// Tunnel, ngrok, or a manually-pinned static URL — same interface,
// different backing.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) Resolves a public, internet-reachable URL that maps to a local port.</summary>
public interface ILocalDevTunnel
{
    /// <summary>Identifier — "cloudflare", "ngrok", "static", "null".</summary>
    string ProviderId { get; }

    /// <summary>Whether this resolver is configured/available.</summary>
    bool IsAvailable { get; }

    /// <summary>Resolve the public URL forwarding to <paramref name="localPort"/>.</summary>
    ValueTask<Uri> GetPublicUrlAsync(int localPort, CancellationToken ct = default);
}

/// <summary>(3.3.0) DI-default that throws — host wires a real tunnel.</summary>
public sealed class NullLocalDevTunnel : ILocalDevTunnel
{
    public static readonly NullLocalDevTunnel Instance = new();
    public string ProviderId   => "null";
    public bool   IsAvailable  => false;
    public ValueTask<Uri> GetPublicUrlAsync(int localPort, CancellationToken ct = default)
        => throw new InvalidOperationException(
            "No local-dev tunnel is configured. Register a CloudflareTunnel / NgrokTunnel / StaticTunnel.");
}

/// <summary>(3.3.0) Static-URL tunnel — caller supplies the public URL up front (best for CI).</summary>
public sealed class StaticLocalDevTunnel : ILocalDevTunnel
{
    private readonly Uri _publicUrl;

    public StaticLocalDevTunnel(Uri publicUrl)
    {
        _publicUrl = publicUrl ?? throw new ArgumentNullException(nameof(publicUrl));
        if (!publicUrl.IsAbsoluteUri) throw new ArgumentException("publicUrl must be absolute.", nameof(publicUrl));
    }

    public string ProviderId   => "static";
    public bool   IsAvailable  => true;
    public ValueTask<Uri> GetPublicUrlAsync(int localPort, CancellationToken ct = default)
        => ValueTask.FromResult(_publicUrl);
}

/// <summary>(3.3.0) Cloudflare Tunnel resolver. Host must point at the cloudflared output URL.</summary>
public sealed class CloudflareTunnel : ILocalDevTunnel
{
    private readonly Func<int, CancellationToken, ValueTask<Uri>> _resolver;

    public CloudflareTunnel(Func<int, CancellationToken, ValueTask<Uri>> resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public string ProviderId   => "cloudflare";
    public bool   IsAvailable  => true;
    public ValueTask<Uri> GetPublicUrlAsync(int localPort, CancellationToken ct = default)
        => _resolver(localPort, ct);
}

/// <summary>(3.3.0) ngrok tunnel resolver.</summary>
public sealed class NgrokTunnel : ILocalDevTunnel
{
    private readonly Func<int, CancellationToken, ValueTask<Uri>> _resolver;

    public NgrokTunnel(Func<int, CancellationToken, ValueTask<Uri>> resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public string ProviderId   => "ngrok";
    public bool   IsAvailable  => true;
    public ValueTask<Uri> GetPublicUrlAsync(int localPort, CancellationToken ct = default)
        => _resolver(localPort, ct);
}
