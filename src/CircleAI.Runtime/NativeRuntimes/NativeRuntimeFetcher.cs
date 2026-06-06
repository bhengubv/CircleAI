// NativeRuntimeFetcher.cs
//
// Concrete INativeRuntimeFetcher. Mirrors ModelDownloadService:
//   - Resolve bundle via NativeRuntimeRegistry.Find(os, arch, backend)
//   - Compute deterministic cache directory under cacheRoot
//   - Fast path: extracted folder already exists -> return immediately
//   - Download to temp file, verify SHA-256 (if pinned), extract atomically,
//     swap into place
//   - On any failure: clean up partial archive + extracted directory
//
// Pure file I/O + HttpClient. No external dependency beyond the BCL.

using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using CircleAI.Runtime.Backends;
using CircleAI.Runtime.Capabilities;

namespace CircleAI.Runtime.NativeRuntimes;

/// <summary>
/// Default <see cref="INativeRuntimeFetcher"/> — downloads + verifies + extracts
/// pre-built MNN bundles into a cache directory and returns the on-disk paths.
/// </summary>
public sealed class NativeRuntimeFetcher : INativeRuntimeFetcher, IDisposable
{
    private const int ProgressChunkBytes = 1 * 1024 * 1024; // 1 MB — matches ModelDownloadService.

    private readonly string _cacheRoot;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly NativeRuntimeRegistry _registry;

    /// <summary>
    /// Creates a new fetcher rooted at <paramref name="cacheRoot"/>. The
    /// embedded registry is loaded and an internally managed
    /// <see cref="HttpClient"/> is created.
    /// </summary>
    public NativeRuntimeFetcher(string cacheRoot)
        : this(cacheRoot, NativeRuntimeRegistry.LoadEmbedded(), new HttpClient(), ownsHttpClient: true) { }

    /// <summary>
    /// Creates a new fetcher with a caller-supplied registry + HttpClient.
    /// </summary>
    public NativeRuntimeFetcher(string cacheRoot, NativeRuntimeRegistry registry, HttpClient httpClient)
        : this(cacheRoot, registry, httpClient, ownsHttpClient: false) { }

