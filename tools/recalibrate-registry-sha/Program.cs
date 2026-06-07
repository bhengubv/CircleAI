// recalibrate-registry-sha
//
// Anti-rot tooling for src/CircleAI.Core/registry.json (and its sibling
// src/CircleAI.Core/Models/embedded_registry.json).
//
// Both catalog files share the same per-model bundle entries:
//   • registry.json                — flat dict keyed by ModelId
//   • Models/embedded_registry.json — list shape consumed by ModelRegistryService.cs
//
// Each entry pins a ModelScope bundle: Repo + BundleFiles[] where every
// BundleFile carries Name + Sha256 + SizeBytes. MNN-LLM needs the COMPLETE
// bundle (config.json, llm.mnn, llm.mnn.weight, tokenizer, llm_config.json,
// configuration.json) to load — not just the weights — so the catalog must
// list every file or the runtime will fail with "config.json not found".
//
// This tool polls ModelScope's file-listing API for the SHA-256 of every
// file in each bundle's repo, optionally downloads one sample file per
// bundle to confirm the API SHAs match reality, then writes both catalog
// files. The originals are backed up to *.bak.
//
// Usage:
//   dotnet run --project tools/recalibrate-registry-sha -- [model-id ...]
//                                                          [--no-sample-verify]
//
//   No model-id args → process every entry in the registry.
//   One or more model-id args → process only those.
//   --no-sample-verify → skip sample download (fast, but trusts the API).
//
// Exit code:
//   0  all requested entries refreshed
//   1  one or more entries failed (registry left untouched for failed entries)
//   2  IO / parse / config error (no entries processed)

using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
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

    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string RegistryPath
        = Path.Combine(RepoRoot, "src", "CircleAI.Core", "registry.json");
    private static readonly string EmbeddedRegistryPath
        = Path.Combine(RepoRoot, "src", "CircleAI.Core", "Models", "embedded_registry.json");

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

        // Pick entries to process. Only bundle entries (with a Repo key) are
        // candidates — legacy single-file entries would need a different tool.
        var allBundleEntries = registry
            .Where(kv => kv.Value is JsonObject obj && obj["Repo"] is not null)
            .Select(kv => kv.Key)
            .ToList();
        var targets = modelArgs.Count == 0 ? allBundleEntries : modelArgs.Where(allBundleEntries.Contains).ToList();
        var missing = modelArgs.Where(a => !allBundleEntries.Contains(a)).ToList();
        foreach (var m in missing)
            Console.WriteLine($"⚠  '{m}' is not a bundle entry in the registry — skipping.");

        if (targets.Count == 0)
        {
            Console.Error.WriteLine("No matching bundle entries — nothing to do.");
            return 2;
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

                var bundle = await FetchBundleFromApiAsync(http, repo).ConfigureAwait(false);
                Console.WriteLine($"  Files: {bundle.Files.Count}  TotalBytes: {bundle.TotalBytes:N0}");

                if (sampleVerify)
                {
                    var sample = PickSampleFile(bundle.Files);
                    Console.WriteLine($"  Sample-verify: {sample.Name} ({sample.SizeBytes:N0} bytes)");
                    var sampleFile = Path.Combine(tempRoot, $"{modelId.Replace('/', '_')}.{sample.Name}");
                    var (actualSha, actualSize) = await DownloadAndHashAsync(
                        http, BuildPrimaryUrl(repo, sample.Name), sampleFile, "sample(Primary)")
                        .ConfigureAwait(false);
                    if (!string.Equals(actualSha, sample.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"Sample SHA mismatch on '{sample.Name}': API={sample.Sha256[..16]}… actual={actualSha[..16]}…");
                    if (actualSize != sample.SizeBytes)
                        throw new InvalidOperationException(
                            $"Sample size mismatch on '{sample.Name}': API={sample.SizeBytes:N0} actual={actualSize:N0}");
                    Console.WriteLine($"  ✓ Sample {sample.Name}: API SHA matches reality.");

                    // Best-effort: also verify the Fallback URL serves identical bytes.
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
            BackupAndRewriteEmbeddedRegistry(refreshedBundles);
            Console.WriteLine($"✓ Wrote {refreshedBundles.Count} refreshed bundle(s) to:");
            Console.WriteLine($"    {RegistryPath}");
            Console.WriteLine($"    {EmbeddedRegistryPath}");
            Console.WriteLine($"  Originals preserved at *.bak");
        }
        else
        {
            Console.WriteLine("No entries verified; registry left untouched.");
        }

        if (failures.Count > 0)
        {
            Console.Error.WriteLine($"\nFailures: {string.Join(", ", failures)}");
            return 1;
        }
        return 0;
    }

    /// <summary>
    /// Calls ModelScope's file-listing API for the given repo, returning a
    /// fully populated bundle (every file's Name + Sha256 + SizeBytes).
    /// </summary>
    private static async Task<BundleSpec> FetchBundleFromApiAsync(HttpClient http, string repo)
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
            registry["Notes"] =
                "Auto-populated from ModelScope file-listing API by tools/recalibrate-registry-sha. " +
                "Each entry's BundleFiles array lists EVERY file MNN-LLM needs to load the model. " +
                "Per-file SHA-256 comes from ModelScope's API; one file per entry is full-download verified " +
                "to confirm the API SHAs are accurate. Re-run the tool to refresh.";

        File.WriteAllText(RegistryPath, registry.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
    }

    private static void BackupAndRewriteEmbeddedRegistry(Dictionary<string, BundleSpec> updates)
    {
        if (!File.Exists(EmbeddedRegistryPath))
        {
            Console.WriteLine($"⚠ embedded_registry.json not found at {EmbeddedRegistryPath} — skipped.");
            return;
        }
        var backup = EmbeddedRegistryPath + ".bak";
        if (!File.Exists(backup)) File.Copy(EmbeddedRegistryPath, backup);

        var doc = JsonNode.Parse(File.ReadAllText(EmbeddedRegistryPath))!.AsObject();
        if (doc["Models"] is not JsonArray models)
        {
            Console.WriteLine("⚠ embedded_registry.json has no 'Models' array — skipped.");
            return;
        }

        foreach (var model in models.OfType<JsonObject>())
        {
            var name = model["Name"]?.GetValue<string>();
            if (name is null) continue;
            if (!updates.TryGetValue(name, out var bundle)) continue;
            model["Repo"] = bundle.Repo;
            model["TotalBytes"] = bundle.TotalBytes;
            model["BundleFiles"] = ToJsonArray(bundle.Files);
        }
        doc["Notes"] =
            "Auto-populated by tools/recalibrate-registry-sha — see ../registry.json header.";

        File.WriteAllText(EmbeddedRegistryPath, doc.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
    }

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
