// ICastSession.cs — (3.5.0) The send seam. A control session bound to one
// ICastTarget: load a media/document URL or bytes, then drive playback. Byte- and
// file-backed media are published through an ILocalMediaHost so the renderer can
// pull them over the LAN (the DLNA pull model). No Google Cast anywhere.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Cast;

/// <summary>
/// A control session bound to one <see cref="ICastTarget"/>. Sends a media/document
/// URL or bytes to the TV and drives transport (play/pause/stop/seek). Disposing the
/// session revokes anything it published to the local media host.
/// </summary>
public interface ICastSession : IAsyncDisposable
{
    /// <summary>The renderer this session controls.</summary>
    ICastTarget Target { get; }

    /// <summary>Load a single item into the renderer (UPnP SetAVTransportURI).</summary>
    ValueTask LoadAsync(CastMedia media, CancellationToken ct = default);

    /// <summary>Begin or resume playback.</summary>
    ValueTask PlayAsync(CancellationToken ct = default);

    /// <summary>Pause playback.</summary>
    ValueTask PauseAsync(CancellationToken ct = default);

    /// <summary>Stop playback and clear the current URI.</summary>
    ValueTask StopAsync(CancellationToken ct = default);

    /// <summary>Seek to an absolute position (best-effort; not all renderers honour it).</summary>
    ValueTask SeekAsync(TimeSpan position, CancellationToken ct = default);

    /// <summary>Query current transport state / position from the renderer.</summary>
    ValueTask<CastStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Play an ordered set of images as a slideshow, advancing every
    /// <paramref name="perImage"/>. This is the "cast a deck" path: once slides are
    /// rasterised to images (see <see cref="IDocumentCastAdapter"/>) it is a real
    /// capability built on repeated SetAVTransportURI — no extra renderer feature.
    /// </summary>
    ValueTask ShowSlideShowAsync(
        IReadOnlyList<CastMedia> images,
        TimeSpan perImage,
        CancellationToken ct = default);
}