    private NativeRuntimeFetcher(
        string cacheRoot, NativeRuntimeRegistry registry, HttpClient http, bool ownsHttpClient)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot))
            throw new ArgumentException("Cache root must not be empty.", nameof(cacheRoot));

        _cacheRoot = cacheRoot;
        _registry  = registry ?? throw new ArgumentNullException(nameof(registry));
        _http      = http     ?? throw new ArgumentNullException(nameof(http));
        _ownsHttpClient = ownsHttpClient;

        Directory.CreateDirectory(_cacheRoot);
    }

    /// <inheritdoc/>
    public IReadOnlyList<NativeRuntimeBundle> ListAvailableBundles() => _registry.All;

    /// <inheritdoc/>
    public Task<bool> IsRuntimeCachedAsync(
        OperatingSystemKind os, ArchitectureKind arch, BackendKind backend,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var bundle = _registry.Find(os, arch, backend);
        if (bundle is null) return Task.FromResult(false);

        var extractDir = GetExtractDir(bundle);
        if (!Directory.Exists(extractDir)) return Task.FromResult(false);

        return Task.FromResult(FindMnnCorePath(extractDir, bundle) is not null);
    }

    /// <inheritdoc/>
    public async Task<NativeRuntimeInstall> EnsureRuntimeAsync(
        OperatingSystemKind os, ArchitectureKind arch, BackendKind backend,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var bundle = _registry.Find(os, arch, backend)
            ?? throw new InvalidOperationException(
                $"No native runtime bundle registered for ({os}, {arch}, {backend}). " +
                "Available bundles: " +
                string.Join(", ", _registry.All.Select(b => $"({b.Os},{b.Arch},{b.Backend})")));

        var extractDir = GetExtractDir(bundle);

        // ── Fast path: extracted, MNN core findable in the tree ──────────────
        if (Directory.Exists(extractDir))
        {
            var cachedCorePath = FindMnnCorePath(extractDir, bundle);
            if (cachedCorePath is not null)
            {
                progress?.Report(1.0);
                return new NativeRuntimeInstall(bundle, extractDir, cachedCorePath);
            }
        }

        // ── Slow path: download archive, verify SHA, extract atomically ──────
        var tempArchive = Path.Combine(_cacheRoot, $"{bundle.MnnVersion}-{bundle.Os}-{bundle.Arch}-{bundle.Backend}.partial");
        var tempExtract = extractDir + ".tmp";
        string corePath;
        try
        {
            await DownloadWithFallbackAsync(bundle, tempArchive, progress, ct).ConfigureAwait(false);

            if (bundle.ArchiveSha256Hex is { } expectedSha
                && !await VerifySha256Async(tempArchive, expectedSha, ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"SHA-256 mismatch for runtime bundle ({bundle.Os}, {bundle.Arch}, {bundle.Backend}, MNN {bundle.MnnVersion}). " +
                    "Partial archive has been deleted.");
            }

            // Clean any prior extract attempt before unpacking.
            if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true);
            Directory.CreateDirectory(tempExtract);

            ExtractArchive(tempArchive, tempExtract);

            // Atomic-ish promote: delete the prior install (if any), then rename.
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
            Directory.Move(tempExtract, extractDir);

            File.Delete(tempArchive);

            corePath = FindMnnCorePath(extractDir, bundle)
                ?? throw new InvalidOperationException(
                    $"Extracted runtime bundle is missing the MNN core library. " +
                    $"Searched recursively under '{extractDir}' for '{bundle.MnnCoreLibraryName}'. " +
                    "Either the bundle layout changed or the wrong archive was served.");
        }
        catch
        {
            // Clean up any partial state on any failure.
            if (File.Exists(tempArchive)) try { File.Delete(tempArchive); } catch { }
            if (Directory.Exists(tempExtract)) try { Directory.Delete(tempExtract, recursive: true); } catch { }
            throw;
        }

        return new NativeRuntimeInstall(bundle, extractDir, corePath);
    }

    // ── MNN binary discovery ────────────────────────────────────────────────
    //
    // Alibaba MNN bundles ship the binary at a deep, platform-specific path:
    //   Windows: lib/x64/Release/Dynamic/MD/MNN.dll          (preferred — matches .NET CRT)
    //            lib/x64/Release/Dynamic/MT/MNN.dll          (alt CRT)
    //   macOS:   Dynamic/MNN.framework/Versions/A/MNN        (framework binary)
    //            (fall back to libMNN.dylib when bundle ships flat)
    //   iOS:     Dynamic/MNN.framework/Versions/A/MNN        (framework binary)
    //   Linux:   lib/x64/libMNN.so                           (typical)
    //   Android: jni/{abi}/libMNN.so                         (per-ABI)
    //
    // We search by file name with per-platform preferences. Returning null
    // means the bundle layout did NOT contain MNN where we'd expect — at
    // which point EnsureRuntimeAsync raises a clear error citing the
    // searched location.

    /// <summary>
    /// Searches the extracted bundle directory tree for the MNN core library
    /// using a platform-specific preference ordering. Returns the absolute
    /// path, or <c>null</c> if not found.
    /// </summary>
    private static string? FindMnnCorePath(string extractRoot, NativeRuntimeBundle bundle)
    {
        // 1. macOS / iOS framework layout: prefer MNN.framework/Versions/<v>/MNN
        if (bundle.Os is OperatingSystemKind.MacOS or OperatingSystemKind.IOS)
        {
            var fw = Directory.EnumerateFiles(extractRoot, "MNN", SearchOption.AllDirectories)
                .Select(NormaliseSep)
                .FirstOrDefault(p => p.Contains("/MNN.framework/Versions/", StringComparison.Ordinal)
                                     && p.EndsWith("/MNN", StringComparison.Ordinal));
            if (fw is not null) return fw;
        }

        // 2. By-name match anywhere in the tree.
        var candidates = Directory.EnumerateFiles(
                extractRoot, bundle.MnnCoreLibraryName, SearchOption.AllDirectories)
            .Select(NormaliseSep)
            .ToList();

        if (candidates.Count == 0) return null;

        // 3. Per-platform preference:
        //    Windows: prefer Dynamic over Static, MD over MT, Release over Debug.
        //    Others : take the first hit.
        if (bundle.Os == OperatingSystemKind.Windows)
        {
            return candidates
                .OrderBy(p => p.Contains("/Static/",  StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy (p => p.Contains("/MT/",      StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy (p => p.Contains("/Debug/",   StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .First();
        }

        return candidates[0];
    }

    private static string NormaliseSep(string p) => p.Replace('\\', '/');

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string GetExtractDir(NativeRuntimeBundle b) =>
        Path.Combine(_cacheRoot,
            $"{b.MnnVersion}-{b.Os.ToString().ToLowerInvariant()}-{b.Arch.ToString().ToLowerInvariant()}-{b.Backend.ToString().ToLowerInvariant()}");

    private async Task DownloadWithFallbackAsync(
        NativeRuntimeBundle bundle, string destPath,
        IProgress<double>? progress, CancellationToken ct)
    {
        // Try primary, then fallback. Each attempt cleans up its own partial file.
        try
        {
            await DownloadToFileAsync(bundle.PrimaryUri, destPath, progress, ct).ConfigureAwait(false);
            return;
        }
        catch (Exception primaryEx)
        {
            if (File.Exists(destPath)) try { File.Delete(destPath); } catch { }
            if (bundle.FallbackUri is null) throw;

            try
            {
                await DownloadToFileAsync(bundle.FallbackUri, destPath, progress, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception fallbackEx)
            {
                if (File.Exists(destPath)) try { File.Delete(destPath); } catch { }
                throw new AggregateException(
                    $"Both primary and fallback download failed for bundle ({bundle.Os}, {bundle.Arch}, {bundle.Backend}, MNN {bundle.MnnVersion}).",
                    primaryEx, fallbackEx);
            }
        }
    }

    private async Task DownloadToFileAsync(
        Uri uri, string destPath, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;

        await using var contentStream = await response.Content
            .ReadAsStreamAsync(ct)
            .ConfigureAwait(false);

        await using var fileStream = new FileStream(
            destPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81_920, useAsync: true);

        var buffer = new byte[81_920];
        long bytesRead = 0;
        long bytesUntilNextReport = ProgressChunkBytes;
        int read;
        while ((read = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            bytesRead += read;
            bytesUntilNextReport -= read;
            if (progress is not null && bytesUntilNextReport <= 0)
            {
                var ratio = totalBytes > 0 ? (double)bytesRead / totalBytes : 0.0;
                progress.Report(Math.Min(ratio, 0.999));
                bytesUntilNextReport = ProgressChunkBytes;
            }
        }
        progress?.Report(1.0);
    }

    private static async Task<bool> VerifySha256Async(
        string filePath, string expectedHex, CancellationToken ct)
    {
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81_920, useAsync: true);
        var actual = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        var actualHex = Convert.ToHexString(actual);
        return string.Equals(actualHex, expectedHex.ToUpperInvariant(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the downloaded archive. Format is sniffed from magic bytes
    /// (ZIP <c>PK\x03\x04</c>, GZIP <c>0x1F 0x8B</c>) so the archive does
    /// not need a particular file-name extension — the on-disk file is a
    /// staging name (<c>{tuple}.partial</c>) regardless of the source URL.
    /// </summary>
    private static void ExtractArchive(string archivePath, string destDir)
    {
        var kind = SniffArchiveKind(archivePath);
        switch (kind)
        {
            case ArchiveKind.Zip:
                ZipFile.ExtractToDirectory(archivePath, destDir, overwriteFiles: true);
                return;

            case ArchiveKind.TarGz:
                var tarPath = archivePath + ".tar";
                try
                {
                    using (var src = File.OpenRead(archivePath))
                    using (var gz  = new GZipStream(src, CompressionMode.Decompress))
                    using (var dst = File.Create(tarPath))
                        gz.CopyTo(dst);

                    System.Formats.Tar.TarFile.ExtractToDirectory(tarPath, destDir, overwriteFiles: true);
                }
                finally
                {
                    if (File.Exists(tarPath)) try { File.Delete(tarPath); } catch { }
                }
                return;

            default:
                throw new InvalidOperationException(
                    $"Unrecognised archive format for '{Path.GetFileName(archivePath)}'. " +
                    "Supported: ZIP (magic PK\\x03\\x04), TAR.GZ (magic 0x1F 0x8B).");
        }
    }

    private enum ArchiveKind { Unknown = 0, Zip, TarGz }

    private static ArchiveKind SniffArchiveKind(string archivePath)
    {
        using var fs = File.OpenRead(archivePath);
        Span<byte> head = stackalloc byte[4];
        int read = fs.Read(head);
        if (read >= 4 && head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04)
            return ArchiveKind.Zip;
        if (read >= 2 && head[0] == 0x1F && head[1] == 0x8B)
            return ArchiveKind.TarGz;
        return ArchiveKind.Unknown;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}
