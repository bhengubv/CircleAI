using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircleAI.Core.Models
{
    public class ModelRegistryService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _registryPath;
        private ModelRegistry? _embeddedRegistry;
        private ModelRegistry? _remoteRegistry;
        private readonly ModelScopeCatalogClient? _catalogClient;
        private bool _disposed;

        public ModelRegistryService(string? registryUrl = null)
            : this(catalogClient: null, registryUrl: registryUrl) { }

        /// <summary>
        /// Construct with an explicit <see cref="ModelScopeCatalogClient"/>.
        /// When supplied, the catalog client's cache becomes the primary
        /// source for <see cref="AllModels"/> + <see cref="GetLatestModel"/>;
        /// the embedded registry resource degrades to a final offline
        /// fallback only.
        /// </summary>
        /// <param name="catalogClient">
        /// Caller-owned catalog client. When <c>null</c>, the service
        /// loads only from the embedded registry — same behaviour as
        /// every release before the catalog client landed.
        /// </param>
        /// <param name="registryUrl">Legacy signed-registry URL (currently unused — see <c>CheckForUpdatesAsync</c>).</param>
        public ModelRegistryService(ModelScopeCatalogClient? catalogClient, string? registryUrl = null)
        {
            _httpClient = new HttpClient();
            _registryPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CircleAI",
                "Models",
                "remote_registry.json");

            _catalogClient = catalogClient;

            // Load embedded registry (fallback). Stream may be null if the resource is not embedded.
            var assembly = typeof(ModelRegistryService).Assembly;
            using var stream = assembly.GetManifestResourceStream("CircleAI.Core.Models.embedded_registry.json");
            if (stream is not null)
            {
                _embeddedRegistry = JsonSerializer.Deserialize<ModelRegistry>(stream, JsonOpts);
            }

            // If a catalog client was supplied, preload whatever is on disk
            // synchronously so AllModels works without an awaitable. Live
            // refresh happens via PrimeFromCatalogAsync.
            if (_catalogClient is not null)
            {
                try { _remoteRegistry = _catalogClient.LoadFromDisk(); }
                catch { /* fall back to embedded */ }
            }
        }

        /// <summary>
        /// Refresh the cached catalog from the ModelScope API when a
        /// catalog client is configured. Safe to call on a cold cache
        /// (populates it) or a warm cache (refreshes per cadence). On
        /// any failure, keeps the existing cache / embedded registry.
        /// Never throws.
        /// </summary>
        public async Task PrimeFromCatalogAsync(CancellationToken ct = default)
        {
            if (_catalogClient is null) return;
            try
            {
                var registry = await _catalogClient
                    .GetCachedCatalogAsync(acceptStaleOnError: true, ct)
                    .ConfigureAwait(false);
                if (registry is not null) _remoteRegistry = registry;
            }
            catch
            {
                // Honour the directive: "Bad signature / network error →
                // keep using cached catalog, raise observer event."
                // Observer wiring is forthcoming.
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

        /// <summary>
        /// Every entry currently in the active registry (remote when present,
        /// embedded otherwise). Returns an empty list when no registry loaded.
        /// <para>
        /// Consumers that need to discover what's available — selectors,
        /// diagnostics endpoints, recalibrators — should walk this instead
        /// of maintaining their own name lists. New entries surface here as
        /// soon as the registry is refreshed; no SDK release required.
        /// </para>
        /// </summary>
        public virtual IReadOnlyList<ModelEntry> AllModels =>
            (_remoteRegistry ?? _embeddedRegistry)?.Models
                ?? (IReadOnlyList<ModelEntry>)Array.Empty<ModelEntry>();

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

        // ------------------------------------------------------------------
        // Device-fit metadata (consumed by IModelSelector — see
        // CircleAI.Inference.DeviceAwareModelSelector). Defaults below
        // intentionally permissive so older registry entries that haven't
        // been re-stamped still load and rank somewhere in the middle.
        // ------------------------------------------------------------------

        /// <summary>
        /// Minimum device RAM in gigabytes required for this model to load
        /// without thrashing. The selector skips entries whose
        /// <see cref="MinRamGb"/> exceeds the device's available RAM.
        /// Default <c>0</c> means "no minimum stated."
        /// </summary>
        public double MinRamGb { get; init; }

        /// <summary>
        /// Minimum free storage in gigabytes required to keep the bundle on
        /// disk after download. Default <c>0</c> means "no minimum stated."
        /// </summary>
        public double MinStorageGb { get; init; }

        /// <summary>
        /// Capabilities this model satisfies — parsed by
        /// <see cref="CircleAI.Inference.ChatCapability"/>. Valid labels:
        /// <c>Default</c>, <c>Tools</c>, <c>Vision</c>, <c>LongContext</c>,
        /// <c>Reasoning</c>. An empty list is treated as
        /// <c>Default</c> only.
        /// </summary>
        public IReadOnlyList<string>? Capabilities { get; init; }

        /// <summary>
        /// Higher = better answer quality at full precision. Used as the
        /// primary ranking key when multiple entries satisfy the device +
        /// capability gates. Default <c>0</c>.
        /// </summary>
        public int QualityRank { get; init; }
    }

    /// <summary>
    /// One file inside a model bundle. SHA-256 is the value ModelScope's
    /// file-listing API reports; the recalibration tool verifies a sample
    /// by full download.
    /// </summary>
    public record BundleFile(string Name, string Sha256, long SizeBytes);
}
