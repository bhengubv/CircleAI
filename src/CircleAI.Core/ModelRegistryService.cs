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
        private readonly byte[]? _signingPublicKey;
        private bool _disposed;

        /// <summary>
        /// (3.3.0) Override to provide a registry-signing public key in
        /// SubjectPublicKeyInfo DER form (ECDSA P-256). Hosts that want
        /// to enable remote registry updates supply the corresponding
        /// public-key bytes for the keypair that signs their
        /// <c>registry.json.sig</c> sidecar.
        /// </summary>
        public ModelRegistryService(byte[]? signingPublicKeyDer, string? registryUrl = null)
            : this(catalogClient: null, registryUrl: registryUrl)
        {
            _signingPublicKey = signingPublicKeyDer;
        }

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

                // Fetch the detached signature from the sidecar URL.
                var sigUrl = registryUrl + ".sig";
                using var sigResp = await _httpClient.GetAsync(sigUrl);
                if (!sigResp.IsSuccessStatusCode)
                    throw new SecurityException("Missing registry signature sidecar");

                var sigBase64 = (await sigResp.Content.ReadAsStringAsync()).Trim();
                byte[] signature;
                try { signature = Convert.FromBase64String(sigBase64); }
                catch { throw new SecurityException("Malformed signature"); }

                if (!VerifySignature(remoteJson, signature))
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

        /// <summary>
        /// Compare every installed model under <paramref name="storageDirectory"/>
        /// against the active registry and surface anything that's drifted —
        /// Version string mismatch, file SHA mismatch, or both. Hosts call
        /// this on a cadence of their choosing (boot, daily timer, manual
        /// "check for updates" button) and react to the returned list.
        /// </summary>
        /// <param name="storageDirectory">
        /// Root directory where bundles are installed (one subdir per modelId,
        /// containing the bundle files + <c>installed.json</c>). Same path
        /// <c>ModelDownloadService</c> writes to.
        /// </param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>
        /// One <see cref="UpgradeInfo"/> per detected upgrade. Empty list
        /// when everything installed is current OR no models are installed.
        /// Never throws on individual model failures — best-effort.
        /// </returns>
        public virtual async Task<IReadOnlyList<UpgradeInfo>> CheckForUpgradesAsync(
            string            storageDirectory,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
            ct.ThrowIfCancellationRequested();

            var upgrades = new List<UpgradeInfo>();
            var now      = DateTimeOffset.UtcNow;

            foreach (var entry in AllModels)
            {
                ct.ThrowIfCancellationRequested();
                var modelDir = Path.Combine(storageDirectory, entry.Name);
                if (!Directory.Exists(modelDir)) continue; // not installed — not an upgrade

                var manifestPath = Path.Combine(modelDir, "installed.json");
                InstalledManifest? manifest = null;
                if (File.Exists(manifestPath))
                {
                    try
                    {
                        using var stream = File.OpenRead(manifestPath);
                        manifest = JsonSerializer.Deserialize<InstalledManifest>(stream, JsonOpts);
                    }
                    catch
                    {
                        // Corrupt manifest — treat as missing.
                        manifest = null;
                    }
                }

                // No manifest but directory exists → pre-feature install. Surface
                // as Unknown so hosts can decide to re-download.
                if (manifest is null)
                {
                    upgrades.Add(new UpgradeInfo(
                        ModelId:                entry.Name,
                        InstalledVersion:       null,
                        AvailableVersion:       entry.Version,
                        Reason:                 UpgradeReason.Unknown,
                        EstimatedDownloadBytes: entry.TotalBytes,
                        DetectedAt:             now));
                    continue;
                }

                var versionChanged = !string.Equals(manifest.Version, entry.Version, StringComparison.Ordinal);
                var (shaChanged, driftBytes) = CompareBundleSha(manifest.Files, entry.BundleFiles);

                if (!versionChanged && !shaChanged) continue; // up to date

                var reason = (versionChanged, shaChanged) switch
                {
                    (true,  true)  => UpgradeReason.Both,
                    (true,  false) => UpgradeReason.VersionChanged,
                    (false, true)  => UpgradeReason.SHAChanged,
                    _              => UpgradeReason.Unknown,
                };

                upgrades.Add(new UpgradeInfo(
                    ModelId:                entry.Name,
                    InstalledVersion:       manifest.Version,
                    AvailableVersion:       entry.Version,
                    Reason:                 reason,
                    EstimatedDownloadBytes: driftBytes,
                    DetectedAt:             now));
            }

            await Task.CompletedTask.ConfigureAwait(false);
            return upgrades;
        }

        // Compare per-file SHA. Returns (any drift, sum-of-bytes for files
        // that would re-download).
        private static (bool DriftDetected, long Bytes) CompareBundleSha(
            IReadOnlyList<BundleFile>? installed,
            IReadOnlyList<BundleFile>? available)
        {
            if (available is null || available.Count == 0) return (false, 0);
            var installedByName = (installed ?? Array.Empty<BundleFile>())
                .ToDictionary(f => f.Name, StringComparer.Ordinal);

            long bytes = 0;
            bool drift = false;
            foreach (var av in available)
            {
                if (!installedByName.TryGetValue(av.Name, out var inst) ||
                    !string.Equals(inst.Sha256, av.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    drift = true;
                    bytes += av.SizeBytes;
                }
            }
            return (drift, bytes);
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>
        /// (3.3.0) Verify <paramref name="json"/> against
        /// <paramref name="signature"/> using ECDSA P-256 / SHA-256
        /// against the host-supplied public key. Returns <c>false</c>
        /// when no key is configured — same fail-closed behaviour as
        /// "no signature trusted by default", so the caller falls back
        /// to the embedded registry.
        /// </summary>
        private bool VerifySignature(string json, byte[] signature)
        {
            if (_signingPublicKey is null || _signingPublicKey.Length == 0) return false;
            if (signature is null || signature.Length == 0) return false;

            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(_signingPublicKey, out _);
                var data = System.Text.Encoding.UTF8.GetBytes(json);

                // Accept either IEEE P1363 (r||s concat) or DER. Try IEEE first
                // because that's the OpenSSL default for ecdsa --raw output;
                // fall back to DER (the .NET default).
                if (ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                {
                    return true;
                }
                return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            }
            catch (CryptographicException)
            {
                return false;
            }
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

        /// <summary>
        /// (RT-08) Smaller-family entry the runtime should hot-swap to when
        /// memory pressure forces a brownout. Empty / unknown name = chain
        /// terminator. Walked transitively by
        /// <c>IModelSelector.ChainFor(modelId)</c>. Default <c>null</c>.
        /// </summary>
        public string? FallbackModelId { get; init; }

        /// <summary>
        /// (RT-04) Best-effort runtime-RSS estimate when loaded with
        /// default KV mode. Drives the brownout trigger and prefetch sizing.
        /// Default <c>0</c> = unknown; callers fall back to
        /// <see cref="TotalBytes"/>.
        /// </summary>
        public long MemoryHintBytes { get; init; }

        /// <summary>
        /// (3.1.0) Minimum device VRAM in gigabytes required for this model
        /// to load on the GPU/NPU. Used by the BestFit selector to gate
        /// VRAM-bound models (notably <c>ChatCapability.Video</c> entries
        /// like CogVideoX-2B which need ≥ 6 GB even quantised). <c>null</c>
        /// = no VRAM requirement stated; the model is presumed to run on
        /// the CPU path. Text-only models leave this null.
        /// </summary>
        public double? MinVramGb { get; init; }
    }

    /// <summary>
    /// One file inside a model bundle. SHA-256 is the value ModelScope's
    /// file-listing API reports; the recalibration tool verifies a sample
    /// by full download.
    /// </summary>
    public record BundleFile(string Name, string Sha256, long SizeBytes);
}
