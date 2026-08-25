// DeviceVoiceHost.cs
//
// The real thing: synthesis on this phone, from the same code path the native
// head uses.
//
// IT CALLS ItTtsProbe.RunCataloguedAsync, and that is deliberate rather than
// lazy. That method is where a season of hard-won detail lives - the ESPnet
// layout detection, the Open JTalk prerequisite, the sideload-before-download
// check, the "ask the graph not the filename" family selection. A second
// implementation here would be a second set of those decisions, and the ones that
// went wrong the first time went wrong silently: a voice that speaks fluent
// nonsense raises no exception.

using System.Diagnostics;
using CircleAI.Samples.It.Voice;

namespace CircleAI.Samples.It.App.Services;

/// <summary>Speaks on the device, using the catalogued voice for a language.</summary>
public sealed class DeviceVoiceHost : IVoiceHost
{
    // One utterance at a time. Tapping a second language mid-download left two
    // synthesisers racing for the speaker on the native head.
    private readonly SemaphoreSlim _one = new(1, 1);

    /// <inheritdoc />
    public VoiceAvailability Availability => VoiceAvailability.OnDevice;

    /// <inheritdoc />
    public string Provenance =>
        "Voices run here, on this device. Nothing is sent anywhere to speak.";

    /// <summary>
    /// Where downloaded bundles live, and where the phonemiser looks for its
    /// dictionary.
    /// </summary>
    /// <remarks>
    /// FileSystem.AppDataDirectory rather than a hand-built path: MAUI gives the
    /// per-platform private directory, and the Open JTalk dictionary has to land
    /// somewhere the phonemiser searches. Spelling that path by hand is how 103 MB
    /// once downloaded correctly into a folder nothing read.
    /// </remarks>
    private static string StorageDir => ModelStore.Path;

