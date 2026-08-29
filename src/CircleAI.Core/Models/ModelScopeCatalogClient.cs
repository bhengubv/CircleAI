// ModelScopeCatalogClient.cs
//
// The real source of truth for "what models exist." Queries the
// ModelScope HTTP API, filters for MNN-LLM bundles, parses metadata,
// caches the result to disk, verifies its signature on apply.
//
// Replaces the embedded registry.json as the runtime catalog source.
// The embedded JSON is retained as a final offline fallback only —
// see ModelRegistryService for the resolution order
// (remote ▶ disk cache ▶ embedded ▶ empty).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Core.Models;

/// <summary>
/// How often <see cref="ModelScopeCatalogClient"/> should refresh from
/// the live API. The catalog client checks the cadence on every read
/// and short-circuits when the on-disk cache is still fresh.
/// </summary>
public enum CatalogRefreshCadence
{
    /// <summary>Refresh on every SDK process startup (default).</summary>
    OnStartup = 0,

    /// <summary>Refresh once per UTC day. Cheapest. Recommended for desktop / server.</summary>
    Daily     = 1,

    /// <summary>Refresh only when the caller invokes <see cref="ModelScopeCatalogClient.RefreshAsync"/> explicitly.</summary>
    Manual    = 2,

    /// <summary>Never refresh — use whatever is on disk forever. For air-gapped hosts.</summary>
    Never     = 3,
}

/// <summary>
/// Options for <see cref="ModelScopeCatalogClient"/>.
/// </summary>
public sealed class ModelScopeCatalogOptions
{
    /// <summary>Base URL of the ModelScope HTTP API. Default <c>https://www.modelscope.cn</c>.</summary>
    public Uri BaseUri { get; init; } = new("https://www.modelscope.cn");

    /// <summary>
    /// Directory to cache the catalog JSON in. The client appends
    /// <c>catalog.json</c> + <c>catalog.sig</c>. Defaults to
    /// <c>{AppData}/CircleAI/catalog/</c> on Windows or the equivalent
    /// XDG path on Linux / macOS.
    /// </summary>
    public string CacheDirectory { get; init; } = DefaultCachePath();

    /// <summary>How often to refresh from the live API. Default <see cref="CatalogRefreshCadence.OnStartup"/>.</summary>
    public CatalogRefreshCadence Cadence { get; init; } = CatalogRefreshCadence.OnStartup;

    /// <summary>
    /// Filter applied to the ModelScope query. Default <c>"MNN"</c> —
    /// matches Alibaba MNN's own model namespace. Override to narrow
    /// further (e.g. <c>"MNN/Qwen3"</c>) or broaden.
    /// </summary>
    public string Filter { get; init; } = "MNN";

    /// <summary>How many models to request per page. Default 100.</summary>
    public int PageSize { get; init; } = 100;

    /// <summary>User-Agent header. ModelScope CDN rejects requests without one.</summary>
    public string UserAgent { get; init; } =
        "Mozilla/5.0 (Circle AI SDK) CircleAI/1.3";

    private static string DefaultCachePath()
    {
        // Beside the models, for the reason in ModelPaths: on Android the old
        // SpecialFolder.ApplicationData is a subdirectory of the app's own
        // storage, so this cache landed somewhere nothing else looked.
        var root = ModelPaths.Root;
        return Path.Combine(string.IsNullOrEmpty(root) ? "." : root, "CircleAI", "catalog");
    }
}

