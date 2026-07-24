// ICastTarget.cs — (3.5.0) The discovery seam. ICastTarget models one local
// renderer (a smart TV); ICastDiscovery finds them on the LAN. De-Googled: the
// only shipped discovery is SSDP/UPnP multicast — no cloud registry, no Chromecast.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Cast;

/// <summary>
/// A single discovered local renderer — typically a smart TV exposing a UPnP/DLNA
/// AVTransport service on the LAN. Produced by <see cref="ICastDiscovery"/>. Opening
/// a session is how you actually push content at it.
/// </summary>
public interface ICastTarget
{
    /// <summary>Stable identity (the renderer's UDN, or its description URL as fallback).</summary>
    CastTargetId Id { get; }

    /// <summary>Human-friendly name advertised by the device (e.g. "Living Room TV").</summary>
    string FriendlyName { get; }

    string Manufacturer { get; }
    string Model { get; }

    /// <summary>Which protocol this target speaks. Currently always <see cref="CastProtocol.Dlna"/>.</summary>
    CastProtocol Protocol { get; }

    /// <summary>Device-description endpoint (the SSDP LOCATION), for diagnostics.</summary>
    Uri Location { get; }

    /// <summary>Optional device icon advertised in the description, or <c>null</c>.</summary>
    Uri? IconUri { get; }

    /// <summary>Open a control session against this renderer.</summary>
    ValueTask<ICastSession> ConnectAsync(CancellationToken ct = default);
}

/// <summary>
/// Discovers local renderers. The DLNA backend issues an SSDP M-SEARCH and resolves
/// each responder's device description — LAN multicast only, nothing leaves the
/// network. Yields <see cref="ICastTarget"/> instances as they answer.
/// </summary>
public interface ICastDiscovery
{
    /// <summary>Backend self-identification — "dlna-ssdp", "null".</summary>
    string BackendId { get; }

    /// <summary>
    /// Stream targets as they answer, until <paramref name="searchWindow"/> elapses or
    /// the token is cancelled. Duplicate responders are collapsed by identity.
    /// </summary>
    IAsyncEnumerable<ICastTarget> DiscoverAsync(
        TimeSpan searchWindow,
        CancellationToken ct = default);
}
