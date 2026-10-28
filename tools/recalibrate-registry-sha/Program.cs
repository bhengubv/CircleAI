// recalibrate-registry-sha
//
// Anti-rot tooling for THE REGISTER: src/CircleAI/Core/registry.json, keyed by
// model id. Every entry pins a bundle — Repo + BundleFiles[] where every file
// carries Name + Sha256 + SizeBytes. MNN-LLM needs the COMPLETE bundle
// (config.json, llm.mnn, llm.mnn.weight, tokenizer, llm_config.json,
// configuration.json) to load, not just the weights, so the register must list
// every file or the runtime fails with "config.json not found".
//
// IT WRITES THE REGISTER AND NOTHING ELSE. It used to write
// Models/embedded_registry.json as well, which is now GENERATED from the
// register by tools/sync-registry — two writers of one derived file is the
// drift this repo keeps paying for. Run sync-registry after this tool; it
// prints the command.
//
// IT IS SOURCE-AWARE, AND IT WAS NOT. This polled ModelScope's file-listing API
// for every entry carrying a Repo, and NOT ONE ENTRY IN THE REGISTER COMES FROM
// MODELSCOPE — measured 2026-10-28: HuggingFaceBucket 65, HuggingFace 25,
// GitHubRelease 6, ModelScope 0. A bare run made 96 doomed API calls and
// reported 96 failures. Each entry now goes to the API its own Source names:
//
//   ModelScope        → modelscope.cn file-listing API; SHA-256 comes back for
//                       every file, so the whole bundle is rebuilt from the repo.
//   HuggingFace       → huggingface.co tree API. An LFS blob carries lfs.oid,
//   HuggingFaceBucket   which IS its SHA-256 (verified 2026-10-28 against
//                       taobao-mnn/Qwen2.5-VL-3B-Instruct-MNN), so a multi-GB
//                       weight is pinned without downloading it. A small plain
//                       blob has only a git SHA-1, so those are downloaded and
//                       hashed. ONLY THE FILES THE ENTRY ALREADY NAMES are
//                       refreshed, never the whole tree: thegeekco/circleai-voices
//                       is ONE repo holding 57 voices, and rebuilding from its
//                       listing would merge all 57 into whichever entry ran.
//   GitHubRelease     → not supported. A release tag is not a repo tree; the
//                       entry is named and skipped rather than attempted.
//
// Usage:
//   dotnet run --project tools/recalibrate-registry-sha -- [model-id ...]
//                                                          [--no-sample-verify]
//
//   No model-id args → process every supported entry in the register.
//   One or more model-id args → process only those.
//   --no-sample-verify → skip sample download (fast, but trusts the API).
//
// Exit code:
//   0  all requested entries refreshed
//   1  one or more entries failed (register left untouched for failed entries)
//   2  IO / parse / config error (no entries processed)

using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CircleAI.Tools.RecalibrateRegistrySha;

internal static class Program
{
    // A realistic browser UA — ModelScope's CDN (resolve/master URLs) returns
    // 403 to clients with no UA, which is also why the runtime's
    // ModelDownloadService fails Fallback in production. Tool + runtime must
    // both send one.
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36 CircleAI-Recalibrator/2.0";

    /// <summary>What the register's own Notes field says about itself.</summary>
    /// <remarks>
    /// IT CLAIMED TO BE A MODELSCOPE ARTEFACT AND NOTHING IN IT CAME FROM
    /// MODELSCOPE. The old text read "Auto-populated from ModelScope file-listing
    /// API by tools/recalibrate-registry-sha", which described this tool's original
    /// job rather than the file: measured 2026-10-28 the register held
    /// HuggingFaceBucket 65, HuggingFace 25, GitHubRelease 6, ModelScope 0. Anyone
    /// reading it would re-run this tool against ModelScope to refresh a HuggingFace
    /// pin, which is how the voices went stale for a fortnight.
    /// </remarks>
    private const string RegisterNotes =
        "THE REGISTER. Every model this product knows about, keyed by model id, with "
        + "every field. src/CircleAI/Core/Models/embedded_registry.json is GENERATED "
        + "from this file by tools/sync-registry and must not be hand-edited; run it "
        + "after any change here. Each entry's BundleFiles array lists EVERY file the "
        + "runtime needs to load the model, with the SHA-256 and size it verifies "
        + "against. Pins are refreshed by tools/recalibrate-registry-sha, which reads "
        + "each entry's Source: ModelScope and HuggingFace/HuggingFaceBucket have "
        + "listing APIs it can poll; a GitHubRelease entry has none and is pinned with "
        + "tools/hf-hash or by hand.";

    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string RegistryPath
        = Path.Combine(RepoRoot, "src", "CircleAI", "Core", "registry.json");
    private static readonly string EmbeddedRegistryPath
        = Path.Combine(RepoRoot, "src", "CircleAI", "Core", "Models", "embedded_registry.json");

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var sampleVerify = !args.Contains("--no-sample-verify", StringComparer.OrdinalIgnoreCase);
        var modelArgs = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();

