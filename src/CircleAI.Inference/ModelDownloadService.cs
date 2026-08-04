#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;           // DownloadProgress, DownloadPhase
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
        : this(storageDirectory, new HttpClient(CreateResilientHandler()), ownsHttpClient: true) { }

    /// <summary>
    /// Builds the handler that survives a dead system resolver.
    /// </summary>
    /// <remarks>
    /// The bypass belongs HERE, at the socket layer, not in the retry loop.
    /// Rewriting the request URI to the resolved IP would send the wrong SNI and
    /// fail certificate validation — the certificate is for <c>modelscope.cn</c>,
    /// not for <c>47.251.62.57</c>. A <c>ConnectCallback</c> keeps the URI (and
    /// therefore the Host header, the SNI and the cert check) intact while
    /// pointing the socket at an address obtained out of band.
    /// <para>
    /// Applies to every request through this client, so nothing else has to know
    /// the resolver might be broken.
    /// </para>
    /// </remarks>
    private static SocketsHttpHandler CreateResilientHandler()
    {
        var preflight = new NetworkPreflight();

        return new SocketsHttpHandler
        {
            // Bound the DNS cache so a stale record cannot outlive a network
            // change for the length of a long-running app session.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),

            ConnectCallback = async (context, ct) =>
            {
                var host = context.DnsEndPoint.Host;
                var port = context.DnsEndPoint.Port;

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    try
                    {
                        // Fast path: the system resolver, used by ConnectAsync.
                        await socket.ConnectAsync(host, port, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (NetworkDiagnosis.Classify(ex).Fault == NetworkFault.DnsFailure)
                    {
                        // Slow path: resolve out of band and connect to the address.
                        var addresses = await preflight.ResolveAsync(host, ct).ConfigureAwait(false);
                        if (addresses.Count == 0) throw;

                        await socket.ConnectAsync(addresses.ToArray(), port, ct).ConfigureAwait(false);
                    }

                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
    }

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

    /// <summary>
    /// Bundle download reporting RICH progress — bytes, rate, ETA, file N of M,
    /// and phase (downloading / resuming / retrying / verifying).
    /// </summary>
    /// <remarks>
    /// The <see cref="IProgress{T}"/>-of-<c>double</c> overloads compute all of
    /// this internally and then throw it away, leaving a host with a bare 0..1
    /// ratio. On a phone pulling 433 MB that is the difference between "10%…
    /// 20%…" — indistinguishable from a hang — and a progress bar that shows MB,
    /// rate, ETA, which file, and whether it is retrying.
    /// <para>
    /// The <c>double</c> overloads remain and simply adapt to this one, so no
    /// existing caller changes.
    /// </para>
    /// <para>
    /// ONE SOURCE-BREAKING CASE: a caller passing a bare <c>null</c> for
    /// progress now hits CS0121, because <c>null</c> fits both
    /// <c>IProgress&lt;DownloadProgress&gt;</c> and <c>IProgress&lt;double&gt;</c>.
    /// Fix by casting — <c>(IProgress&lt;DownloadProgress&gt;?)null</c> — or by
    /// passing a real reporter. Binary compatibility is unaffected; only source
    /// with an untyped null needs the cast.
    /// </para>
    /// </remarks>
    public Task<string> EnsureBundleAsync(
        string modelId,
        string repo,
        CircleAI.Core.ModelSource source,
        IReadOnlyList<BundleFileSpec> bundleFiles,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
        => EnsureBundleCoreAsync(modelId, repo, source, bundleFiles, progress, ct);

    public Task<string> EnsureBundleAsync(
        string modelId,
        string repo,
        CircleAI.Core.ModelSource source,
        IReadOnlyList<BundleFileSpec> bundleFiles,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        // Adapt the legacy ratio-only contract onto the rich one.
        IProgress<DownloadProgress>? rich = progress is null
            ? null
            : new Progress<DownloadProgress>(p => progress.Report(p.Ratio));

        return EnsureBundleCoreAsync(modelId, repo, source, bundleFiles, rich, ct);
    }

    private async Task<string> EnsureBundleCoreAsync(
        string modelId,
        string repo,
        CircleAI.Core.ModelSource source,
        IReadOnlyList<BundleFileSpec> bundleFiles,
        IProgress<DownloadProgress>? progress,
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

        // Rate/ETA are measured across the WHOLE bundle, not per file. Per-file
        // figures would reset the ETA at every file boundary, which on a 7-file
        // bundle reads as the estimate jumping around at random.
        var startedAt = System.Diagnostics.Stopwatch.StartNew();
        var fileIndex = 0;

        void Report(string name, long received, DownloadPhase phase, int attempt = 1)
        {
            if (progress is null) return;

            var elapsed = startedAt.Elapsed.TotalSeconds;
            var rate = elapsed > 0.5 ? received / elapsed : 0.0;
            var remaining = rate > 0 && totalBytes > received
                ? TimeSpan.FromSeconds((totalBytes - received) / rate)
                : TimeSpan.Zero;

            progress.Report(new DownloadProgress
            {
                FileName              = name,
                BytesReceived         = received,
                TotalBytes            = totalBytes,
                BytesPerSecond        = rate,
                EstimatedTimeRemaining = remaining,
                Phase                 = phase,
                FileIndex             = fileIndex,
                FileCount             = bundleFiles.Count,
                Attempt               = attempt,
            });
        }

        foreach (var file in bundleFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(file.Name))
                throw new InvalidOperationException(
                    $"Bundle for '{modelId}' contains a file with no Name.");

            fileIndex++;
            var destPath = Path.Combine(modelDir, file.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            // Skip when cached + valid. Hashing a 400 MB file is not instant, so
            // say so — otherwise a cached start looks like a freeze.
            if (File.Exists(destPath))
            {
                Report(file.Name, doneBytes, DownloadPhase.Verifying);
                if (await VerifySha256Async(destPath, file.Sha256, ct).ConfigureAwait(false))
                {
                    doneBytes += file.SizeBytes;
                    Report(file.Name, doneBytes, DownloadPhase.Cached);
                    continue;
                }
                File.Delete(destPath);
            }

            var tempPath = destPath + ".tmp";
            try
            {
                // Resume shows up as a non-zero starting offset for this file.
                var resumeFrom = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0L;
                var startPhase = resumeFrom > 0 ? DownloadPhase.Resuming : DownloadPhase.Downloading;
                Report(file.Name, doneBytes + resumeFrom, startPhase);

                IProgress<double>? perFile = progress is null
                    ? null
                    : new Progress<double>(p =>
                        Report(file.Name, doneBytes + (long)(file.SizeBytes * p), DownloadPhase.Downloading));

                // PrimaryUrl → FallbackUrl. Either one is the same bytes; we try
                // both before giving up so a transient CDN hiccup doesn't kill an
                // otherwise viable bundle download.
                var primary = BuildPrimaryUrl(source, repo, file.Name);
                var fallback = BuildFallbackUrl(source, repo, file.Name);
                try
                {
                    await DownloadWithRetryAsync(primary, tempPath, perFile, ct,
                        attempt => Report(file.Name, doneBytes, DownloadPhase.Retrying, attempt))
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // The CALLER cancelled. The old `catch (Exception)` swallowed
                    // this and started a second download against the fallback URL
                    // — so cancelling a download made it download again.
                    throw;
                }
                catch (Exception)
                {
                    // KEEP THE PARTIAL. This used to delete it before trying the
                    // other URL, so a hiccup on the primary at 600 MB threw away
                    // 600 MB — on a phone, that is somebody's data bundle spent
                    // twice for nothing. Both URLs serve the SAME bytes and both
                    // honour Range, so the fallback simply carries on from where
                    // the primary stopped.
                    await DownloadWithRetryAsync(fallback, tempPath, perFile, ct,
                        attempt => Report(file.Name, doneBytes, DownloadPhase.Retrying, attempt))
                        .ConfigureAwait(false);
                }

                // Hashing hundreds of MB on a phone takes real seconds during
                // which no bytes move. Name the phase so it does not read as a
                // stall right at the finish line.
                Report(file.Name, doneBytes + file.SizeBytes, DownloadPhase.Verifying);

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
                Report(file.Name, doneBytes, DownloadPhase.Downloading);
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

        Report(string.Empty, totalBytes, DownloadPhase.Complete);
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
        => source switch
        {
            // Buckets take no branch segment — "resolve/main/x" would 404.
            CircleAI.Core.ModelSource.HuggingFaceBucket =>
                new($"https://huggingface.co/buckets/{repo}/resolve/{EscapePath(fileName)}?download=true"),
            CircleAI.Core.ModelSource.HuggingFace =>
                new($"https://huggingface.co/{repo}/resolve/main/{EscapePath(fileName)}?download=true"),
            _ =>
                new($"https://modelscope.cn/api/v1/models/{repo}/repo?Revision=master&FilePath={Uri.EscapeDataString(fileName)}"),
        };

    private static Uri BuildFallbackUrl(CircleAI.Core.ModelSource source, string repo, string fileName)
        => source switch
        {
            CircleAI.Core.ModelSource.HuggingFaceBucket =>
                new($"https://huggingface.co/buckets/{repo}/resolve/{EscapePath(fileName)}"),
            CircleAI.Core.ModelSource.HuggingFace =>
                new($"https://huggingface.co/{repo}/resolve/main/{EscapePath(fileName)}"),
            _ =>
                new($"https://modelscope.cn/models/{repo}/resolve/master/{Uri.EscapeDataString(fileName)}"),
        };

    private static void ReportOverall(IProgress<double>? p, long done, long total)
    {
        if (p is null) return;
        if (total <= 0) p.Report(0.0);
        else p.Report(Math.Min(0.999, (double)done / total));
    }

    // ── Common ───────────────────────────────────────────────────────────

    /// <summary>Is this model fully downloaded and usable?</summary>
    /// <remarks>
    /// "The directory exists" is NOT the answer, though it used to be the one
    /// this returned. <see cref="EnsureBundleCoreAsync"/> creates that directory
    /// before fetching a single byte, so an interrupted download leaves a folder
    /// that reports itself cached forever — and a model that can never repair
    /// itself, because nothing will re-download something already "cached".
    ///
    /// The completion marker is <c>installed.json</c>, written only after every
    /// file has landed and verified. Its contents are checked against the disk,
    /// so a manifest left over from a since-deleted file does not count either.
    /// </remarks>
    public Task<bool> IsModelCachedAsync(string modelId, CancellationToken ct)
    {
        ValidateModelId(modelId);
        ct.ThrowIfCancellationRequested();

        var singleFile = GetSingleFilePath(modelId);
        if (File.Exists(singleFile)) return Task.FromResult(true);

        var dir = Path.Combine(_storageDirectory, modelId);
        if (!Directory.Exists(dir)) return Task.FromResult(false);

        var manifestPath = Path.Combine(dir, "installed.json");
        if (!File.Exists(manifestPath)) return Task.FromResult(false);

        try
        {
            var manifest = JsonSerializer.Deserialize<InstalledManifest>(File.ReadAllText(manifestPath));
            if (manifest?.Files is null || manifest.Files.Count == 0) return Task.FromResult(false);

            foreach (var f in manifest.Files)
            {
                var path = Path.Combine(dir, f.Name);
                // Size, not just existence: a truncated weight file is the exact
                // failure this is here to catch.
                if (!File.Exists(path) || new FileInfo(path).Length != f.SizeBytes)
                    return Task.FromResult(false);
            }
            return Task.FromResult(true);
        }
        catch
        {
            // An unreadable manifest means we cannot prove the model is complete,
            // and "re-download" is the safe answer to that.
            return Task.FromResult(false);
        }
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

    /// <summary>Attempts per URL before giving up. 1 + 3 retries.</summary>
    private const int MaxAttempts = 4;

    /// <summary>
    /// Downloads with backoff, RESUMING from whatever is already on disk.
    /// </summary>
    /// <remarks>
    /// The download had no retry at all. On a phone that means one DNS blip or
    /// one signal drop, at any point in a 433 MB transfer, threw the whole
    /// thing away — and the retry (if a human pressed the button again) started
    /// from byte 0. On a metered or rural connection that is not a nuisance, it
    /// is the difference between the product working and not.
    /// <para>
    /// Only <see cref="NetworkDiagnosis.IsTransient"/> faults are retried:
    /// spinning on a 404 or a failed TLS handshake is just a slower failure
    /// with a worse error message.
    /// </para>
    /// </remarks>
    private async Task DownloadWithRetryAsync(
        Uri uri, string destPath, IProgress<double>? progress, CancellationToken ct,
        Action<int>? onRetry = null)
    {
        NetworkDiagnosis? last = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await DownloadToFileAsync(uri, destPath, progress, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = NetworkDiagnosis.Classify(ex);

                // A DNS failure here has ALREADY been through the DoH bypass —
                // it lives in the ConnectCallback on the owned HttpClient (see
                // CreateResilientHandler), so it applies to every request
                // transparently and keeps the hostname for SNI and certificate
                // validation. Reaching this point means both the system resolver
                // AND the out-of-band resolvers failed, which means the link is
                // genuinely dead rather than just the resolver.
                if (!last.IsTransient || attempt == MaxAttempts)
                {
                    throw new ModelDownloadException(
                        $"Download of '{uri}' failed after {attempt} attempt(s). {last}", last, ex);
                }

                // Tell the host we are retrying. Sitting silently through a
                // backoff is indistinguishable from a hang, and it is the moment
                // a user is most likely to force-quit.
                onRetry?.Invoke(attempt + 1);

                // Exponential backoff with jitter. Jitter matters because every
                // device that lost the same access point would otherwise retry
                // in lockstep the moment it returns.
                var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1))
                            + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 750));
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
        }

        throw new ModelDownloadException(
            $"Download of '{uri}' failed. {last}", last ?? NetworkDiagnosis.Healthy, null);
    }

    private async Task DownloadToFileAsync(
        Uri uri, string destPath, IProgress<double>? progress, CancellationToken ct)
    {
        // RESUME: continue from whatever survived the last attempt.
        var existing = File.Exists(destPath) ? new FileInfo(destPath).Length : 0L;

        var response = await SendRangeAwareAsync(uri, existing, ct).ConfigureAwait(false);
        try
        {
            var resuming = response.Resuming;
            if (!resuming) existing = 0;

            response.Message.EnsureSuccessStatusCode();

            // ContentLength on a ranged reply is the length of the RANGE, not of
            // the file, so the whole-file total is range-length + what we already have.
            var totalBytes = response.Message.Content.Headers.ContentLength is { } len
                ? len + existing
                : -1L;

            await using var contentStream =
                await response.Message.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fileStream = new FileStream(
                destPath,
                resuming ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None,
                bufferSize: 81_920, useAsync: true);
            await PumpAsync(contentStream, fileStream, existing, totalBytes, progress, ct)
                .ConfigureAwait(false);
        }
        finally { response.Message.Dispose(); }
    }

    private readonly record struct RangeAwareResponse(HttpResponseMessage Message, bool Resuming);

    /// <summary>
    /// GETs <paramref name="uri"/>, resuming from <paramref name="existing"/> when
    /// the server will genuinely serve that byte range.
    /// </summary>
    /// <remarks>
    /// Deciding "am I resuming?" from a 206 status alone is wrong, and it
    /// corrupted every large download from one of our two ModelScope endpoints.
    /// The two disagree:
    ///
    ///   resolve/master/…            honours Range, replies 206  (as expected)
    ///   api/v1/…/repo?FilePath=…    honours Range, replies 200 WITH Content-Range
    ///
    /// On the second one the old code saw "not 206", concluded the server had
    /// ignored the range, deleted the partial file — and then wrote the ranged
    /// TAIL it had already asked for into a fresh file as though it were the whole
    /// thing. Retries alternating between the two endpoints appended tails onto
    /// tails: the 450 MB Qwen3 weight file arrived as 775 MB of garbage, failed
    /// its SHA-256, was deleted, and left a directory that every later launch read
    /// as "already downloaded". That is why chat never once worked on the P30.
    ///
    /// So: trust Content-Range, not the status line, and require that the range
    /// actually starts where we asked. Anything else means discard and refetch
    /// from zero — with a SECOND request, because the body in hand is a tail and
    /// writing it as a whole file is precisely the bug being fixed.
    /// </remarks>
    private async Task<RangeAwareResponse> SendRangeAwareAsync(Uri uri, long existing, CancellationToken ct)
    {
        if (existing <= 0)
            return new(await GetAsync(uri, from: null, ct).ConfigureAwait(false), Resuming: false);

        var ranged = await GetAsync(uri, from: existing, ct).ConfigureAwait(false);

        var cr = ranged.Content.Headers.ContentRange;
        var servesOurRange = cr is { HasRange: true, From: { } from } && from == existing;

        if (servesOurRange && ranged.IsSuccessStatusCode)
            return new(ranged, Resuming: true);

        // Not a usable partial. Ask again for the whole file; the caller opens the
        // destination with FileMode.Create, which truncates whatever was there.
        ranged.Dispose();
        return new(await GetAsync(uri, from: null, ct).ConfigureAwait(false), Resuming: false);
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, long? from, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (from is { } f) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(f, null);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// How long a download may deliver nothing before it counts as stalled.
    /// </summary>
    /// <remarks>
    /// A STALL IS NOT AN ERROR AND THAT IS WHY IT WAS FATAL. The socket stays
    /// open, no exception is thrown, and the read below simply never returns — so
    /// the retry machinery around it never runs and the whole thing waits forever.
    /// Caught on the P30 fetching an 879 MB model: it stopped dead at 25%, sat
    /// there for a quarter of an hour, and the screen went on promising "about 10
    /// minutes left". Only a person tapping Stop and starting again recovered it,
    /// and it resumed from 25% and finished — so nothing was wrong except that
    /// nobody noticed.
    /// <para>
    /// Forty-five seconds is deliberately generous. A slow link on a cheap phone
    /// can genuinely go quiet for a while between TCP retransmissions, and the
    /// cost of being wrong is a resumed connection rather than a lost download.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(45);

    private static async Task PumpAsync(
        Stream source, Stream destination, long existing, long totalBytes,
        IProgress<double>? progress, CancellationToken ct)
    {

        var buffer = new byte[81_920];
        long bytesRead = existing;
        long bytesUntilNextReport = ProgressChunkBytes;
        int read;

        while ((read = await ReadOrStallAsync(source, buffer, ct).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
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

    /// <summary>
    /// Reads, but gives up if the connection goes quiet for <see cref="StallTimeout"/>.
    /// </summary>
    /// <remarks>
    /// Turns a silent hang into an ordinary transient failure, which the retry and
    /// resume machinery above already knows how to handle: it reconnects with a
    /// Range header and carries on from the bytes already on disk. Nothing is
    /// re-downloaded.
    /// <para>
    /// The caller's own cancellation is re-thrown untouched — someone pressing
    /// Stop must not be mistaken for a bad network and quietly retried.
    /// </para>
    /// </remarks>
    private static async ValueTask<int> ReadOrStallAsync(
        Stream source, Memory<byte> buffer, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        try
        {
            return await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException(
                $"The download stopped sending data for {StallTimeout.TotalSeconds:F0} seconds.");
        }
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
