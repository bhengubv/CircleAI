// CatalogueHousekeeping.cs
//
// "If a better model is found, housekeeping kicks in and a new model checks in."
// This is the reclaim half: an INFERIOR model that has been SUPERSEDED by a better
// one already on the device is clutter, and on a phone it is clutter that costs
// real storage. The user's example: a P30 holding both Qwen3-0.6B (quality 6) and
// Qwen3.5-0.8B (quality 7) — the 0.6B "should not be there, it is inferior."
//
// THE POLICY IS DELIBERATELY CONSERVATIVE, because a delete is irreversible and a
// re-download costs data on exactly the users this product is for. A model M is
// reclaimed ONLY when every one of these holds:
//   1. M is installed (a complete download — installed.json present).
//   2. Another INSTALLED model K of the SAME modality is strictly higher quality.
//   3. K is not dramatically larger than M (K.TotalBytes <= M.TotalBytes * FACTOR),
//      so keeping M offers no real FIT advantage — a genuinely smaller "it least
//      fits" fallback is NOT reclaimed. (TotalBytes, not MinRamGb: an MoE's MinRamGb
//      is its active-expert footprint, which would wrongly make a 22 GB model look
//      "similar size" to a 1 GB one.)
//   4. Either K is compatible on THIS device, OR M is not — so we NEVER remove a
//      model that runs here in favour of a better one that does not. On a device
//      where nothing is compatible, the better installed model is still the keeper.
//   5. K HAS LOADED ON THIS DEVICE (loaded_at), and is neither refused nor retired.
//      Added 2026-10-16, when this policy was first switched on in the service.
//      Without it the 0.6B would have been deleted the moment Qwen3.5-0.8B finished
//      downloading - before the 0.8B had ever run on a P30 whose OS killed it
//      mid-load on 2026-10-10. A replacement proves itself first; until then the
//      model it would replace is the phone's only brain.
// Rule 3 is why the Redmi keeps its 2 B alongside the 35 B (15x larger, a real
// fallback for the un-soaked MoE) but the P30 sheds its 0.6 B for the 0.8 B.
//
// AND A RETIRED MODEL IS DEAD WEIGHT, whatever its size, once any model above the
// floor has loaded here. "Nothing less than Qwen3.5 is the LLM": a retired model can
// never be the brain again, so it is not a fallback; it is disk.
//
// CHAT ONLY. The same arithmetic would delete Piper's medium English voice for its
// high one - similar size, higher rank - when a phone may be on the medium voice on
// purpose, for speed. Voices are chosen by name and language, not by this ranking,
// so they are not this rule's to judge.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Inference;

/// <summary>One model the housekeeping policy would reclaim, and why.</summary>
public readonly record struct HousekeepingReclaim(string Id, string Reason);

/// <summary>
/// Decides which installed models are superseded clutter, and (optionally) reclaims
/// them. The decision (<see cref="Plan"/>) is pure over the catalogue so it is a
/// unit test, not a phone run; <see cref="Reclaim"/> does the deleting.
/// </summary>
public static class CatalogueHousekeeping
{
    /// <summary>
    /// How much larger a better model may be and still make a smaller one
    /// redundant. At 2.0 a strictly-better model up to twice the size supersedes
    /// this one; a fallback that is less than half the size of the next-better
    /// model is kept, because on a tight device its smallness is the whole point.
    /// </summary>
    public const double SizeDominanceFactor = 2.0;