    /// <inheritdoc />
    /// <remarks>
    /// EXACTLY WHAT LanguagePickerActivity.LoadLanguages DOES, and deliberately so:
    /// every Tts tag in the registry, sized by asking SpeechModelSelector which
    /// voice would actually play on THIS device. Picking independently here - the
    /// smallest voice, say - is how a row came to advertise 122 MB while "Hear it"
    /// played a 137.6 MB voice.
    /// <para>
    /// A selector that declines or throws for one language must not empty the whole
    /// list, so that language falls back to the smallest voice serving it. Rough is
    /// better than a row that vanishes.
    /// </para>
    /// </remarks>
    public Task<IReadOnlyList<VoiceRow>> CatalogueAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<VoiceRow>>(() =>
        {
            using var registry = new CircleAI.Core.Models.ModelRegistryService();
            var voices = registry.AllModels
                .Where(m => m.Modality == CircleAI.Core.ModelModality.Tts)
                .ToList();

            CircleAI.Inference.ISpeechModelSelector selector =
                new CircleAI.Inference.SpeechModelSelector(registry);
            var device = CircleAI.Core.DeviceProbe.Snapshot();

            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in voices)
                foreach (var raw in (v.Language ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                    if (raw.Trim().Length > 0) tags.Add(raw.Trim());

            var best = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in tags)
            {
                try
                {
                    var plan = selector.PlanFor(device, CircleAI.Core.ModelModality.Tts, tag);
                    if (plan.IsAvailable && plan.Model is not null)
                    {
                        var entry = registry.GetLatestModel(plan.Model.ModelId);
                        if (entry is not null) { best[tag] = entry.TotalBytes; continue; }
                    }
                }
                catch
                {
                    // One language the selector cannot answer for must not empty
                    // the list.
                }

                foreach (var v in voices)
                    foreach (var raw in (v.Language ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                        if (string.Equals(raw.Trim(), tag, StringComparison.OrdinalIgnoreCase)
                            && (!best.TryGetValue(tag, out var cur) || v.TotalBytes < cur))
                            best[tag] = v.TotalBytes;
            }

            return best.Select(kv => new VoiceRow(kv.Key, kv.Value)).ToList();
        }, ct);

    /// <inheritdoc />
    public Task<SpeakOutcome> SpeakAsync(
        string tag, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var lang = SampleLanguages.Find(tag);
        return lang?.Greeting is null
            // Not a fabricated sentence. The table leaves a greeting null when
            // nobody could confirm it, and mispronouncing an invented phrase at a
            // native speaker is worse than saying nothing.
            ? Task.FromResult(new SpeakOutcome(false, $"No checked phrase for '{tag}'."))
            : SayAsync(tag, lang.Greeting, progress, ct);
    }

    /// <inheritdoc />
    public async Task<SpeakOutcome> SayAsync(
        string tag, string text,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new SpeakOutcome(false, "Nothing to say.");

        var lang = SampleLanguages.Find(tag);

        if (!await _one.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
            return new SpeakOutcome(false, "Already speaking.");

        try
        {
            Directory.CreateDirectory(StorageDir);
            var wav = Path.Combine(FileSystem.CacheDirectory, $"say-{tag}.wav");

            var sw = Stopwatch.StartNew();
            var report = await Task.Run(
                () => ItTtsProbe.RunCataloguedAsync(
                    StorageDir, tag, text, wav,
                    log: line => progress?.Report(line),
                    ct: ct),
                ct).ConfigureAwait(false);
            sw.Stop();

            if (!File.Exists(wav))
            {
                // The probe reports what happened in prose; hand that back rather
                // than a generic failure, because it names the actual cause -
                // a missing phonemiser reads very differently from a 404.
                return new SpeakOutcome(false, Tidy(report), sw.ElapsedMilliseconds);
            }

            var audioMs = WavMilliseconds(wav);
            await PlayAsync(wav, ct).ConfigureAwait(false);

            return new SpeakOutcome(
                true,
                $"{lang?.Name ?? tag}: {audioMs} ms of audio in {sw.ElapsedMilliseconds} ms",
                sw.ElapsedMilliseconds,
                audioMs);
        }
        catch (OperationCanceledException)
        {
            return new SpeakOutcome(false, "Cancelled.");
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Play the file, and wait for it to finish.</summary>
    /// <remarks>
    /// Awaited rather than fired and forgotten, so the caller can put the mark back
    /// to Idle when the sound actually stops instead of when the file was written.
    /// A mark that goes still while audio is still playing is the small lie that
    /// makes an interface feel broken.
    /// </remarks>
    private static Task PlayAsync(string wav, CancellationToken ct)
        // MAUI ships no first-party audio player, so the platform one is reached
        // through a partial this head owns.
        => PlatformAudio.PlayAsync(wav, ct);

    /// <summary>Length of a PCM wav, from its own header.</summary>
    /// <remarks>
    /// Read rather than assumed. A voice's sample rate is not a constant across
    /// this catalogue - 16 kHz for MMS, 22.05 kHz for Piper and ESPnet, 44.1 kHz
    /// for one - and dividing by the wrong one reports a duration that is wrong by
    /// up to 2.75x while the audio itself is fine.
    /// </remarks>
    private static long WavMilliseconds(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var r = new BinaryReader(fs);
            fs.Position = 24;
            var rate = r.ReadInt32();
            fs.Position = 34;
            var bits = r.ReadInt16();
            fs.Position = 22;
            var channels = r.ReadInt16();
            var bytesPerSample = Math.Max(1, bits / 8 * channels);
            var dataBytes = Math.Max(0, fs.Length - 44);
            return rate <= 0 ? 0 : dataBytes * 1000 / (rate * bytesPerSample);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>The probe's report as one readable line.</summary>
    private static string Tidy(string report)
    {
        var lines = report.Split('\n', StringSplitOptions.RemoveEmptyEntries
                                     | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "No audio was produced." : lines[^1];
    }
}
