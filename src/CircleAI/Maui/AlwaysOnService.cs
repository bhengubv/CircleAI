// AlwaysOnService.cs
//
// (Phase A4 / A5) Always-on listener for the wake-word detector. Wraps
// the platform-specific lifecycle that keeps a microphone open + a tiny
// CNN running even when the app is backgrounded or the screen is off.
//
// Android:   sticky ForegroundService with the `microphone` service type.
// iOS:       AudioSession "voip" category + background audio entitlement.
// Windows/macCatalyst/headless: runs in-process as a hosted service (no
// special OS plumbing is required — the .NET Generic Host keeps the
// process alive).

using System;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Voice;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
#endif

#if IOS || MACCATALYST
using AVFoundation;
using Foundation;
using UIKit;

#endif

namespace CircleAI.Maui;


/// <summary>
/// (Phase A4 / A5) The platform-agnostic, hosted always-on listener. DI
/// registers <see cref="IWakeWordDetector"/> (typically
/// <see cref="KwsWakeWordDetector"/>) and <see cref="AlwaysOnService"/>;
/// the hosting layer starts/stops the service over the process lifetime.
/// </summary>
public sealed class AlwaysOnService : IHostedService, IAsyncDisposable
{
    private readonly IWakeWordDetector _wakeWord;
    private readonly ILogger<AlwaysOnService> _logger;
    private bool _running;

#if ANDROID
    private Intent? _foregroundIntent;
#endif

#if IOS || MACCATALYST
    private NSObject? _interruptionToken;
#endif

    public AlwaysOnService(IWakeWordDetector wakeWord, ILogger<AlwaysOnService> logger)
    {
        _wakeWord = wakeWord ?? throw new ArgumentNullException(nameof(wakeWord));
        _logger   = logger   ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>True when the listener is currently engaged.</summary>
    public bool IsRunning => _running;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_running) return;
        _running = true;

#if ANDROID
        StartAndroidForegroundService();
#elif IOS || MACCATALYST
        StartIosBackgroundAudio();
#endif

        try
        {
            await _wakeWord.StartAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("[AlwaysOnService] wake-word listener started (phrase={Phrase}).", _wakeWord.WakeWord);
        }
        catch
        {
            _running = false;
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_running) return;
        _running = false;

        try { await _wakeWord.StopAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "[AlwaysOnService] wake-word stop failed"); }

#if ANDROID
        StopAndroidForegroundService();
#elif IOS || MACCATALYST
        StopIosBackgroundAudio();
#endif
        _logger.LogInformation("[AlwaysOnService] stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { /* tear-down */ }
        await _wakeWord.DisposeAsync().ConfigureAwait(false);
    }

    // ── Android: sticky foreground service ─────────────────────────────

#if ANDROID
    private void StartAndroidForegroundService()
    {
        try
        {
            var context = Android.App.Application.Context;
            if (context is null) return;
            // A foreground service the app holds no permission for is not a quiet
            // failure. StartForeground throws inside the service, whose catch only
            // writes to Debug, and Android then kills the whole app because the
            // service never reached the foreground. So check first, say which one is
            // missing, and listen without the anchor - it still hears while the app
            // is in front.
            var missing = MissingForegroundPermission(context);
            if (missing is not null)
            {
                _logger.LogWarning(
                    "[AlwaysOnService] {Permission} is not granted, so the listener runs without its foreground service and stops when Android puts the app away.",
                    missing);
                return;
            }
            _foregroundIntent = new Intent(context, typeof(CircleAlwaysOnAndroidService));
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                context.StartForegroundService(_foregroundIntent);
            else
                context.StartService(_foregroundIntent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AlwaysOnService] Could not start Android foreground service.");
        }
    }

    private void StopAndroidForegroundService()
    {
        try
        {
            if (_foregroundIntent is null) return;
            var context = Android.App.Application.Context;
            context?.StopService(_foregroundIntent);
            _foregroundIntent.Dispose();
            _foregroundIntent = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AlwaysOnService] Could not stop Android foreground service.");
        }
    }

    /// <summary>The first permission a microphone foreground service needs that this app lacks, or null.</summary>
    /// <remarks>
    /// Android 9 (API 28) gates <c>startForeground</c> on FOREGROUND_SERVICE; Android 14
    /// (API 34) adds the per-type FOREGROUND_SERVICE_MICROPHONE, and RECORD_AUDIO itself.
    /// </remarks>
    private static string? MissingForegroundPermission(Context context)
    {
        string[] needed = (int)Build.VERSION.SdkInt switch
        {
            >= 34 =>
            [
                "android.permission.FOREGROUND_SERVICE",
                "android.permission.FOREGROUND_SERVICE_MICROPHONE",
                "android.permission.RECORD_AUDIO",
            ],
            >= 28 => ["android.permission.FOREGROUND_SERVICE"],
            _ => [],
        };
        foreach (var permission in needed)
        {
            if (context.CheckSelfPermission(permission) != global::Android.Content.PM.Permission.Granted)
                return permission;
        }
        return null;
    }
