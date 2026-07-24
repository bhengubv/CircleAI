// DlnaCastEngine.cs — (3.5.0) The one type most callers touch: discover TVs on the LAN,
// then fling a CircleAI-generated asset at one. Wires an SSDP discovery to a per-target
// LAN-bound TcpMediaHost so byte/file media "just work". De-Googled + offline by
// construction — UPnP/DLNA only, nothing leaves the network.
//
// DI wiring (registering ICastEngine, choosing a real IDocumentCastAdapter, etc.) belongs
// in the hosting layer; this library stays dependency-free so any host can consume it.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Cast.Dlna;
using CircleAI.Cast.Http;
using CircleAI.Cast.Net;

namespace CircleAI.Cast;

/// <summary>High-level casting entry point. De-Googled: open UPnP/DLNA over the LAN only.</summary>
public interface ICastEngine
{
    /// <summary>Backend self-identification — "dlna", "null".</summary>
    string BackendId { get; }

    /// <summary>Discover renderers on the LAN over the given search window.</summary>
    IAsyncEnumerable<ICastTarget> DiscoverAsync(TimeSpan searchWindow, CancellationToken ct = default);

    /// <summary>Connect to <paramref name="target"/>, load <paramref name="media"/>, and start playback.</summary>
    ValueTask<ICastSession> CastAsync(ICastTarget target, CastMedia media, CancellationToken ct = default);
}

/// <summary>DLNA implementation of <see cref="ICastEngine"/>.</summary>
public sealed class DlnaCastEngine : ICastEngine, IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly DlnaCastDiscovery _discovery;
    private readonly Dictionary<string, TcpMediaHost> _hostsByBind = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public string BackendId => "dlna";

    /// <param name="http">Optional shared HttpClient. When null, the engine owns one and disposes it.</param>
    public DlnaCastEngine(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _ownsHttp = http is null;
        _discovery = new DlnaCastDiscovery(_http, HostForTarget);
    }

    public IAsyncEnumerable<ICastTarget> DiscoverAsync(TimeSpan searchWindow, CancellationToken ct = default)
        => _discovery.DiscoverAsync(searchWindow, ct);

    public async ValueTask<ICastSession> CastAsync(ICastTarget target, CastMedia media, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(media);

        var session = await target.ConnectAsync(ct).ConfigureAwait(false);
        try
        {
            await session.LoadAsync(media, ct).ConfigureAwait(false);
            await session.PlayAsync(ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // One media host per LAN bind address, created on first use and reused thereafter.
    private ILocalMediaHost? HostForTarget(ICastTarget target)
    {
        IPAddress bind = ResolveBind(target);
        string key = bind.ToString();
        lock (_gate)
        {
            if (_hostsByBind.TryGetValue(key, out var existing)) return existing;
            var host = new TcpMediaHost(bind);
            _hostsByBind[key] = host;
            return host;
        }
    }

    private static IPAddress ResolveBind(ICastTarget target)
        => IPAddress.TryParse(target.Location.Host, out var ip)
            ? LocalAddress.ForRoute(ip)
            : (LocalAddress.FirstPrivateV4() ?? IPAddress.Loopback);

    public async ValueTask DisposeAsync()
    {
        List<TcpMediaHost> hosts;
        lock (_gate)
        {
            hosts = new List<TcpMediaHost>(_hostsByBind.Values);
            _hostsByBind.Clear();
        }

        foreach (var host in hosts)
        {
            try { await host.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { /* best-effort teardown */ }
        }

        if (_ownsHttp) _http.Dispose();
    }
}
