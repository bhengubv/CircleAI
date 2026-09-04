#nullable enable

// WakeWordFactory.cs
//
// Decides WHICH wake engine runs and HOW hard its second stage judges, so that a
// host does not have to know either.
//
// THERE ARE TWO ENGINES AND NOTHING CHOSE BETWEEN THEM. KwsWakeWordDetector runs
// a single-graph classifier trained on one phrase; ZipformerWakeWordDetector runs
// three graphs and matches any number of phrases written as text. Both existed,
// both implemented the same interface, and every host picked by hard-coding a
// constructor — which meant the choice was made once, invisibly, by whoever wrote
// that line. Now it is made from what the bundle on disk actually is.
//
// THE SECOND STAGE IS CHOSEN BY WHAT THE PHONE CAN AFFORD, which is the split
// asked for in so many words: high-end should feel like air, low-end should not
// be throttled. The onset check costs nothing and removes three quarters of the
// false accepts; the transcript check removes the rest and needs a speech model
// resident. That is exactly a device-tier decision and DeviceProbe already knows
// the tier — it was simply never asked.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircleAI.Voice;

/// <summary>Which wake engine a bundle needs.</summary>
public enum WakeEngine
{
    /// <summary>Three-graph streaming transducer; keywords are text.</summary>
    ZipformerTransducer,
    /// <summary>Single-graph classifier; one trained phrase.</summary>
    SingleGraphClassifier,
}

/// <summary>
/// Per-device wake tuning that survives a restart.
/// </summary>
/// <remarks>
/// Thresholds were compile-time constants, which is a claim that every phone,
/// room and voice behaves like the ones they were measured on. They do not: the
/// same phrase read 0.42 on one synthetic voice and 0.94 on another. Persisting
/// per device lets a phone that consistently under-scores be nudged once instead
/// of the default being loosened for everybody.
/// </remarks>
public sealed record WakeCalibration
{
    /// <summary>Acceptance probability. Null uses the phrase or engine default.</summary>
    public double? Threshold { get; init; }

    /// <summary>Stage-two lead-in allowance in milliseconds.</summary>
    public double? MaxLeadInMs { get; init; }

    /// <summary>How many wakes have been recorded, for the record.</summary>
    public int Wakes { get; init; }

    /// <summary>How many were vetoed by stage two.</summary>
    public int Vetoes { get; init; }

    /// <summary>The weakest wake this phone has actually accepted.</summary>
    /// <remarks>
    /// THE ONLY NUMBER THAT CAN MOVE A THRESHOLD. The gate ships at
    /// <see cref="ZipformerKwsSpotter.MeasuredThreshold"/>, measured on one
    /// phone, one voice, one room — and until this file has some numbers in it
    /// that is what every other phone inherits too. The distance between this
    /// value and the gate is the margin the owner actually has; if it sits on
    /// top of the gate the phrase is barely getting through, and if it never
    /// approaches it the gate could be tighter and reject fewer false wakes.
    /// </remarks>
    public double? LowestWakeScore { get; init; }

    /// <summary>The strongest wake recorded, for the other end of the range.</summary>
    public double? HighestWakeScore { get; init; }

    /// <summary>This calibration plus one more accepted wake.</summary>
    /// <remarks>
    /// Returns a new record rather than mutating: this is read on the audio
    /// thread and written from the detector's event, and a struct-copy is
    /// cheaper to reason about than a lock around four fields.
    /// </remarks>
    public WakeCalibration WithWake(double score) => this with
    {
        Wakes = Wakes + 1,
        LowestWakeScore = LowestWakeScore is { } low ? Math.Min(low, score) : score,
        HighestWakeScore = HighestWakeScore is { } high ? Math.Max(high, score) : score,
    };

    /// <summary>This calibration plus one more stage-two veto.</summary>
    public WakeCalibration WithVeto() => this with { Vetoes = Vetoes + 1 };

    [JsonIgnore]
    public bool IsDefault => Threshold is null && MaxLeadInMs is null;

    /// <summary>Whether anything has ever been recorded on this device.</summary>
    /// <remarks>
    /// Distinct from <see cref="IsDefault"/>, which asks whether the tuning has
    /// been OVERRIDDEN. This asks whether any evidence exists at all — and for
    /// the whole life of this file the answer was no on every device, because
    /// <see cref="Load"/> was called at every start and <see cref="Save"/> was
    /// called nowhere.
    /// </remarks>
    [JsonIgnore]
    public bool HasEvidence => Wakes > 0 || Vetoes > 0;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Reads calibration, returning defaults when absent or unreadable.</summary>
    /// <remarks>
    /// Never throws. A corrupt tuning file must not stop the assistant from
    /// listening — the worst outcome of ignoring it is the factory defaults, which
    /// are what a fresh install uses anyway.
    /// </remarks>
    public static WakeCalibration Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<WakeCalibration>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch { return new(); }
    }

    /// <summary>Writes calibration. Best effort — tuning is not worth an exception.</summary>
    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch { /* advisory */ }
    }
}

