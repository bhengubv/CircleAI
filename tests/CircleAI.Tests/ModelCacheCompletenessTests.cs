// ModelCacheCompletenessTests.cs
//
// A half-downloaded model must never report itself cached.
//
// This is not hypothetical. On the Huawei P30 Lite the chat model had been dead
// since the first interrupted download: the fetch created the model directory,
// wrote the two small config files, started the 450 MB weight file and stopped.
// From then on every launch saw a directory that existed, skipped the download,
// and handed MNN a bundle with no weights — which failed, identically, forever.
// Nothing self-repaired, because nothing re-downloads what is already "cached".
//
// These tests are offline: they build the failure on disk directly, because the
// failure is about what is on disk, not about the network.

using System.Text.Json;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class ModelCacheCompletenessTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "circleai-cache-tests-" + Guid.NewGuid().ToString("N")[..8]);

    private ModelDownloadService NewService() => new(_root);

    /// <summary>Lays down a model directory exactly as an interrupted fetch leaves it.</summary>
    private string PartialBundle(string modelId)
    {
        var dir = Path.Combine(_root, modelId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.json"), "{\"llm_model\":\"llm.mnn\"}");
        File.WriteAllText(Path.Combine(dir, "configuration.json"), "{}");
        return dir;   // note: no llm.mnn, no llm.mnn.weight, no installed.json
    }

    private void WriteManifest(string dir, params (string Name, long Size)[] files)
    {
        foreach (var (name, size) in files)
        {
            var path = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path)) File.WriteAllBytes(path, new byte[size]);
        }
        var manifest = new
        {
            ModelId = Path.GetFileName(dir),
            Version = "1.0",
            Repo = "MNN/Qwen3-0.6B-MNN",
            TotalBytes = files.Sum(f => f.Size),
            Files = files.Select(f => new { f.Name, Sha256 = new string('0', 64), SizeBytes = f.Size }),
            InstalledAtUtc = DateTimeOffset.UtcNow,
        };
        File.WriteAllText(Path.Combine(dir, "installed.json"), JsonSerializer.Serialize(manifest));
    }

    [Fact]
    public async Task A_directory_left_by_an_interrupted_download_is_NOT_cached()
    {
        PartialBundle("Qwen3-0.6B-MNN");
        using var svc = NewService();

        // The whole bug in one assertion: the folder is there, the configs are
        // there, and the model is nonetheless unusable.
        Assert.False(await svc.IsModelCachedAsync("Qwen3-0.6B-MNN", default));
    }

    [Fact]
    public async Task A_complete_bundle_with_its_manifest_IS_cached()
    {
        var dir = Path.Combine(_root, "Qwen3-0.6B-MNN");
        Directory.CreateDirectory(dir);
        WriteManifest(dir, ("config.json", 403), ("llm.mnn", 4096), ("llm.mnn.weight", 65536));

        using var svc = NewService();
        Assert.True(await svc.IsModelCachedAsync("Qwen3-0.6B-MNN", default));
    }

    [Fact]
    public async Task A_manifest_whose_weight_file_was_deleted_is_NOT_cached()
    {
        var dir = Path.Combine(_root, "Qwen3-0.6B-MNN");
        Directory.CreateDirectory(dir);
        WriteManifest(dir, ("config.json", 403), ("llm.mnn.weight", 65536));
        File.Delete(Path.Combine(dir, "llm.mnn.weight"));

        using var svc = NewService();
        Assert.False(await svc.IsModelCachedAsync("Qwen3-0.6B-MNN", default));
    }

    [Fact]
    public async Task A_TRUNCATED_weight_file_is_NOT_cached()
    {
        // The nastiest shape: the file is present, so an existence check passes,
        // but it is short because the transfer died mid-write.
        var dir = Path.Combine(_root, "Qwen3-0.6B-MNN");
        Directory.CreateDirectory(dir);
        WriteManifest(dir, ("config.json", 403), ("llm.mnn.weight", 65536));
        File.WriteAllBytes(Path.Combine(dir, "llm.mnn.weight"), new byte[1024]);

        using var svc = NewService();
        Assert.False(await svc.IsModelCachedAsync("Qwen3-0.6B-MNN", default));
    }

    [Fact]
    public async Task An_absent_model_is_NOT_cached()
    {
        using var svc = NewService();
        Assert.False(await svc.IsModelCachedAsync("Never-Downloaded", default));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* a temp directory that outlives the test harms nothing */ }
    }
}
