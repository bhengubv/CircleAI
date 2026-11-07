// VoicePace.cs
//
// Whether a voice can keep up with speech on this device, as measured here.
//
// THE BEST VOICE WAS THE ONE WITH THE HIGHEST RANK THAT FITTED IN MEMORY, AND THAT IS NOT THE SAME
// THING. A voice that takes two seconds to render one second of speech cannot be spoken at all: the
// person hears a phrase, then silence while the next one renders. Measured on the P30 on 2026-11-06,
// the catalogue's best English voice took 2,08 s a second alone on all four big cores, and more
// beside the brain, with the same speaker's medium voice sitting in the store unused. Fitting says
// the phone can HOLD a voice. Only rendering on it says the phone can SPEAK with it.
//
// THE BEST PACE EVER SEEN, NOT THE LATEST. A render beside a brain writing flat out is slower than
// the voice is, and one bad moment must not cost somebody the better voice for the rest of the day.
// A voice is given up on only when it has never once kept up.

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace CircleAI.Voice;

/// <summary>What each voice has been measured at on this device, in seconds of work per second of speech.</summary>
public static class VoicePace
{
    /// <summary>
    /// The most work a second of speech may take. Under one, because the voice shares the phone
    /// with the brain still writing what comes next.
    /// </summary>
    public const double MostPerSecond = 0.9;

    private static readonly ConcurrentDictionary<string, double> Best = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> Silent = new(StringComparer.OrdinalIgnoreCase);

    private static string? _kept;
    private static int _verdicts;

    /// <summary>How many voices have been found, in this process, not to keep up.</summary>
    public static int Verdicts => Volatile.Read(ref _verdicts);

    /// <summary>Remembers the paces in <paramref name="file"/>, and takes up what it already holds.</summary>
    /// <remarks>
    /// A PHONE'S VOICES DO NOT CHANGE PACE BETWEEN ONE START AND THE NEXT, so what was learned is
    /// kept: without this every start opened and measured every voice on the way to the same
    /// answer, seconds of work and three models in and out of memory beside a brain loading.
    /// </remarks>
    public static void KeepIn(string file)
    {
        if (Interlocked.CompareExchange(ref _kept, file, null) is not null) return;
        try
        {
            if (!File.Exists(file)) return;
            foreach (var line in File.ReadAllLines(file))
            {
                var at = line.LastIndexOf('\t');
                if (at > 0 && double.TryParse(line[(at + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var pace))
                    Best[line[..at]] = pace;
            }
        }
        catch (IOException) { /* measured again */ }
    }

    /// <summary>Records one render of <paramref name="audio"/> of speech that took <paramref name="work"/>.</summary>
    public static void Note(string voice, TimeSpan work, TimeSpan audio)
    {
        if (string.IsNullOrWhiteSpace(voice) || audio <= TimeSpan.Zero) return;
        var pace = work / audio;
        var before = Of(voice);
        var best = Best.AddOrUpdate(voice, pace, (_, seen) => Math.Min(seen, pace));
        if (before == best) return;
        if (before is null or <= MostPerSecond && best > MostPerSecond) Interlocked.Increment(ref _verdicts);

        try
        {
            if (Volatile.Read(ref _kept) is { } file)
                File.WriteAllLines(file, Best.Select(p => p.Key + "\t" + p.Value.ToString("0.###", CultureInfo.InvariantCulture)));
        }
        catch (IOException) { /* learned again next time */ }
    }

    /// <summary>True until the voice has shown it cannot keep up: a voice not yet heard gets its chance.</summary>
    public static bool KeepsUp(string voice) => !Best.TryGetValue(voice, out var pace) || pace <= MostPerSecond;

    /// <summary>The voice was asked to speak here and produced nothing: it is passed over until the process ends.</summary>
    public static void CouldNotSpeak(string voice) => Silent[voice] = 0;

    /// <summary>False once the voice has failed to produce anything here.</summary>
    public static bool CanSpeak(string voice) => !Silent.ContainsKey(voice);

    /// <summary>The best pace seen for <paramref name="voice"/>, if it has rendered here.</summary>
    public static double? Of(string voice) => Best.TryGetValue(voice, out var pace) ? pace : null;

    /// <summary>Forgets everything measured.</summary>
    public static void Reset()
    {
        Best.Clear();
        Silent.Clear();
        _kept = null;
        _verdicts = 0;
    }
}
