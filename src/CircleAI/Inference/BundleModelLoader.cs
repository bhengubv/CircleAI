#nullable enable

// BundleModelLoader.cs
//
// IModelLoader that understands the *bundle* registry shape — the shape every
// entry in the embedded registry actually uses today.
//
// Why this exists (two concrete defects in LocalModelLoader):
//
//   1. LocalModelLoader.DownloadModelAsync THROWS on any entry with
//      BundleFiles[], telling the caller to "use ModelDownloadService.
//      EnsureBundleAsync via MnnInferenceBridgeFactory instead". Since every
//      registry entry is bundle-shaped, that loader cannot fetch any current
//      model — so AIService.StartAsync could never download one.
//
//   2. LocalModelLoader.GetModelPath returns "<modelDir>/llm.mnn.weight" (the
//      SHA anchor). AIService passes that straight into QwenTextGenerator ->
//      mnn_llm_create, but MNN-LLM's Llm::create() expects the *config.json*
//      path (see MnnInferenceBridgeFactory, which passes
//      Path.Combine(modelDir, "config.json")). So even a fully-downloaded
//      bundle would hand MNN the weight blob instead of its config.
//
// This loader mirrors what MnnInferenceBridgeFactory already does server-side,
// but behind the IModelLoader seam that AIService actually calls — so an
// on-device host gets device-aware selection -> download -> load with no wiring.
//
// The weight file remains the integrity anchor (largest file, so a hash
// mismatch is the most diagnostic); it is just no longer the *load* path.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;
using CircleAI.Core.Models;

namespace CircleAI.Inference;

/// <summary>
/// <see cref="IModelLoader"/> backed by <see cref="ModelDownloadService"/> and the
/// embedded model registry. Handles both the bundle shape (MNN multi-file models)
/// and the legacy single-file shape.
/// </summary>
public sealed class BundleModelLoader : IModelLoader
{
    /// <summary>What MNN-LLM's <c>Llm::create()</c> actually loads.</summary>
    private const string ConfigFileName = "config.json";

    /// <summary>
    /// Canonical weight file — present in every MNN-LLM bundle and the largest
    /// member, so a SHA-256 mismatch here is the most diagnostic integrity check.
    /// </summary>
    private const string AnchorFileName = "llm.mnn.weight";

    private readonly ModelRegistryService _registry;
    private readonly ModelDownloadService _downloads;
    private readonly string _storageRoot;
    private readonly bool _ownsRegistry;
    private readonly IModelDownloadGate? _gate;
    private bool _disposed;

    /// <param name="modelDirectory">
    /// Root directory for cached models. Defaults to
    /// <c>{ApplicationData}/CircleAI/Models</c>. Mobile hosts should pass the
    /// app's own data directory.
    /// </param>
    /// <param name="registry">
    /// Shared registry instance. When <c>null</c> the loader constructs (and
    /// disposes) its own from the embedded catalog.
    /// </param>
    /// <param name="gate">
    /// Optional policy deciding whether a large download may proceed — e.g. not
    /// on mobile data. <c>null</c> means no gate, matching the pre-2026-07-20
    /// behaviour where AIOptions.WifiOnlyModelDownload was inert.
    /// </param>
    public BundleModelLoader(
        string? modelDirectory = null,
        ModelRegistryService? registry = null,
        IModelDownloadGate? gate = null)
    {
        _gate = gate;
        // See ModelPaths: this used SpecialFolder.ApplicationData, which on
        // Android is a subdirectory of the folder the app actually uses, so a
        // caller that passed nothing downloaded a second copy of every model.
        _storageRoot = CircleAI.Core.ModelPaths.Resolve(modelDirectory);

        Directory.CreateDirectory(_storageRoot);

        _registry     = registry ?? new ModelRegistryService();
        _ownsRegistry = registry is null;
        _downloads    = new ModelDownloadService(_storageRoot);
    }

    /// <summary>
    /// Ensures the model is on disk and returns the path the generator should
    /// load — <c>config.json</c> for bundles, the weight file for legacy entries.
    /// </summary>
    /// <summary>
    /// Same as <see cref="DownloadModelAsync(string, IProgress{float})"/>, but
    /// reporting bytes, rate, ETA, which file and what phase.
    /// </summary>
    /// <remarks>
    /// A BARE RATIO IS NOT ENOUGH TO WAIT ON, and how much it is not enough by
    /// depends entirely on the phone. The same 22.8 GB bundle is minutes on a
    /// premium handset and the better part of an hour on a P30 Lite over 48 Mbps
    /// — measured, on both a single stream and eight. A percentage that creeps
    /// is indistinguishable from a hang at the slow end, and the slow end is
    /// exactly where the people this is built for are.
    /// <para>
    /// The download service has computed all of this all along and the ratio-only
    /// overload threw it away one call before the screen. This is that call, not
    /// throwing it away.
    /// </para>
    /// </remarks>
    public Task<string> DownloadModelAsync(
        string modelName, IProgress<CircleAI.Core.DownloadProgress>? progress,
        CancellationToken ct = default)
        => DownloadCoreAsync(modelName, progress, ct);

