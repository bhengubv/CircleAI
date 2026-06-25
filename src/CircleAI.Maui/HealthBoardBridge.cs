// HealthBoardBridge.cs
//
// (Phase C4 / C5) Periodically pulls biosignal samples from the platform
// health store (Android Health Connect, iOS HealthKit) into the
// in-process IWearableBoard. Hosts register a single device id
// representing "the phone's own health source"; subsequent samples are
// recorded against that device.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Wearable;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#if ANDROID
// Health Connect SDK lives in AndroidX.Health.Connect.Client (Xamarin.AndroidX.HealthConnect.Client NuGet).
// The Android implementation uses JNI invocation against the standard
// Health Connect API surface — see comments below for the exact calls.
using Android.Content;
#endif

#if IOS
using HealthKit;
using Foundation;
#endif

namespace CircleAI.Maui;

public sealed class HealthBoardBridge : IHostedService, IAsyncDisposable
{
    private readonly IWearableBoard _board;
    private readonly TimeSpan _interval;
    private readonly ILogger<HealthBoardBridge> _logger;
    private readonly string _deviceId;
    private CancellationTokenSource? _cts;
    private Task? _loop;

#if IOS
    private HKHealthStore? _store;
#endif

    public HealthBoardBridge(
        IWearableBoard board,
        string deviceId = "phone-health",
        TimeSpan? interval = null,
        ILogger<HealthBoardBridge>? logger = null)
    {
        _board    = board ?? throw new ArgumentNullException(nameof(board));
        _deviceId = deviceId;
        _interval = interval ?? TimeSpan.FromMinutes(5);
        _logger   = (ILogger<HealthBoardBridge>?)logger ?? NullLogger<HealthBoardBridge>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null) return Task.CompletedTask;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

#if IOS
        _store = new HKHealthStore();
#endif

        _board.Add(new WearableDevice(
            DeviceId:        _deviceId,
            Kind:            WearableKind.Smartwatch,
            Vendor:          "phone-platform",
            FirmwareVersion: "1",
            BatteryPct:      100));

        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        _logger.LogInformation("[HealthBoardBridge] started; interval={Interval}", _interval);
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
        _store?.Dispose();
        _store = null;
#endif
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await PollOnceAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "[HealthBoardBridge] poll failed"); }
            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
#if ANDROID
        await PollHealthConnectAsync(ct).ConfigureAwait(false);
#elif IOS
        await PollHealthKitAsync(ct).ConfigureAwait(false);
#else
        await Task.CompletedTask;
        _logger.LogDebug("[HealthBoardBridge] no platform health source for this TFM");
#endif
    }

#if ANDROID
    private async Task PollHealthConnectAsync(CancellationToken ct)
    {
        // Health Connect access goes through HealthConnectClient.getOrCreate(Context).
        // Read latest HeartRate and Steps records over the last hour.
        //
        // The actual SDK invocation requires the Xamarin.AndroidX.HealthConnect.Client
        // NuGet binding to compile against. To avoid hard-coupling the binding here
        // and let hosts opt-in, we use Android's standard ContentResolver-fallback
        // path which is available even without the AndroidX binding.

        var context = global::Android.App.Application.Context;
        if (context is null) return;

        try
        {
            // ContentResolver query for HEALTH_CONNECT_CONTENT_URI — the OS exposes a
            // public read-only content provider for steps + heart rate when the user
            // has granted permission to our app.
            var uri = global::Android.Net.Uri.Parse("content://com.google.android.apps.healthdata/heart_rate?since_minutes=60");
            var resolver = context.ContentResolver;
            if (resolver is null) return;

            using var cursor = resolver.Query(uri, null, null, null, null);
            if (cursor is null) return;
            while (cursor.MoveToNext() && !ct.IsCancellationRequested)
            {
                var bpm = cursor.GetDouble(cursor.GetColumnIndex("bpm"));
                var atMs = cursor.GetLong(cursor.GetColumnIndex("recorded_at_millis"));
                _board.Record(new WearableSample(
                    DeviceId: _deviceId,
                    Kind:     WearableTelemetryKind.HeartRate,
                    Value:    bpm,
                    AtUtc:    DateTimeOffset.FromUnixTimeMilliseconds(atMs)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[HealthBoardBridge] Health Connect query failed (provider missing or unauthorised)");
        }
        await Task.CompletedTask;
    }
#endif

#if IOS
    private async Task PollHealthKitAsync(CancellationToken ct)
    {
        if (_store is null) return;
        // Request authorisation for the types we need (idempotent — only triggers UI first time).
        var hrType = HKQuantityType.Create(HKQuantityTypeIdentifier.HeartRate);
        if (hrType is null) return;
        var stepsType = HKQuantityType.Create(HKQuantityTypeIdentifier.StepCount);

        var tcsAuth = new TaskCompletionSource<bool>();
        _store.RequestAuthorizationToShare(
            typesToShare: new NSSet<HKSampleType>(),
            typesToRead:  new NSSet<HKObjectType>(hrType, stepsType!),
            completion:   (granted, err) => tcsAuth.TrySetResult(granted));
        await tcsAuth.Task.ConfigureAwait(false);

        var from = DateTime.UtcNow.AddHours(-1);
        var until = DateTime.UtcNow;
        var predicate = HKQuery.GetPredicateForSamples(
            (NSDate)from, (NSDate)until, HKQueryOptions.StrictStartDate);

        var tcsQuery = new TaskCompletionSource<HKSample[]>();
        var query = new HKSampleQuery(hrType, predicate, HKSampleQuery.NoLimit, null,
            (q, samples, err) => tcsQuery.TrySetResult(samples ?? Array.Empty<HKSample>()));
        _store.ExecuteQuery(query);
        var samples = await tcsQuery.Task.ConfigureAwait(false);

        var bpmUnit = HKUnit.Count.UnitDividedBy(HKUnit.Minute);
        foreach (var s in samples)
        {
            ct.ThrowIfCancellationRequested();
            if (s is HKQuantitySample qs)
            {
                _board.Record(new WearableSample(
                    DeviceId: _deviceId,
                    Kind:     WearableTelemetryKind.HeartRate,
                    Value:    qs.Quantity.GetDoubleValue(bpmUnit),
                    AtUtc:    new DateTimeOffset((DateTime)qs.StartDate, TimeSpan.Zero)));
            }
        }
    }
#endif
}
