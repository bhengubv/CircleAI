#nullable enable

// FirstRun.cs
//
// "Tap to start" has to actually start something.
//
// THE FIRST SCREEN A NEW PERSON SEES SAID "Let's set it up — It needs a few things
// first. Tap to start." Tapping played a greeting in isiZulu and set nothing up.
// Tap again and it greeted you in Kiswahili. There was no path from a fresh
// install to a working assistant anywhere on that screen, and the screen was
// promising one.
//
// The greeting is right when parts are still ARRIVING — it says "alive, wait a
// second". It is wrong when nothing is coming, and the home screen could not tell
// those apart because both are "cannot talk yet". Readiness already names them
// separately (Waking vs NeedsSetup); only the tap conflated them.
//
// The other half of the fix is refusing to hand the person a menu. Setup could
// have opened the abilities screen and let them pick, and that is the shape most
// apps take — but choosing between sixteen models is our filing system, not their
// problem, and the whole argument of AbilitiesActivity is that we are equipped to
// make that call and they are not. One tap, we pick, it downloads.
//
// ORDER IS THE DESIGN. The parts cost wildly different amounts and Readiness
// already stages them, so setup fetches them in the order that makes the phone
// useful soonest rather than the order that finishes soonest:
//
//     the voice        ~60 MB     it can say something back
//     the ears         ~78 MB     it can hear you
//     the wake word    ~6 MB      you can stop touching it
//     the brain       ~433 MB     it can think — the download, essentially
//
// Each finished step visibly upgrades the screen behind it, so the wait is spent
// watching it become able to do things rather than watching one bar. The brain is
// last because it is nine tenths of the bytes and the only part you can usefully
// wait for while already talking to it.
//
// There is no UI in this file, and it lives in the SHARED sample rather than the
// Android head, so both the plan and the fetch are testable off-device. The reason
// the first-run path was never tested is that it only ever existed inside a click
// handler, where nothing but a phone can reach it.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;

namespace CircleAI.Samples.It;

/// <summary>One thing first-run will fetch, in the words the screen will use.</summary>
/// <param name="Title">What this gives the person — "the voice", not "MMS TTS".</param>
/// <param name="Modality">Which ability it serves.</param>
/// <param name="Model">The chosen model. Picked for this device, not by the user.</param>
public readonly record struct SetupStep(string Title, ModelModality Modality, ModelEntry Model);

/// <summary>How far setup has got, for showing on the home screen.</summary>
/// <param name="Index">Zero-based step being fetched.</param>
/// <param name="Count">How many steps in total.</param>
/// <param name="Title">The current step's words.</param>
/// <param name="Fraction">0..1 across the whole of setup, weighted by bytes.</param>
/// <param name="BytesPerSecond">Live rate, or 0 before one is known.</param>
/// <param name="Remaining">What is left of the WHOLE setup, not just this part.</param>
/// <remarks>
/// THE NUMBERS ARE THE POINT, because the wait is not one length. The same
/// bundle is minutes on a premium handset and most of an hour on a P30 Lite over
/// 48 Mbps — measured on the device, and unchanged by opening eight sockets
/// instead of one, so it is the link and it varies per person. A bare percentage
/// is honest on the fast phone and reads as a hang on the slow one.
/// </remarks>
public readonly record struct SetupProgress(
    int Index, int Count, string Title, float Fraction,
    double BytesPerSecond = 0, TimeSpan Remaining = default)
{
    /// <summary>A line fit to put on screen: what, how fast, how long left.</summary>
    public string Describe()
    {
        var pct = $"{Fraction * 100:0}%";
        if (BytesPerSecond <= 0) return $"{Title} — {pct}";

        var mbps = $"{BytesPerSecond / (1024 * 1024):0.0} MB/s";
        if (Remaining <= TimeSpan.Zero || Remaining > TimeSpan.FromHours(12))
            return $"{Title} — {pct} · {mbps}";

        // Minutes, not hh:mm:ss. Nobody waiting on a download is counting
        // seconds, and "43 minutes left" is a decision they can act on —
        // put the phone down, or stop and come back on wifi.
        var left = Remaining.TotalMinutes >= 1
            ? $"{Remaining.TotalMinutes:0} min left"
            : "less than a minute left";
        return $"{Title} — {pct} · {mbps} · {left}";
    }
}

