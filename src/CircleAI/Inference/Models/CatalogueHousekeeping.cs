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
//   2. Another INSTALLED model K of the SAME modality is the better brain, by the
//      one rule every pick uses (ModelChoice.Better: the Bonsai 2 fleet first, then
//      quality).
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
// AND RULE 3 DOES NOT PROTECT A MODEL FROM A BRAIN THE DEVICE READS FROM FLASH
// (the owner, 2026-11-13: "did housekeeping do its job and make space by removing
// old LLM? 2B or 9B or 27B house keeping must make the space"). The rule keeps a
// smaller model because it is the one that at least FITS. A brain the engine
// computes from flash (ModelFit.ReadFromFlash) fits wherever its file does - "RAM
// is the turbo, not the engine" - so nothing smaller is a fallback for it; it is
// the old LLM, and the flash it holds is what the best one's index needs. That P30
// held the 2B beside the 27B, fourteen times its size, with 4.8 GB free, and this
// policy would have kept both for good. Rules 1, 2, 4 and 5 stand: the 27B removes
// nothing until it has loaded on the phone.
//
// AND "BETTER" HAS ONE OWNER (2026-11-13, the same phone: "Qwen3.5-2B-MNN (1.4 GB)
// ... untouched. ==>> flash not RAM"). Rule 2 asked the quality rank alone. The
// selector does not: a fleet brain the device can hold comes before any other
// engine's, whatever their ranks (ModelChoice.Best, the owner on 2026-11-04). So
// the fleet's 2B and Qwen3.5-2B-MNN, both rank 9, were neither better than the
// other here - the old one stayed, 1.4 GB of a brain the selector never picks -
// and a brain of another engine one rank above the fleet's would have had the
// selector's own pick removed for it. Rule 2 asks ModelChoice.Better, so what is
// kept is what the selector picks - and a brain outside the family goes for one
// of the family by the rule below, whatever their ranks.
//
// AND A BRAIN OUTSIDE THE BONSAI 2 FAMILY GOES, whatever its size or its rank, once
// a brain of the family has loaded here and answers (the owner, 2026-11-13: "Qwen
// should be gone as we are currently working only with Bonsai 2 family - 27B 9B 4B
// and 2B. All other must go and make space on FLASH"). Rule 3 kept a far smaller
// model as the one that at least fits, and rule 2 kept one ranked above the
// family's brain; neither is ever picked again while a brain of the family runs
// here (ModelChoice.Best: the fleet first), so neither is a fallback - it is flash.
// Rules 4 and 5 stand, so no device is left without a brain: nothing goes for a
// family brain that has not loaded here, that the device refused, that cannot
// answer yet, or that does not run here while the other one does.
//
// AND A RETIRED MODEL IS DEAD WEIGHT, whatever its size, once any model above the
// floor has loaded here. "Nothing less than Qwen3.5 is the LLM": a retired model can
// never be the brain again, so it is not a fallback; it is disk.
//
// AND SO IS A BRAIN WITH NOTHING LEFT TO TRY HERE: its index failed its check with every
// neuron listed, by this engine (the owner, 2026-11-14, of the P30's 27B and 9B: "Yes,
// remove them"; and 2026-11-15: "no models should be blocked - if it fails then check why
// it failed. clean up and try again, everything depends on the reason why"). A brain
// whose index failed at a smaller share STAYS: its next index lists more of its neurons
// (IndexingRungs), and it needs its file for that. One with nothing left to try answers
// nobody on this device until the engine changes (BrainInWaiting.FailedHere), is not the
// brain meanwhile (BrainInWaiting.PastAFailedIndex) and is nobody's fallback. Rules 4 and
// 5 stand: it goes only for a brain that has loaded here and answers, so a device whose
// only brain it is keeps it, and that brain answers as well as it can. The record of its
// check stays behind in the engine's folder: it says what was tried (BrainInWaiting.StepOf).
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
    /// <param name="readFromFlash">
    /// Whether this device's engine computes a brain from flash as it goes (ModelFit.ReadFromFlash, with the device's
    /// probe). Such a brain makes a lesser one redundant whatever their sizes. Null says no brain is.
    /// </param>
    /// <param name="ready">
    /// Whether a brain answers as it should on this device now (BrainInWaiting.Ready). One that does not - read from
    /// flash, its index still being built - replaces nothing yet. Null says every brain is ready.
    /// </param>
    /// <param name="failedHere">
    /// Whether nothing is left to try of a brain on this device - its index failed its check with every neuron listed
    /// (BrainInWaiting.FailedHere): such a brain goes once another has loaded here and answers. Null says none has.
    /// </param>
    public static IReadOnlyList<HousekeepingReclaim> Plan(IModelCatalog catalog, Func<string, bool>? keep = null,
                                                          Func<ModelEntry, bool>? readFromFlash = null,
                                                          Func<ModelEntry, bool>? ready = null,
                                                          Func<ModelEntry, bool>? failedHere = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var installed = catalog.AllAssessed()
            .Where(a => a.Installed && a.Entry.Modality == ModelModality.Chat)
            .ToList();
        if (installed.Count < 2) return Array.Empty<HousekeepingReclaim>();

        // THE ONLY MODELS ALLOWED TO REPLACE ANOTHER: loaded here, not refused, not
        // retired. Proof, not prediction - `compatible` is today's free-RAM reading
        // and says no to a model the P30 runs every day.
        //
        // AND READY TO ANSWER (2026-11-13). A brain read from flash whose index is still being built has loaded here
        // and answers at seconds a token; the brain beside it is what the phone answers with until that index is
        // complete (BrainInWaiting), so it is not clutter yet. The space is made when the replacement can take over.
        bool IsReady(ModelEntry entry)
        {
            try { return ready?.Invoke(entry) != false; }
            catch { return true; }
        }

        var keepers = installed
            .Where(k => k.Proven && !k.Refused && !k.Entry.Retired && IsReady(k.Entry))
            .ToList();
        if (keepers.Count == 0) return Array.Empty<HousekeepingReclaim>();

        // BEST FIRST, AND A MODEL BEING REMOVED REPLACES NOTHING. Decided in one pass
        // over a fixed list of keepers, a ladder of steps each within the size rule -
        // 1.0 GB, 1.9 GB, 3.7 GB - removed both lower rungs, leaving 3.7x the size of
        // the fallback rule 3 promises to keep. A replacement is the better brain
        // (ModelChoice.Better), so walking down from the best by that same order
        // settles each one's fate before anything below asks it to stand in.
        bool FromFlash(ModelEntry entry)
        {
            try { return readFromFlash?.Invoke(entry) == true; }
            catch { return false; }
        }

        // A reading that fails is no sign the index failed: the brain stays.
        bool Failed(ModelEntry entry)
        {
            try { return failedHere?.Invoke(entry) == true; }
            catch { return false; }
        }

        var planned = new HashSet<string>(StringComparer.Ordinal);
        var reclaim = new List<HousekeepingReclaim>();
        foreach (var a in installed.OrderByDescending(x => ModelChoice.Fleet(x.Entry)).ThenByDescending(x => x.Entry.QualityRank))
        {
            var m = a.Entry;
            if (keep is not null && keep(m.Name)) continue;

            if (m.Retired)
            {
                var best = ModelChoice.Best(keepers.Select(k => k.Entry))!;
                reclaim.Add(new HousekeepingReclaim(
                    m.Name,
                    $"retired below the brain's floor, and {best.Name} has loaded here"));
                planned.Add(m.Name);
                continue;
            }

            // NOTHING IS LEFT TO TRY OF IT HERE (its index failed with every neuron listed): it answers nobody, and goes
            // for a brain that has loaded here and answers - never for one being removed, nor for one with nothing left to
            // try as well. With none, it stays: it is this device's only brain, and answers as well as it can.
            if (Failed(m))
            {
                var answering = ModelChoice.Best(keepers
                    .Where(k => k.Entry.Name != m.Name && !planned.Contains(k.Entry.Name) && !Failed(k.Entry)
                                && (k.Compatible || !a.Compatible))
                    .Select(k => k.Entry));
                if (answering is not null)
                {
                    reclaim.Add(new HousekeepingReclaim(m.Name,
                        $"its index failed its check here with every neuron listed - nothing is left to try until the engine changes - and {answering.Name} has loaded here and answers"));
                    planned.Add(m.Name);
                    continue;
                }
            }

            // OUTSIDE THE BONSAI 2 FAMILY IS THE OLD LLM, WHATEVER ITS SIZE OR ITS RANK ("All other must go and make
            // space on FLASH"): once a brain of the family has loaded here and answers, no other engine's brain is
            // picked again, so it is not a fallback. With no such brain here, the rules below judge it as before.
            if (!ModelChoice.Fleet(m))
            {
                var family = ModelChoice.Best(keepers
                    .Where(k => ModelChoice.Fleet(k.Entry) && !planned.Contains(k.Entry.Name) && (k.Compatible || !a.Compatible))
                    .Select(k => k.Entry));
                if (family is not null)
                {
                    reclaim.Add(new HousekeepingReclaim(m.Name, $"outside the Bonsai 2 fleet, and {family.Name} has loaded here"));
                    planned.Add(m.Name);
                    continue;
                }
            }

            // THE SIZE RULE KEEPS THE MODEL THAT AT LEAST FITS, so it is not asked of a keeper read from flash: that
            // one fits wherever its file does, and a smaller model beside it is the old LLM, not a fallback.
            var dominator = keepers.FirstOrDefault(k =>
                !planned.Contains(k.Entry.Name) &&
                k.Entry.Name != m.Name &&
                ModelChoice.Better(k.Entry, m) &&
                k.Entry.TotalBytes > 0 && m.TotalBytes > 0 &&
                (k.Entry.TotalBytes <= (long)(m.TotalBytes * SizeDominanceFactor) || FromFlash(k.Entry)) &&
                (k.Compatible || !a.Compatible))?.Entry;

            if (dominator is not null)
            {
                // BETTER BY QUALITY: the two are both of the family, or neither is and no brain of the family runs here -
                // a brain outside the family went above for one that does.
                var size = FromFlash(dominator) && dominator.TotalBytes > (long)(m.TotalBytes * SizeDominanceFactor)
                    ? "read from flash here"
                    : "similar size";
                reclaim.Add(new HousekeepingReclaim(m.Name,
                    $"superseded by {dominator.Name} (quality {dominator.QualityRank} > {m.QualityRank}, {size}, loaded here)"));
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
    /// Remove the model folders under <paramref name="modelsDirectory"/> that hold nothing at all and
    /// have not been written to for <paramref name="idleFor"/>, and report how many.
    /// </summary>
    /// <remarks>
    /// A DOWNLOAD THAT STOPS BEFORE ITS FIRST BYTE LEAVES ITS FOLDER BEHIND (OPEN-GAPS E29): the
    /// downloader makes a model's folder before it asks for anything, and nothing looked at it again.
    /// On the P30 on 2026-11-02 the store held an empty Ternary-Bonsai-2-2B: the setup line called
    /// it "nothing yet" though nothing was fetching it, and the soak's engine check took it for the
    /// model and failed while the 2B sat in the sideload folder.
    /// <para>
    /// EMPTY MEANS NOTHING AT ALL - no file, no sidecar, no subfolder - so nothing resumable or
    /// installed is ever touched; IDLE keeps a download that is starting this minute out of it, as
    /// the .tmp sweep above does; and the delete itself refuses a folder that has gained a file
    /// since it was looked at.
    /// </para>
    /// </remarks>
    public static int SweepEmptyFolders(string modelsDirectory, TimeSpan idleFor)
    {
        if (string.IsNullOrWhiteSpace(modelsDirectory) || !Directory.Exists(modelsDirectory))
            return 0;

        var cutoff = DateTime.UtcNow - idleFor;
        var removed = 0;
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(modelsDirectory))
            {
                try
                {
                    if (Directory.EnumerateFileSystemEntries(folder).Any()) continue;
                    if (Directory.GetLastWriteTimeUtc(folder) > cutoff) continue;   // a download may be starting
                    Directory.Delete(folder, recursive: false);
                    removed++;
                }
                catch { /* gained a file, in use or vanished: leave it */ }
            }
        }
        catch { /* the walk failing is not fatal */ }

        return removed;
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
    /// <param name="readFromFlash">
    /// Whether this device's engine computes a brain from flash (see <see cref="Plan"/>). Null says no brain is.
    /// </param>
    /// <remarks>
    /// AND IT SAYS WHAT THE STORE HOLDS, EVERY TIME (2026-11-13). "Did housekeeping do its job and make space" had no
    /// answer on the phone: the count went to the console, which a Release build's log file does not keep, and nothing
    /// anywhere listed the store - which adb cannot read either. Each model's folder and its size, what went and what
    /// that freed are said and logged at every pass, whether or not anything was removed.
    /// </remarks>
    /// <param name="ready">Whether a brain answers as it should here now (see <see cref="Plan"/>). Null says every brain is.</param>
    /// <param name="removeStore">
    /// Removes what the engine kept for a model that has just been reclaimed - its stores and its index, outside the
    /// model store - and returns the bytes that gave back. Null removes nothing.
    /// </param>
    /// <remarks>
    /// A BRAIN THAT GOES TAKES WHAT WAS MADE OF IT WITH IT (the owner, 2026-11-14: "we need to clean up after ourselves or
    /// we will kill users phones"). The folder in the model store was all that went; the head's store, and for a brain
    /// that answered through its index gigabytes of pieces, sat on in the app's cache folder under the file's name.
    /// </remarks>
    /// <param name="failedHere">
    /// Whether a brain's index failed its check on this device (see <see cref="Plan"/>). Null says none has.
    /// </param>
    public static int Reclaim(IModelCatalog catalog, string modelsDirectory, ILogger? logger = null,
                              Func<string, bool>? keep = null, Action<string>? say = null,
                              Func<ModelEntry, bool>? readFromFlash = null,
                              Func<ModelEntry, bool>? ready = null,
                              Func<string, long>? removeStore = null,
                              Func<ModelEntry, bool>? failedHere = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var log = logger ?? CircleAI.Core.Logging.CircleLog.Logger("Models.Housekeeping");
        if (string.IsNullOrWhiteSpace(modelsDirectory)) return 0;

        var held = Held(modelsDirectory);
        foreach (var (name, bytes) in held) say?.Invoke($"holds {name}: {Megabytes(bytes)} MB");

        var plan = Plan(catalog, keep, readFromFlash, ready, failedHere);
        var reclaimed = 0;
        long freed = 0;
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
                var size = held.FirstOrDefault(h => string.Equals(h.Name, item.Id, StringComparison.Ordinal)).Bytes;
                Directory.Delete(dir, recursive: true);
                catalog.SetInstalled(item.Id, false);
                reclaimed++;
                freed += size;
                log.LogInformation("Model housekeeping: reclaimed {Id} — {Reason}.", item.Id, item.Reason);
                say?.Invoke($"reclaimed {item.Id}: {item.Reason}");

                // AND WHAT THE ENGINE KEPT FOR IT, outside the model store. The model is gone whatever becomes of this.
                long store = 0;
                try { store = removeStore?.Invoke(item.Id) ?? 0; }
                catch (Exception ex) { log.LogWarning(ex, "Model housekeeping: what the engine kept for {Id} could not be removed.", item.Id); }
                if (store > 0)
                {
                    freed += store;
                    log.LogInformation("Model housekeeping: {Store} MB the engine kept for {Id} went with it.", Megabytes(store), item.Id);
                    say?.Invoke($"with {item.Id} went {Megabytes(store)} MB the engine kept for it");
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Model housekeeping: failed to reclaim {Id}; it stays on disk.", item.Id);
                say?.Invoke($"could not reclaim {item.Id}: {ex.Message}");
            }
        }
        if (reclaimed > 0)
            log.LogInformation("Model housekeeping: reclaimed {Count} superseded model(s).", reclaimed);
        log.LogInformation(
            "Model housekeeping: the store held {Held} ({Total} MB); {Planned} planned, {Reclaimed} reclaimed, {Freed} MB freed.",
            held.Count == 0 ? "nothing" : string.Join(", ", held.Select(h => $"{h.Name} {Megabytes(h.Bytes)} MB")),
            Megabytes(held.Sum(h => h.Bytes)), plan.Count, reclaimed, Megabytes(freed));
        say?.Invoke($"freed {Megabytes(freed)} MB");
        say?.Invoke($"planned={plan.Count} reclaimed={reclaimed}");
        return reclaimed;
    }

    // Every model's folder in the store and the bytes in it, largest first - measured off the disk, whatever the
    // catalogue says of it (a voice, the ears, a half-fetched download: all of it is the store).
    private static List<(string Name, long Bytes)> Held(string modelsDirectory)
    {
        var held = new List<(string Name, long Bytes)>();
        try
        {
            if (!Directory.Exists(modelsDirectory)) return held;
            foreach (var folder in Directory.EnumerateDirectories(modelsDirectory))
            {
                long bytes = 0;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    {
                        try { bytes += new FileInfo(file).Length; } catch { /* vanished or unreadable: not counted */ }
                    }
                }
                catch { /* the folder went while it was read */ }
                held.Add((Path.GetFileName(folder), bytes));
            }
        }
        catch { /* the walk failing is not fatal: housekeeping still runs */ }
        held.Sort((a, b) => b.Bytes != a.Bytes ? b.Bytes.CompareTo(a.Bytes) : string.CompareOrdinal(a.Name, b.Name));
        return held;
    }

    private static string Megabytes(long bytes) =>
        (bytes / 1_000_000.0).ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
}
