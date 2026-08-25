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
    private readonly IWakePhrases _phrases;

    /// <summary>
    /// Takes settings for the language, and the phrase book for what to listen for.
    /// </summary>
    /// <remarks>
    /// THE LANGUAGE IS THE APP'S, NOT THE WAKE WORD'S. There used to be a separate
    /// wake language setting here, on the reasoning that the phrase and the
    /// answering language are different properties - which is true, and produced a
    /// control that let somebody run the app in English and wake it with ビーさん.
    /// Nobody wants that. What you say to wake a phone is the language you are
    /// already speaking to it in.
    /// <para>
    /// WHICH phrase is still a choice, because a language can have several and the
    /// owner can add their own; that is what <see cref="IWakePhrases"/> answers.
    /// The original bug - choosing a language silently changing the phrase - is
    /// fixed by SHOWING the phrase on the settings screen, not by detaching it.
    /// </para>
    /// </remarks>
    public DeviceWakeWord(ISettings settings, IWakePhrases phrases)
    {
        _settings = settings;
        _phrases = phrases;
    }

    private const string ModelName = "KWS-Zipformer-HeyB";

    private static string StorageDir => ModelStore.Path;

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
        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);

        // WHAT TO SAY, RESOLVED BEFORE ANYTHING IS SHOWN.
        //
        // Every line on this screen used to read "Say “Hey B”" whatever language
        // the phone was in, which is the same failure as the old wake language
        // setting seen from the other side: a phone that has been told to work in
        // Japanese telling its owner to say an English phrase.
        var chosen = await ChosenPhraseAsync(settings.Language, ct).ConfigureAwait(false);
        var say = $"Say “{chosen}”";

        updates.Report(new WakeStatus(WakeState.Preparing, say, "Getting ready…"));

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
                say, "Needs permission to hear you"));
            return;
        }

        var bundle = FindBundle();
        if (bundle is null)
        {
            // NAMED FOR WHERE IT IS NOW. This said "under What it can do", which
            // was a marketing screen carrying turn-on buttons; the buttons moved
            // to Settings and this line did not follow them for a while.
            updates.Report(new WakeStatus(WakeState.NotInstalled,
                "Not turned on yet", "Turn on Waking under Settings › Phone"));
            return;
        }

        var heard = 0;
        try
        {
            // TWO STAGES. Stage one is generous so the wake never misses; stage
            // two throws out the ones that were the word rather than the wake -
            // "let us circle back" - by checking the phrase STARTED what was said.
            // The phrase follows the app's language. Null means the bundle's
            // built-in English phrase, which is the right last resort: a phone that
            // answers to the wrong name is workable, one that answers to nothing
            // is not - but the screen says which it is rather than hiding it.
            var keywords = await KeywordsAsync(settings.Language, chosen, ct).ConfigureAwait(false);
            using var kws = new ConfirmedKeywordSpotter(new ZipformerKwsSpotter(bundle, keywords));

            kws.Woke += (_, d) =>
            {
                heard++;
                updates.Report(new WakeStatus(WakeState.Heard, "Heard you",
                    heard == 1 ? "Say it again to try once more" : $"{heard} times", heard));
            };

            updates.Report(new WakeStatus(WakeState.Listening, say, "Listening", heard));

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
                say, $"Could not start listening ({ex.GetType().Name})", heard));
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
    /// <summary>The phrase this language is currently listened for with.</summary>
    /// <remarks>
    /// FALLS BACK TO "Hey B" AND SAYS SO ELSEWHERE. A language with no phrase is
    /// the common case - seventy of seventy-five - and the listener still has to
    /// listen for something. The settings screen is where that is confessed and
    /// where a phrase can be added; here it only has to name what the microphone
    /// is actually waiting for.
    /// </remarks>
    private async Task<string> ChosenPhraseAsync(string language, CancellationToken ct)
    {
        try
        {
            var options = await _phrases.ForAsync(language, ct).ConfigureAwait(false);
            var chosen = options.FirstOrDefault(o => o.Chosen) ?? options.FirstOrDefault();
            return chosen?.Text ?? "Hey B";
        }
        catch
        {
            return "Hey B";
        }
    }

    /// <summary>
    /// The keyword file the spotter should read, written to match the chosen phrase.
    /// </summary>
    /// <remarks>
    /// ONE WRITER FOR THAT FILE, and it is <see cref="DeviceWakePhrases"/>. This
    /// method used to derive the phrase itself and write the file too, which meant
    /// two places deciding what the phone answers to - the settings screen and the
    /// listener - with no rule about which won. They disagreed exactly as you would
    /// expect: the screen showed the phrase somebody picked and the microphone
    /// waited for the one the engine liked best.
    /// <para>
    /// Choosing is idempotent, so calling it here with the phrase already in force
    /// simply rewrites the file - which is what makes a first run work, where
    /// nobody has chosen anything yet.
    /// </para>
    /// </remarks>
    private async Task<string?> KeywordsAsync(string language, string phrase, CancellationToken ct)
    {
        try
        {
            await _phrases.ChooseAsync(language, phrase, ct).ConfigureAwait(false);
            var path = DeviceWakePhrases.KeywordFile(language);
            if (File.Exists(path)) return path;

            // Null means the bundle's own built-in English keywords. Traced rather
            // than silent: this is the phone answering to a name its owner may not
            // have been told, and the trace is how that gets diagnosed.
            VoiceTrace.Write($"kws: no keyword file for '{language}' - "
                           + "falling back to the bundle's English phrase");
            return null;
        }
        catch (Exception ex)
        {
            VoiceTrace.Write($"kws: could not prepare keywords for '{language}' - {ex.GetType().Name}");
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
