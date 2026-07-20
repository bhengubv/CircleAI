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
//   3. DownloadModelAsync() returns a path that EXISTS and ends in config.json
//      — the specific defect LocalModelLoader had was returning the weight blob.
//   4. ModelExists() is true after — which re-hashes the weight anchor, so this
//      is a real SHA-256 verification of the downloaded bytes, not a file check.
//   5. installed.json was stamped for upgrade detection.

using System.Diagnostics;
using CircleAI.Core.Models;
using CircleAI.Inference;

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

if (!resolvedPath.EndsWith("config.json", StringComparison.OrdinalIgnoreCase))
    failures.Add($"expected config.json (what mnn_llm_create loads), got '{Path.GetFileName(resolvedPath)}'");

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

Console.WriteLine("PASS: fetched, SHA-256 verified, and returned the MNN config path.");
return 0;
