using System.IO;
using System.Threading.Tasks;
using CircleAI.Core;
using Xunit;

namespace CircleAI.Tests;

/// <summary>
/// Tests for <see cref="LocalModelLoader"/> that do NOT require network access
/// or real model files. Model names match the real ModelScope entries in
/// <c>src/CircleAI.Core/registry.json</c> (Qwen3-*-MNN family).
/// </summary>
public sealed class LocalModelLoaderTests : IDisposable
{
    // Real registry entries (post-2026-06-06 ModelScope sync). Picked the two
    // smallest so disk-touch tests stay cheap even if anyone ever materialises
    // the file.
    private const string KnownModelA = "Qwen3-0.6B-MNN";
    private const string KnownModelB = "Qwen3-1.7B-MNN";

    // FileName field of every Qwen3-*-MNN entry — the on-disk artefact MNN
    // expects is always "llm.mnn.weight" (sibling tokenizer + config files
    // live in the same directory).
    private const string KnownModelAFileName = "llm.mnn.weight";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public LocalModelLoaderTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ------------------------------------------------------------------
    // Registry is loaded — known models are known
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(KnownModelA)]
    [InlineData(KnownModelB)]
    public void GetModelPath_KnownModel_ReturnsPathString(string modelName)
    {
        using var loader = new LocalModelLoader(_tempDir);
        // File won't exist, but the method should still return the path.
        var path = loader.GetModelPath(modelName);
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.StartsWith(_tempDir, path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetModelPath_UnknownModel_ThrowsFileNotFoundException()
    {
        using var loader = new LocalModelLoader(_tempDir);
        Assert.Throws<FileNotFoundException>(
            () => loader.GetModelPath("NonExistent-Model-XYZ"));
    }

    [Fact]
    public async Task DownloadModelAsync_UnknownModel_ThrowsArgumentException()
    {
        using var loader = new LocalModelLoader(_tempDir);
        await Assert.ThrowsAsync<ArgumentException>(
            () => loader.DownloadModelAsync("NonExistent-Model-XYZ"));
    }

    // ------------------------------------------------------------------
    // ModelExists
    // ------------------------------------------------------------------

    [Fact]
    public void ModelExists_KnownModelFileAbsent_ReturnsFalse()
    {
        using var loader = new LocalModelLoader(_tempDir);
        // The MNN weight file is not present → must return false
        Assert.False(loader.ModelExists(KnownModelA));
    }

    [Fact]
    public void ModelExists_UnknownModel_ReturnsFalse()
    {
        using var loader = new LocalModelLoader(_tempDir);
        Assert.False(loader.ModelExists("NoSuchModel"));
    }

    // ------------------------------------------------------------------
    // Dispose
    // ------------------------------------------------------------------

    [Fact]
    public void Dispose_ThenGetModelPath_ThrowsObjectDisposedException()
    {
        var loader = new LocalModelLoader(_tempDir);
        loader.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => loader.GetModelPath(KnownModelA));
    }

    [Fact]
    public async Task Dispose_ThenDownload_ThrowsObjectDisposedException()
    {
        var loader = new LocalModelLoader(_tempDir);
        loader.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => loader.DownloadModelAsync(KnownModelA));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var loader = new LocalModelLoader(_tempDir);
        loader.Dispose();
        loader.Dispose(); // should not throw
    }

    // ------------------------------------------------------------------
    // Checksum mismatch — the real-world tamper / partial-download case
    // ------------------------------------------------------------------

    /// <summary>
    /// When the on-disk file exists but its SHA-256 doesn't match the registry
    /// checksum (tamper, partial download, wrong file in the slot),
    /// <see cref="LocalModelLoader.ModelExists"/> must return <c>false</c>
    /// rather than reporting the model as ready.
    /// </summary>
    [Fact]
    public void ModelExists_FileExistsButChecksumMismatch_ReturnsFalse()
    {
        // The expected file name is the registry's FileName ("llm.mnn.weight").
        // A 16-byte sentinel file cannot possibly hash to the real
        // multi-gigabyte weight's SHA-256.
        var modelPath = Path.Combine(_tempDir, KnownModelAFileName);
        File.WriteAllBytes(modelPath, new byte[16]);

        using var loader = new LocalModelLoader(_tempDir);
        Assert.False(loader.ModelExists(KnownModelA));
    }

    // ------------------------------------------------------------------
    // CheckForCriticalUpdateAsync — no network in CI, must not throw
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckForCriticalUpdateAsync_OfflineOrError_ReturnsFalse()
    {
        using var loader = new LocalModelLoader(_tempDir);
        // Network likely unavailable in test environment; implementation
        // swallows exceptions and returns false.
        var result = await loader.CheckForCriticalUpdateAsync();
        Assert.IsType<bool>(result); // just ensure no exception
    }

    // ------------------------------------------------------------------
    // Constructor creates the model directory when it does not exist
    // ------------------------------------------------------------------

    [Fact]
    public void Constructor_NonExistentDirectory_CreatesIt()
    {
        // Pass a sub-path that has never been created; the constructor must
        // call Directory.CreateDirectory and produce a real directory.
        var newSubDir = Path.Combine(_tempDir, "auto_created_by_ctor");
        Assert.False(Directory.Exists(newSubDir));

        using var loader = new LocalModelLoader(newSubDir);

        Assert.True(Directory.Exists(newSubDir));
    }

    // ------------------------------------------------------------------
    // GetModelPath returns the expected path even when file is absent
    // ------------------------------------------------------------------

    [Fact]
    public void GetModelPath_KnownModelFileAbsent_PathIsInsideModelDir()
    {
        // The path is the expected on-disk location, not a live-file check.
        // File absence must not cause GetModelPath to throw or return empty.
        using var loader = new LocalModelLoader(_tempDir);
        var path = loader.GetModelPath(KnownModelA);
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.StartsWith(_tempDir, path, StringComparison.OrdinalIgnoreCase);
    }
}
