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
    private readonly ISettings _settings;

    /// <summary>Takes settings, because the wake phrase is configured there.</summary>
    /// <remarks>
    /// NOT FROM ISpokenLanguage. The wake phrase and the answering language are
    /// different properties: somebody can reasonably want it to wake to English
    /// and reply in Japanese. Reading the conversation language here is what made
    /// choosing a language silently change the phrase you had to say.
    /// </remarks>
    public DeviceWakeWord(ISettings settings) => _settings = settings;

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

        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
        if (!settings.WakeEnabled)
        {
            // Turned off deliberately. Said plainly rather than shown as a
            // listener that never fires.
            updates.Report(new WakeStatus(WakeState.NotInstalled,
                "Waking is off", "Turn it on in Settings."));
            return;
        }

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
            // The phrase follows settings.WakeLanguage, which is deliberately its
            // own property - see the constructor. Null means the bundle's built-in
            // English phrase, which is the right fallback: a phone that answers to
            // the wrong name is workable, one that answers to nothing is not.
            var keywords = KeywordsFor(bundle, settings.WakeLanguage);
            using var kws = new ConfirmedKeywordSpotter(new ZipformerKwsSpotter(bundle, keywords));

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

    /// <summary>
    /// A keywords file holding this language's wake phrase, or null for the
    /// bundle's built-in one.
    /// </summary>
    /// <remarks>
    /// SILENCE IS NOT AN OPTION, so this never returns a phrase the model cannot
    /// hear. Every candidate is judged against the bundle's OWN tokenizer, and
    /// today that tokenizer is 500 uppercase English sub-words with no kana and no
    /// han - so a Japanese kana phrase scores Unusable and is dropped here rather
    /// than installed as a wake word that would never fire.
    /// <para>
    /// When nothing survives, the phone keeps answering to its English name. That
    /// is wrong in a way a person can work around; a phone that answers to nothing
    /// is not. The trace says which happened, because "it stopped waking" is
    /// otherwise indistinguishable from a broken microphone.
    /// </para>
    /// </remarks>
    private static string? KeywordsFor(string bundleDirectory, string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode)) return null;

        try
        {
            var bpe = Directory
                .EnumerateFiles(bundleDirectory, "bpe.model", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (bpe is null) return null;

            var book = new WakePhraseBook(new SentencePieceTokenizer(bpe));
            var best = book.BestFor(languageCode);
            if (best is null)
            {
                VoiceTrace.Write($"kws: no wake phrase this model can hear for "
                               + $"'{languageCode}' - staying on the English name");
                return null;
            }

            if (!book.TryAdd(best.Text, out _)) return null;

            var path = Path.Combine(FileSystem.AppDataDirectory, "CircleAI",
                                    $"wake-{languageCode}.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            book.Save(path);

            VoiceTrace.Write($"kws: wake phrase for '{languageCode}': \"{best.Text}\" "
                           + $"({best.Tokens.Count} tokens, {best.Verdict})");
            return path;
        }
        catch (Exception ex)
        {
            VoiceTrace.Write($"kws: could not build a wake phrase for '{languageCode}' - {ex.GetType().Name}");
            return null;
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