        if (!File.Exists(RegistryPath))
        {
            Console.Error.WriteLine($"registry.json not found at {RegistryPath}");
            return 2;
        }

        JsonObject registry;
        try
        {
            registry = JsonNode.Parse(await File.ReadAllTextAsync(RegistryPath).ConfigureAwait(false))!.AsObject();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to parse {RegistryPath}: {ex.Message}");
            return 2;
        }

        // Pick entries to process. A candidate needs a Repo AND a Source this
        // tool can actually reach — see SourceOf. An entry whose Source has no
        // listing API is reported by name, because "skipped 6" in a summary is
        // how a stale pin survives a run that looked clean.
        var allBundleEntries = registry
            .Where(kv => kv.Value is JsonObject obj && obj["Repo"] is not null)
            .Select(kv => kv.Key)
            .ToList();

        var unsupported = allBundleEntries
            .Where(id => SourceOf(registry, id) is BundleSource.Unsupported)
            .ToList();
        var supported = allBundleEntries.Except(unsupported, StringComparer.Ordinal).ToList();

        var targets = modelArgs.Count == 0 ? supported : modelArgs.Where(supported.Contains).ToList();
        var missing = modelArgs.Where(a => !allBundleEntries.Contains(a)).ToList();
        foreach (var m in missing)
            Console.WriteLine($"⚠  '{m}' is not a bundle entry in the register — skipping.");
        foreach (var m in modelArgs.Where(unsupported.Contains))
            Console.WriteLine(
                $"⚠  '{m}' has Source '{SourceNameOf(registry, m)}', which has no file-listing API "
                + "this tool can read — skipping. Pin it with tools/hf-hash or by hand.");

        if (targets.Count == 0)
        {
            Console.Error.WriteLine(
                "No matching bundle entries — nothing to do."
                + (unsupported.Count > 0
                    ? $" {unsupported.Count} entr{(unsupported.Count == 1 ? "y" : "ies")} in the register "
                      + $"carry an unsupported Source: {string.Join(", ", unsupported)}"
                    : ""));
            return 2;
        }

        if (modelArgs.Count == 0 && unsupported.Count > 0)
        {
            Console.WriteLine(
                $"Unsupported ({unsupported.Count}), not attempted: {string.Join(", ", unsupported)}");
        }

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(60),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        var refreshedBundles = new Dictionary<string, BundleSpec>();
        var failures = new List<string>();

        var tempRoot = Path.Combine(Path.GetTempPath(), "circleai-recalibrate");
        Directory.CreateDirectory(tempRoot);

        Console.WriteLine($"Repo root        : {RepoRoot}");
        Console.WriteLine($"Registry         : {RegistryPath}");
        Console.WriteLine($"Embedded         : {EmbeddedRegistryPath}");
        Console.WriteLine($"Temp DL root     : {tempRoot}");
        Console.WriteLine($"Sample-verify    : {(sampleVerify ? "ON (one file per bundle full-download verified)" : "OFF (trust API)")}");
        Console.WriteLine($"Targets ({targets.Count}): {string.Join(", ", targets)}");
        Console.WriteLine();

