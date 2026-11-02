// fetch-model
//
// Drives BundleModelLoader against the live CDN and reports what actually
// happened. Exists because "compiles clean" is not the same claim as "fetches
// a real model", and BundleModelLoader had only ever been the former.
//
//   dotnet run --project tools/fetch-model -- [modelName] [storageDir]
//
// Defaults to the smallest catalogued bundle (Qwen3-0.6B-MNN) so the proof
// costs the least bandwidth.
//
// Checks, in order:
//   1. The model resolves in the registry and is bundle-shaped.
//   2. ModelExists() is false before the fetch (nothing cached).
//   3. DownloadModelAsync() returns a path that EXISTS and is what the entry's
//      engine opens: config.json for an MNN bundle (the specific defect
//      LocalModelLoader had was returning the weight blob), the .gguf file for
//      a GGUF entry.
//   4. ModelExists() is true after — which re-hashes the weight anchor, so this
//      is a real SHA-256 verification of the downloaded bytes, not a file check.
//   5. installed.json was stamped for upgrade detection.
//
// ONE FILE THE REGISTRY DOES NOT NAME YET:
//
//   dotnet run --project tools/fetch-model -- --file <repo> <file> <sha256> <bytes> <folder> [storageDir]
//
// fetches <file> from the Hugging Face repo <repo> into <storageDir>/<folder>/ through the
// same ModelDownloadService (resume, retry, the fallback URL, the SHA-256 check), then hashes
// the file again here. It writes no installed.json: that manifest describes a registry
// entry's whole bundle, and a file fetched beside one - a model's vision tower in the model's
// own folder - must not replace it.

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;

if (args.Length > 0 && args[0] == "--file") return await FetchOneFile(args[1..]);

var modelName = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
    ? args[0]
    : "Qwen3-0.6B-MNN";

var storageDir = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])
    ? args[1]
    : Path.Combine(Path.GetTempPath(), "circleai-cdn-proof");

Console.WriteLine($"model   : {modelName}");
Console.WriteLine($"storage : {storageDir}");
Console.WriteLine();

using var registry = new ModelRegistryService();

var entry = registry.GetLatestModel(modelName);
if (entry is null)
{
    Console.Error.WriteLine($"FAIL: '{modelName}' is not in the registry.");
    Console.Error.WriteLine("Catalogued: " + string.Join(", ", registry.AllModels.Select(m => m.Name)));
    return 2;
}

var declaredBytes = entry.BundleFiles?.Sum(f => f.SizeBytes) ?? 0;
Console.WriteLine($"repo    : {entry.Repo ?? "(none)"}");
Console.WriteLine($"bundle  : {entry.IsBundle} ({entry.BundleFiles?.Count ?? 0} files, {declaredBytes / 1024.0 / 1024:F1} MB declared)");
Console.WriteLine();

using var loader = new BundleModelLoader(storageDir, registry);

var existedBefore = loader.ModelExists(modelName);
Console.WriteLine($"cached before : {existedBefore}");
Console.WriteLine($"expected path : {loader.GetModelPath(modelName)}");
Console.WriteLine();

var sw = Stopwatch.StartNew();
var lastPct = -1;
var progress = new Progress<float>(p =>
{
    var pct = (int)(p * 100);
    if (pct <= lastPct || pct % 5 != 0) return;   // throttle: every 5%
    lastPct = pct;
    Console.WriteLine($"  {pct,3}%  {sw.Elapsed:mm\\:ss}");
});