/// <summary>What the factory needs to know about the phone it is running on.</summary>
/// <param name="TotalRamBytes">Physical RAM, for the tier decision.</param>
/// <param name="TranscriberAvailable">
/// True when a speech model is loaded and can afford to be asked. Not "could be
/// downloaded" — the transcript confirmer is only worth choosing if it can answer
/// inside its timeout right now.
/// </param>
public readonly record struct WakeHostCapabilities(long TotalRamBytes, bool TranscriberAvailable);

/// <summary>Builds the right wake detector for a bundle and a device.</summary>
public static class WakeWordFactory
{
    /// <summary>Above this much RAM the transcript confirmer is affordable.</summary>
    /// <remarks>
    /// 4 GB. Below it a resident speech model competes with the generalist for the
    /// memory the assistant needs to answer at all, and a wake word that is precise
    /// but leaves nothing to think with is a bad trade.
    /// </remarks>
    public const long TranscriptConfirmerMinRam = 4L * 1000 * 1000 * 1000;

    /// <summary>
    /// Works out which engine a bundle is by looking at what is in it.
    /// </summary>
    /// <remarks>
    /// By CONTENTS, not by name. A registry entry can be renamed, a folder can be
    /// copied, and someone side-loading a bundle names it whatever they like — but
    /// a three-graph transducer always has an encoder, a decoder and a joiner, and
    /// a classifier never does.
    /// </remarks>
    public static WakeEngine EngineFor(string bundleDirectory)
    {
        if (!Directory.Exists(bundleDirectory)) return WakeEngine.SingleGraphClassifier;

        var onnx = Directory.EnumerateFiles(bundleDirectory, "*.onnx", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!.ToLowerInvariant())
            .ToList();

        var hasAll = onnx.Any(n => n.Contains("encoder"))
                  && onnx.Any(n => n.Contains("decoder"))
                  && onnx.Any(n => n.Contains("joiner"));

        return hasAll ? WakeEngine.ZipformerTransducer : WakeEngine.SingleGraphClassifier;
    }

    /// <summary>Picks stage two for this device.</summary>
    public static IWakeConfirmer ConfirmerFor(
        WakeHostCapabilities host,
        WakeCalibration calibration,
        IVoiceTranscriber? transcriber = null)
    {
        var onset = new UtteranceOnsetConfirmer
        {
            MaxLeadInMs = calibration.MaxLeadInMs ?? new UtteranceOnsetConfirmer().MaxLeadInMs,
        };

        if (transcriber is null || !host.TranscriberAvailable ||
            host.TotalRamBytes < TranscriptConfirmerMinRam)
            return onset;

        // BOTH, in order: the cheap one first so the expensive one is never asked
        // about a wake it would have let through anyway. On the measured corpus
        // that is 27 of 30 clips never reaching the transcriber at all, which is
        // most of the battery the precise tier would otherwise cost.
        return new EitherConfirmer(onset, new TranscriptConfirmer(transcriber));
    }

    /// <summary>Builds a detector for whatever the bundle turns out to be.</summary>
    public static IWakeWordDetector Create(
        IAudioCapture capture,
        string bundleDirectory,
        WakeHostCapabilities host,
        WakeCalibration? calibration = null,
        IVoiceTranscriber? transcriber = null,
        string? keywordsFile = null)
    {
        var cal = calibration ?? new WakeCalibration();

        if (EngineFor(bundleDirectory) == WakeEngine.ZipformerTransducer)
            return new ZipformerWakeWordDetector(capture, new ZipformerWakeConfig(
                bundleDirectory,
                keywordsFile,
                // NOT A LITERAL. This was 0.5 and nothing had ever measured it:
                // the run that measured a working wake word ("6/6 through air")
                // took ZipformerKwsSpotter's own default of 0.25 by passing none
                // at all, so the shipped number and the measured number were
                // never the same and nothing compared them. On a P30 "Hey B"
                // scores 0.24-0.34, so 0.5 could not fire - arithmetically, not
                // unreliably. Calibration still overrides; the FALLBACK is now
                // the only value anybody checked.
                cal.Threshold ?? ZipformerKwsSpotter.MeasuredThreshold,
                ConfirmerFor(host, cal, transcriber)));

        var model = Directory.EnumerateFiles(bundleDirectory, "*.onnx", SearchOption.AllDirectories)
            .OrderBy(f => f.Length)
            .FirstOrDefault()
            ?? throw new FileNotFoundException(
                $"No wake-word model in '{bundleDirectory}'.", bundleDirectory);

        return new KwsWakeWordDetector(capture, new KwsConfig(
            ModelPath: model,
            Threshold: (float)(cal.Threshold ?? 0.7)));
    }
}

