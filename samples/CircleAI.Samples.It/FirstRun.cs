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
public readonly record struct SetupProgress(int Index, int Count, string Title, float Fraction);

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

        var wanted = new List<(string Title, ModelModality Modality)>();
        if (speech)
        {
            wanted.Add(("the voice",     ModelModality.Tts));
            wanted.Add(("the ears",      ModelModality.Asr));
            wanted.Add(("the wake word", ModelModality.WakeWord));
        }
        wanted.Add(("the brain", ModelModality.Chat));

        var steps = new List<SetupStep>();
        foreach (var (title, modality) in wanted)
        {
            // Already there is already done. Note this asks the loader, not the
            // registry: what matters is bytes on disk, not what we know about.
            if (registry.AllModels.Any(m => m.Modality == modality && loader.ModelExists(m.Name)))
                continue;

            // The same choice the abilities screen makes, deliberately: best that
            // fits, smallest among equals. Two places asking "which model" and
            // disagreeing would mean setup installs one thing and the screen
            // reports another.
            var pick = registry.AllModels
                .Where(m => m.Modality == modality && Fits(m, probe))
                .Where(m => declined is null || !declined(m.Name))
                .OrderByDescending(m => m.QualityRank)
                .ThenBy(m => m.MinRamGb)
                .FirstOrDefault();

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

            var inner = new Progress<float>(f =>
                progress?.Report(new SetupProgress(
                    index, steps.Count, step.Title,
                    (start + step.Model.TotalBytes * Math.Clamp(f, 0f, 1f)) / (float)total)));

            // DownloadModelAsync takes no token, so cancellation lands between
            // steps rather than mid-file. Task.Run keeps the wait off the UI
            // thread and lets the token cut the await loose immediately.
            await Task.Run(() => loader.DownloadModelAsync(step.Model.Name, inner), ct)
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
