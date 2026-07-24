// DlnaCastSession.cs — (3.5.0) ICastSession over UPnP AVTransport. Byte/file media are
// published through the injected ILocalMediaHost first (renderer pull model), then
// SetAVTransportURI + Play drive it. Slideshow = the real "cast a deck" leg, built by
// cycling SetAVTransportURI over an image sequence.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Cast.Dlna;

/// <summary>A live control session against one DLNA renderer.</summary>
public sealed class DlnaCastSession : ICastSession
{
    private readonly UpnpControlPoint _control;
    private readonly ILocalMediaHost? _host;
    private readonly List<Uri> _published = new();
    private Uri? _currentUrl;

    public DlnaCastSession(ICastTarget target, UpnpControlPoint control, ILocalMediaHost? host)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _host = host;
    }

    public ICastTarget Target { get; }

    public async ValueTask LoadAsync(CastMedia media, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(media);

        Uri url = await ResolveUrlAsync(media, ct).ConfigureAwait(false);
        string protocolInfo = DidlLite.ProtocolInfo(media.MimeType);
        string didl = DidlLite.For(media, url, protocolInfo);

        await _control.SetAvTransportUriAsync(url, didl, ct).ConfigureAwait(false);
        _currentUrl = url;
    }

    public ValueTask PlayAsync(CancellationToken ct = default) => new(_control.PlayAsync(ct));
    public ValueTask PauseAsync(CancellationToken ct = default) => new(_control.PauseAsync(ct));
    public ValueTask StopAsync(CancellationToken ct = default) => new(_control.StopAsync(ct));
    public ValueTask SeekAsync(TimeSpan position, CancellationToken ct = default) => new(_control.SeekAsync(position, ct));

    public async ValueTask<CastStatus> GetStatusAsync(CancellationToken ct = default)
    {
        string state = await _control.GetTransportStateAsync(ct).ConfigureAwait(false);
        (TimeSpan pos, TimeSpan dur) = await _control.GetPositionAsync(ct).ConfigureAwait(false);
        return new CastStatus(MapState(state), pos, dur, _currentUrl?.ToString());
    }

    public async ValueTask ShowSlideShowAsync(IReadOnlyList<CastMedia> images, TimeSpan perImage, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (perImage <= TimeSpan.Zero) perImage = TimeSpan.FromSeconds(8);

        foreach (var image in images)
        {
            ct.ThrowIfCancellationRequested();
            await LoadAsync(image, ct).ConfigureAwait(false);
            await PlayAsync(ct).ConfigureAwait(false);
            try { await Task.Delay(perImage, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async ValueTask<Uri> ResolveUrlAsync(CastMedia media, CancellationToken ct)
    {
        if (media.Source is CastMediaSource.Url u)
            return u.Address;

        if (_host is null)
            throw new InvalidOperationException(
                "Byte/file media requires an ILocalMediaHost so the renderer can pull it over the LAN. " +
                "Construct the session with a host (DlnaCastEngine wires one automatically).");

        Uri url = await _host.PublishAsync(media.Source, media.MimeType, ct).ConfigureAwait(false);
        _published.Add(url);
        return url;
    }

    private static CastPlaybackState MapState(string s) => s.ToUpperInvariant() switch
    {
        "PLAYING" => CastPlaybackState.Playing,
        "PAUSED_PLAYBACK" or "PAUSED" => CastPlaybackState.Paused,
        "STOPPED" => CastPlaybackState.Stopped,
        "TRANSITIONING" => CastPlaybackState.Buffering,
        "NO_MEDIA_PRESENT" => CastPlaybackState.Idle,
        _ => CastPlaybackState.Unknown,
    };

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            foreach (var url in _published)
            {
                try { await _host.UnpublishAsync(url, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { /* best-effort revoke */ }
            }
        }
        _published.Clear();
    }
}
