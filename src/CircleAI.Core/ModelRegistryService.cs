using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircleAI.Core.Models
{
    public sealed class ModelRegistryService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _registryPath;
        private ModelRegistry? _embeddedRegistry;
        private ModelRegistry? _remoteRegistry;
        private bool _disposed;

        public ModelRegistryService(string? registryUrl = null)
        {
            _httpClient = new HttpClient();
            _registryPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CircleAI",
                "Models",
                "remote_registry.json");

            // Load embedded registry (fallback). Stream may be null if the resource is not embedded.
            var assembly = typeof(ModelRegistryService).Assembly;
            using var stream = assembly.GetManifestResourceStream("CircleAI.Core.Models.embedded_registry.json");
            if (stream is not null)
            {
                _embeddedRegistry = JsonSerializer.Deserialize<ModelRegistry>(stream, JsonOpts);
            }
        }

        public async Task CheckForUpdatesAsync()
        {
            try
            {
                var registryUrl = _embeddedRegistry?.RegistryUrl;
                if (string.IsNullOrWhiteSpace(registryUrl))
                {
                    _remoteRegistry = _embeddedRegistry;
                    return;
                }

                var response = await _httpClient.GetAsync(registryUrl);
                response.EnsureSuccessStatusCode();

                var remoteJson = await response.Content.ReadAsStringAsync();
                if (!VerifySignature(remoteJson))
                    throw new SecurityException("Invalid registry signature");

                _remoteRegistry = JsonSerializer.Deserialize<ModelRegistry>(remoteJson, JsonOpts);
                await File.WriteAllTextAsync(_registryPath, remoteJson);
            }
            catch
            {
                // Fallback to embedded registry
                _remoteRegistry = _embeddedRegistry;
            }
        }

        public ModelEntry? GetLatestModel(string modelName)
        {
            var registry = _remoteRegistry ?? _embeddedRegistry;
            return registry?.Models.FirstOrDefault(m => m.Name.Equals(modelName, StringComparison.OrdinalIgnoreCase));
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private static bool VerifySignature(string json)
        {
            // SECURITY: Signature verification infrastructure is not yet in place.
            // Throwing here causes CheckForUpdatesAsync's catch block to fall back
            // to the embedded registry, ensuring that no unsigned remote payload
            // (including one from a MITM or a compromised server) can ever be
            // deserialised and used as a source of model URLs.
            //
            // TODO: Replace with ECDSA / Ed25519 verification once the signing key
            //       and registry-signing workflow are established.  Until then remote
            //       registry updates are intentionally blocked.
            throw new NotSupportedException(
                "Remote registry signature verification is not yet implemented. " +
                "Remote updates are blocked until cryptographic signing is in place.");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _httpClient.Dispose();
            _disposed = true;
        }
    }

    public record ModelRegistry(
        string RegistryUrl,
        DateTime LastUpdated,
        List<ModelEntry> Models);

    /// <summary>
    /// One entry in the model catalog.
    /// <para>
    /// The legacy single-file shape (<see cref="Url"/> + <see cref="Checksum"/>) is
    /// retained for backward compatibility. The new bundle shape uses
    /// <see cref="Repo"/> + <see cref="BundleFiles"/> so MNN-LLM gets the
    /// complete set of files it needs to load (config.json, llm.mnn,
    /// llm.mnn.weight, tokenizer, etc.) — not just one weight.
    /// </para>
    /// </summary>
    public record ModelEntry(
        string Name,
        string Version,
        string Quantization,
        string? Url = null,
        string? Checksum = null)
    {
        /// <summary>ModelScope repo path (e.g. <c>MNN/Qwen3-0.6B-MNN</c>) for bundle entries.</summary>
        public string? Repo { get; init; }

        /// <summary>Sum of every <see cref="BundleFile.SizeBytes"/> when this is a bundle entry; 0 otherwise.</summary>
        public long TotalBytes { get; init; }

        /// <summary>
        /// Bundle file list — populated for MNN-style multi-file models. Null
        /// for legacy single-file entries that use <see cref="Url"/> +
        /// <see cref="Checksum"/>.
        /// </summary>
        public IReadOnlyList<BundleFile>? BundleFiles { get; init; }

        /// <summary>True when this entry is a bundle (must download every file in <see cref="BundleFiles"/>).</summary>
        public bool IsBundle => BundleFiles is { Count: > 0 };
    }

    /// <summary>
    /// One file inside a model bundle. SHA-256 is the value ModelScope's
    /// file-listing API reports; the recalibration tool verifies a sample
    /// by full download.
    /// </summary>
    public record BundleFile(string Name, string Sha256, long SizeBytes);
}
