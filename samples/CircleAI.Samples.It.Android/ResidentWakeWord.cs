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

    /// <summary>
    /// Builds the detector this device should use and hands it to the service.
    /// </summary>
    /// <remarks>
    /// Everything about WHICH engine and HOW strict is decided by
    /// <see cref="WakeWordFactory"/> from the bundle on disk and the phone's own
    /// measurements — nothing here hard-codes a choice, which is what let the two
    /// engines drift apart with no selector in the first place.
    /// </remarks>
    /// <summary>
    /// The language the resident listener was built for, or null if none is
    /// installed.
    /// </summary>
    /// <remarks>
    /// THE WAKE PHRASE IS FIXED AT INSTALL, so this is what tells a caller the
    /// installed listener has gone stale. Install ran once, guarded on
    /// "Listener is null", which meant changing the language on the languages
    /// screen left the phone still waiting for the previous language's name
    /// until the process happened to restart — and nothing on screen said so.
    /// </remarks>
    public static string? InstalledLanguage { get; private set; }

    /// <param name="languageCode">
    /// The language the phone is set to, so it listens for its name in that
    /// language rather than in English.
    /// </param>
    /// <param name="keywordsFile">
    /// The phrase the OWNER chose, as already written by the head's own phrase
    /// store. Null derives one instead, which is right for a head that has no
    /// such store.
    /// </param>
    public static bool Install(Android.Content.Context context, string bundleDirectory,
                               IVoiceTranscriber? transcriber = null,
                               string? languageCode = null,
                               string? keywordsFile = null)
    {
        try
        {
            var probe = CircleAI.Core.DeviceProbe.Snapshot();
            var host = new WakeHostCapabilities(
                probe.RamTotalBytes,
                TranscriberAvailable: transcriber is not null);

            var appData = System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.ApplicationData);
            var calibrationPath = System.IO.Path.Combine(appData, "CircleAI", "wake-calibration.json");

            // WHAT TO LISTEN FOR, IN THE LANGUAGE THAT WAS CHOSEN. "Hey B" was
            // fixed regardless of language, so setting the phone to Japanese left
            // it waiting for an English phrase nobody would say to it.
            // THE PHRASE THE OWNER ACTUALLY CHOSE, WHEN THE HEAD KNOWS IT.
            //
            // This used to always derive its own from WakePhraseBook.BestFor,
            // which ignores the choice made in the UI - so the hybrid ended up
            // with TWO keyword files holding TWO different phrases:
            //
            //   files/CircleAI/wake-en.txt          "Hey B"          (the UI)
            //   files/.config/CircleAI/wake-en.txt  "Hey Circle AI"  (here)
            //
            // The paths differ because FileSystem.AppDataDirectory and
            // SpecialFolder.ApplicationData are two spellings of "app data" that
            // differ by a /.config - the same trap ModelStore exists to close.
            // The screen said it was listening for one name and the service was
            // listening for another, which is indistinguishable from a wake word
            // that simply does not work.
            var keywords = keywordsFile is not null && System.IO.File.Exists(keywordsFile)
                ? keywordsFile
                : KeywordsFor(bundleDirectory, appData, languageCode);

            Android.Util.Log.Info("CircleAI.Kws",
                $"wake keywords: {keywords ?? "the bundle's own"}");

            var detector = WakeWordFactory.Create(
                new AndroidAudioCapture(),
                bundleDirectory,
                host,
                WakeCalibration.Load(calibrationPath),
                transcriber,
                keywords);

            CircleNeuronService.Listener = new ResidentWakeWord(detector);
            InstalledLanguage = languageCode;
            return true;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("CircleAI.Kws", "could not install resident wake word: " + ex);
            return false;
        }
    }

    /// <summary>
    /// A keywords file holding this language's wake phrase, or null to use the
    /// bundle's built-in one.
    /// </summary>
    /// <remarks>
    /// SILENCE IS NOT AN OPTION, so this never returns a phrase the model cannot
    /// hear. Every candidate is judged against the bundle's OWN tokenizer, and
    /// today that tokenizer is 500 uppercase English sub-words with no kana and
    /// no han — so Japanese kana phrases score Unusable and are dropped here
    /// rather than installed as a wake word that would never fire.
    /// <para>
    /// When nothing survives, this returns null and the phone keeps answering to
    /// its English name. That is wrong in a way a person can work around; a
    /// phone that answers to nothing is not. The log says which happened, because
    /// "it stopped waking" is otherwise indistinguishable from a broken mic.
    /// </para>
    /// </remarks>
    static string? KeywordsFor(string bundleDirectory, string appData, string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode)) return null;

        try
        {
            var bpe = System.IO.Directory
                .EnumerateFiles(bundleDirectory, "bpe.model", System.IO.SearchOption.AllDirectories)
                .FirstOrDefault();
            if (bpe is null) return null;

            var book = new WakePhraseBook(new SentencePieceTokenizer(bpe));
            var best = book.BestFor(languageCode);
            if (best is null)
            {
                Android.Util.Log.Warn("CircleAI.Kws",
                    $"no wake phrase this model can hear for '{languageCode}' — " +
                    "staying on the English name");
                return null;
            }

            if (!book.TryAdd(best.Text, out _)) return null;
            var path = System.IO.Path.Combine(appData, "CircleAI", $"wake-{languageCode}.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            book.Save(path);

            Android.Util.Log.Info("CircleAI.Kws",
                $"wake phrase for '{languageCode}': \"{best.Text}\" " +
                $"({best.Tokens.Count} tokens, {best.Verdict})");
            return path;
        }
        catch (Exception ex)
        {
            // A wake phrase that will not build is not worth losing the wake word
            // over — fall back to the bundle's own English keywords.
            Android.Util.Log.Warn("CircleAI.Kws",
                $"could not build a '{languageCode}' wake phrase: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
#endif
