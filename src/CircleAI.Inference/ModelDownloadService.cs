#nullable enable

using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Inference;

/// <summary>
/// Default implementation of <see cref="IModelDownloadService"/>.
/// Models are stored as <c>{storageDirectory}/{modelId}.gguf</c>.
/// </summary>
public sealed class ModelDownloadService : IModelDownloadService
{
    private const int ProgressChunkBytes = 1 * 1024 * 1024; // 1 MB

    private readonly string _storageDirectory;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// Creates a new instance using an internally managed <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="storageDirectory">
    /// Root directory where GGUF files are cached. Created if it does not exist.
    /// </param>
    public ModelDownloadService(string storageDirectory)
        : this(storageDirectory, new HttpClient(), ownsHttpClient: true) { }

    /// <summary>
    /// Creates a new instance using a caller-supplied <see cref="HttpClient"/>.
    /// The caller retains ownership and is responsible for disposing it.
    /// </summary>
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
        // User-Agent — so FallbackUrl in the catalog is unreachable without one.
        // Setting a realistic UA on the owned HttpClient makes both PrimaryUrl
        // and FallbackUrl work. Callers that pass their own HttpClient are
        // expected to configure their own UA; we only touch ours.
        if (ownsHttpClient && !_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36 CircleAI/1.0");
        }

        Directory.CreateDirectory(_storageDirectory);
    }

    /// <inheritdoc/>
    public async Task<string> EnsureModelAsync(
        string modelId,
        Uri downloadUri,
        string? expectedSha256,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        ValidateModelId(modelId);
        ArgumentNullException.ThrowIfNull(downloadUri);

        var filePath = GetFilePath(modelId);

        // Fast path: file exists and hash matches.
        if (File.Exists(filePath) && expectedSha256 is not null)
        {
            if (await VerifySha256Async(filePath, expectedSha256, ct).ConfigureAwait(false))
            {
                progress?.Report(1.0);
                return filePath;
            }
            // Hash mismatch — delete stale file and re-download.
            File.Delete(filePath);
        }
        else if (File.Exists(filePath) && expectedSha256 is null)
        {
            // No hash supplied — trust the existing file.
            progress?.Report(1.0);
            return filePath;
        }

        // Download the file.
        var tempPath = filePath + ".tmp";
        try
        {
            await DownloadToFileAsync(downloadUri, tempPath, progress, ct).ConfigureAwait(false);

            // Verify SHA-256 when a digest was supplied.
            if (expectedSha256 is not null)
            {
                bool valid = await VerifySha256Async(tempPath, expectedSha256, ct).ConfigureAwait(false);
                if (!valid)
                {
                    File.Delete(tempPath);
                    throw new InvalidOperationException(
                        $"SHA-256 mismatch for model '{modelId}'. " +
                        "The downloaded file has been deleted.");
                }
            }

            // Atomically promote temp file.
            if (File.Exists(filePath))
                File.Delete(filePath);
            File.Move(tempPath, filePath);
        }
        catch
        {
            // Clean up partial downloads on any failure.
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }

        return filePath;
    }

    /// <inheritdoc/>
    public Task<bool> IsModelCachedAsync(string modelId, CancellationToken ct)
    {
        ValidateModelId(modelId);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(GetFilePath(modelId)));
    }

    /// <inheritdoc/>
    public Task DeleteModelAsync(string modelId, CancellationToken ct)
    {
        ValidateModelId(modelId);
        ct.ThrowIfCancellationRequested();

        var filePath = GetFilePath(modelId);
        if (File.Exists(filePath))
            File.Delete(filePath);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<long> GetAvailableDiskSpaceBytesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Resolve the absolute path so DriveInfo works correctly.
        var absoluteDir = Path.GetFullPath(_storageDirectory);
        var root = Path.GetPathRoot(absoluteDir)
            ?? throw new InvalidOperationException(
                $"Cannot determine drive root for '{absoluteDir}'.");

        var drive = new DriveInfo(root);
        return ValueTask.FromResult(drive.AvailableFreeSpace);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string GetFilePath(string modelId) =>
        Path.Combine(_storageDirectory, $"{modelId}.gguf");

    private static void ValidateModelId(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("Model ID must not be empty.", nameof(modelId));
    }

    private async Task DownloadToFileAsync(
        Uri uri,
        string destPath,
        IProgress<double>? progress,
        CancellationToken ct)
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
        long bytesRead = 0L;
        long bytesUntilNextReport = ProgressChunkBytes;
        int read;

        while ((read = await contentStream
                   .ReadAsync(buffer, ct)
                   .ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            bytesRead += read;
            bytesUntilNextReport -= read;

            if (progress is not null && bytesUntilNextReport <= 0)
            {
                var ratio = totalBytes > 0 ? (double)bytesRead / totalBytes : 0.0;
                progress.Report(Math.Min(ratio, 0.999)); // Reserve 1.0 for completion.
                bytesUntilNextReport = ProgressChunkBytes;
            }
        }

        progress?.Report(1.0);
    }

    private static async Task<bool> VerifySha256Async(
        string filePath,
        string expectedHex,
        CancellationToken ct)
    {
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81_920, useAsync: true);

        var actualHash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        var actualHex = Convert.ToHexString(actualHash);

        // The registry pins SHA-256 in "sha256:<hex>" form (the conventional
        // multihash-style prefix). Strip an algorithm prefix when present so
        // both forms compare correctly. Without this strip, EVERY model
        // download fails with a spurious mismatch — the compare sees
        // "SHA256:<HEX>" vs "<HEX>" and rejects the file even though the
        // bytes hash exactly to the pinned value.
        var expectedNormalised = StripShaAlgorithmPrefix(expectedHex);

        return string.Equals(actualHex, expectedNormalised,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the hex portion of a checksum, stripping an optional leading
    /// algorithm token of the form <c>sha256:</c>, <c>SHA-256:</c>, etc.
    /// </summary>
    internal static string StripShaAlgorithmPrefix(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        // Trim whitespace BEFORE inspecting so "  sha256:abc  " strips cleanly.
        var trimmed = raw.Trim();
        var colon = trimmed.IndexOf(':');
        if (colon < 0) return trimmed;
        // Only treat the token before ':' as an algorithm name when it is
        // short and consists entirely of [A-Za-z0-9-_] — never throw away
        // part of the hex.
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

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }
}
