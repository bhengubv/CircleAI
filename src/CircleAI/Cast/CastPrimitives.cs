// CastPrimitives.cs — (3.5.0) Core value types for casting CircleAI-generated
// media/documents to a local UPnP/DLNA MediaRenderer (a smart TV) over the LAN.
//
// De-Googled by design: NO Google Cast / Chromecast SDK anywhere. Everything here
// is protocol-neutral; the only backend that ships is open UPnP/DLNA (see Dlna/).
// Offline/LAN-only — nothing in this library reaches the internet.

using System;

namespace CircleAI.Cast;

/// <summary>Stable identifier for a discovered cast target (a renderer / TV).</summary>
public readonly record struct CastTargetId(string Value);

/// <summary>
/// Casting protocol family. Only <see cref="Dlna"/> ships a real backend today;
/// the enum exists so additional open protocols (never Google Cast) can slot in.
/// </summary>
public enum CastProtocol
{
    /// <summary>Open UPnP/DLNA AV MediaRenderer over the LAN — the shipped backend.</summary>
    Dlna = 0,
}

/// <summary>What kind of content is being cast — drives DIDL-Lite upnp:class + host headers.</summary>
public enum CastContentKind
{
    Image = 0,
    Audio = 1,
    Video = 2,
    /// <summary>A sequence of images played on a timer — decks, generated slides.</summary>
    SlideShow = 3,
}

/// <summary>Transport state reported by the renderer.</summary>
public enum CastPlaybackState
{
    Unknown = 0,
    Idle,
    Buffering,
    Playing,
    Paused,
    Stopped,
    Error,
}

/// <summary>
/// Where the bytes for a cast come from. A closed hierarchy — a source is exactly
/// one of: an already-reachable <see cref="Url"/>, a local <see cref="File"/>, or
/// in-memory <see cref="Bytes"/> (e.g. an asset CircleAI just generated).
/// </summary>
public abstract record CastMediaSource
{
    private CastMediaSource() { }

    /// <summary>A URL already reachable by the renderer (no local host needed).</summary>
    public sealed record Url(Uri Address) : CastMediaSource;

    /// <summary>A file on local storage (served via <c>ILocalMediaHost</c>).</summary>
    public sealed record File(string Path) : CastMediaSource;

    /// <summary>In-memory bytes (served via <c>ILocalMediaHost</c>).</summary>
    public sealed record Bytes(ReadOnlyMemory<byte> Data) : CastMediaSource;

    public static CastMediaSource FromUrl(Uri address) => new Url(address);
    public static CastMediaSource FromFile(string path) => new File(path);
    public static CastMediaSource FromBytes(ReadOnlyMemory<byte> data) => new Bytes(data);
}

/// <summary>A single castable item — the source plus the metadata a renderer needs.</summary>
public sealed record CastMedia(
    CastMediaSource Source,
    string MimeType,
    CastContentKind Kind,
    string Title = "",
    TimeSpan? Duration = null)
{
    public static CastMedia Video(CastMediaSource src, string mime = "video/mp4", string title = "", TimeSpan? duration = null)
        => new(src, mime, CastContentKind.Video, title, duration);

    public static CastMedia Image(CastMediaSource src, string mime = "image/jpeg", string title = "")
        => new(src, mime, CastContentKind.Image, title);

    public static CastMedia Audio(CastMediaSource src, string mime = "audio/mpeg", string title = "", TimeSpan? duration = null)
        => new(src, mime, CastContentKind.Audio, title, duration);
}

/// <summary>Snapshot of a renderer's transport state.</summary>
public sealed record CastStatus(
    CastPlaybackState State,
    TimeSpan Position,
    TimeSpan Duration,
    string? CurrentUri);

/// <summary>Base type for casting failures surfaced by this library.</summary>
public class CastException : Exception
{
    public CastException(string message) : base(message) { }
    public CastException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>A UPnP/AVTransport control action was rejected by the renderer.</summary>
public sealed class CastControlException : CastException
{
    public CastControlException(string message) : base(message) { }
}

/// <summary>Internal XML-escaping helper (SecurityElement.Escape can return null).</summary>
internal static class XmlText
{
    public static string Escape(string s) => System.Security.SecurityElement.Escape(s) ?? string.Empty;
}