/// <summary>Which wake model serves a language, and whether it really does.</summary>
/// <param name="ModelName">The catalogued model to use, or null when none exists.</param>
/// <param name="IsNative">True when the model was built for the requested language.</param>
/// <param name="Note">Plain language for a UI when <paramref name="IsNative"/> is false.</param>
public readonly record struct WakeLanguageChoice(string? ModelName, bool IsNative, string Note);

/// <summary>
/// Works out which wake model to use for a language, and says so when there is not one.
/// </summary>
/// <remarks>
/// THE HONEST VERSION OF A GAP. The catalogue has sixty-five languages of speech
/// OUT and exactly one of wake IN — English. That is a real limitation and the
/// temptation is to hide it by silently loading the English model for everyone,
/// which produces an isiZulu speaker whose phone ignores them and no explanation
/// anywhere. So the fallback still happens, because an English-tokenised wake word
/// spoken clearly does work, but it is REPORTED as a fallback rather than passed
/// off as support.
/// <para>
/// No entries are invented here for models that do not exist. When a wake model
/// for another language is sourced, it gets a catalogue row with its Language set
/// and this starts returning it — no code change.
/// </para>
/// </remarks>
public static class WakeLanguages
{
    /// <summary>Picks the best catalogued wake model for a language.</summary>
    /// <param name="available">Catalogued wake models as (name, language, quality).</param>
    /// <param name="languageCode">BCP-47 / ISO 639, e.g. "zu" or "en-ZA".</param>
    public static WakeLanguageChoice For(
        IEnumerable<(string Name, string? Language, int Quality)> available,
        string languageCode)
    {
        var all = available.ToList();
        if (all.Count == 0)
            return new WakeLanguageChoice(null, false,
                "No wake word is available yet, so it cannot listen for a phrase.");

        var wanted = Base(languageCode);

        var native = all
            .Where(m => string.Equals(Base(m.Language), wanted, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.Quality)
            .FirstOrDefault();

        if (native.Name is not null)
            return new WakeLanguageChoice(native.Name, true, string.Empty);

        var fallback = all
            .Where(m => string.Equals(Base(m.Language), "en", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.Quality)
            .FirstOrDefault();
        if (fallback.Name is null) fallback = all.OrderByDescending(m => m.Quality).First();

        return new WakeLanguageChoice(fallback.Name, false,
            $"There is no wake word for this language yet, so an English one is being used. " +
            "It will still hear you, but the phrase has to be said the English way.");
    }

    private static string Base(string? code) =>
        string.IsNullOrWhiteSpace(code) ? "" : code.Split('-', '_')[0].Trim();
}

/// <summary>
/// Runs a cheap confirmer first and only consults the expensive one if it passes.
/// </summary>
/// <remarks>
/// AND, not OR, and deliberately in this order. The onset check rejects most false
/// accepts for free; anything it lets through is then read back by the
/// transcriber. Reversing them would pay for a transcription on every candidate,
/// including the many the cheap rule was going to reject anyway.
/// </remarks>
public sealed class EitherConfirmer : IWakeConfirmer
{
    private readonly IWakeConfirmer _cheap, _precise;

    public EitherConfirmer(IWakeConfirmer cheap, IWakeConfirmer precise)
    {
        _cheap = cheap;
        _precise = precise;
    }

    public string? LastReason { get; private set; }

    public async System.Threading.Tasks.ValueTask<bool> ConfirmAsync(
        WakeCandidate candidate, System.Threading.CancellationToken ct = default)
    {
        if (!await _cheap.ConfirmAsync(candidate, ct).ConfigureAwait(false))
        {
            LastReason = _cheap.LastReason;
            return false;
        }
        if (!await _precise.ConfirmAsync(candidate, ct).ConfigureAwait(false))
        {
            LastReason = _precise.LastReason;
            return false;
        }
        LastReason = null;
        return true;
    }
}