#endif

    // ── iOS / macCatalyst: voip + background audio entitlement ─────────

#if IOS || MACCATALYST
    private void StartIosBackgroundAudio()
    {
        try
        {
            var session = AVAudioSession.SharedInstance();
            session.SetCategory(
                AVAudioSessionCategory.PlayAndRecord,
                AVAudioSessionCategoryOptions.MixWithOthers
                | AVAudioSessionCategoryOptions.AllowBluetooth);
            session.SetActive(true);

            _interruptionToken = AVAudioSession.Notifications.ObserveInterruption(OnAudioInterruption);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AlwaysOnService] Could not configure iOS background audio session.");
        }
    }

    private void StopIosBackgroundAudio()
    {
        try
        {
            _interruptionToken?.Dispose();
            _interruptionToken = null;
            AVAudioSession.SharedInstance().SetActive(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AlwaysOnService] Could not deactivate iOS audio session.");
        }
    }

    private void OnAudioInterruption(object? sender, AVAudioSessionInterruptionEventArgs e)
    {
        if (e.InterruptionType == AVAudioSessionInterruptionType.Began)
        {
            _logger.LogInformation("[AlwaysOnService] iOS audio interruption began (call / siri).");
        }
        else if (e.InterruptionType == AVAudioSessionInterruptionType.Ended)
        {
            try { AVAudioSession.SharedInstance().SetActive(true); }
            catch (Exception ex) { _logger.LogWarning(ex, "[AlwaysOnService] iOS audio resume failed"); }
        }
    }
#endif
}

#if ANDROID
/// <summary>
/// (Phase A4) Android <see cref="Service"/> that runs as a sticky foreground
/// notification so the OS doesn't kill our microphone listener when the app
/// is backgrounded. The actual wake-word detection runs in the in-process
/// <see cref="AlwaysOnService"/>; this class is just the OS lifecycle anchor.
/// </summary>
/// <remarks>
/// IT REGISTERS ITSELF, as it did in CircleAI.Maui: the build of every Android app
/// that references CircleAI writes this service into the app's manifest, so an app
/// that runs <see cref="AlwaysOnService"/> gets it without a manifest line of its
/// own. It is not exported, and nothing starts it but <see cref="AlwaysOnService"/>.
/// An app that will never listen drops it by name -
/// <c>&lt;service android:name="ai.circle.AlwaysOnAndroidService" tools:node="remove" /&gt;</c> -
/// as the thin sample app does with the service's other components.
/// </remarks>
[Service(
    Name           = "ai.circle.AlwaysOnAndroidService",
    Exported       = false,
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeMicrophone)]
public sealed class CircleAlwaysOnAndroidService : Service
{
    public const string ChannelId   = "circleai-always-on";
    public const string ChannelName = "Circle AI always-on listener";
    public const int    NotificationId = 0xC1A1;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        try
        {
            EnsureChannel();
            var notification = BuildNotification();
            if (notification is not null)
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                {
                    StartForeground(NotificationId, notification,
                        global::Android.Content.PM.ForegroundService.TypeMicrophone);
                }
                else
                {
                    StartForeground(NotificationId, notification);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CircleAlwaysOnAndroidService] start failed: {ex.Message}");
        }
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }

    private void EnsureChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var nm = (NotificationManager?)GetSystemService(NotificationService);
        if (nm is null) return;
        if (nm.GetNotificationChannel(ChannelId) is not null) return;
        var channel = new NotificationChannel(ChannelId, ChannelName, NotificationImportance.Low)
        {
            Description = "Keeps Circle AI listening for the wake word."
        };
        nm.CreateNotificationChannel(channel);
    }

    private Notification? BuildNotification()
    {
        var context = this;
        var builder = new NotificationCompat.Builder(context, ChannelId)
            .SetContentTitle("Circle AI is listening")
            .SetContentText("Say \"hey B\" anywhere to talk.")
            .SetSmallIcon(Android.Resource.Drawable.StatNotifyVoicemail)
            .SetOngoing(true)
            .SetSilent(true);
        return builder.Build();
    }
}
#endif
