#if IT_VOICE_ANDROID
#nullable enable

// ResidentWakeWord.cs
//
// Plugs the real wake detector into the resident service, which is what turns
// "listening" from a screen you have to be looking at into something the phone
// actually does.
//
// This is the whole reason CircleAI.Device declares a small IResidentListener
// instead of referencing the speech stack: the adapter lives HERE, in the head
// that already has ONNX Runtime loaded, so the chat-only build stays free of it.

using System;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Device;
using CircleAI.Voice;

namespace CircleAI.Samples.It.Mobile;

/// <summary>Adapts a wake detector to the shape the resident service holds.</summary>
public sealed class ResidentWakeWord : IResidentListener
{
    private readonly IWakeWordDetector _detector;

    public ResidentWakeWord(IWakeWordDetector detector)
    {
        _detector = detector;
        _detector.WakeWordDetected += (_, e) => Woke?.Invoke(this, e.WakeWord);
    }

    public bool IsListening => _detector.IsListening;

    /// <summary>The primary phrase, for the notification.</summary>
    public string Describe => _detector.WakeWord;

    public event EventHandler<string>? Woke;

    public Task StartAsync(CancellationToken ct = default) => _detector.StartAsync(ct);
    public Task StopAsync(CancellationToken ct = default) => _detector.StopAsync(ct);
    public ValueTask DisposeAsync() => _detector.DisposeAsync();

    /// <summary>Where a side-loaded wake bundle sits, if the owner put one there.</summary>
    public static string? SideloadedBundleFolder(Android.Content.Context c) =>
        WakeWordActivity.SideloadedBundle(c);

    /// <summary>
    /// Builds the detector this device should use and hands it to the service.
    /// </summary>
    /// <remarks>
    /// Everything about WHICH engine and HOW strict is decided by
    /// <see cref="WakeWordFactory"/> from the bundle on disk and the phone's own
    /// measurements — nothing here hard-codes a choice, which is what let the two
    /// engines drift apart with no selector in the first place.
    /// </remarks>
    public static bool Install(Android.Content.Context context, string bundleDirectory,
                               IVoiceTranscriber? transcriber = null)
    {
        try
        {
            var probe = CircleAI.Core.DeviceProbe.Snapshot();
            var host = new WakeHostCapabilities(
                probe.RamTotalBytes,
                TranscriberAvailable: transcriber is not null);

            var calibrationPath = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "CircleAI", "wake-calibration.json");

            var detector = WakeWordFactory.Create(
                new AndroidAudioCapture(),
                bundleDirectory,
                host,
                WakeCalibration.Load(calibrationPath),
                transcriber);

            CircleNeuronService.Listener = new ResidentWakeWord(detector);
            return true;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("CircleAI.Kws", "could not install resident wake word: " + ex);
            return false;
        }
    }
}
#endif
