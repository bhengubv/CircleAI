// UpnpControlPoint.cs — (3.5.0) UPnP AVTransport SOAP control: SetAVTransportURI,
// Play, Pause, Stop, Seek, GetTransportInfo, GetPositionInfo. Also builds the DIDL-Lite
// metadata document a renderer expects. Pure BCL over HttpClient — open protocol, no SDK.

using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CircleAI.Cast.Dlna;

/// <summary>Sends AVTransport actions to one renderer's control URL.</summary>
public sealed class UpnpControlPoint
{
    private const string ServiceType = "urn:schemas-upnp-org:service:AVTransport:1";

    private readonly HttpClient _http;
    private readonly Uri _controlUrl;

    public UpnpControlPoint(HttpClient http, Uri controlUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _controlUrl = controlUrl ?? throw new ArgumentNullException(nameof(controlUrl));
    }

    public Task SetAvTransportUriAsync(Uri mediaUrl, string didlMetadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mediaUrl);
        string inner =
            "<InstanceID>0</InstanceID>" +
            "<CurrentURI>" + XmlText.Escape(mediaUrl.ToString()) + "</CurrentURI>" +
            "<CurrentURIMetaData>" + XmlText.Escape(didlMetadata ?? string.Empty) + "</CurrentURIMetaData>";
        return InvokeAsync("SetAVTransportURI", inner, ct);
    }

    public Task PlayAsync(CancellationToken ct = default)
        => InvokeAsync("Play", "<InstanceID>0</InstanceID><Speed>1</Speed>", ct);

    public Task PauseAsync(CancellationToken ct = default)
        => InvokeAsync("Pause", "<InstanceID>0</InstanceID>", ct);

    public Task StopAsync(CancellationToken ct = default)
        => InvokeAsync("Stop", "<InstanceID>0</InstanceID>", ct);

    public Task SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        string target = position.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        return InvokeAsync("Seek", "<InstanceID>0</InstanceID><Unit>REL_TIME</Unit><Target>" + target + "</Target>", ct);
    }

    public async Task<string> GetTransportStateAsync(CancellationToken ct = default)
    {
        string xml = await InvokeAsync("GetTransportInfo", "<InstanceID>0</InstanceID>", ct).ConfigureAwait(false);
        try
        {
            var doc = XDocument.Parse(xml);
            return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "CurrentTransportState")?.Value ?? "UNKNOWN";
        }
        catch (System.Xml.XmlException) { return "UNKNOWN"; }
    }

    public async Task<(TimeSpan Position, TimeSpan Duration)> GetPositionAsync(CancellationToken ct = default)
    {
        string xml = await InvokeAsync("GetPositionInfo", "<InstanceID>0</InstanceID>", ct).ConfigureAwait(false);
        try
        {
            var doc = XDocument.Parse(xml);
            var rel = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "RelTime")?.Value;
            var dur = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "TrackDuration")?.Value;
            return (ParseClock(rel), ParseClock(dur));
        }
        catch (System.Xml.XmlException) { return (TimeSpan.Zero, TimeSpan.Zero); }
    }

    private async Task<string> InvokeAsync(string action, string innerXml, CancellationToken ct)
    {
        string soap =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
            "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
            "<s:Body>" +
            "<u:" + action + " xmlns:u=\"" + ServiceType + "\">" + innerXml + "</u:" + action + ">" +
            "</s:Body></s:Envelope>";

        using var req = new HttpRequestMessage(HttpMethod.Post, _controlUrl)
        {
            Content = new StringContent(soap, Encoding.UTF8, "text/xml"),
        };
        req.Headers.TryAddWithoutValidation("SOAPACTION", "\"" + ServiceType + "#" + action + "\"");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new CastControlException(
                $"AVTransport {action} rejected by renderer: HTTP {(int)resp.StatusCode}. {Truncate(body)}");
        return body;
    }

    private static TimeSpan ParseClock(string? hhmmss)
        => TimeSpan.TryParse(hhmmss, CultureInfo.InvariantCulture, out var t) ? t : TimeSpan.Zero;

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}

/// <summary>Builds DIDL-Lite metadata for a cast item.</summary>
public static class DidlLite
{
    /// <summary>The protocolInfo string advertised for a MIME type (wildcard flags = broad TV compat).</summary>
    public static string ProtocolInfo(string mime) => "http-get:*:" + mime + ":*";

    /// <summary>A single-item DIDL-Lite document pointing at <paramref name="url"/>.</summary>
    public static string For(CastMedia media, Uri url, string protocolInfo)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(url);

        string upnpClass = media.Kind switch
        {
            CastContentKind.Image => "object.item.imageItem.photo",
            CastContentKind.Audio => "object.item.audioItem.musicTrack",
            CastContentKind.Video => "object.item.videoItem",
            CastContentKind.SlideShow => "object.item.imageItem.photo",
            _ => "object.item",
        };

        string title = XmlText.Escape(string.IsNullOrEmpty(media.Title) ? "CircleAI" : media.Title);
        string res = XmlText.Escape(url.ToString());
        string pInfo = XmlText.Escape(protocolInfo);

        return
            "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" " +
            "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
            "xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\">" +
            "<item id=\"0\" parentID=\"-1\" restricted=\"1\">" +
            "<dc:title>" + title + "</dc:title>" +
            "<upnp:class>" + upnpClass + "</upnp:class>" +
            "<res protocolInfo=\"" + pInfo + "\">" + res + "</res>" +
            "</item></DIDL-Lite>";
    }
}
