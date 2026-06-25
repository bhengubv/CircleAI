// LocationBridge.cs
//
// (Phase C6) Periodically pulls the device's GPS position and emits a
// check-in to the user-supplied IChildSafetyBoard (which already exposes
// RecordCheckIn + IsInsideAnyFence). On Android we use
// FusedLocationProviderClient via the Google Play Services LocationServices
// API; on iOS we use CLLocationManager. Headless TFMs become no-ops.

using System;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Safety.Child;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#if ANDROID
using Android.Content;
using Android.Locations;
#endif

#if IOS
using CoreLocation;
using Foundation;
#endif

namespace CircleAI.Maui;

public sealed class LocationBridge : IHostedService, IAsyncDisposable
{
    private readonly IChildSafetyBoard _board;
    private readonly string _childId;
    private readonly TimeSpan _interval;
    private readonly ILogger<LocationBridge> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;

#if IOS
    private CLLocationManager? _clLocationManager;
#endif

    public LocationBridge(
        IChildSafetyBoard board,
        string childId,
        TimeSpan? interval = null,
        ILogger<LocationBridge>? logger = null)
    {
        _board    = board ?? throw new ArgumentNullException(nameof(board));
        _childId  = childId ?? throw new ArgumentNullException(nameof(childId));
        _interval = interval ?? TimeSpan.FromMinutes(5);
        _logger   = (ILogger<LocationBridge>?)logger ?? NullLogger<LocationBridge>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null) return Task.CompletedTask;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
#if IOS
        _clLocationManager = new CLLocationManager();
        _clLocationManager.RequestWhenInUseAuthorization();
#endif
        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        _logger.LogInformation("[LocationBridge] started; interval={Interval}", _interval);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null) return;
        _cts.Cancel();
        try { if (_loop is not null) await _loop.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
#if IOS
        _clLocationManager?.Dispose();
        _clLocationManager = null;
#endif
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var fix = await TryGetFixAsync(ct).ConfigureAwait(false);
                if (fix is (double lat, double lon))
                {
                    _board.RecordCheckIn(new CheckIn(
                        ChildId: _childId,
                        Status:  _board.IsInsideAnyFence(lat, lon) ? "inside-fence" : "outside-fence",
                        Lat:     lat,
                        Lon:     lon,
                        AtUtc:   DateTimeOffset.UtcNow));
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "[LocationBridge] fix failed"); }
            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async ValueTask<(double Lat, double Lon)?> TryGetFixAsync(CancellationToken ct)
    {
#if ANDROID
        return await GetAndroidFixAsync(ct).ConfigureAwait(false);
#elif IOS
        return await GetIosFixAsync(ct).ConfigureAwait(false);
#else
        await Task.CompletedTask;
        return null;
#endif
    }

#if ANDROID
    private async Task<(double, double)?> GetAndroidFixAsync(CancellationToken ct)
    {
        // Use the basic LocationManager (always available — no Play Services dependency).
        var context = global::Android.App.Application.Context;
        if (context is null) return null;
        var manager = (LocationManager?)context.GetSystemService(Context.LocationService);
        if (manager is null) return null;
        var providers = manager.GetProviders(enabledOnly: true);
        if (providers is null || providers.Count == 0) return null;
        var preferred = providers.Contains(LocationManager.GpsProvider)
            ? LocationManager.GpsProvider
            : providers[0];
        var loc = manager.GetLastKnownLocation(preferred);
        if (loc is null) return null;
        await Task.CompletedTask;
        return (loc.Latitude, loc.Longitude);
    }
#endif

#if IOS
    private async Task<(double, double)?> GetIosFixAsync(CancellationToken ct)
    {
        if (_clLocationManager is null) return null;
        var loc = _clLocationManager.Location;
        if (loc is null) return null;
        await Task.CompletedTask;
        return (loc.Coordinate.Latitude, loc.Coordinate.Longitude);
    }
#endif
}