    public async Task<string> DownloadModelAsync(string modelName, IProgress<float>? progress = null)
    {
        // Adapt onto the rich path so there is one implementation, not two that
        // drift.
        IProgress<CircleAI.Core.DownloadProgress>? rich = progress is null
            ? null
            : new Progress<CircleAI.Core.DownloadProgress>(p => progress.Report((float)p.Ratio));

        return await DownloadCoreAsync(modelName, rich, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Raised in this process when a model that was not here has finished arriving,
    /// whichever path fetched it. The argument is the registry name.
    /// </summary>
    /// <remarks>
    /// "START THE WAKE WORD WHEN ITS BUNDLE LANDS" WAS WIRED INTO ONE CALLER OF FOUR.
    /// The first fix for a phone that could not hear its name after a fresh install
    /// passed a callback to the two loops in ModelSetup, which the background fetch
    /// service drives - and the repo has three other pull paths: setup started from
    /// the app (DeviceSetup -> FirstRun.RunAsync), the abilities row's Turn on, and
    /// anything that calls this loader directly. A bundle landed by any of them
    /// started nothing, and the phone stayed deaf with every byte it needed on disk:
    /// the exact defect the callback was added to end, surviving in the paths it did
    /// not reach.
    /// <para>
    /// Every download in this process goes through <see cref="DownloadModelAsync(string, IProgress{CircleAI.Core.DownloadProgress}, CancellationToken)"/>
    /// and through InFlightDownloads, so this is the one place that can say it once
    /// and say it for all of them. Static because the thing that wants to know - the
    /// resident listener - cannot hold a reference to a loader built inside a loop it
    /// has never heard of.
    /// </para>
    /// <para>
    /// Raised only when the model was ABSENT before the call: re-verifying a bundle
    /// that is already on disk is not an arrival, and a listener that restarts the
    /// microphone on every census would be its own bug. Subscribers are called one by
    /// one and an exception from one is swallowed - the bytes are down either way.
    /// </para>
    /// </remarks>
    public static event Action<string>? ModelLanded;

    private Task<string> DownloadCoreAsync(
        string modelName, IProgress<CircleAI.Core.DownloadProgress>? progress, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        // ASKED BEFORE THE BYTES MOVE, so "it landed" cannot be confused with "it was
        // already here": this method is also how a present bundle is re-verified.
        var wasHere = ModelPresent(modelName);

        // ONE DOWNLOAD PER MODEL IN THIS PROCESS - see InFlightDownloads. Two paths
        // in the service, each with its own loader, fetched the same 548 MB at once on
        // the P30 and wrote the same .tmp until one of them died.
        var key = Path.GetFullPath(Path.Combine(_storageRoot, modelName));
        return InFlightDownloads.RunAsync(
            key, progress,
            relay => DownloadOnceAsync(modelName, relay, ct),
            joined: _ => Console.WriteLine(
                $"CIRCLEAI-DOWNLOAD {modelName}: joined the download already in flight"),
            completed: _ => { if (!wasHere) Arrived(modelName); });
    }

    /// <summary>Tell every listener that <paramref name="modelName"/> is here now.</summary>
    private static void Arrived(string modelName)
    {
        var landed = ModelLanded;
        if (landed is null) return;

        foreach (var one in landed.GetInvocationList().Cast<Action<string>>())
        {
            try { one(modelName); }
            catch (Exception ex)
            {
                // NAMED, NOT SWALLOWED SILENTLY. A listener that throws here means
                // something that was waiting for this model did not start, and the
                // download log is the only place that knows the model is on disk.
                Console.WriteLine(
                    $"CIRCLEAI-DOWNLOAD {modelName}: is here, and what was waiting for it failed: {ex.Message}");
            }
        }
    }

    private async Task<string> DownloadOnceAsync(
        string modelName, IProgress<CircleAI.Core.DownloadProgress>? progress, CancellationToken ct)
    {
        var entry = _registry.GetLatestModel(modelName)
            ?? throw new ArgumentException(
                $"Model '{modelName}' is not in the registry.", nameof(modelName));

        // Metered-connection gate. Checked BEFORE any bytes move, and skipped
        // when the bundle is already cached — re-verifying an on-disk model
        // must never be refused for being "on mobile data".
        if (_gate is not null && !ModelExists(modelName))
        {
            var blocked = _gate.BlockReason(entry.TotalBytes);
            if (blocked is not null)
                throw new ModelDownloadBlockedException(blocked);
        }

        var relay = progress;

        if (entry.IsBundle)
        {
            if (string.IsNullOrWhiteSpace(entry.Repo))
                throw new InvalidOperationException(
                    $"Registry entry '{modelName}' has BundleFiles but no Repo — bundle URLs cannot be built.");

            var spec = ToSpec(entry.BundleFiles!);

            // Pass entry.Source explicitly. The 5-arg overload hard-codes
            // ModelScope, so every HuggingFace-hosted entry (all the speech
            // models — whisper.cpp, Piper) would be fetched from ModelScope URLs
            // that do not exist. It fails as a 404 at download time, far from
            // the registry entry that actually caused it.
            var modelDir = await _downloads
                .EnsureBundleAsync(modelName, entry.Repo!, entry.Source, spec, relay, ct)
                .ConfigureAwait(false);

            // Stamp installed.json so ModelRegistryService.CheckForUpgradesAsync
            // can detect drift later. Best-effort — never fail the load for it.
            try
            {
                await _downloads.WriteInstalledManifestAsync(
                        modelDir, modelName, entry.Version, entry.Repo, spec, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch { /* manifest is advisory */ }

            return ResolveLoadPath(entry, modelDir, modelName);
        }

        // Legacy single-file entry.
        if (string.IsNullOrWhiteSpace(entry.Url) ||
            !Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException(
                $"Registry entry '{modelName}' has neither BundleFiles nor a valid Url.");

        // The single-file path still speaks ratio-only; it is used by the legacy
        // entries, none of which are large enough for the difference to matter.
        IProgress<double>? ratio = relay is null
            ? null
            : new Progress<double>(d => relay.Report(new CircleAI.Core.DownloadProgress
            {
                FileName      = modelName,
                BytesReceived = (long)(d * Math.Max(0, entry.TotalBytes)),
                TotalBytes    = Math.Max(0, entry.TotalBytes),
            }));

        return await _downloads
            .EnsureModelAsync(modelName, uri, entry.Checksum, ratio, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Path the generator should load. Returns the expected location even when
    /// the file is not present yet — callers (AIService) test
    /// <see cref="File.Exists(string)"/> and download when it is missing.
    /// </summary>
    public string GetModelPath(string modelName)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var entry = _registry.GetLatestModel(modelName)
            ?? throw new FileNotFoundException($"Model '{modelName}' is not in the registry.");

        if (!entry.IsBundle)
            return Path.Combine(_storageRoot, modelName + ".gguf"); // single-file layout

        // Bundles land in {root}/{modelId}/. WHICH file the runtime loads is
        // modality-specific — resolve it when the bundle is on disk; otherwise
        // return the conventional chat anchor so callers can File.Exists-test
        // and trigger a download.
        var modelDir = Path.Combine(_storageRoot, modelName);
        if (Directory.Exists(modelDir))
        {
            try { return ResolveLoadPath(entry, modelDir, modelName); }
            catch (InvalidOperationException) { /* not fully downloaded yet */ }
        }
        return Path.Combine(modelDir, ConfigFileName);
    }
    /// <summary>The best chat model this device actually HAS, or null.</summary>
    /// <remarks>
    /// THE SELECTOR RANKS THE CATALOGUE, NOT THE PHONE. DeviceAwareModelSelector
    /// scores every entry in the registry by capability, RAM and storage fit, and
    /// has no idea which of them were ever downloaded - so it can name a model that
    /// is not here while a better one sits on disk.
    ///
    /// Measured on a Circle OS device (Tensor G2, 7.6 GB) on 2026-10-03:
    ///
    ///   wanted:  Qwen3.5-0.8B-MNN          <- not downloaded
    ///   on disk: Qwen3.5-2B-MNN, Qwen3.6-35B-A3B-MNN, SmolVLM-256M-Instruct-MNN
    ///
    /// A phone carrying a complete 1.17 GB 2B was told it had no model at all.
    ///
    /// NOT A CHANGE TO THE RANKER, deliberately - OPEN-GAPS E15 is what happens
    /// when that is done on a bad measurement. This answers a different question
    /// the ranker was never asked: of the models actually PRESENT, which is best.
    /// The service uses it only when the ranker's own choice is missing.
    /// </remarks>
    /// <param name="probe">
    /// The device, so this cannot hand back something that does not fit. Omitting it
    /// keeps the old behaviour and is only correct when the caller has no probe.
    /// </param>
    /// <param name="refused">
    /// Models this device has already refused from evidence. Null refuses nothing.
    /// </param>
    public string? BestInstalledChatModel(DeviceProbe? probe = null, Func<string, bool>? refused = null)
    {
        try
        {
            ThrowIfDisposed();
            if (!Directory.Exists(_storageRoot)) return null;

            // TWO ANSWERS, BECAUSE THE QUESTION HAS TWO HALVES - see the long
            // comment below. "Fits right now" and "could ever fit" are both
            // real, both have taken a phone down when used alone, and the right
            // pick is the best of the first and the SMALLEST of the second.
            string? bestNow = null;
            var bestNowRank = int.MinValue;

            string? smallestEver = null;
            var smallestEverGb = double.MaxValue;

            foreach (var dir in Directory.EnumerateDirectories(_storageRoot))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name)) continue;

                var entry = _registry.GetLatestModel(name);
                if (entry is null || entry.Modality != ModelModality.Chat) continue;

                // NOT A RETIRED ONE, HOWEVER COMPLETE ON DISK. This fallback is the
                // route that put Qwen3-0.6B in charge of the P30 while the model it
                // stands in for was missing. "Nothing less than Qwen3.5 is the LLM":
                // a missing Qwen3.5 is fetched, never substituted.
                if (entry.Retired) continue;

                // PRESENT, NOT MERELY NAMED. A half-finished download has a
                // directory too, and handing one to an inference engine is how a
                // phone gets a native crash instead of an answer.
                if (!ModelPresent(name)) continue;

                // AND IT HAS TO BE SOMETHING THIS PHONE COULD EVER HOLD, which this
                // did not ask and it took the phone down with it. Ranking by
                // QualityRank alone made this fallback OVERRULE the selector it
                // stands in for, so the heaviest thing on disk won whatever the
                // device could hold.
                //
                // CouldEverHold AND NOT Fits, and that distinction cost a regression.
                // Fits reads FREE RAM, so with 1.8 GB spare on a 7.6 GB phone it
                // refused a complete 1.4 GB model too and the service reported "no
                // model on this device yet" with the model sitting on disk. This
                // fallback answers a durable question - of what is HERE, what could
                // this phone ever run - and free RAM is not a durable input.
                //
                // Measured on a Circle OS device (Tensor G2, 7.6 GB) on 2026-10-04,
                // the moment a 22.8 GB Qwen3.6-35B-A3B finished downloading:
                //
                //   the selector wanted Qwen3-1.7B-MNN, which is not on this phone;
                //   using Qwen3.6-35B-A3B-MNN, which is
                //   -> Process ... has died: signal 6 (Aborted)
                //   -> lowmemorykiller: Kill 'com.android.contacts' ... 'camera2'
                //      ... 'calendar' ... 'za.co.circleos.settings' (ten more)
                //
                // It did not just fail to answer. It aborted and took a dozen system
                // apps with it, because the fit gate the selector had already applied
                // was discarded here. The selector was RIGHT and this overrode it.
                if (probe is not null && !ModelFit.CouldEverHold(entry, probe)) continue;

                // AND NOT ONE THIS PHONE HAS ALREADY BEEN KILLED BY. The fit gate
                // above is a prediction from declared metadata; this is the result of
                // an attempt. On the device that found it, the prediction said yes to
                // a 22.8 GB MoE on 7.6 GB because the metadata claimed 2.5 GB, and
                // the attempt aborted the process and took a dozen system apps down.
                // A guard that only ever recomputes the prediction picks it again
                // every launch, forever.
                if (refused is not null && refused(name)) continue;

                // BOTH RULES, NOT ONE, AND THIS IS THE THIRD TIME THIS LINE HAS
                // BEEN THE BUG.
                //
                // The comment above is right that Fits is the wrong DURABLE
                // question - it refused a complete 1.4 GB model on a 7.6 GB
                // phone with 1.8 GB spare and reported "no model on this device
                // yet". Replacing it with CouldEverHold fixed that and created
                // the opposite failure, measured on the P30 on 2026-10-10:
                //
                //   MemAvailable 982 MB, Qwen3.5-2B declares 1.9 GB
                //   -> Killing 7875:...circleai.service (adj 100): iAwareF[LowMem]
                //   -> Killing 6840:com.huawei.lbs
                //   -> Killing 8185:com.huawei.android.totemweather
                //
                // Not just our process - a system-wide low-memory event, from
                // loading a model that is only 49% of total RAM. CouldEverHold
                // said yes because 1.9 < 3.867 x 0.90, and free RAM is exactly
                // what it deliberately ignores.
                //
                // So: prefer the best model that fits RIGHT NOW, and keep the
                // smallest one this phone could EVER hold as the answer when
                // nothing fits now. The old regression cannot come back, because
                // a phone that is momentarily full still names a model instead
                // of claiming it has none - it just names a smaller one.
                // A NULL PROBE ASKS NO FIT QUESTION AT ALL, so everything counts
                // as fitting and the highest rank wins - the behaviour before any
                // of this. My first version of this branch required a probe, which
                // silently turned a probe-less caller into "pick the smallest",
                // the opposite of what the comment below it promised.
                if (probe is null || ModelFit.Fits(entry, probe))
                {
                    if (entry.QualityRank > bestNowRank)
                    {
                        bestNowRank = entry.QualityRank;
                        bestNow = name;
                    }
                    continue;
                }

                // Does not fit this second. Remember it as a last resort, smallest
                // first - if nothing fits now, the least-bad is the one most
                // likely to actually load.
                var needGb = ModelFit.EagerGb(entry);
                if (needGb < smallestEverGb)
                {
                    smallestEverGb = needGb;
                    smallestEver = name;
                }
            }

            // A probe-less caller gets the old behaviour: no fit question was
            // asked, so whatever ranked highest among what is present wins.
            var best = bestNow ?? smallestEver;

            return best;
        }
        catch { return null; }
    }


    /// <summary>How much of a model is on disk, against how much it needs.</summary>
    /// <remarks>
    /// "PRESENT" IS A BOOLEAN AND A DOWNLOAD IS NOT. A 22.8 GB model that got 1.1 GB
    /// in before something stopped it is neither here nor absent - and every surface
    /// that asks ModelPresent is told the same thing it would be told about a model
    /// nobody ever started. Found on a Circle OS device on 2026-10-04:
    ///
    ///   embeddings_bf16.bin        1 017 118 720   on disk
    ///   llm.mnn.json                 118 970 661   on disk
    ///   llm.mnn.weight            21 346 165 150   never arrived
    ///   llm.mnn.weight.tmp.parts             278   a resume marker nobody read
    ///
    /// The census called it "not on this phone yet" and quoted the full 22.8 GB, so
    /// the one fact that mattered - that five per cent of it was already down and
    /// resumable - existed only in a directory listing.
    ///
    /// SIZES, NOT HASHES. This is asked on a loading screen about every model in the
    /// catalogue; hashing 21 GB to answer "how far did it get" would be worse than
    /// the question. A truncated file counts what it actually has, which is the
    /// honest answer to how much is left to fetch.
    ///
    /// AND THE BYTES IN FLIGHT COUNT, which is the half this first missed. Only the
    /// FINISHED file names were looked at, so the 8.6 GB sitting in
    /// llm.mnn.weight.tmp was reported as zero and a download two thirds of the way
    /// through looked no different from one that had never started. DownloadSidecar
    /// owns that reading, because the temp file's own LENGTH is not it: a segmented
    /// fetch preallocates to the full size, so the file would claim 21.3 GB the
    /// moment the first socket opened.
    /// </remarks>
    /// <returns>Bytes on disk and bytes needed. Both zero when the model is unknown.</returns>
    public (long Have, long Need) Progress(string modelName)
    {
        try
        {
            ThrowIfDisposed();
            var entry = _registry.GetLatestModel(modelName);
            if (entry?.BundleFiles is null || entry.BundleFiles.Count == 0) return (0, 0);

            var dir = Path.Combine(_storageRoot, modelName);
            long have = 0;
            long need = 0;

            foreach (var f in entry.BundleFiles)
            {
                need += f.SizeBytes;

                // CAPPED AT WHAT IS WANTED, inside Fetched. A file larger than the
                // catalogue says is a different file, not progress, and counting it
                // would report more downloaded than there is to download.
                have += DownloadSidecar.Fetched(Path.Combine(dir, f.Name), f.SizeBytes);
            }

            return (have, need);
        }
        catch { return (0, 0); }
    }

    /// <summary>Has this model been started and not finished?</summary>
    /// <remarks>
    /// THE STATE THE CODE COULD NOT NAME. Missing, partial and present need three
    /// different sentences and three different offers - start, carry on, or nothing -
    /// and until now there were two.
    /// </remarks>
    public bool ModelPartial(string modelName)
    {
        if (ModelPresent(modelName)) return false;
        var (have, need) = Progress(modelName);
        return have > 0 && need > 0;
    }


    /// <summary>The model directories actually sitting in the store, for a log line.</summary>
    /// <remarks>
    /// "NO MODEL ON THIS DEVICE" CAN MEAN TWO OPPOSITE THINGS. Either nothing is
    /// downloaded, or something IS and the selector picked a different one - and
    /// those need opposite fixes. On a Circle OS device on 2026-10-03 the second
    /// happened while a complete 1.17 GB Qwen3.5-2B sat on disk, and nothing in the
    /// log distinguished them.
    ///
    /// DIRECTORY NAMES, NOT A CENSUS. This is for a human reading a log, so it must
    /// be cheap and must never throw - ModelPresent above is the real check, and it
    /// costs a size comparison per model. Listing a folder costs nothing.
    /// </remarks>
    public string InstalledIdsForLog()
    {
        try
        {
            if (!Directory.Exists(_storageRoot)) return "nothing - the store does not exist";

            var names = InstalledIds();
            return names.Count == 0 ? "nothing" : string.Join(", ", names);
        }
        catch (Exception ex) { return "could not be listed: " + ex.Message; }
    }

    /// <summary>The model directories actually sitting in the store.</summary>
    /// <remarks>
    /// THE SENTENCE WAS BEING PARSED BACK INTO DATA. Callers that needed the LIST
    /// were splitting InstalledIdsForLog on commas - so an empty store handed them
    /// a model called "nothing", and a store that did not exist handed them one
    /// called "nothing - the store does not exist". The prose version is for a
    /// human; this is for code, and it is the one the prose is built from so the
    /// two cannot drift.
    /// </remarks>
    public IReadOnlyList<string> InstalledIds()
    {
        try
        {
            if (!Directory.Exists(_storageRoot)) return [];

            return Directory.EnumerateDirectories(_storageRoot)
                            .Select(Path.GetFileName)
                            .Where(n => !string.IsNullOrEmpty(n))
                            .Select(n => n!)
                            .OrderBy(n => n, StringComparer.Ordinal)
                            .ToList();
        }
        catch { return []; }
    }



    /// <summary>
    /// True when the model is cached AND passes its integrity check — the
    /// weight file's pinned SHA-256 for bundles, the file checksum otherwise.
    /// </summary>
    /// <summary>
    /// Is this model present, without hashing it?
    /// </summary>
    /// <remarks>
    /// THE HASH IS FOR LOADING, NOT FOR ASKING. ModelExists verifies the anchor
    /// file's SHA-256, which for the chat model means hashing 470 MB - fine
    /// before you load a model into an inference engine, ruinous on a loading
    /// screen that asks about every model on every launch. That was the four
    /// seconds a census took on a P30.
    ///
    /// "Is it here" is a different question from "is it intact", and a census
    /// asks the first: the anchor file exists at its full catalogued size. A
    /// truncated download fails the size check; a bit-flip it will not catch,
    /// and does not need to - the load path still hashes.
    /// </remarks>
    /// <summary>Whether the registry retires this model below the brain's floor.</summary>
    /// <remarks>
    /// "Nothing less than Qwen3.5 is the LLM": a retired model is never loaded, so
    /// nothing should spend somebody's data finishing its download either.
    /// </remarks>
    public bool IsRetired(string modelName)
    {
        try
        {
            ThrowIfDisposed();
            return _registry.GetLatestModel(modelName)?.Retired == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Whether this build can load the model: a brain needs an engine it ships.</summary>
    /// <param name="modelName">The registry name.</param>
    /// <param name="engines">The engines this build ships; null asks the build.</param>
    /// <remarks>
    /// A DOWNLOAD NOTHING HERE CAN OPEN IS NOT WORTH FINISHING. The Windows service on
    /// 2026-10-19 held 3.6 GB of Ternary-Bonsai-2-27B - a GGUF - in a build that ships
    /// no llama.cpp for Windows, and the resume would have spent 2.3 GB more on it.
    /// Chat models only: the speech stack's engines are not the brain's.
    /// </remarks>
    public bool CanRunHere(string modelName, IReadOnlyCollection<ModelEngine>? engines = null)
    {
        try
        {
            ThrowIfDisposed();
            var entry = _registry.GetLatestModel(modelName);
            if (entry is null || entry.Modality != ModelModality.Chat) return true;
            return (engines ?? DeviceModelAssessor.ShippedEngines()).Contains(entry.Engine);
        }
        catch
        {
            return true;   // cannot tell: a resume is never refused on a guess
        }
    }

    public bool ModelPresent(string modelName)
    {
        try
        {
            ThrowIfDisposed();
            var entry = _registry.GetLatestModel(modelName);
            if (entry is null) return false;

            if (entry.IsBundle)
            {
                var modelDir = Path.Combine(_storageRoot, modelName);
                if (!Directory.Exists(modelDir)) return false;

                if (entry.Modality == ModelModality.Chat &&
                    !File.Exists(Path.Combine(modelDir, ConfigFileName)))
                    return false;

                var anchor = entry.BundleFiles!
                    .FirstOrDefault(f => string.Equals(f.Name, AnchorFileName, StringComparison.OrdinalIgnoreCase));

                // EVERY FILE, FOR A BUNDLE WHOSE PARTS ARE ALL LOAD-BEARING - AND THAT
                // IS WHY A WAKE WORD COULD BE "DOWNLOADED" AND REFUSE TO START FOR EVER.
                //
                // With no llm.mnn.weight to anchor on, this used to take the LARGEST
                // catalogued file as the stand-in for the whole bundle. For
                // KWS-Zipformer-HeyB that is kws-hey-b/encoder.int8.onnx, 4,807,159 of
                // the bundle's 6.76 MB - and it is also the FIRST file fetched. Files
                // arrive one at a time and are renamed into place as they finish, so
                // from the moment the encoder landed the bundle read as PRESENT with
                // 1.96 MB still owed: the decoder, the joiner, tokens.txt, bpe.model
                // and keywords.txt.
                //
                // Anything that then interrupted it - a kill, a cancel, a network that
                // outlasted both URLs' retries, Android 15's dataSync time limit - left
                // a state nothing could get out of. ModelPartial returns false whenever
                // this returns true, so the resume skipped it; NotStarted excludes
                // anything "present", so the fetch skipped it; the first-run plan saw
                // its wake-word slot filled, so setup skipped it. Meanwhile the spotter
                // opens all five files and the listener refused with "the wake word is
                // not downloaded - set CircleAI up and it will fetch what it needs",
                // advice no path on the phone would ever act on.
                //
                // ONE File.Exists AND ONE LENGTH PER ENTRY. For the six-file speech and
                // keyword bundles that is microseconds, and it is the same rule the
                // anchor already used, applied to every file instead of one.
                //
                // THE MNN ANCHOR KEEPS THE OLD RULE on purpose: a chat bundle's weight
                // file IS the bundle - 470 MB to 21 GB of it - its siblings are
                // kilobytes of config, and config.json is already checked above. The
                // trap this fixes needs a bundle whose small files matter, which is
                // exactly the set that has no weight file.
                if (anchor is null)
                    return entry.BundleFiles!.Count > 0
                        && entry.BundleFiles!.All(f =>
                        {
                            var part = new FileInfo(Path.Combine(modelDir, f.Name));
                            return part.Exists && part.Length >= f.SizeBytes;
                        });

                var info = new FileInfo(Path.Combine(modelDir, anchor.Name));
                return info.Exists && info.Length >= anchor.SizeBytes;
            }

            var filePath = Path.Combine(_storageRoot, modelName + ".gguf");
            return File.Exists(filePath);
        }
        catch
        {
            return false;
        }
    }

    public bool ModelExists(string modelName)
    {
        try
        {
            ThrowIfDisposed();
            var entry = _registry.GetLatestModel(modelName);
            if (entry is null) return false;

            if (entry.IsBundle)
            {
                var modelDir = Path.Combine(_storageRoot, modelName);
                if (!Directory.Exists(modelDir)) return false;

                // Chat (MNN) additionally requires config.json to load at all.
                if (entry.Modality == ModelModality.Chat &&
                    !File.Exists(Path.Combine(modelDir, ConfigFileName)))
                    return false;

                // Integrity anchor: the weight file for MNN, else the LARGEST
                // catalogued file (Piper's .onnx, Whisper's ggml .bin) — biggest
                // file means a hash mismatch is the most diagnostic.
                var anchor = entry.BundleFiles!
                    .FirstOrDefault(f => string.Equals(f.Name, AnchorFileName, StringComparison.OrdinalIgnoreCase))
                    ?? entry.BundleFiles!.OrderByDescending(f => f.SizeBytes).FirstOrDefault();
                if (anchor is null) return false;

                var anchorPath = Path.Combine(modelDir, anchor.Name);
                return File.Exists(anchorPath) && VerifySha256(anchorPath, anchor.Sha256);
            }

            var filePath = Path.Combine(_storageRoot, modelName + ".gguf");
            if (!File.Exists(filePath)) return false;
            return entry.Checksum is null || VerifySha256(filePath, entry.Checksum);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Upgrade detection lives in <c>ModelRegistryService.CheckForUpgradesAsync</c>
    /// (surfaced by <c>AIService.CheckForUpgradesAsync</c> and the
    /// <c>installed.json</c> manifests this loader writes), so this loader does
    /// not perform its own out-of-band version check.
    /// </summary>
    public Task<bool> CheckForCriticalUpdateAsync() => Task.FromResult(false);

    // ── helpers ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<BundleFileSpec> ToSpec(IReadOnlyList<BundleFile> files)
        => files.Select(f => new BundleFileSpec(f.Name, f.Sha256, f.SizeBytes)).ToList();

    /// <summary>
    /// The file a bundle's RUNTIME actually loads — which differs by modality.
    /// MNN chat wants config.json; Piper TTS wants the .onnx; Whisper ASR wants
    /// the ggml .bin. Hard-coding config.json (as this did) made the loader
    /// MNN-only and it REJECTED every speech bundle outright.
    /// </summary>
    private static string ResolveLoadPath(ModelEntry entry, string modelDir, string modelName)
    {
        string? Find(params string[] patterns)
        {
            foreach (var p in patterns)
            {
                var hit = Directory.EnumerateFiles(modelDir, p, SearchOption.AllDirectories)
                    .OrderBy(f => f.Length)   // prefer the shallowest/simplest match
                    .FirstOrDefault();
                if (hit is not null) return hit;
            }
            return null;
        }

        var path = entry.Modality switch
        {
            ModelModality.Tts      => Find("*.onnx"),
            ModelModality.Asr      => Find("*.bin", "*.onnx"),
            ModelModality.Vad      => Find("*.onnx"),
            ModelModality.WakeWord => ResolveWakeWord(modelDir) ?? Find("*.onnx", "*.tflite"),
            _                      => Find(ConfigFileName),   // Chat: MNN config.json
        };

        if (path is null)
            throw new InvalidOperationException(
                $"Bundle '{modelName}' ({entry.Modality}) downloaded to '{modelDir}' but no loadable " +
                "model file was found for that modality.");

        return path;
    }

    /// <summary>
    /// A wake bundle's load path: the DIRECTORY when it holds a three-graph
    /// transducer, otherwise null so the single-file rule applies.
    /// </summary>
    /// <remarks>
    /// A streaming transducer is encoder + decoder + joiner + tokens, and no one
    /// of those files is loadable on its own — the runtime needs the folder around
    /// them. Returning "the shortest .onnx name" for such a bundle hands back an
    /// arbitrary third of a model, which cannot be made to work and does not look
    /// broken: it is a real path to a real file. The first consumer worked around
    /// it by scanning for an encoder and taking its directory, and every later one
    /// would have had to rediscover the same trick.
    /// </remarks>
    private static string? ResolveWakeWord(string modelDir)
    {
        var byDirectory = Directory
            .EnumerateFiles(modelDir, "*.onnx", SearchOption.AllDirectories)
            .GroupBy(f => Path.GetDirectoryName(f)!);

        foreach (var group in byDirectory)
        {
            var names = group.Select(f => Path.GetFileName(f).ToLowerInvariant()).ToList();
            if (names.Any(n => n.Contains("encoder")) &&
                names.Any(n => n.Contains("decoder")) &&
                names.Any(n => n.Contains("joiner")))
                return group.Key;
        }
        return null;
    }

    /// <summary>
    /// Compares a file's SHA-256 against an expected value in either
    /// <c>sha256:&lt;hex&gt;</c> or bare-hex form.
    /// </summary>
    private static bool VerifySha256(string filePath, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return false;

        var want = expected.Trim();
        if (want.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            want = want["sha256:".Length..].Trim();

        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var actual = Convert.ToHexString(sha.ComputeHash(stream));

        return string.Equals(want, actual, StringComparison.OrdinalIgnoreCase);
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _downloads.Dispose();
        if (_ownsRegistry) _registry.Dispose();
    }
}
