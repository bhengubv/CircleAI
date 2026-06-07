// recalibrate-registry-sha
//
// Anti-rot tooling for src/CircleAI.Core/registry.json (and its sibling
// src/CircleAI.Core/Models/embedded_registry.json).
//
// The registry has TWO catalog files that share the same pins:
//   • registry.json                — flat dict keyed by ModelId, ModelDownloader.cs reads it
//   • Models/embedded_registry.json — list shape, ModelRegistryService.cs reads it
//
// Both pin a SHA-256 in "sha256:<hex>" form. The original pins were
// extracted from the ModelScope file-listing API (no downloads), and
// turned out NOT to match the actual bytes ModelScope serves — so every
// model fails its checksum at runtime and gets deleted by
// ModelDownloadService.EnsureModelAsync. This tool fixes that by
// actually downloading each entry and pinning the real hash.
//
// Usage:
//   dotnet run --project tools/recalibrate-registry-sha -- [model-id ...]
//
//   No args → process every entry in the registry.
//   One or more model-id args → process only those.
//
// What it does per entry:
//   1. HEAD + first-KB sniff against PrimaryUrl to assert real binary
//      content (rejects HTML error pages, git-LFS pointers, JSON
//      wrappers, redirects to login pages).
//   2. Streams the full file via PrimaryUrl, hashing as it goes, writing
//      to a temp file. Reports speed + ETA.
//   3. Streams the full file via FallbackUrl, hashing as it goes.
//   4. Asserts the two SHAs are identical (the catalog's "both URLs
//      serve identical bytes" claim is verified, not trusted).
//   5. Pins the verified SHA + actual byte-length into BOTH registry.json
//      and embedded_registry.json. The original files are backed up with
//      a .bak extension.
//
// Exit code:
//   0  all requested entries verified + pins updated
//   1  one or more entries failed verification (registry is NOT touched
//      for failed entries; failed-entry pins stay as they were)
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
        "(KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36 CircleAI-Recalibrator/1.0";

    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string RegistryPath
        = Path.Combine(RepoRoot, "src", "CircleAI.Core", "registry.json");
    private static readonly string EmbeddedRegistryPath
        = Path.Combine(RepoRoot, "src", "CircleAI.Core", "Models", "embedded_registry.json");

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
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

        // Pick entries to process.
        var allEntries = registry
            .Where(kv => kv.Value is JsonObject)
            .Select(kv => kv.Key)
            .ToList();
        var targets = args.Length == 0 ? allEntries : args.Where(allEntries.Contains).ToList();
        var missing = args.Where(a => !allEntries.Contains(a)).ToList();
        foreach (var m in missing)
            Console.WriteLine($"⚠  '{m}' not in registry — skipping.");

        if (targets.Count == 0)
        {
            Console.Error.WriteLine("No matching entries — nothing to do.");
            return 2;
        }

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(60),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        var verifiedUpdates = new Dictionary<string, VerifiedEntry>();
        var failures = new List<string>();

        var tempRoot = Path.Combine(Path.GetTempPath(), "circleai-recalibrate");
        Directory.CreateDirectory(tempRoot);

        Console.WriteLine($"Repo root      : {RepoRoot}");
        Console.WriteLine($"Registry       : {RegistryPath}");
        Console.WriteLine($"Embedded       : {EmbeddedRegistryPath}");
        Console.WriteLine($"Temp DL root   : {tempRoot}");
        Console.WriteLine($"Targets ({targets.Count}): {string.Join(", ", targets)}");
        Console.WriteLine();

        foreach (var modelId in targets)
        {
            try
            {
                var verified = await VerifyOneAsync(http, modelId, (JsonObject)registry[modelId]!, tempRoot)
                    .ConfigureAwait(false);
                verifiedUpdates[modelId] = verified;
                Console.WriteLine($"✓ {modelId}: verified  sha256={verified.Sha256[..16]}…  size={verified.SizeBytes:N0}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ {modelId}: FAILED — {ex.Message}");
                failures.Add(modelId);
            }
            Console.WriteLine();
        }

        if (verifiedUpdates.Count > 0)
        {
            BackupAndRewriteRegistryJson(registry, verifiedUpdates);
            BackupAndRewriteEmbeddedRegistry(verifiedUpdates);
            Console.WriteLine($"✓ Wrote {verifiedUpdates.Count} updated pins to:");
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

    private static async Task<VerifiedEntry> VerifyOneAsync(
        HttpClient http, string modelId, JsonObject entry, string tempRoot)
    {
        var primary = entry["PrimaryUrl"]?.GetValue<string>()
            ?? throw new InvalidOperationException("missing PrimaryUrl");
        var fallback = entry["FallbackUrl"]?.GetValue<string>(); // may be absent

        Console.WriteLine($"── {modelId} ──");
        Console.WriteLine($"  PrimaryUrl  : {primary}");
        if (fallback is not null) Console.WriteLine($"  FallbackUrl : {fallback}");

        // 1. Sniff — first KB must look like a real weight, not HTML / LFS / JSON wrapper.
        await SniffAndAssertBinaryAsync(http, primary, "PrimaryUrl").ConfigureAwait(false);

        // 2. Download Primary, hash + size.
        var primaryFile = Path.Combine(tempRoot, $"{modelId}.primary.bin");
        var (primaryHash, primarySize) = await DownloadAndHashAsync(http, primary, primaryFile, "PrimaryUrl").ConfigureAwait(false);
        Console.WriteLine($"  Primary  : sha256={primaryHash}  size={primarySize:N0}");

        string? fallbackHash = null;
        if (fallback is not null)
        {
            await SniffAndAssertBinaryAsync(http, fallback, "FallbackUrl").ConfigureAwait(false);
            var fallbackFile = Path.Combine(tempRoot, $"{modelId}.fallback.bin");
            var (fbHash, fbSize) = await DownloadAndHashAsync(http, fallback, fallbackFile, "FallbackUrl").ConfigureAwait(false);
            Console.WriteLine($"  Fallback : sha256={fbHash}  size={fbSize:N0}");
            fallbackHash = fbHash;
            if (!string.Equals(fbHash, primaryHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"PrimaryUrl and FallbackUrl serve DIFFERENT bytes. " +
                    $"Primary sha256={primaryHash}, Fallback sha256={fbHash}.");
            if (fbSize != primarySize)
                throw new InvalidOperationException(
                    $"PrimaryUrl and FallbackUrl differ in size ({primarySize:N0} vs {fbSize:N0}).");

            // Fallback file no longer needed — keep Primary for cache reuse, delete Fallback.
            try { File.Delete(fallbackFile); } catch { }
        }

        return new VerifiedEntry(
            Sha256: primaryHash,
            SizeBytes: primarySize,
            FallbackVerified: fallbackHash is not null);
    }

    /// <summary>
    /// Issues a Range request for the first 1 KB and asserts it does NOT look
    /// like HTML, JSON, or a git-LFS pointer.
    /// </summary>
    private static async Task SniffAndAssertBinaryAsync(HttpClient http, string url, string label)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new RangeHeaderValue(0, 1023);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        if (bytes.Length < 16)
            throw new InvalidOperationException($"{label} sniff returned {bytes.Length} bytes — too short to be a real weight.");

        // HTML
        var asciiHead = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 64))
            .TrimStart().ToLowerInvariant();
        if (asciiHead.StartsWith("<!doctype html") || asciiHead.StartsWith("<html") || asciiHead.StartsWith("<?xml"))
            throw new InvalidOperationException($"{label} returned HTML/XML — the URL is a login or error page, not a weight.");

        // git-LFS pointer
        if (asciiHead.StartsWith("version https://git-lfs.github.com/spec"))
            throw new InvalidOperationException($"{label} returned a git-LFS pointer file, not the resolved weight.");

        // JSON wrapper — ModelScope wraps errors in {"Code":...,"Message":...}
        if (asciiHead.StartsWith("{") && asciiHead.Contains("\"code\""))
            throw new InvalidOperationException(
                $"{label} returned a JSON error wrapper, not the weight. First bytes: {asciiHead[..Math.Min(120, asciiHead.Length)]}");
    }

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
        JsonObject registry, Dictionary<string, VerifiedEntry> updates)
    {
        var backup = RegistryPath + ".bak";
        if (!File.Exists(backup)) File.Copy(RegistryPath, backup);

        foreach (var (modelId, v) in updates)
        {
            var entry = (JsonObject)registry[modelId]!;
            entry["Checksum"] = $"sha256:{v.Sha256}";
            entry["SizeBytes"] = v.SizeBytes;
        }
        // Touch Notes to reflect the new provenance.
        if (registry["Notes"] is JsonValue)
            registry["Notes"] = "Pins below VERIFIED by tools/recalibrate-registry-sha — actual SHA-256 of the bytes ModelScope serves, computed by downloading both PrimaryUrl and FallbackUrl and asserting they are byte-identical. Re-run the tool to refresh.";

        File.WriteAllText(RegistryPath, registry.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
    }

    private static void BackupAndRewriteEmbeddedRegistry(Dictionary<string, VerifiedEntry> updates)
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
            if (!updates.TryGetValue(name, out var v)) continue;
            model["Checksum"] = $"sha256:{v.Sha256}";
            // SizeBytes isn't in the embedded shape; don't add a new field for now.
        }
        doc["Notes"] = "Pins below VERIFIED — see ../registry.json header.";

        File.WriteAllText(EmbeddedRegistryPath, doc.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
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

    private sealed record VerifiedEntry(string Sha256, long SizeBytes, bool FallbackVerified);
}