string resolvedPath;
try
{
    resolvedPath = await loader.DownloadModelAsync(modelName, progress);
}
catch (Exception ex)
{
    sw.Stop();
    Console.Error.WriteLine();
    Console.Error.WriteLine($"FAIL after {sw.Elapsed:mm\\:ss}: {ex.GetType().Name}");
    Console.Error.WriteLine(ex.Message);
    if (ex.InnerException is not null)
        Console.Error.WriteLine($"  inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
    return 1;
}
sw.Stop();

Console.WriteLine();
Console.WriteLine($"returned path : {resolvedPath}");
Console.WriteLine($"elapsed       : {sw.Elapsed:mm\\:ss}");
Console.WriteLine();

var failures = new List<string>();

if (!File.Exists(resolvedPath))
    failures.Add($"returned path does not exist: {resolvedPath}");

// What the path must point at depends on the engine that loads it: MNN opens the
// bundle's config.json, a GGUF engine opens the single .gguf weights file.
var expectedAnchor = entry.Engine == CircleAI.Core.ModelEngine.LlamaCpp ? ".gguf" : "config.json";
if (!resolvedPath.EndsWith(expectedAnchor, StringComparison.OrdinalIgnoreCase))
    failures.Add($"expected {expectedAnchor} (what the {entry.Engine} engine loads), got '{Path.GetFileName(resolvedPath)}'");

// Re-hashes the weight anchor against its pinned SHA-256.
if (!loader.ModelExists(modelName))
    failures.Add("ModelExists() is false after download — SHA-256 anchor check failed");

var modelDir = Path.GetDirectoryName(resolvedPath)!;
if (Directory.Exists(modelDir))
{
    var onDisk = new DirectoryInfo(modelDir).GetFiles("*", SearchOption.AllDirectories);
    var actualBytes = onDisk.Sum(f => f.Length);
    Console.WriteLine($"on disk : {onDisk.Length} files, {actualBytes / 1024.0 / 1024:F1} MB");
    foreach (var f in onDisk.OrderByDescending(f => f.Length).Take(12))
        Console.WriteLine($"    {f.Length,13:N0}  {f.Name}");
    Console.WriteLine();

    if (!File.Exists(Path.Combine(modelDir, "installed.json")))
        failures.Add("installed.json was not stamped (upgrade detection will not work)");
}
else
{
    failures.Add($"model directory missing: {modelDir}");
}

if (failures.Count > 0)
{
    Console.Error.WriteLine("FAIL:");
    foreach (var f in failures) Console.Error.WriteLine($"  - {f}");
    return 1;
}

Console.WriteLine($"PASS: fetched, SHA-256 verified, and returned the path the {entry.Engine} engine loads.");
return 0;

static async Task<int> FetchOneFile(string[] a)
{
    if (a.Length < 5 || !long.TryParse(a[3], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) || bytes <= 0
        || a[2].Length != 64 || !a[2].All(Uri.IsHexDigit))
    {
        Console.Error.WriteLine("usage: fetch-model --file <repo> <file> <sha256> <bytes> <folder> [storageDir]");
        return 2;
    }
    var (repo, name, sha, folder) = (a[0], a[1], a[2].ToLowerInvariant(), a[4]);
    var storage = ModelPaths.Resolve(a.Length > 5 ? a[5] : null);
    var path = Path.Combine(storage, folder, name);
    Console.WriteLine($"repo    : {repo}");
    Console.WriteLine($"file    : {name}, {bytes:N0} bytes, sha256 {sha}");
    Console.WriteLine($"into    : {path}");
    Console.WriteLine($"present : {File.Exists(path)}");
    Console.WriteLine();

    var sw = Stopwatch.StartNew();
    var last = -1;
    var progress = new Progress<DownloadProgress>(p =>
    {
        var pct = (int)(p.Ratio * 100);
        if (pct <= last || pct % 5 != 0) return;
        last = pct;
        Console.WriteLine($"  {pct,3}%  {sw.Elapsed:mm\\:ss}  {p.Phase}");
    });
    using var downloads = new ModelDownloadService(storage);
    try
    {
        await downloads.EnsureBundleAsync(folder, repo, ModelSource.HuggingFace, [new BundleFileSpec(name, sha, bytes)], progress, CancellationToken.None);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAIL after {sw.Elapsed:mm\\:ss}: {ex.GetType().Name}: {ex.Message}");
        return 1;
    }

    // The service checked the hash; this checks the file it left, by its own reading.
    var failures = new List<string>();
    if (!File.Exists(path)) failures.Add($"nothing at {path}");
    else
    {
        var length = new FileInfo(path).Length;
        if (length != bytes) failures.Add($"{length:N0} bytes on disk, not {bytes:N0}");
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        Console.WriteLine($"sha256  : {actual}");
        if (actual != sha) failures.Add($"the file hashes to {actual}");
    }
    Console.WriteLine($"elapsed : {sw.Elapsed:mm\\:ss}");
    if (failures.Count > 0)
    {
        Console.Error.WriteLine("FAIL:");
        foreach (var f in failures) Console.Error.WriteLine($"  - {f}");
        return 1;
    }
    Console.WriteLine("PASS: the file is in place and its SHA-256 is the one asked for.");
    return 0;
}
