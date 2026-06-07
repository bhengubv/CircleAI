// ModelDownloadServiceBundleTests.cs
//
// End-to-end proof that ModelDownloadService.EnsureBundleAsync correctly
// pulls a multi-file MNN bundle off ModelScope and lands every file with
// the right SHA-256 + the right size. Gated by CIRCLEAI_NETWORK_TESTS=1
// because the smallest MNN bundle (Qwen3-0.6B-MNN) is still ~433 MB.
//
// Run on a clean host:
//   $env:CIRCLEAI_NETWORK_TESTS = "1"
//   dotnet test tests/CircleAI.Tests/CircleAI.Tests.csproj -c Release \
//     --filter "FullyQualifiedName~ModelDownloadServiceBundleTests"
//
// Without the env var the test is silently skipped — the assembly is still
// built and the legacy 1148-test offline suite stays green.

using System.Security.Cryptography;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class ModelDownloadServiceBundleTests : IDisposable
{
    private const string SkipReason =
        "Network-gated end-to-end test. Set CIRCLEAI_NETWORK_TESTS=1 to run (downloads ~433 MB from ModelScope).";

    private static bool NetworkTestsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("CIRCLEAI_NETWORK_TESTS"),
            "1",
            StringComparison.OrdinalIgnoreCase);

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "circleai-bundle-e2e-" + Guid.NewGuid().ToString("N"));

    public ModelDownloadServiceBundleTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Download Qwen3-0.6B-MNN's complete bundle into a temp dir, assert
    /// every file lands at the right path with the catalog's pinned SHA-256
    /// and size. Hashes are recomputed locally — we do NOT trust the
    /// downloader's own VerifySha256Async for the assertion.
    /// </summary>
    [Fact]
    public async Task EnsureBundleAsync_Qwen3_0_6B_DownloadsBundleAndAllFilesVerify()
    {
        if (!NetworkTestsEnabled) return; // opt-in network test

        using var registry = new ModelRegistryService();
        var entry = registry.GetLatestModel("Qwen3-0.6B-MNN");
        Assert.NotNull(entry);
        Assert.True(entry!.IsBundle, "Registry must expose Qwen3-0.6B-MNN as a bundle entry.");
        Assert.False(string.IsNullOrWhiteSpace(entry.Repo));
        Assert.NotNull(entry.BundleFiles);
        Assert.NotEmpty(entry.BundleFiles!);

        using var svc = new ModelDownloadService(_tempDir);

        var spec = entry.BundleFiles!
            .Select(f => new BundleFileSpec(f.Name, f.Sha256, f.SizeBytes))
            .ToList();

        var modelDir = await svc.EnsureBundleAsync(
            modelId:     "Qwen3-0.6B-MNN",
            repo:        entry.Repo!,
            bundleFiles: spec,
            progress:    null,
            ct:          CancellationToken.None);

        Assert.True(Directory.Exists(modelDir), $"Bundle dir '{modelDir}' missing after EnsureBundleAsync.");

        foreach (var f in entry.BundleFiles!)
        {
            var filePath = Path.Combine(modelDir, f.Name);
            Assert.True(File.Exists(filePath), $"Bundle file '{f.Name}' missing from '{modelDir}'.");

            var actualSize = new FileInfo(filePath).Length;
            Assert.Equal(f.SizeBytes, actualSize);

            var actualSha = await ComputeSha256HexAsync(filePath);
            var expected  = StripSha256Prefix(f.Sha256);
            Assert.Equal(expected, actualSha);
        }

        // MNN-LLM's Llm::create() needs config.json — the runtime points its
        // QwenTextGenerator at <modelDir>/config.json.
        var configJson = Path.Combine(modelDir, "config.json");
        Assert.True(File.Exists(configJson),
            "config.json must be in the bundle dir so QwenTextGenerator can load the model.");
    }

    /// <summary>
    /// Second call to EnsureBundleAsync must be a no-op when every file is
    /// already on disk with the right hash — no re-download. Asserted by
    /// stamping mtimes and confirming none change across two consecutive
    /// EnsureBundleAsync calls.
    /// </summary>
    [Fact]
    public async Task EnsureBundleAsync_RunTwice_SecondCallReusesCache()
    {
        if (!NetworkTestsEnabled) return; // opt-in network test

        using var registry = new ModelRegistryService();
        var entry = registry.GetLatestModel("Qwen3-0.6B-MNN");
        Assert.NotNull(entry);

        using var svc = new ModelDownloadService(_tempDir);
        var spec = entry!.BundleFiles!
            .Select(f => new BundleFileSpec(f.Name, f.Sha256, f.SizeBytes))
            .ToList();

        var modelDir = await svc.EnsureBundleAsync(
            "Qwen3-0.6B-MNN", entry.Repo!, spec, progress: null, ct: CancellationToken.None);

        var stamps = entry.BundleFiles!
            .ToDictionary(f => f.Name, f => File.GetLastWriteTimeUtc(Path.Combine(modelDir, f.Name)));

        // Second call — should detect every file is cached + valid and skip.
        await svc.EnsureBundleAsync(
            "Qwen3-0.6B-MNN", entry.Repo!, spec, progress: null, ct: CancellationToken.None);

        foreach (var f in entry.BundleFiles!)
        {
            var current = File.GetLastWriteTimeUtc(Path.Combine(modelDir, f.Name));
            Assert.Equal(stamps[f.Name], current);
        }
    }

    private static async Task<string> ComputeSha256HexAsync(string path)
    {
        await using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string StripSha256Prefix(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var trimmed = raw.Trim();
        return trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? trimmed["sha256:".Length..].Trim()
            : trimmed;
    }
}
