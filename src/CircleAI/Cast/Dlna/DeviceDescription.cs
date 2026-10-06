// DeviceDescription.cs — (3.5.0) Fetch + parse a UPnP device description and pull out
// the AVTransport control URL plus friendly identity. Namespace-agnostic (matches by
// element local-name) to tolerate the XML-namespace quirks real TVs ship.

using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CircleAI.Cast.Dlna;

/// <summary>Resolved MediaRenderer facts needed to identify and control it.</summary>
public sealed record RendererDescription(
    string Udn,
    string FriendlyName,
    string Manufacturer,
    string ModelName,
    Uri Location,
    Uri AvTransportControlUrl,
    Uri? IconUrl);

/// <summary>Fetches and parses a UPnP device-description document.</summary>
public static class DeviceDescription
{
    /// <summary>GET and parse the description at <paramref name="location"/>. Null on any failure.</summary>
    public static async Task<RendererDescription?> FetchAsync(HttpClient http, Uri location, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(location);

        string xml;
        try
        {
            xml = await http.GetStringAsync(location, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }

        return Parse(xml, location);
    }

    /// <summary>Parse a description document already in hand. Null if not a controllable renderer.</summary>
    public static RendererDescription? Parse(string xml, Uri location)
    {
        ArgumentNullException.ThrowIfNull(location);

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return null; }

        string Local(string name) =>
            doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim() ?? string.Empty;

        // Base URL: explicit <URLBase> if present, else the description authority.
        Uri baseUri = location;
        if (Uri.TryCreate(Local("URLBase"), UriKind.Absolute, out var ub)) baseUri = ub;

        // Locate the AVTransport service and its control URL.
        var avService = doc.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "service" &&
            (e.Elements().FirstOrDefault(c => c.Name.LocalName == "serviceType")?.Value ?? string.Empty)
                .Contains("AVTransport", StringComparison.OrdinalIgnoreCase));
        if (avService is null) return null;

        var controlPath = avService.Elements().FirstOrDefault(c => c.Name.LocalName == "controlURL")?.Value?.Trim();
        if (string.IsNullOrEmpty(controlPath)) return null;
        if (!Uri.TryCreate(baseUri, controlPath, out var controlUrl)) return null;

        var udn = Local("UDN");
        var friendly = Local("friendlyName");

        Uri? iconUrl = null;
        var iconPath = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "icon")
            ?.Elements().FirstOrDefault(c => c.Name.LocalName == "url")?.Value?.Trim();
        if (!string.IsNullOrEmpty(iconPath))
            Uri.TryCreate(baseUri, iconPath, out iconUrl);

        return new RendererDescription(
            Udn: string.IsNullOrEmpty(udn) ? location.ToString() : udn,
            FriendlyName: string.IsNullOrEmpty(friendly) ? "DLNA Renderer" : friendly,
            Manufacturer: Local("manufacturer"),
            ModelName: Local("modelName"),
            Location: location,
            AvTransportControlUrl: controlUrl,
            IconUrl: iconUrl);
    }
}
