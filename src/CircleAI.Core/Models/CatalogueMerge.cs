// CatalogueMerge.cs
//
// Folding a live catalogue into the curated one, without losing the app.
//
// THE REPLACEMENT WAS A SHIPPING-BREAKING BUG SITTING ONE LINE FROM BEING
// SWITCHED ON. ModelRegistryService.AllModels read
//
//     (_remoteRegistry ?? _embeddedRegistry)?.Models
//
// so the first successful refresh REPLACED the curated catalogue outright - and
// the two are disjoint in both name and HOST. Counted, not assumed: of the 88
// shipped entries, ZERO are sourced from ModelScope. Sixty-nine text-to-speech
// voices, the Whisper ASR, the "Hey B" wake word and the Open JTalk dictionary
// sit on Hugging Face buckets and GitHub releases; so does the entire curated
// chat ladder. The live query asks ModelScope for MNN bundles, and InferModality
// answers Chat, Vision, Embedding — or null, meaning nothing here can open the
// file, in which case it is not catalogued at all. That last answer is newer
// than this file: the fall-through used to be Chat, so a Stable Diffusion
// checkpoint and a paraformer ASR bundle both qualified as models to TALK to.
//
// So a refresh that WORKED would not have updated the catalogue - it would have
// SWAPPED it for a different one, and left no trace of why the phone had stopped
// speaking. Nothing called PrimeFromCatalogAsync, so it never happened; wiring
// the refresh up without this file would have been the change that shipped it.
//
// A SECOND LOSS, QUIETER. The live client derives MinRamGb and MinStorageGb from
// bundle size - it says so, and it is right to - but it does not set
// QualityRank, because ModelScope's listing API cannot know it. Everything
// discovered therefore arrives at rank 0, and ModelChoice orders by QualityRank
// descending with smallest-RAM as the tie-break. A wholesale replacement would
// have collapsed model choice from "the best model that fits this phone" to
// "the smallest model that fits this phone", on every device, silently.
//
// THE RULE, THEN: the curated catalogue is the spine and always survives. A live
// entry the curated one already names is IGNORED - the shipped pin, rank and
// device-fit win, because those were measured on a phone and a pin the app
// downloads against must not change underneath it (tools/sync-registry makes the
// same argument about our own sidecars). A live entry with a NEW name is added,
// with a rank derived from its size so it can actually be chosen.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Core.Models;

/// <summary>Combines the curated catalogue with a freshly-fetched one.</summary>
public static class CatalogueMerge
{
    /// <summary>
    /// The curated catalogue, plus any model the live one knows about that the
    /// curated one does not.
    /// </summary>
    /// <param name="curated">
    /// What shipped in the APK. Never discarded, never altered.
    /// </param>
    /// <param name="live">
    /// What was just fetched. May be <c>null</c> (no refresh has succeeded), in
    /// which case the curated list is returned unchanged.
    /// </param>
    /// <returns>Curated entries first, in their original order, then discovered ones.</returns>
    /// <remarks>
    /// CURATED FIRST AND IN ORDER, because a list order is a decision somebody
    /// made. Appending discovered entries keeps every existing screen rendering
    /// exactly as it did, and makes "what is new" visible as a block at the end
    /// rather than scattered through the ladder.
    /// </remarks>
    public static IReadOnlyList<ModelEntry> Combine(
        IReadOnlyList<ModelEntry>? curated,
        IReadOnlyList<ModelEntry>? live)
    {
        var spine = curated ?? [];
        if (live is null || live.Count == 0) return spine;

        var known = new HashSet<string>(
            spine.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);

        var combined = new List<ModelEntry>(spine);

        foreach (var found in live)
        {
            if (string.IsNullOrWhiteSpace(found.Name)) continue;
            if (!known.Add(found.Name)) continue;          // curated wins, always

            combined.Add(found.QualityRank > 0 ? found : found with
            {
                QualityRank = RankFor(found),
            });
        }

        return combined;
    }

    /// <summary>
    /// A plausible <c>QualityRank</c> for a model nobody has tested on a phone.
    /// </summary>
    /// <remarks>
    /// RANK 0 MEANS NEVER CHOSEN, which is what a discovered model gets from the
    /// live client and is the opposite of the point: cataloguing a model that
    /// can never be selected is the same as not cataloguing it.
    /// <para>
    /// The curated ladder's ranks visibly track SIZE - each doubling is about
    /// two ranks - so the same scale is used here, fitted to it:
    /// </para>
    /// <list type="bullet">
    /// <item>Qwen3-0.6B, 0.5 GB on disk — curated 6, derived 5</item>
    /// <item>Qwen3-4B, 2.6 GB — curated 10, derived 10</item>
    /// <item>Qwen3-14B, 8.9 GB — curated 14, derived 13</item>
    /// </list>
    /// <para>
    /// DELIBERATELY LANDING AT OR JUST BELOW THE CURATED EQUIVALENT. A model
    /// that has been run on a P30 should beat one that has not, at the same
    /// size, and the two ways of losing that argument are not symmetrical:
    /// preferring the untested one costs somebody a model that will not load.
    /// </para>
    /// <para>
    /// Size is the only honest signal available. ModelScope's listing API
    /// reports file sizes and nothing about quality, and parsing "4B" out of a
    /// name guesses at a convention no one is obliged to follow.
    /// </para>
    /// </remarks>
    public static int RankFor(ModelEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var gigabytes = entry.TotalBytes / 1_000_000_000.0;

        // Nothing to go on - a listing with no size. Rank it at the bottom of
        // the usable range rather than at 0: selectable, but last.
        if (gigabytes <= 0) return 1;

        var rank = 2 * Math.Log2(Math.Max(gigabytes, 0.05)) + 7;

        // Clamped to the curated range. Sixteen is the largest rank in the
        // shipped ladder; letting a discovered entry exceed it would put an
        // untested model above everything on every device that could hold it.
        return (int)Math.Clamp(Math.Round(rank), 1, 16);
    }

    /// <summary>
    /// Whether <paramref name="entry"/> came from a live refresh rather than the
    /// shipped catalogue.
    /// </summary>
    /// <remarks>
    /// For a screen that wants to say so. "Discovered, not tested on a phone" is
    /// a true and useful thing to show beside a model, and the alternative -
    /// presenting curated and discovered entries identically - claims a
    /// confidence about the second that nobody has earned.
    /// </remarks>
    public static bool IsDiscovered(
        ModelEntry entry,
        IReadOnlyList<ModelEntry>? curated)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return curated is not null
            && !curated.Any(c => string.Equals(c.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
    }
}