/// <summary>
/// Discovers MNN-compatible models on ModelScope, caches the catalog
/// to disk, and surfaces it to <see cref="ModelRegistryService"/> +
/// <c>CircleAI.Inference.IModelSelector</c>.
/// </summary>
/// <remarks>
/// Network behaviour: the client treats ModelScope as the authoritative
/// source, but every network call is wrapped in try/catch so a failed
/// refresh never breaks an already-cached SDK. The cache wins by
/// design — the principle is "best-effort live, always-on cached."
/// </remarks>
public sealed class ModelScopeCatalogClient : IDisposable
{
    private readonly ModelScopeCatalogOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ICatalogSignatureVerifier _verifier;
    private readonly IDeviceContext? _deviceContext;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        WriteIndented               = true,
    };

    /// <summary>
    /// Construct with default options, an owned HttpClient, and the
    /// fail-closed <see cref="NullCatalogSignatureVerifier"/>.
    /// </summary>
    public ModelScopeCatalogClient() : this(new ModelScopeCatalogOptions(), null, null, null) { }

    /// <summary>
    /// Construct with explicit options + optional caller-supplied
    /// HttpClient + verifier + device context. The client owns and
    /// disposes an HttpClient only when one isn't supplied. The optional
    /// <paramref name="deviceContext"/> lets the client short-circuit
    /// refresh attempts when the host reports no network connectivity —
    /// the cache is returned untouched and no HTTPS roundtrip is wasted.
    /// </summary>
    public ModelScopeCatalogClient(
        ModelScopeCatalogOptions options,
        HttpClient? httpClient = null,
        ICatalogSignatureVerifier? verifier = null,
        IDeviceContext? deviceContext = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient();
        if (_ownsHttp)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(_options.UserAgent);
        _verifier      = verifier ?? NullCatalogSignatureVerifier.Instance;
        _deviceContext = deviceContext;

        Directory.CreateDirectory(_options.CacheDirectory);
    }

    /// <summary>Absolute path to the cached catalog JSON.</summary>
    public string CacheFilePath => Path.Combine(_options.CacheDirectory, "catalog.json");

    /// <summary>Absolute path to the detached signature file (base64).</summary>
    public string SignatureFilePath => Path.Combine(_options.CacheDirectory, "catalog.sig");

    /// <summary>
    /// Return the catalog from disk, refreshing first if the cadence
    /// says it's due. Always returns — falls back to whatever JSON is
    /// on disk when the network call fails.
    /// </summary>
    public async Task<ModelRegistry?> GetCachedCatalogAsync(
        bool acceptStaleOnError = true,
        CancellationToken ct = default)
    {
        if (await IsRefreshDueAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await RefreshAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                if (!acceptStaleOnError) throw;
                // Fall through to disk read below.
            }
        }
        return LoadFromDisk();
    }

    /// <summary>
    /// Returns <c>true</c> when the cadence + last-modified time
    /// indicate a refresh is due. Never throws; returns <c>false</c>
    /// on disk read errors (treat as "not due").
    /// </summary>
    public Task<bool> IsRefreshDueAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_options.Cadence == CatalogRefreshCadence.Never)  return Task.FromResult(false);
        if (_options.Cadence == CatalogRefreshCadence.Manual) return Task.FromResult(false);

        // Connectivity gate: when the host reports "none" we skip the
        // HTTPS roundtrip entirely. "online" / unknown / unset all pass.
        var network = _deviceContext?.NetworkType;
        if (string.Equals(network, "none", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(false);

        if (!File.Exists(CacheFilePath)) return Task.FromResult(true);

        if (_options.Cadence == CatalogRefreshCadence.OnStartup)
        {
            // True every process-startup — cheap, callers cache the
            // first result for the lifetime of the SDK instance.
            return Task.FromResult(_refreshedThisProcess == false);
        }

        // Daily — refresh if last write was on a different UTC date.
        try
        {
            var lastUtc = File.GetLastWriteTimeUtc(CacheFilePath);
            return Task.FromResult(lastUtc.Date < DateTime.UtcNow.Date);
        }
        catch { return Task.FromResult(false); }
    }

    private bool _refreshedThisProcess;

    /// <summary>
    /// Pull the live catalog from ModelScope, verify its signature
    /// (when a verifier is configured), and write to disk. On signature
    /// failure or network error, leaves the existing cache untouched
    /// and throws — callers should use <see cref="GetCachedCatalogAsync"/>
    /// to keep cache-precedence behaviour.
    /// </summary>
    public async Task<ModelRegistry> RefreshAsync(CancellationToken ct = default)
    {
        var registry = await FetchLiveAsync(ct).ConfigureAwait(false);

        // Serialise to canonical JSON for signing.
        var json = JsonSerializer.SerializeToUtf8Bytes(registry, JsonOpts);

        // Verify any incoming signature. With NullCatalogSignatureVerifier
        // we get NotConfigured — the catalog is still cached (it's our
        // own freshly-fetched data) but observers learn no signing is
        // active. Real verifiers gate cache writes.
        var sigPath = SignatureFilePath;
        var existingSig = File.Exists(sigPath) ? File.ReadAllText(sigPath) : null;
        var sigResult = _verifier.Verify(json, existingSig);

        if (sigResult == CatalogSignatureResult.Invalid)
        {
            throw new InvalidOperationException(
                "Catalog signature did not verify against the configured public key. " +
                "Keeping previous cache; not applying fetched payload.");
        }

        await File.WriteAllBytesAsync(CacheFilePath, json, ct).ConfigureAwait(false);
        _refreshedThisProcess = true;
        return registry;
    }

    /// <summary>
    /// Load whatever catalog is currently on disk. Returns <c>null</c>
    /// when no cache file exists OR when the cache is malformed.
    /// </summary>
    public ModelRegistry? LoadFromDisk()
    {
        if (!File.Exists(CacheFilePath)) return null;
        try
        {
            using var stream = File.OpenRead(CacheFilePath);
            return JsonSerializer.Deserialize<ModelRegistry>(stream, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Live fetch
    // ─────────────────────────────────────────────────────────────

    private async Task<ModelRegistry> FetchLiveAsync(CancellationToken ct)
    {
        var listingUrl =
            $"{_options.BaseUri.TrimEndSlash()}/api/v1/models?Name={Uri.EscapeDataString(_options.Filter)}&PageSize={_options.PageSize}";

        using var listingReq = new HttpRequestMessage(HttpMethod.Get, listingUrl);
        using var listingResp = await _http.SendAsync(listingReq, ct).ConfigureAwait(false);
        listingResp.EnsureSuccessStatusCode();
        var listingJson = await listingResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // ModelScope's catalog shape:
        //   { "Code": 200, "Data": { "Models": [ { "Path": "MNN", "Name": "Qwen3-4B-MNN", ... }, ... ] } }
        // Names are returned without the "MNN/" prefix; combine Path + Name to form the repo.
        var modelList = ParseModelListing(listingJson);

        var entries = new List<ModelEntry>();
        foreach (var (repo, name) in modelList)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var entry = await BuildEntryAsync(repo, name, ct).ConfigureAwait(false);
                if (entry is not null) entries.Add(entry);
            }
            catch
            {
                // Don't let one malformed entry kill the whole refresh.
                // Observers can be wired in later to count skipped repos.
            }
        }

        return new ModelRegistry(
            RegistryUrl: $"{_options.BaseUri.TrimEndSlash()}/api/v1/models?Name={_options.Filter}",
            LastUpdated: DateTime.UtcNow,
            Models:      entries);
    }

    private static IEnumerable<(string Repo, string Name)> ParseModelListing(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Data", out var data)) yield break;
        if (!data.TryGetProperty("Models", out var models) || models.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var m in models.EnumerateArray())
        {
            var name = m.TryGetProperty("Name", out var n) ? n.GetString() : null;
            var path = m.TryGetProperty("Path", out var p) ? p.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var repo = string.IsNullOrWhiteSpace(path) ? name : $"{path}/{name}";
            yield return (repo!, name!);
        }
    }

    private async Task<ModelEntry?> BuildEntryAsync(string repo, string name, CancellationToken ct)
    {
        var filesUrl =
            $"{_options.BaseUri.TrimEndSlash()}/api/v1/models/{repo}/repo/files?Revision=master";

        using var req = new HttpRequestMessage(HttpMethod.Get, filesUrl);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Data", out var data)) return null;
        if (!data.TryGetProperty("Files", out var files) || files.ValueKind != JsonValueKind.Array)
            return null;

        var bundle = new List<BundleFile>();
        long total = 0;
        foreach (var f in files.EnumerateArray())
        {
            var type = f.TryGetProperty("Type", out var t) ? t.GetString() : null;
            if (type is not null && !type.Equals("blob", StringComparison.OrdinalIgnoreCase))
                continue;
            var fname = f.TryGetProperty("Name", out var fn) ? fn.GetString() : null;
            var sha   = f.TryGetProperty("Sha256", out var sh) ? sh.GetString() : null;
            var size  = f.TryGetProperty("Size",  out var sz) && sz.TryGetInt64(out var s) ? s : 0L;
            if (string.IsNullOrWhiteSpace(fname) || string.IsNullOrWhiteSpace(sha)) continue;
            bundle.Add(new BundleFile(fname!, sha!.ToLowerInvariant(), size));
            total += size;
        }

        if (bundle.Count == 0) return null;

        var modality = InferModality(name, repo);

        // A VLM is only usable if vision selection can SEE it. Tag the vision
        // capability so a caller asking "can this build understand an image"
        // finds it; chat entries keep the default (null → Default only).
        var capabilities = modality == ModelModality.Vision
            ? new[] { "Default", "Vision" }
            : null;

        // Device-fit derivation. The listing API reports file sizes but no RAM
        // guidance, so estimate conservatively from the on-disk footprint — the
        // same shape tools/recalibrate-registry-sha stamps for the embedded
        // entries (runtime RSS ≈ 1.4× bundle bytes). WITHOUT this a discovered
        // entry carries MinRamGb = 0, and the selector would treat a 4B model as
        // fitting a 2 GB phone — the exact OOM the embedded metadata prevents.
        var memoryHint   = (long)(total * 1.4);
        var minRamGb     = memoryHint / 1_000_000_000.0;
        var minStorageGb = total      / 1_000_000_000.0;

        return new ModelEntry(
            Name:         name,
            Version:      "",   // ModelScope's listing API doesn't expose model version cleanly
            Quantization: "MNN")
        {
            Repo            = repo,
            TotalBytes      = total,
            BundleFiles     = bundle,
            Modality        = modality,
            Capabilities    = capabilities,
            MemoryHintBytes = memoryHint,
            MinRamGb        = minRamGb,
            MinStorageGb    = minStorageGb,
        };
    }

    /// <summary>
    /// Infer a discovered repo's <see cref="ModelModality"/> from its name.
    /// The ModelScope listing API does not report modality, so a
    /// vision-language bundle (Qwen2-VL, Qwen2.5-VL, MiniCPM-V, SmolVLM,
    /// InternVL, LLaVA) would otherwise be catalogued as the default
    /// <see cref="ModelModality.Chat"/> and be invisible to vision selection —
    /// the one thing that makes an on-device VLM usable. Anything not
    /// recognised as a VLM stays <see cref="ModelModality.Chat"/>, exactly as
    /// before this method existed.
    /// </summary>
    /// <remarks>
    /// Deliberately public + static so it is unit-testable offline without an
    /// HTTP round-trip — the VLM-naming table is the load-bearing part of
    /// cataloguing a vision model and must be pinned by a test.
    /// </remarks>
    public static ModelModality InferModality(string name, string? repo = null)
    {
        var hay = $"{name} {repo}";

        // "VL" as a delimited token is MNN's own VLM marker
        // (Qwen2-VL-2B-Instruct-MNN, Qwen2.5-VL-3B-Instruct-MNN). The named
        // families below cover the VLMs whose marker is NOT a standalone "VL"
        // token (InternVL, SmolVLM). "Vision" catches anything self-labelled.
        if (ContainsToken(hay, "VL")
            || hay.Contains("MiniCPM-V", StringComparison.OrdinalIgnoreCase)
            || hay.Contains("SmolVLM",   StringComparison.OrdinalIgnoreCase)
            || hay.Contains("InternVL",  StringComparison.OrdinalIgnoreCase)
            || hay.Contains("LLaVA",     StringComparison.OrdinalIgnoreCase)
            || hay.Contains("Vision",    StringComparison.OrdinalIgnoreCase))
        {
            return ModelModality.Vision;
        }

        return ModelModality.Chat;
    }

    // True when <paramref name="needle"/> appears in <paramref name="haystack"/>
    // bounded by non-alphanumeric characters, so "VL" matches "Qwen2-VL-2B" but
    // never the "VL" inside "InternVL" or "SmolVLM" (those get explicit checks).
    private static bool ContainsToken(string haystack, string needle)
    {
        int i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var beforeOk = i == 0 || !char.IsLetterOrDigit(haystack[i - 1]);
            var afterIx  = i + needle.Length;
            var afterOk  = afterIx >= haystack.Length || !char.IsLetterOrDigit(haystack[afterIx]);
            if (beforeOk && afterOk) return true;
            i = afterIx;
        }
        return false;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

file static class UriExtensions
{
    public static string TrimEndSlash(this Uri u) =>
        u.ToString().TrimEnd('/');
}
