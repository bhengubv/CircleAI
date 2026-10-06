using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace CircleAI.Core
{
    public sealed class LocalModelLoader : IModelLoader
    {
        private const string RegistryResourceName = "CircleAI.Core.registry.json";

        // For bundle entries we identify the model by the canonical weight file
        // ("llm.mnn.weight"). It's present in every MNN-LLM model bundle and
        // is the largest file (so a SHA-256 mismatch is the most diagnostic).
        private const string BundleAnchorFileName = "llm.mnn.weight";

        private readonly HttpClient _httpClient = new();
        private readonly string _modelDir;
        private readonly Dictionary<string, ModelInfo> _modelRegistry;
        private bool _disposed;

        public LocalModelLoader(string? modelDirectory = null)
        {
            // See ModelPaths: this used SpecialFolder.ApplicationData, which on
            // Android is a subdirectory of the folder the app actually uses.
            _modelDir = ModelPaths.Resolve(modelDirectory);
            _modelRegistry = LoadEmbeddedRegistry();
        }

        public async Task<string> DownloadModelAsync(string modelName, IProgress<float>? progress = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LocalModelLoader));
            if (!_modelRegistry.TryGetValue(modelName, out var modelInfo))
                throw new ArgumentException($"Model {modelName} not supported");

            // Bundle-shape entries route to MnnInferenceBridgeFactory's
            // ModelDownloadService.EnsureBundleAsync. LocalModelLoader's
            // single-file download path can't service a multi-file bundle.
            if (modelInfo.IsBundle)
            {
                throw new InvalidOperationException(
                    $"Model '{modelName}' is a multi-file bundle (registry entry has BundleFiles[]); " +
                    "use ModelDownloadService.EnsureBundleAsync via MnnInferenceBridgeFactory instead. " +
                    "LocalModelLoader.DownloadModelAsync only handles legacy single-file entries.");
            }

            string localPath = Path.Combine(_modelDir, modelInfo.FileName!);

            if (File.Exists(localPath))
            {
                if (modelInfo.Checksum is null || modelInfo.Checksum.StartsWith("sha256:TBD"))
                {
                    Trace.TraceWarning(
                        "CircleAI: Model '{0}' has no verified checksum (sha256:TBD) — integrity check skipped. Update registry.json before production use.",
                        modelInfo.FileName);
                    return localPath;
                }
                if (VerifyChecksum(localPath, modelInfo.Checksum))
                    return localPath;
                File.Delete(localPath);
            }

            // Try primary (ModelScope) first, fall back to ModelScope CDN.
            var sources = new[] { modelInfo.PrimaryUrl, modelInfo.FallbackUrl };
            Exception? lastError = null;
            foreach (var url in sources)
            {
                if (string.IsNullOrWhiteSpace(url)) continue;
                try
                {
                    await DownloadFileAsync(url, localPath, progress ?? new Progress<float>());
                    if (modelInfo.Checksum is null || modelInfo.Checksum.StartsWith("sha256:TBD"))
                    {
                        Trace.TraceWarning(
                            "CircleAI: Model '{0}' downloaded but has no verified checksum (sha256:TBD) — integrity check skipped. Update registry.json before production use.",
                            modelInfo.FileName);
                        return localPath;
                    }
                    if (VerifyChecksum(localPath, modelInfo.Checksum))
                        return localPath;
                    File.Delete(localPath);
                    lastError = new InvalidDataException("Downloaded model failed checksum verification.");
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            throw lastError ?? new InvalidOperationException("All sources failed.");
        }

        private async Task DownloadFileAsync(string url, string outputPath, IProgress<float> progress)
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var fs = new FileStream(outputPath, FileMode.Create);
            await response.Content.CopyToAsync(fs);
        }

        public string GetModelPath(string modelName)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LocalModelLoader));
            if (!_modelRegistry.TryGetValue(modelName, out var modelInfo))
                throw new FileNotFoundException($"Model {modelName} not found");

            if (modelInfo.IsBundle)
            {
                // Per-model directory layout — same shape ModelDownloadService.EnsureBundleAsync writes to.
                return Path.Combine(_modelDir, modelName, BundleAnchorFileName);
            }

            return Path.Combine(_modelDir, modelInfo.FileName!);
        }

        public bool ModelExists(string modelName)
        {
            try
            {
                if (!_modelRegistry.TryGetValue(modelName, out var modelInfo))
                    return false;

                var path = GetModelPath(modelName);
                if (!File.Exists(path))
                    return false;

                if (modelInfo.IsBundle)
                {
                    // Anchor file's expected SHA from the bundle's anchor entry.
                    var anchor = modelInfo.BundleFiles?
                        .FirstOrDefault(f => string.Equals(f.Name, BundleAnchorFileName, StringComparison.OrdinalIgnoreCase));
                    if (anchor is null) return false;
                    return VerifyChecksum(path, anchor.Sha256);
                }

                return modelInfo.Checksum is not null && VerifyChecksum(path, modelInfo.Checksum);
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> CheckForCriticalUpdateAsync()
        {
            try
            {
                var response = await _httpClient.GetStringAsync(
                    "https://raw.githubusercontent.com/BhenguAI/models/main/versions.txt");
                return response.Contains("[CRITICAL]");
            }
            catch
            {
                return false;
            }
        }

        private Dictionary<string, ModelInfo> LoadEmbeddedRegistry()
        {
            var assembly = typeof(LocalModelLoader).Assembly;
            using var stream = assembly.GetManifestResourceStream(RegistryResourceName)
                ?? throw new FileNotFoundException("Embedded registry not found");

            // Walk top-level properties; skip non-object values so free-text metadata
            // (e.g. a "Notes" field) can coexist with model entries without blowing up
            // the loader.
            var registry = new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return registry;
            }

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            };
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var entry = property.Value.Deserialize<ModelInfo>(options);
                if (entry is not null)
                {
                    registry[property.Name] = entry;
                }
            }
            return registry;
        }

        private bool VerifyChecksum(string filePath, string expectedChecksum)
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hashBytes = sha256.ComputeHash(stream);
            var actualHex = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

            // Accept both "sha256:<hex>" and bare-hex forms — bundle entries store bare hex,
            // legacy entries store "sha256:..." prefix.
            var expected = expectedChecksum?.Trim() ?? string.Empty;
            if (expected.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                expected = expected.Substring("sha256:".Length).Trim();

            return string.Equals(expected, actualHex, StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _httpClient.Dispose();
            _disposed = true;
        }

        /// <summary>
        /// Internal registry-row shape. Supports BOTH the legacy single-file
        /// shape (FileName/PrimaryUrl/FallbackUrl/Checksum) AND the new bundle
        /// shape (Repo + BundleFiles[]). Exactly one shape is populated per
        /// entry; <see cref="IsBundle"/> selects which.
        /// </summary>
        private sealed record ModelInfo
        {
            // Legacy single-file shape (nullable so bundle entries deserialize cleanly).
            public string? FileName { get; init; }
            public string? PrimaryUrl { get; init; }
            public string? FallbackUrl { get; init; }
            public string? Checksum { get; init; }
            public long SizeBytes { get; init; }
            public string Version { get; init; } = "";
            public string Architecture { get; init; } = "";
            public string QuantizationType { get; init; } = "";

            // Bundle shape.
            public string? Repo { get; init; }
            public long TotalBytes { get; init; }
            public IReadOnlyList<BundleFileInfo>? BundleFiles { get; init; }

            public bool IsBundle => BundleFiles is { Count: > 0 };
        }

        private sealed record BundleFileInfo(string Name, string Sha256, long SizeBytes);
    }
}