    /// <summary>
    /// The installed models that are superseded clutter on this device, given the
    /// catalogue's current <c>installed</c> and <c>compatible</c> state. Pure — no
    /// I/O. Order is stable (catalogue order).
    /// </summary>
    /// <param name="catalog">This device's catalogue.</param>
    /// <param name="keep">
    /// Models that must not be reclaimed whatever the policy says - the one the brain
    /// is running or loading. Null keeps nothing back.
    /// </param>
    public static IReadOnlyList<HousekeepingReclaim> Plan(IModelCatalog catalog, Func<string, bool>? keep = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var installed = catalog.AllAssessed()
            .Where(a => a.Installed && a.Entry.Modality == ModelModality.Chat)
            .ToList();
        if (installed.Count < 2) return Array.Empty<HousekeepingReclaim>();

        // THE ONLY MODELS ALLOWED TO REPLACE ANOTHER: loaded here, not refused, not
        // retired. Proof, not prediction - `compatible` is today's free-RAM reading
        // and says no to a model the P30 runs every day.
        var keepers = installed
            .Where(k => k.Proven && !k.Refused && !k.Entry.Retired)
            .ToList();
        if (keepers.Count == 0) return Array.Empty<HousekeepingReclaim>();

        // BEST FIRST, AND A MODEL BEING REMOVED REPLACES NOTHING. Decided in one pass
        // over a fixed list of keepers, a ladder of steps each within the size rule -
        // 1.0 GB, 1.9 GB, 3.7 GB - removed both lower rungs, leaving 3.7x the size of
        // the fallback rule 3 promises to keep. A replacement has strictly higher
        // quality, so walking down from the best settles each one's fate before
        // anything below asks it to stand in.
        var planned = new HashSet<string>(StringComparer.Ordinal);
        var reclaim = new List<HousekeepingReclaim>();
        foreach (var a in installed.OrderByDescending(x => x.Entry.QualityRank))
        {
            var m = a.Entry;
            if (keep is not null && keep(m.Name)) continue;

            if (m.Retired)
            {
                var best = keepers.OrderByDescending(k => k.Entry.QualityRank).First().Entry;
                reclaim.Add(new HousekeepingReclaim(
                    m.Name,
                    $"retired below the brain's floor, and {best.Name} has loaded here"));
                planned.Add(m.Name);
                continue;
            }

            var dominator = keepers.FirstOrDefault(k =>
                !planned.Contains(k.Entry.Name) &&
                k.Entry.Name != m.Name &&
                k.Entry.QualityRank > m.QualityRank &&
                k.Entry.TotalBytes > 0 && m.TotalBytes > 0 &&
                k.Entry.TotalBytes <= (long)(m.TotalBytes * SizeDominanceFactor) &&
                (k.Compatible || !a.Compatible))?.Entry;

            if (dominator is not null)
            {
                reclaim.Add(new HousekeepingReclaim(
                    m.Name,
                    $"superseded by {dominator.Name} (quality {dominator.QualityRank} > {m.QualityRank}, similar size, loaded here)"));
                planned.Add(m.Name);
            }

            // A MODEL THAT NO LONGER FITS IS NOT REMOVED, AND THAT IS DELIBERATE.
            // It was tried here and a test said no: Never_removes_a_model_that_runs
            // _here_for_a_better_one_that_does_not keeps an installed model that
            // stopped fitting, because free space comes back, a fit rule gets
            // corrected, and the bytes are already paid for. Re-downloading something
            // already on the phone is a worse outcome than the disk it holds.
            //
            // The waste that prompted the question was not this shape anyway: it was
            // 2.9 GB of a download that STOPPED - never installed, invisible to every
            // rule here, and handled by SweepAbandoned below.
        }
        return reclaim;
    }

    /// <summary>
    /// Delete download leftovers under <paramref name="modelsDirectory"/> that no
    /// run is still writing to, and report the bytes freed.
    /// </summary>
    /// <param name="modelsDirectory">The model store.</param>
    /// <param name="idleFor">
    /// How long a temp file must have gone untouched before it counts as abandoned.
    /// </param>
    /// <remarks>
    /// A CANCELLED DOWNLOAD LEAVES ITS BYTES BEHIND AND NOTHING EVER LOOKS AGAIN. The
    /// reclaim policy above cannot see them: it reasons about INSTALLED models, and a
    /// half-fetched bundle was never installed. On the device that found this, 2.9 GB
    /// of a model nothing would ever load again sat in the store, invisible to every
    /// rule and to the storage census's own idea of what is precious.
    /// <para>
    /// IDLE, NOT MERELY PRESENT, AND THAT IS THE WHOLE SAFETY OF IT. A download in
    /// flight is a .tmp being written to this second; deleting it would kill a fetch
    /// somebody is watching a progress bar for. Anything touched inside the window is
    /// left alone, so the test for "abandoned" is time, not shape.
    /// </para>
    /// <para>
    /// IDLE IS NOT THE SAME AS ABANDONED, AND THIS SWEEP COST 8.6 GB BY ASSUMING IT
    /// WAS. Measured on a Circle OS device, 2026-10-04: a 21.3 GB llm.mnn.weight.tmp
    /// was being fetched over eight sockets, each recording its position in a
    /// llm.mnn.weight.tmp.parts sidecar beside it. The fetch stopped; an hour later
    /// this sweep deleted the .tmp — 8.6 GB of RESUMABLE progress, the whole point of
    /// writing the sidecar — and kept the sidecar, which does not match *.tmp. The
    /// next attempt found no file, planned fresh segments from zero, correctly
    /// rejected the now-stale sidecar, and started the 21.3 GB again. Then stopped,
    /// then got swept, then started again. Three days, a 2 B model answering in its
    /// place, and nothing anywhere said why.
    /// </para>
    /// <para>
    /// SO A SIDECAR MAKES ITS .tmp OFF LIMITS. Those bytes are not litter: somebody
    /// chose this model with its size written next to it, and the sidecar is the
    /// record of how far their choice got. It is the same judgement the reclaim
    /// policy above already makes about an installed model that stopped fitting —
    /// the bytes are already paid for, and re-fetching them is the worse outcome.
    /// What IS swept is the mirror case: a sidecar whose .tmp is gone, which can only
    /// mislead whoever reads it next.
    /// </para>
    /// </remarks>
    public static long SweepAbandoned(string modelsDirectory, TimeSpan idleFor)
    {
        if (string.IsNullOrWhiteSpace(modelsDirectory) || !Directory.Exists(modelsDirectory))
            return 0;

        var cutoff = DateTime.UtcNow - idleFor;
        long freed = 0;

        try
        {
            foreach (var tmp in Directory.EnumerateFiles(modelsDirectory, "*.tmp", SearchOption.AllDirectories))
            {
                try
                {
                    // RESUMABLE, THEREFORE NOT RUBBISH. Checked before the clock,
                    // because age is exactly what a resumable download looks like.
                    if (File.Exists(DownloadSidecar.For(tmp))) continue;

                    var fi = new FileInfo(tmp);
                    if (fi.LastWriteTimeUtc > cutoff) continue;   // still being written
                    var size = fi.Length;
                    fi.Delete();
                    freed += size;

                    // Never leave the marker without the bytes it describes - that
                    // is the state this whole remark exists because of.
                    try { File.Delete(DownloadSidecar.For(tmp)); } catch { }
                }
                catch { /* in use or vanished: leave it */ }
            }

            // AND THE ORPHANS ALREADY OUT THERE. Every device that ran the old sweep
            // is carrying a sidecar for bytes that are gone; it is 278 bytes of
            // active misinformation and it does not heal itself.
            foreach (var orphan in Directory.EnumerateFiles(
                         modelsDirectory,
                         "*" + DownloadSidecar.TempSuffix + DownloadSidecar.Suffix,
                         SearchOption.AllDirectories))
            {
                try
                {
                    var owner = orphan[..^DownloadSidecar.Suffix.Length];
                    if (File.Exists(owner)) continue;
                    var size = new FileInfo(orphan).Length;
                    File.Delete(orphan);
                    freed += size;
                }
                catch { /* in use or vanished: leave it */ }
            }
        }
        catch { /* the walk failing is not fatal */ }

        return freed;
    }