        foreach (var modelId in targets)
        {
            try
            {
                var entry = (JsonObject)registry[modelId]!;
                var repo = entry["Repo"]?.GetValue<string>()
                    ?? throw new InvalidOperationException($"{modelId}: missing Repo");

                Console.WriteLine($"── {modelId} ──");
                Console.WriteLine($"  Repo : {repo}");

                var source = SourceOf(registry, modelId);
                Console.WriteLine($"  Source: {SourceNameOf(registry, modelId)}");

                var bundle = source switch
                {
                    BundleSource.ModelScope =>
                        await FetchBundleFromModelScopeAsync(http, repo).ConfigureAwait(false),
                    BundleSource.HuggingFace =>
                        await FetchBundleFromHuggingFaceAsync(http, repo, entry, tempRoot).ConfigureAwait(false),
                    _ => throw new InvalidOperationException(
                        $"{modelId}: Source '{SourceNameOf(registry, modelId)}' has no listing API here"),
                };
                Console.WriteLine($"  Files: {bundle.Files.Count}  TotalBytes: {bundle.TotalBytes:N0}");

                if (sampleVerify)
                {
                    var sample = PickSampleFile(bundle.Files);
                    Console.WriteLine($"  Sample-verify: {sample.Name} ({sample.SizeBytes:N0} bytes)");
                    // FLATTEN THE FILE NAME, not just the model id. A bucket entry's
                    // file is "mms-swh/model.onnx.json", and Path.Combine turns that
                    // into a subdirectory nobody created — DirectoryNotFoundException
                    // on the first voice, reported as that voice failing to verify.
                    var sampleFile = Path.Combine(
                        tempRoot, $"{modelId.Replace('/', '_')}.{sample.Name.Replace('/', '_')}");
                    var (actualSha, actualSize) = await DownloadAndHashAsync(
                        http, BuildDownloadUrl(source, repo, sample.Name), sampleFile, "sample(Primary)")
                        .ConfigureAwait(false);
                    if (!string.Equals(actualSha, sample.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"Sample SHA mismatch on '{sample.Name}': API={sample.Sha256[..16]}… actual={actualSha[..16]}…");
                    if (actualSize != sample.SizeBytes)
                        throw new InvalidOperationException(
                            $"Sample size mismatch on '{sample.Name}': API={sample.SizeBytes:N0} actual={actualSize:N0}");
                    Console.WriteLine($"  ✓ Sample {sample.Name}: API SHA matches reality.");

                    // THE FALLBACK CHECK IS MODELSCOPE'S, because the two URLs are
                    // ModelScope's: the API form and the CDN form, which the runtime
                    // tries in that order. HuggingFace serves one resolve/main URL and
                    // asking it twice proves nothing.
                    if (source is BundleSource.ModelScope)
                    {
                        var fallbackFile = sampleFile + ".fallback";
                        try
                        {
                            var (fbSha, fbSize) = await DownloadAndHashAsync(
                                http, BuildFallbackUrl(repo, sample.Name), fallbackFile, "sample(Fallback)")
                                .ConfigureAwait(false);
                            if (!string.Equals(fbSha, sample.Sha256, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException(
                                    $"Fallback SHA mismatch on '{sample.Name}': API={sample.Sha256[..16]}… fallback={fbSha[..16]}…");
                            Console.WriteLine($"  ✓ Fallback URL also serves byte-identical '{sample.Name}'.");
                        }
                        finally
                        {
                            try { File.Delete(fallbackFile); } catch { /* best-effort */ }
                        }
                    }
                }

                refreshedBundles[modelId] = bundle;
                Console.WriteLine($"✓ {modelId}: {bundle.Files.Count} files, total {bundle.TotalBytes / 1024.0 / 1024.0:F1} MB");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ {modelId}: FAILED — {ex.Message}");
                failures.Add(modelId);
            }
            Console.WriteLine();
        }

        if (refreshedBundles.Count > 0)
        {
            BackupAndRewriteRegistryJson(registry, refreshedBundles);
            Console.WriteLine($"✓ Wrote {refreshedBundles.Count} refreshed bundle(s) to:");
            Console.WriteLine($"    {RegistryPath}");
            Console.WriteLine($"  Original preserved at *.bak");
            Console.WriteLine();

            // THE GENERATED FILE IS NOT WRITTEN HERE ANY MORE. It used to be, and
            // that made two tools the owner of one derived file — the drift this
            // repo keeps paying for. Say the command rather than doing it, so the
            // regeneration is one step a person can see in the diff.
            Console.WriteLine("Now regenerate the runtime file from the register:");
            Console.WriteLine("    dotnet run --project tools/sync-registry");
            Console.WriteLine($"  (writes {EmbeddedRegistryPath})");
        }
        else
        {
            Console.WriteLine("No entries verified; register left untouched.");
        }

        if (failures.Count > 0)
        {
            Console.Error.WriteLine($"\nFailures: {string.Join(", ", failures)}");
            return 1;
        }
        return 0;
    }

    /// <summary>Which listing API an entry's pins can be refreshed from.</summary>
    private enum BundleSource
    {
        /// <summary>modelscope.cn — returns SHA-256 for every file in the repo.</summary>
        ModelScope,

        /// <summary>huggingface.co — tree API, LFS blobs carry their SHA-256 as lfs.oid.</summary>
        HuggingFace,

        /// <summary>No listing API here. Named and skipped, never attempted.</summary>
        Unsupported,
    }

    /// <summary>The raw Source string, for messages a person has to act on.</summary>
    private static string SourceNameOf(JsonObject registry, string modelId)
        => (registry[modelId] as JsonObject)?["Source"]?.GetValue<string>() ?? "(none)";

    /// <summary>
    /// Where this entry's bytes live, read from its own Source field.
    /// </summary>
    /// <remarks>
    /// ModelEntry.Source is the register's own word for this and it is the only
    /// honest input — the previous rule was "has a Repo", which is true of all 96
    /// entries and told the tool nothing about which API to call.
    /// <para>
    /// An ABSENT Source is Unsupported rather than a guess. Guessing ModelScope is
    /// exactly what this tool did for every entry in the register, and it produced
    /// 96 failures that looked like 96 broken models.
    /// </para>
    /// </remarks>
    private static BundleSource SourceOf(JsonObject registry, string modelId)
        => SourceNameOf(registry, modelId) switch
        {
            "ModelScope"                           => BundleSource.ModelScope,
            "HuggingFace" or "HuggingFaceBucket"   => BundleSource.HuggingFace,
            _                                      => BundleSource.Unsupported,
        };

    /// <summary>The URL a single file is downloaded from, per source.</summary>
    private static string BuildDownloadUrl(BundleSource source, string repo, string fileName)
        => source switch
        {
            BundleSource.ModelScope  => BuildPrimaryUrl(repo, fileName),
            BundleSource.HuggingFace => BuildHuggingFaceFileUrl(repo, fileName),
            _ => throw new InvalidOperationException($"no download URL for source {source}"),
        };

    /// <summary>huggingface.co/{repo}/resolve/main/{file} — the form the runtime uses.</summary>
    private static string BuildHuggingFaceFileUrl(string repo, string fileName)
        => $"https://huggingface.co/{repo}/resolve/main/"
         + Uri.EscapeDataString(fileName).Replace("%2F", "/", StringComparison.Ordinal);

    /// <summary>
    /// A HuggingFace token from the environment, or null.
    /// </summary>
    /// <remarks>
    /// SIXTY-FIVE OF NINETY-SIX ENTRIES ARE IN A PRIVATE REPO, so without this the
    /// tool can refresh 25 and nothing else. Measured 2026-10-28: the tree API for
    /// thegeekco/circleai-voices answers 401 Unauthorized unauthenticated, which is
    /// the same 401 the install log shows against those voices.
    /// <para>
    /// FROM THE ENVIRONMENT AND NOWHERE ELSE. The token is not read from a file in
    /// the repo, not written anywhere, not echoed, and not in any message this tool
    /// prints — only whether one was found. A tool that stores a credential is a
    /// tool that leaks one.
    /// </para>
    /// </remarks>
    private static string? HuggingFaceToken()
    {
        foreach (var name in (string[])["HF_TOKEN", "HUGGINGFACE_TOKEN", "HUGGING_FACE_HUB_TOKEN"])
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        return null;
    }

    /// <summary>Adds the HuggingFace bearer token to one request, when there is one.</summary>
    private static void AuthorizeHuggingFace(HttpRequestMessage req)
    {
        if (HuggingFaceToken() is { } token)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>
    /// Refreshes the pins for the files THIS ENTRY ALREADY NAMES, from the
    /// HuggingFace tree API.
    /// </summary>
    /// <remarks>
    /// IT REFRESHES, IT DOES NOT REBUILD, and that is not a shortcut. The
    /// ModelScope path below rebuilds a bundle from the whole repo listing because
    /// those repos hold one model each. thegeekco/circleai-voices holds FIFTY-SEVEN
    /// voices in one repo, so rebuilding from its tree would give every voice entry
    /// every other voice's files — a 57-way merge that would look like a successful
    /// run and break every download.
    /// <para>
    /// A NEW FILE IS THEREFORE NOT DISCOVERED HERE. That is the trade and it is the
    /// right way round: a missing file fails loudly at load time with "config.json
    /// not found", while a bundle silently carrying another voice's files fails
    /// after spending the user's data. Add a file with tools/hf-hash.
    /// </para>
    /// <para>
    /// LFS FIRST, DOWNLOAD SECOND. An LFS blob's lfs.oid IS its SHA-256 — verified
    /// 2026-10-28 against taobao-mnn/Qwen2.5-VL-3B-Instruct-MNN, where
    /// embeddings_bf16.bin (622 MB) reported oid b84907567aa8… — so weights are
    /// pinned from metadata. A small plain blob has only a git SHA-1, which is not
    /// what the runtime verifies, so those are downloaded and hashed. This is the
    /// same metadata-first-then-verify shape the ModelScope path has.
    /// </para>
    /// </remarks>
    private static async Task<BundleSpec> FetchBundleFromHuggingFaceAsync(
        HttpClient http, string repo, JsonObject entry, string tempRoot)
    {
        if (entry["BundleFiles"] is not JsonArray wanted || wanted.Count == 0)
            throw new InvalidOperationException(
                $"'{repo}': the entry names no BundleFiles, and this source refreshes the "
                + "files an entry already has rather than rebuilding from the repo tree "
                + "(one bucket repo holds many models). Pin it first with tools/hf-hash.");

        var treeUrl = $"https://huggingface.co/api/models/{repo}/tree/main?recursive=true";
        using var req = new HttpRequestMessage(HttpMethod.Get, treeUrl);
        AuthorizeHuggingFace(req);
        using var resp = await http.SendAsync(req).ConfigureAwait(false);

        if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized
                            or System.Net.HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                $"'{repo}' answered {(int)resp.StatusCode} {resp.ReasonPhrase} — it is a private "
                + "repo and this run has no token. Set HF_TOKEN in the environment for this "
                + "command only (do not persist it) and re-run."
                + (HuggingFaceToken() is null ? "" : " A token WAS sent and was refused, so it "
                  + "does not grant read on this repo."));
        }

        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                $"HuggingFace tree API for '{repo}' did not return an array. "
                + $"First 200 chars: {json[..Math.Min(200, json.Length)]}");

        // path -> (size, lfs oid or null)
        var tree = new Dictionary<string, (long Size, string? Oid)>(StringComparer.Ordinal);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.TryGetProperty("type", out var ty) && ty.GetString() is "directory") continue;
            if (!e.TryGetProperty("path", out var p) || p.GetString() is not { } path) continue;

