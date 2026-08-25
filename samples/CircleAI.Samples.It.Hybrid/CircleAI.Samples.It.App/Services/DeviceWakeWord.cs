// DeviceWakeWord.cs
//
// Microphone in, keyword out.

using CircleAI.Voice;

// GLOBAL-QUALIFIED ALIAS, because this file lives in CircleAI.Samples.It.App and
// the capture class lives in CircleAI.Samples.It.Mobile. From inside the former,
// "CircleAI.Samples.It.Mobile" binds against the enclosing namespace first and
// does not resolve; the alias says exactly which one is meant.
using AndroidAudioCapture = global::CircleAI.Samples.It.Mobile.AndroidAudioCapture;

namespace CircleAI.Samples.It.App.Services;

/// <inheritdoc />
public sealed class DeviceWakeWord : IWakeWord
{
    private const string ModelName = "KWS-Zipformer-HeyB";

    private static string StorageDir
        => Path.Combine(FileSystem.AppDataDirectory, "CircleAI", "Models");

    /// <inheritdoc />
    public async Task<bool> RequestMicrophoneAsync()
    {
        var status = await Permissions.CheckStatusAsync<Permissions.Microphone>()
            .ConfigureAwait(false);
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.Microphone>()
                .ConfigureAwait(false);
        return status == PermissionStatus.Granted;
    }

    /// <inheritdoc />
    public async Task ListenAsync(IProgress<WakeStatus> updates, CancellationToken ct)
    {
        updates.Report(new WakeStatus(WakeState.Preparing, "Say “Hey B”", "Getting ready…"));

        // RECORD_AUDIO IS A RUNTIME PERMISSION, and without it AudioRecord does
        // not fail - it hands back silence, which looks exactly like a wake word
        // that does not work. So it is checked before anything opens the mic.
        if (!await RequestMicrophoneAsync().ConfigureAwait(false))
        {
            updates.Report(new WakeStatus(WakeState.NeedsPermission,
                "Say “Hey B”", "Needs permission to hear you"));
            return;
        }

        var bundle = FindBundle();
        if (bundle is null)
        {
            updates.Report(new WakeStatus(WakeState.NotInstalled,
                "Not turned on yet", "Turn on Waking under “What it can do”"));
            return;
        }

        var heard = 0;
        try
        {
            // TWO STAGES. Stage one is generous so the wake never misses; stage
            // two throws out the ones that were the word rather than the wake -
            // "let us circle back" - by checking the phrase STARTED what was said.
            using var kws = new ConfirmedKeywordSpotter(new ZipformerKwsSpotter(bundle));

            kws.Woke += (_, d) =>
            {
                heard++;
                updates.Report(new WakeStatus(WakeState.Heard, "Heard you",
                    heard == 1 ? "Say it again to try once more" : $"{heard} times", heard));
            };

            updates.Report(new WakeStatus(WakeState.Listening,
                "Say “Hey B”", "Listening", heard));

            await using var mic = new AndroidAudioCapture();
            var pcm = new float[1600];

            await foreach (var chunk in mic.CaptureAsync(ct).ConfigureAwait(false))
            {
                // PCM16 little-endian to float in [-1, 1]. NOT scaled to the int16
                // range: KaldiFbank takes normalised samples, and multiplying here
                // is exactly the bug that made this deaf for a day.
                var samples = chunk.Length / 2;
                if (samples > pcm.Length) pcm = new float[samples];
                var span = chunk.Span;
                for (var i = 0; i < samples; i++)
                    pcm[i] = (short)(span[i * 2] | (span[i * 2 + 1] << 8)) / 32768f;

                kws.AcceptWaveform(pcm.AsSpan(0, samples));
            }
        }
        catch (OperationCanceledException)
        {
            // Leaving the screen, not a failure.
        }
        catch (Exception ex)
        {
            updates.Report(new WakeStatus(WakeState.Failed,
                "Say “Hey B”", $"Could not start listening ({ex.GetType().Name})", heard));
        }
    }

    /// <summary>The wake bundle on this device, or null.</summary>
    /// <remarks>
    /// Located by finding the encoder rather than by trusting the folder to exist:
    /// a half-finished download leaves a directory with no model in it, and that
    /// must read as "not installed" rather than crash the listener.
    /// </remarks>
    private static string? FindBundle()
    {
        try
        {
            var dir = Path.Combine(StorageDir, ModelName);
            if (!Directory.Exists(dir)) return null;

            var encoder = Directory
                .EnumerateFiles(dir, "*encoder*.onnx", SearchOption.AllDirectories)
                .FirstOrDefault();
            return encoder is null ? null : Path.GetDirectoryName(encoder);
        }
        catch
        {
            return null;
        }
    }
}