/// <summary>Getting a fresh install to a working assistant, in one tap.</summary>
public static class FirstRun
{
    /// <summary>
    /// What this device needs, in the order that makes it useful soonest.
    /// </summary>
    /// <remarks>
    /// Anything already on disk is skipped, so this doubles as "finish what was
    /// interrupted" — a setup that died halfway through the brain resumes at the
    /// brain rather than starting again from the voice.
    /// </remarks>
    /// <param name="speech">
    /// Whether this BUILD can speak and listen. The chat-only APK ships without
    /// CircleAI.Voice, whisper and the wake loop — none of that code is compiled
    /// in — so fetching a voice, ears and a wake bundle there spends somebody's
    /// data on a hundred and forty megabytes that no line of the binary can open.
    /// The models are real and the catalogue lists them either way; what differs
    /// is whether anything on the phone can use them.
    /// </param>
    /// <param name="declined">
    /// Returns true for a model the person explicitly turned off. Without this the
    /// abilities screen's "Turning it back on downloads it again" becomes a lie in
    /// the other direction: setup would quietly re-fetch, on the next resume, the
    /// thing they just chose to remove.
    /// </param>
    public static IReadOnlyList<SetupStep> Plan(
        ModelRegistryService registry, BundleModelLoader loader, DeviceProbe probe,
        bool speech, Func<string, bool>? declined = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(loader);

        // TWO VOICES, BOTH BY NAME, AND NEITHER LEFT TO THE SELECTOR.
        //
        // Asking for "a TTS model that fits" fetched exactly one, and which one
        // depended on quality ranks — the same fit-based guessing that once put a
        // Nepali voice in an English assistant's mouth. Speech needs both, for
        // different reasons: Vits-11ZA is grapheme-driven and right for the ten
        // South African languages, and structurally wrong for English, where it
        // measured 0.17 word error rate against Piper lessac's 0.00 because
        // English spelling cannot be sounded out letter by letter.
        //
        // Named from ItSpeaker's own constants so setup cannot fetch one voice
        // while the speaker asks for another. Together they are about 185 MB, or
        // 95 MB once the quantised SA voice is published — next to a 22 GB brain,
        // the English voice is rounding error and it is the difference between an
        // assistant you can act on and one you cannot.
        var wanted = new List<(string Title, ModelModality Modality, string? Named)>();
        if (speech)
        {
            wanted.Add(("the English voice", ModelModality.Tts,
                        CircleAI.Samples.It.Voice.ItSpeaker.EnglishVoice));
            wanted.Add(("the local voice",   ModelModality.Tts,
                        CircleAI.Samples.It.Voice.ItSpeaker.PreferredVoice));
            wanted.Add(("the ears",          ModelModality.Asr,      null));
            wanted.Add(("the wake word",     ModelModality.WakeWord, null));
        }
        wanted.Add(("the brain", ModelModality.Chat, null));

        var steps = new List<SetupStep>();
        foreach (var (title, modality, named) in wanted)
        {
            ModelEntry? pick;
            if (named is not null)
            {
                // A NAMED ENTRY IS CHECKED BY NAME, not by modality. The old test
                // asked "is there any model of this modality on disk", which for
                // two voices would have seen the first one land and skipped the
                // second — fetching one voice and reporting both as done.
                if (loader.ModelExists(named)) continue;
                if (declined is not null && declined(named)) continue;

                pick = registry.AllModels.FirstOrDefault(
                    m => string.Equals(m.Name, named, StringComparison.OrdinalIgnoreCase)
                         && Fits(m, probe));
            }
            else
            {
                // Already there is already done. Note this asks the loader, not the
                // registry: what matters is bytes on disk, not what we know about.
                if (registry.AllModels.Any(m => m.Modality == modality && loader.ModelExists(m.Name)))
                    continue;

                // The same choice the abilities screen makes, deliberately: best that
                // fits, smallest among equals. Two places asking "which model" and
                // disagreeing would mean setup installs one thing and the screen
                // reports another.
                pick = registry.AllModels
                    .Where(m => m.Modality == modality && Fits(m, probe))
                    .Where(m => declined is null || !declined(m.Name))
                    .OrderByDescending(m => m.QualityRank)
                    .ThenBy(m => m.MinRamGb)
                    .FirstOrDefault();
            }

            // NO ENTRY IS NOT AN ERROR HERE. A phone too small for any chat model
            // still gets a voice and ears, and should be set up that far rather
            // than refused outright. The screen keeps saying what is missing.
            if (pick is not null) steps.Add(new SetupStep(title, modality, pick));
        }

        return steps;
    }

    /// <summary>Fetches the plan, reporting progress across the whole of it.</summary>
    /// <remarks>
    /// Progress is WEIGHTED BY BYTES, not by step. Four equal-looking steps where
    /// one is nine tenths of the download produce a bar that races to 75% and then
    /// appears to hang for four minutes, which reads as broken. Weighted, it moves
    /// at roughly the speed of the download the whole way.
    /// </remarks>
    public static async Task RunAsync(
        BundleModelLoader loader, IReadOnlyList<SetupStep> steps,
        IProgress<SetupProgress>? progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0) return;

        var total = Math.Max(1L, steps.Sum(s => s.Model.TotalBytes));
        long done = 0;

        for (var i = 0; i < steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var step  = steps[i];
            var start = done;
            var index = i;

            var inner = new Progress<CircleAI.Core.DownloadProgress>(p =>
            {
                var doneNow  = start + step.Model.TotalBytes * Math.Clamp(p.Ratio, 0, 1);
                var fraction = (float)(doneNow / total);

                // ETA ACROSS THE WHOLE OF SETUP, not just this file. The download
                // service knows how long the current file has left; a person
                // wants to know when they can use the phone, which is when the
                // LAST byte lands. Extrapolated from the live rate over
                // everything still outstanding.
                var left = p.BytesPerSecond > 0
                    ? TimeSpan.FromSeconds((total - doneNow) / p.BytesPerSecond)
                    : TimeSpan.Zero;

                progress?.Report(new SetupProgress(
                    index, steps.Count, step.Title, fraction, p.BytesPerSecond, left));
            });

            await Task.Run(() => loader.DownloadModelAsync(step.Model.Name, inner, ct), ct)
                      .ConfigureAwait(false);

            done += step.Model.TotalBytes;
            progress?.Report(new SetupProgress(index, steps.Count, step.Title, done / (float)total));
        }
    }

    /// <summary>Whether this model can run on this phone at all.</summary>
    /// <remarks>
    /// Identical to the abilities screen's rule, and it depends on the platform
    /// memory probe being installed — unwired, DeviceProbe reports the GC heap
    /// (~100 MB) and this says no to everything. See ItApplication.
    /// </remarks>
    static bool Fits(ModelEntry m, DeviceProbe probe) =>
        m.MinRamGb <= probe.UsableRamGb + 0.0001 &&
        (probe.StorageFreeGb <= 0 || m.MinStorageGb <= probe.StorageFreeGb + 0.0001);
}
