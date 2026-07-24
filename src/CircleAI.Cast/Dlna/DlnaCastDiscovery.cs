// DlnaCastDiscovery.cs — (3.5.0) ICastDiscovery over SSDP. M-SEARCH for MediaRenderers,
// fetch + parse each responder's description, yield a DlnaCastTarget. A per-target host
// factory lets the owner (DlnaCastEngine) inject the right LAN-bound media host lazily,
// only when a session is actually opened.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Cast.Dlna;

/// <summary>Finds DLNA renderers via SSDP multicast — LAN only, no cloud.</summary>
public sealed class DlnaCastDiscovery : ICastDiscovery
{
    private readonly HttpClient _http;
    private readonly Func<ICastTarget, ILocalMediaHost?> _hostForTarget;

    public string BackendId => "dlna-ssdp";

    /// <param name="http">Shared HttpClient (caller owns disposal). One is created if null.</param>
    /// <param name="hostForTarget">
    /// Supplies the media host used when a session for a target needs to publish byte/file
    /// media. Return null to force URL-only casting for that target.
    /// </param>
    public DlnaCastDiscovery(HttpClient? http = null, Func<ICastTarget, ILocalMediaHost?>? hostForTarget = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _hostForTarget = hostForTarget ?? (_ => null);
    }

    public async IAsyncEnumerable<ICastTarget> DiscoverAsync(
        TimeSpan searchWindow,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var resp in SsdpClient.SearchAsync(SsdpClient.MediaRendererTarget, searchWindow, ct).ConfigureAwait(false))
        {
            if (!seen.Add(resp.Location.ToString())) continue;

            RendererDescription? desc = null;
            bool cancelled = false;
            try
            {
                desc = await DeviceDescription.FetchAsync(_http, resp.Location, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { cancelled = true; }

            if (cancelled) yield break;
            if (desc is null) continue;
            if (!seen.Add(desc.Udn)) continue; // also collapse by UDN

            yield return new DlnaCastTarget(desc, CreateSession);
        }
    }

    private ICastSession CreateSession(DlnaCastTarget target)
    {
        var control = new UpnpControlPoint(_http, target.Description.AvTransportControlUrl);
        var host = _hostForTarget(target);
        return new DlnaCastSession(target, control, host);
    }
}
