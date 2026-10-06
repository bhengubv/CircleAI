using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Core
{
    /// <summary>
    /// Source-agnostic model downloader. Walks a list of <see cref="IModelSource"/>
    /// instances in order, falling through on failure so that one supplier going dark
    /// (sanctions, regional blocks, outages) does not break model bootstrap.
    /// </summary>
    public sealed class ModelDownloader : IModelDownloader, IDisposable
    {
        /// <summary>
        /// Progress report shape emitted during downloads.
        /// Mirrors <see cref="DownloadProgress"/> as a class+event for consumer compatibility.
        /// </summary>
        public sealed class DownloadProgressReport
        {
            public string FileName { get; set; } = "";
            public long BytesReceived { get; set; }
            public long TotalBytes { get; set; }
            public double BytesPerSecond { get; set; }
            public TimeSpan EstimatedTimeRemaining { get; set; }
        }

        public delegate void DownloadProgressHandler(DownloadProgressReport progress);
        public event DownloadProgressHandler? ProgressChanged;

        private const string RegistryResourceName = "CircleAI.Core.registry.json";

        private readonly IReadOnlyList<IModelSource> _sources;
        private readonly bool _ownsSources;
        private readonly Lazy<IReadOnlyDictionary<string, ModelEntry>> _registry;
        private bool _disposed;

        public ModelDownloader(IReadOnlyList<IModelSource> sources, bool ownsSources = false)
        {
            if (sources is null) throw new ArgumentNullException(nameof(sources));
            if (sources.Count == 0)
                throw new ArgumentException("At least one model source is required", nameof(sources));

            _sources = sources;
            _ownsSources = ownsSources;
            _registry = new Lazy<IReadOnlyDictionary<string, ModelEntry>>(LoadEmbeddedRegistry);
        }

        /// <inheritdoc />
        public async Task DownloadModelAsync(string modelId, string localPath, CancellationToken ct = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ModelDownloader));
            if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentNullException(nameof(modelId));
            if (string.IsNullOrWhiteSpace(localPath)) throw new ArgumentNullException(nameof(localPath));

            if (!_registry.Value.TryGetValue(modelId, out var entry))
            {
                throw new KeyNotFoundException(
                    $"Model '{modelId}' is not in the embedded registry. Known models: " +
                    string.Join(", ", _registry.Value.Keys));
            }

            Directory.CreateDirectory(localPath);

            // Bundle entries (no FileName, no PrimaryUrl/FallbackUrl, but a
            // Repo + BundleFiles array) cannot be serviced by this legacy
            // single-file downloader. Steer callers to the right path
            // instead of throwing the generic "no URL configured" message.
            if (entry.IsBundle)
            {
                throw new InvalidOperationException(
                    $"Model '{modelId}' is a multi-file MNN bundle (registry entry has BundleFiles[]). " +
                    "Use CircleAI.Inference.ModelDownloadService.EnsureBundleAsync from " +
                    "MnnInferenceBridgeFactory instead — this legacy single-file downloader " +
                    "cannot fetch a multi-file bundle.");
            }

            var targetFile = Path.Combine(localPath, entry.FileName);

            var candidates = BuildCandidateList(entry);
            if (candidates.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Model '{modelId}' has no PrimaryUrl or FallbackUrl configured.");
            }

            var bridge = new Progress<DownloadProgress>(p =>
                ProgressChanged?.Invoke(new DownloadProgressReport
                {
                    FileName = p.FileName,
                    BytesReceived = p.BytesReceived,
                    TotalBytes = p.TotalBytes,
                    BytesPerSecond = p.BytesPerSecond,
                    EstimatedTimeRemaining = p.EstimatedTimeRemaining,
                }));

            try
            {
                var winner = await DownloadFromCandidatesAsync(candidates, targetFile, bridge, ct).ConfigureAwait(false);
                Console.WriteLine($"[ModelDownloader] '{modelId}' downloaded via {winner}.");
            }
            catch
            {
                CleanupPartialFile(targetFile);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<string> DownloadFromCandidatesAsync(
            IReadOnlyList<string> candidateUrls,
            string localFilePath,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ModelDownloader));
            if (candidateUrls is null) throw new ArgumentNullException(nameof(candidateUrls));
            if (candidateUrls.Count == 0)
                throw new ArgumentException("At least one candidate URL is required", nameof(candidateUrls));
            if (string.IsNullOrWhiteSpace(localFilePath)) throw new ArgumentNullException(nameof(localFilePath));

            var dir = Path.GetDirectoryName(localFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var failures = new List<string>();

            foreach (var url in candidateUrls)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(url)) continue;

                var source = MatchSource(url);
                if (source is null)
                {
                    Console.WriteLine($"[ModelDownloader] Warning: no registered source matched URL '{url}' — skipping. Add a source whose Name matches the hostname, or extend MatchSource.");
                    failures.Add($"(no registered source for '{url}')");
                    continue;
                }

                try
                {
                    Console.WriteLine($"[ModelDownloader] Trying {source.Name}: {url}");
                    await source.DownloadAsync(url, localFilePath, progress, ct).ConfigureAwait(false);
                    Console.WriteLine($"[ModelDownloader] {source.Name} succeeded.");
                    return source.Name;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Add($"{source.Name}: {ex.Message}");
                    Console.WriteLine($"[ModelDownloader] {source.Name} failed: {ex.Message}. Falling through.");
                    // Drop the partial so the next source can start clean.
                    CleanupPartialFile(localFilePath);
                }
            }

            throw new InvalidOperationException(
                "All model sources failed:\n  " + string.Join("\n  ", failures));
        }

        private IModelSource? MatchSource(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            var host = uri.Host;

            // Heuristic match by source Name, then by host substring.
            foreach (var s in _sources)
            {
                if (host.Contains(s.Name, StringComparison.OrdinalIgnoreCase))
                    return s;
            }

            // All registered download URLs are on modelscope.cn (Alibaba).
            // No Western source is registered — if a URL slips through that isn't
            // on a registered source, MatchSource returns null and the downloader skips it.
            if (host.Contains("modelscope", StringComparison.OrdinalIgnoreCase))
                return _sources.FirstOrDefault(s => s.Name.Equals("ModelScope", StringComparison.OrdinalIgnoreCase));

            return null;
        }

        private static IReadOnlyList<string> BuildCandidateList(ModelEntry entry)
        {
            var list = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(entry.PrimaryUrl)) list.Add(entry.PrimaryUrl);
            if (!string.IsNullOrWhiteSpace(entry.FallbackUrl)) list.Add(entry.FallbackUrl);
            return list;
        }

        private static IReadOnlyDictionary<string, ModelEntry> LoadEmbeddedRegistry()
        {
            var assembly = typeof(ModelDownloader).Assembly;
            using var stream = assembly.GetManifestResourceStream(RegistryResourceName);
            if (stream is not null)
            {
                return ParseRegistry(stream);
            }

            // Registry isn't embedded — fall back to a sibling registry.json next to the assembly.
            var assemblyDir = Path.GetDirectoryName(assembly.Location);
            var fallback = assemblyDir is null
                ? null
                : Path.Combine(assemblyDir, "registry.json");

            if (fallback is null || !File.Exists(fallback))
            {
                return new Dictionary<string, ModelEntry>(StringComparer.OrdinalIgnoreCase);
            }

            using var fs = File.OpenRead(fallback);
            return ParseRegistry(fs);
        }

        /// <summary>
        /// Reads registry.json as a flat dictionary of <see cref="ModelEntry"/>. Any top-level
        /// entries that are not JSON objects (e.g. a free-text <c>"Notes"</c> field) are skipped
        /// so registry metadata can coexist with model entries without breaking deserialization.
        /// </summary>
        private static IReadOnlyDictionary<string, ModelEntry> ParseRegistry(Stream stream)
        {
            var registry = new Dictionary<string, ModelEntry>(StringComparer.OrdinalIgnoreCase);
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return registry;
            }

            var options = JsonOpts();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    // Skip metadata fields (Notes, $schema, etc.) — only object values are entries.
                    continue;
                }

                var entry = property.Value.Deserialize<ModelEntry>(options);
                if (entry is not null)
                {
                    registry[property.Name] = entry;
                }
            }
            return registry;
        }

        private static JsonSerializerOptions JsonOpts() => new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private static void CleanupPartialFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Best effort.
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (_ownsSources)
            {
                foreach (var s in _sources)
                {
                    (s as IDisposable)?.Dispose();
                }
            }
            _disposed = true;
        }

        private sealed record ModelEntry
        {
            // Legacy single-file shape (nullable so bundle entries deserialize cleanly).
            [JsonPropertyName("FileName")] public string FileName { get; init; } = "";
            [JsonPropertyName("PrimaryUrl")] public string? PrimaryUrl { get; init; }
            [JsonPropertyName("FallbackUrl")] public string? FallbackUrl { get; init; }
            [JsonPropertyName("Checksum")] public string? Checksum { get; init; }
            [JsonPropertyName("SizeBytes")] public long SizeBytes { get; init; }
            [JsonPropertyName("Version")] public string? Version { get; init; }
            [JsonPropertyName("Architecture")] public string? Architecture { get; init; }
            [JsonPropertyName("QuantizationType")] public string? QuantizationType { get; init; }

            // Bundle shape (Repo + BundleFiles array). When present, signals
            // that the multi-file MNN downloader (CircleAI.Inference) must be
            // used instead of this legacy single-file downloader.
            [JsonPropertyName("Repo")] public string? Repo { get; init; }
            [JsonPropertyName("TotalBytes")] public long TotalBytes { get; init; }
            [JsonPropertyName("BundleFiles")] public IReadOnlyList<BundleFileEntry>? BundleFiles { get; init; }

            public bool IsBundle => BundleFiles is { Count: > 0 };
        }

        private sealed record BundleFileEntry(string Name, string Sha256, long SizeBytes);
    }
}