            var size = e.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var sv) ? sv : 0L;
            string? oid = null;
            if (e.TryGetProperty("lfs", out var lfs)
                && lfs.ValueKind == JsonValueKind.Object
                && lfs.TryGetProperty("oid", out var o))
            {
                oid = o.GetString();
            }
            tree[path] = (size, oid);
        }

        var files = new List<BundleFileSpec>();
        long total = 0;

        foreach (var w in wanted.OfType<JsonObject>())
        {
            var name = w["Name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (!tree.TryGetValue(name, out var found))
                throw new InvalidOperationException(
                    $"'{name}' is pinned in the register but is not in {repo}'s tree. "
                    + "The upstream file moved or was removed; the runtime would 404 after "
                    + "starting the download.");

            string sha;
            long size;
            if (found.Oid is { Length: 64 } oid && oid.All(Uri.IsHexDigit))
            {
                sha = oid.ToLowerInvariant();
                size = found.Size;
                Console.WriteLine($"    {name}: lfs.oid {sha[..12]}… ({size:N0} bytes)");
            }
            else
            {
                // Plain blob: git gives a SHA-1 of a different preimage, so the only
                // way to the number the runtime checks is to hash the bytes.
                var dest = Path.Combine(tempRoot, $"hf.{repo.Replace('/', '_')}.{name.Replace('/', '_')}");
                try
                {
                    (sha, size) = await DownloadAndHashAsync(
                        http, BuildHuggingFaceFileUrl(repo, name), dest, name).ConfigureAwait(false);
                    Console.WriteLine($"    {name}: hashed {sha[..12]}… ({size:N0} bytes)");
                }
                finally
                {
                    try { File.Delete(dest); } catch { /* best-effort */ }
                }
            }

            files.Add(new BundleFileSpec(name, sha, size));
            total += size;
        }

        if (files.Count == 0)
            throw new InvalidOperationException($"'{repo}': no pinned file resolved against the tree.");

        return new BundleSpec(repo, files, total);
    }

    /// <summary>
    /// Calls ModelScope's file-listing API for the given repo, returning a
    /// fully populated bundle (every file's Name + Sha256 + SizeBytes).
    /// </summary>
    private static async Task<BundleSpec> FetchBundleFromModelScopeAsync(HttpClient http, string repo)
    {
        var apiUrl = $"https://www.modelscope.cn/api/v1/models/{repo}/repo/files?Revision=master";
        using var resp = await http.GetAsync(apiUrl).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Data", out var data) ||
            !data.TryGetProperty("Files", out var files) ||
            files.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"ModelScope API response for repo '{repo}' has no Data.Files[] array. " +
                $"First 200 chars: {json[..Math.Min(200, json.Length)]}");
        }

        var bundleFiles = new List<BundleFileSpec>();
        long total = 0;
        foreach (var f in files.EnumerateArray())
        {
            var type = f.TryGetProperty("Type", out var t) ? t.GetString() : null;
            if (type is not null && !type.Equals("blob", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = f.TryGetProperty("Name", out var n) ? n.GetString() : null;
            var sha = f.TryGetProperty("Sha256", out var s) ? s.GetString() : null;
            var size = f.TryGetProperty("Size", out var sz) && sz.TryGetInt64(out var sval) ? sval : 0L;

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(sha))
                continue;

            // Skip directories (some API responses list dirs as Type="tree" but also
            // some responses have no Type — second-line filter on Sha256 emptiness)
            bundleFiles.Add(new BundleFileSpec(name, sha.ToLowerInvariant(), size));
            total += size;
        }

        if (bundleFiles.Count == 0)
            throw new InvalidOperationException(
                $"Repo '{repo}' file list is empty after filtering. API may have changed shape.");

        return new BundleSpec(repo, bundleFiles, total);
    }

    /// <summary>
    /// Pick a file to full-download verify: prefer a small text file
    /// (config.json) over the multi-GB weights. Falls back to the smallest
    /// file ≥ 100 bytes if config.json isn't present.
    /// </summary>
    private static BundleFileSpec PickSampleFile(IReadOnlyList<BundleFileSpec> files)
    {
        var configJson = files.FirstOrDefault(f => f.Name.Equals("config.json", StringComparison.OrdinalIgnoreCase));
        if (configJson is not null) return configJson;
        return files.Where(f => f.SizeBytes >= 100).OrderBy(f => f.SizeBytes).First();
    }

    /// <summary>
    /// Primary URL — ModelScope API form: api/v1/models/{repo}/repo?Revision=master&FilePath={file}
    /// </summary>
    private static string BuildPrimaryUrl(string repo, string fileName) =>
        $"https://www.modelscope.cn/api/v1/models/{repo}/repo?Revision=master&FilePath={Uri.EscapeDataString(fileName)}";

    /// <summary>
    /// Fallback URL — ModelScope CDN form: {repo}/resolve/master/{file}
    /// </summary>
    private static string BuildFallbackUrl(string repo, string fileName) =>
        $"https://www.modelscope.cn/{repo}/resolve/master/{fileName}";

    private static async Task<(string Sha256Hex, long Bytes)> DownloadAndHashAsync(
        HttpClient http, string url, string destPath, string label)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        // The token goes to huggingface.co and to no other host. Attaching it by
        // default would send our credential to ModelScope's CDN on every sample
        // verify, which is how a secret ends up in somebody else's access log.
        if (url.StartsWith("https://huggingface.co/", StringComparison.OrdinalIgnoreCase))
            AuthorizeHuggingFace(req);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var totalBytes = resp.Content.Headers.ContentLength;
        await using var net = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);

        await using var file = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var sha = SHA256.Create();
        var buf = new byte[1 << 20];
        long total = 0;
        var sw = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        int read;
        while ((read = await net.ReadAsync(buf).ConfigureAwait(false)) > 0)
        {
            sha.TransformBlock(buf, 0, read, null, 0);
            await file.WriteAsync(buf.AsMemory(0, read)).ConfigureAwait(false);
            total += read;
            if (sw.Elapsed - lastReport > TimeSpan.FromSeconds(5))
            {
                var mbs = total / 1024.0 / 1024.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                var pct = totalBytes is > 0 ? $"{100.0 * total / totalBytes.Value:F1}%" : "?";
                var etaSec = totalBytes is > 0 && mbs > 0
                    ? (totalBytes.Value - total) / 1024.0 / 1024.0 / mbs
                    : 0;
                Console.WriteLine($"    {label}: {total / 1024.0 / 1024.0:F1} MB  ({pct})  {mbs:F2} MB/s  eta {TimeSpan.FromSeconds(etaSec):mm\\:ss}");
                lastReport = sw.Elapsed;
            }
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return (Convert.ToHexString(sha.Hash!).ToLowerInvariant(), total);
    }

    private static void BackupAndRewriteRegistryJson(
        JsonObject registry, Dictionary<string, BundleSpec> updates)
    {
        var backup = RegistryPath + ".bak";
        if (!File.Exists(backup)) File.Copy(RegistryPath, backup);

        foreach (var (modelId, bundle) in updates)
        {
            var entry = (JsonObject)registry[modelId]!;
            entry["TotalBytes"] = bundle.TotalBytes;
            entry["BundleFiles"] = ToJsonArray(bundle.Files);
        }
        if (registry["Notes"] is not null)
            registry["Notes"] = RegisterNotes;

        // UnsafeRelaxedJsonEscaping, FOR THE SAME REASON sync-registry uses it.
        // The default encoder turns a Portuguese voice's "tugão" into "tugão":
        // identical JSON, but it rewrites every accented name in the file and the
        // one place that lists our languages stops being readable in them. The
        // register now holds 65 bucket voices, so this is most of the file.
        File.WriteAllText(RegistryPath, registry.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    // BackupAndRewriteEmbeddedRegistry WAS HERE AND IS DELIBERATELY GONE.
    //
    // It opened Models/embedded_registry.json, patched Repo/TotalBytes/BundleFiles
    // into it and overwrote its Notes. That file is GENERATED from the register by
    // tools/sync-registry, so this made two tools the writer of one derived file —
    // and it wrote without UnsafeRelaxedJsonEscaping, so a run would also have
    // re-escaped every accented voice name in it.
    //
    // The register is written above; sync-registry projects it. The command is
    // printed rather than run, so the regeneration shows up as its own step in the
    // diff instead of arriving invisibly inside a pin refresh.

    private static JsonArray ToJsonArray(IReadOnlyList<BundleFileSpec> files)
    {
        var arr = new JsonArray();
        foreach (var f in files)
        {
            arr.Add(new JsonObject
            {
                ["Name"]      = f.Name,
                ["Sha256"]    = f.Sha256,
                ["SizeBytes"] = f.SizeBytes,
            });
        }
        return arr;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CircleAI.slnx"))
                || File.Exists(Path.Combine(dir.FullName, "CircleAI.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not find CircleAI repo root (no CircleAI.slnx / .sln in any parent directory).");
    }

    private sealed record BundleSpec(string Repo, IReadOnlyList<BundleFileSpec> Files, long TotalBytes);
    private sealed record BundleFileSpec(string Name, string Sha256, long SizeBytes);
}