    /// <summary>
    /// Reclaim the superseded models under <paramref name="modelsDirectory"/>: delete
    /// each planned model's folder (only when its <c>installed.json</c> marks it a
    /// COMPLETE download — a partial download is left for the downloader to resume)
    /// and clear its <c>installed</c> flag. Best-effort per model; never throws.
    /// Returns the number reclaimed.
    /// </summary>
    /// <param name="catalog">This device's catalogue.</param>
    /// <param name="modelsDirectory">Where the model folders live.</param>
    /// <param name="logger">Verbose diagnostics; a no-op logger by default.</param>
    /// <param name="keep">Models never to touch - the one the brain is running.</param>
    /// <param name="say">
    /// Where to report what was done, one line per model and a count at the end.
    /// </param>
    /// <remarks>
    /// SAID, NOT ONLY LOGGED. ILogger reaches nothing on Android unless a host wires
    /// a provider, so a deletion reported only there is a deletion nobody can see.
    /// The count line is written even when it is zero: a sweep that says nothing is
    /// indistinguishable from a sweep that never ran.
    /// </remarks>
    public static int Reclaim(IModelCatalog catalog, string modelsDirectory, ILogger? logger = null,
                              Func<string, bool>? keep = null, Action<string>? say = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var log = logger ?? NullLogger.Instance;
        if (string.IsNullOrWhiteSpace(modelsDirectory)) return 0;

        var plan = Plan(catalog, keep);
        var reclaimed = 0;
        foreach (var item in plan)
        {
            try
            {
                var dir = Path.Combine(modelsDirectory, item.Id);
                if (!File.Exists(Path.Combine(dir, "installed.json")))
                {
                    log.LogDebug("Model housekeeping: {Id} has no installed.json; leaving it (not a complete download).", item.Id);
                    say?.Invoke($"left {item.Id}: not a complete download");
                    continue;
                }
                Directory.Delete(dir, recursive: true);
                catalog.SetInstalled(item.Id, false);
                reclaimed++;
                log.LogInformation("Model housekeeping: reclaimed {Id} — {Reason}.", item.Id, item.Reason);
                say?.Invoke($"reclaimed {item.Id}: {item.Reason}");
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Model housekeeping: failed to reclaim {Id}; it stays on disk.", item.Id);
                say?.Invoke($"could not reclaim {item.Id}: {ex.Message}");
            }
        }
        if (reclaimed > 0)
            log.LogInformation("Model housekeeping: reclaimed {Count} superseded model(s).", reclaimed);
        say?.Invoke($"planned={plan.Count} reclaimed={reclaimed}");
        return reclaimed;
    }
}
