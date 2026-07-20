#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core.Models;

namespace CircleAI.Inference;

/// <summary>
/// Default implementation of <see cref="IModelDownloadService"/>.
/// <para>
/// Single-file entries land at <c>{storageDirectory}/{modelId}.gguf</c>;
/// bundle entries land at <c>{storageDirectory}/{modelId}/</c> with every
/// bundle file written under that directory.
/// </para>
/// </summary>
public sealed class ModelDownloadService : IModelDownloadService, IDisposable
{
    private const int ProgressChunkBytes = 1 * 1024 * 1024; // 1 MB

    private readonly string _storageDirectory;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public ModelDownloadService(string storageDirectory)
        : this(storageDirectory, new HttpClient(), ownsHttpClient: true) { }

    public ModelDownloadService(string storageDirectory, HttpClient httpClient)
        : this(storageDirectory, httpClient, ownsHttpClient: false) { }

    private ModelDownloadService(string storageDirectory, HttpClient httpClient, bool ownsHttpClient)
    {
        if (string.IsNullOrWhiteSpace(storageDirectory))
            throw new ArgumentException("Storage directory must not be empty.", nameof(storageDirectory));

        _storageDirectory = storageDirectory;
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;

        // ModelScope's CDN (resolve/master URLs) returns 403 to clients with no
        // User-Agent — without one, FallbackUrl in the catalog is unreachable.
        // Set a realistic UA on the owned HttpClient so both PrimaryUrl and
        // FallbackUrl work. Callers that pass their own HttpClient are
        // expected to configure their own UA.
        if (ownsHttpClient && !_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36 CircleAI/1.0");
        }

        Directory.CreateDirectory(_storageDirectory);
    }

    // ── Single-file (legacy) ─────────────────────────────────────────────

    public async Task<string> EnsureModelAsync(
        string modelId,
        Uri downloadUri,
        string? expectedSha256,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        ValidateModelId(modelId);
        ArgumentNullException.ThrowIfNull(downloadUri);

        var filePath = GetSingleFilePath(modelId);

        if (File.Exists(filePath) && expectedSha256 is not null)
        {
            if (await VerifySha256Async(filePath, expectedSha256, ct).ConfigureAwait(false))
            {
                progress?.Report(1.0);
                return filePath;
            }
            File.Delete(filePath);
        }
        else if (File.Exists(filePath) && expectedSha256 is null)
        {
            progress?.Report(1.0);
            return filePath;
        }

        var tempPath = filePath + ".tmp";
        try
        {
            await DownloadToFileAsync(downloadUri, tempPath, progress, ct).ConfigureAwait(false);

            if (expectedSha256 is not null)
            {
                if (!await VerifySha256Async(tempPath, expectedSha256, ct).ConfigureAwait(false))
                {
                    File.Delete(tempPath);
                    throw new InvalidOperationException(
                        $"SHA-256 mismatch for model '{modelId}'. The downloaded file has been deleted.");
                }
            }

            if (File.Exists(filePath)) File.Delete(filePath);
            File.Move(tempPath, filePath);
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
        return filePath;
    }

    // ── Bundle ────────────────────────────────────────────────────────────

    public Task<string> EnsureBundleAsync(
        string modelId,
        string repo,
        IReadOnlyList<BundleFileSpec> bundleFiles,
        IProgress<double>? progress,
        CancellationToken ct)
        => EnsureBundleAsync(modelId, repo, CircleAI.Core.ModelSource.ModelScope, bundleFiles, progress, ct);

    public async Task<string> EnsureBundleAsync(
        string modelId,
        string repo,
        CircleAI.Core.ModelSource source,
        IReadOnlyList<BundleFileSpec> bundleFiles,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        ValidateModelId(modelId);
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repo path is required for bundle entries.", nameof(repo));
        ArgumentNullException.ThrowIfNull(bundleFiles);
        if (bundleFiles.Count == 0)
            throw new ArgumentException("Bundle file list must not be empty.", nameof(bundleFiles));

        var modelDir = Path.Combine(_storageDirectory, modelId);
        Directory.CreateDirectory(modelDir);

        var totalBytes = 0L;
        foreach (var f in bundleFiles) totalBytes += Math.Max(0, f.SizeBytes);
        var doneBytes = 0L;

        foreach (var file in bundleFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(file.Name))
                throw new InvalidOperationException(
                    $"Bundle for '{modelId}' contains a file with no Name.");

            var destPath = Path.Combine(modelDir, file.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            // Skip when cached + valid.
            if (File.Exists(destPath) &&
                await VerifySha256Async(destPath, file.Sha256, ct).ConfigureAwait(false))
            {
                doneBytes += file.SizeBytes;
                ReportOverall(progress, doneBytes, totalBytes);
                continue;
            }
            if (File.Exists(destPath)) File.Delete(destPath);

            var tempPath = destPath + ".tmp";
            try
            {
                IProgress<double>? perFile = progress is null
                    ? null
                    : new Progress<double>(p =>
                        ReportOverall(progress, doneBytes + (long)(file.SizeBytes * p), totalBytes));

                // PrimaryUrl → FallbackUrl. Either one is the same bytes; we try
                // both before giving up so a transient CDN hiccup doesn't kill an
                // otherwise viable bundle download.
                var primary = BuildPrimaryUrl(source, repo, file.Name);
                var fallback = BuildFallbackUrl(source, repo, file.Name);
                try
                {
                    await DownloadToFileAsync(primary, tempPath, perFile, ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                    await DownloadToFileAsync(fallback, tempPath, perFile, ct).ConfigureAwait(false);
                }

                if (!await VerifySha256Async(tempPath, file.Sha256, ct).ConfigureAwait(false))
                {
                    File.Delete(tempPath);
                    throw new InvalidOperationException(
                        $"SHA-256 mismatch for bundle file '{file.Name}' of model '{modelId}'. " +
                        "The downloaded file has been deleted.");
                }
                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(tempPath, destPath);
                doneBytes += file.SizeBytes;
                ReportOverall(progress, doneBytes, totalBytes);
            }
            catch
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
                throw;
            }
        }

        progress?.Report(1.0);
        return modelDir;
    }

    /// <summary>
    /// Stamps an <c>installed.json</c> file in <paramref name="modelDir"/>
    /// describing what's now on disk. Read later by
    /// <see cref="ModelRegistryService.CheckForUpgradesAsync"/> to detect
    /// drift against the live registry.
    /// <para>
    /// Call this immediately after a successful <c>EnsureBundleAsync</c> when you
    /// have the model's Version string available (typically from the
    /// <see cref="ModelEntry"/> the download was driven by). Best-effort — silent
    /// failures are swallowed so a manifest hiccup never breaks a working install.
    /// </para>
    /// </summary>
    /// <param name="modelDir">Absolute path returned by <c>EnsureBundleAsync</c>.</param>
    /// <param name="modelId">Model identifier (must match the registry's <see cref="ModelEntry.Name"/>).</param>
    /// <param name="version">Version string from the registry entry.</param>
    /// <param name="repo">Repo path (e.g. <c>MNN/Qwen3-0.6B-MNN</c> or <c>rhasspy/piper-voices</c>).</param>
    /// <param name="bundleFiles">The same file list passed to <c>EnsureBundleAsync</c>.</param>
    public async Task WriteInstalledManifestAsync(
        string                       modelDir,
        string                       modelId,
        string                       version,
        string?                      repo,
        IReadOnlyList<BundleFileSpec> bundleFiles,
        CancellationToken            ct = default)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(modelDir);
            ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
            ArgumentNullException.ThrowIfNull(bundleFiles);

            long totalBytes = 0;
            var files = new List<BundleFile>(bundleFiles.Count);
            foreach (var f in bundleFiles)
            {
                files.Add(new BundleFile(f.Name, f.Sha256, f.SizeBytes));
                totalBytes += Math.Max(0, f.SizeBytes);
            }

            var manifest = new InstalledManifest(
                ModelId:        modelId,
                Version:        version ?? string.Empty,
                Repo:           repo,
                TotalBytes:     totalBytes,
                Files:          files,
                InstalledAtUtc: DateTimeOffset.UtcNow);

            var path = Path.Combine(modelDir, "installed.json");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJsonOpts);
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort. A missing manifest just downgrades CheckForUpgradesAsync
            // to UpgradeReason.Unknown — never a hard failure.
        }
    }

    private static readonly JsonSerializerOptions ManifestJsonOpts = new()
    {
        WriteIndented = true,
    };

    // A bundle-relative file name may contain '/', which must survive into the
    // URL as a path separator, not be escaped to %2F. Escape each SEGMENT.
    private static string EscapePath(string fileName)
        => string.Join('/', fileName.Split('/').Select(Uri.EscapeDataString));

    private static Uri BuildPrimaryUrl(CircleAI.Core.ModelSource source, string repo, string fileName)
        => source == CircleAI.Core.ModelSource.HuggingFace
            ? new($"https://huggingface.co/{repo}/resolve/main/{EscapePath(fileName)}?download=true")
            : new($"https://modelscope.cn/api/v1/models/{repo}/repo?Revision=master&FilePath={Uri.EscapeDataString(fileName)}");

    private static Uri BuildFallbackUrl(CircleAI.Core.ModelSource source, string repo, string fileName)
        => source == CircleAI.Core.ModelSource.HuggingFace
            ? new($"https://huggingface.co/{repo}/resolve/main/{EscapePath(fileName)}")
            : new($"https://modelscope.cn/models/{repo}/resolve/master/{Uri.EscapeDataString(fileName)}");

    private static void ReportOverall(IProgress<double>? p, long done, long total)
    {
        if (p is null) return;
        if (total <= 0) p.Report(0.0);
        else p.Report(Math.Min(0.999, (double)done / total));
    }

    // ── Common ───────────────────────────────────────────────────────────

    public Task<bool> IsModelCachedAsync(string modelId, CancellationToken ct)
    {
        ValidateModelId(modelId);
        ct.ThrowIfCancellationRequested();
        var singleFile = GetSingleFilePath(modelId);
        if (File.Exists(singleFile)) return Task.FromResult(true);
        var dir = Path.Combine(_storageDirectory, modelId);
        return Task.FromResult(Directory.Exists(dir));
    }

    public Task DeleteModelAsync(string modelId, CancellationToken ct)
    {
        ValidateModelId(modelId);
        ct.ThrowIfCancellationRequested();
        var singleFile = GetSingleFilePath(modelId);
        if (File.Exists(singleFile)) File.Delete(singleFile);
        var dir = Path.Combine(_storageDirectory, modelId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        return Task.CompletedTask;
    }

    public ValueTask<long> GetAvailableDiskSpaceBytesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var absoluteDir = Path.GetFullPath(_storageDirectory);
        var root = Path.GetPathRoot(absoluteDir)
            ?? throw new InvalidOperationException($"Cannot determine drive root for '{absoluteDir}'.");
        return ValueTask.FromResult(new DriveInfo(root).AvailableFreeSpace);
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private string GetSingleFilePath(string modelId) =>
        Path.Combine(_storageDirectory, $"{modelId}.gguf");

    private static void ValidateModelId(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("Model ID must not be empty.", nameof(modelId));
    }

    private async Task DownloadToFileAsync(
        Uri uri, string destPath, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fileStream = new FileStream(
            destPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81_920, useAsync: true);

        var buffer = new byte[81_920];
        long bytesRead = 0L;
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

        var actualHash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        var actualHex = Convert.ToHexString(actualHash);

        // The registry pins SHA-256 in "sha256:<hex>" form. Strip the prefix
        // (and trim whitespace) before comparing. Without this, every model
        // load fails with a spurious mismatch.
        var expectedNormalised = StripShaAlgorithmPrefix(expectedHex);

        return string.Equals(actualHex, expectedNormalised, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the hex portion of a SHA-256 checksum, stripping an optional
    /// leading algorithm token of the form <c>sha256:</c>, <c>SHA-256:</c>, etc.
    /// </summary>
    internal static string StripShaAlgorithmPrefix(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var trimmed = raw.Trim();
        var colon = trimmed.IndexOf(':');
        if (colon < 0) return trimmed;
        var prefix = trimmed.AsSpan(0, colon);
        if (prefix.Length is > 0 and <= 16)
        {
            bool isAlgName = true;
            foreach (var c in prefix)
            {
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_')) { isAlgName = false; break; }
            }
            if (isAlgName) return trimmed[(colon + 1)..].Trim();
        }
        return trimmed;
    }
}
