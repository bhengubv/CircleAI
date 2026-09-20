// HuggingFaceCatalogClient.cs
//
// A SECOND internet discovery source, so the model catalogue is not hostage to a
// single host — sanction-proof-is-the-rule: if ModelScope is unreachable (or
// blocked), HuggingFace can still supply the ladder, and vice versa. The MNN
// bundles this device runs are published at huggingface.co/taobao-mnn/*, the same
// files the embedded registry already pins.
//
// ERROR HANDLING IS DELIBERATELY LOUD. A discovery client that fails silently is
// how "the live catalogue could never load" sat unnoticed for months (see
// ModelCatalogue's own history). Every failure here is logged WITH CONTEXT — the
// URL, the repo, the reason a model was skipped — and each run ends with a
// one-line summary (discovered / skipped / why). Failures never throw out of
// RefreshAsync; the app keeps whatever catalogue it already had.
//
// INTEGRITY. HuggingFace exposes a content SHA-256 only for LFS files (lfs.oid);
// small non-LFS files (config, tokenizer) carry only a git SHA-1, so this client
// FETCHES and hashes them itself — the downloader verifies SHA-256, so a row that
// cannot be honestly pinned is dropped rather than shipped unverifiable.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Core.Models;

/// <summary>Options for <see cref="HuggingFaceCatalogClient"/>.</summary>
public sealed class HuggingFaceCatalogOptions
{
    /// <summary>Base URL of the HuggingFace Hub. Default <c>https://huggingface.co</c>.</summary>
    public Uri BaseUri { get; init; } = new("https://huggingface.co");

    /// <summary>The author/org to discover models from. Default <c>taobao-mnn</c> — the MNN bundles this device runs.</summary>
    public string Author { get; init; } = "taobao-mnn";

    /// <summary>How many models to catalogue. Each costs a tree fetch (+ small-file hashes), so kept modest for a phone.</summary>
    public int MaxModels { get; init; } = 12;

    /// <summary>Licence policy — reuses the fully-free-open-source allow/deny lists.</summary>
    public ModelScopeCatalogOptions Licence { get; init; } = new();

    /// <summary>A non-LFS file larger than this is not fetched-and-hashed; a model with one is skipped (cannot be pinned honestly).</summary>
    public long MaxInlineHashBytes { get; init; } = 8_000_000;

    /// <summary>
    /// Shortest gap between refresh attempts, so a host may call
    /// <see cref="HuggingFaceCatalogClient.RefreshAsync"/> on every launch without
    /// hammering the radio — the set of published models does not change fast.
    /// </summary>
    public TimeSpan MinRefreshInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>User-Agent header. HuggingFace throttles some agent-less requests.</summary>
    public string UserAgent { get; init; } = "Mozilla/5.0 (Circle AI SDK) CircleAI/1.3";
}

/// <summary>
/// Discovers MNN-compatible models on HuggingFace and turns them into a
/// <see cref="ModelRegistry"/>. <see cref="RefreshAsync"/> announces the result as
/// an internet source (so it reaches the runtime catalogue AND propagates to mesh
/// peers). Best-effort and loud: it never throws, and it says why when it skips.
/// </summary>
public sealed class HuggingFaceCatalogClient : IDisposable
{
    private readonly HuggingFaceCatalogOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ILogger<HuggingFaceCatalogClient> _logger;
    private readonly object _gate = new();
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;

    public HuggingFaceCatalogClient(
        HuggingFaceCatalogOptions? options = null,
        HttpClient? httpClient = null,
        ILogger<HuggingFaceCatalogClient>? logger = null)
    {
        _options = options ?? new HuggingFaceCatalogOptions();
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient();
        if (_ownsHttp) _http.DefaultRequestHeaders.UserAgent.ParseAdd(_options.UserAgent);
        _logger = logger ?? NullLogger<HuggingFaceCatalogClient>.Instance;
    }

    /// <summary>
    /// Fetch from HuggingFace and, if anything came back, announce it as an
    /// internet source (<c>ModelCatalogue.Announce(reg, "internet")</c>). Never
    /// throws; logs the outcome either way. Returns <c>true</c> when a catalogue
    /// was announced.
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow - _lastAttempt < _options.MinRefreshInterval)
            {
                _logger.LogDebug("HuggingFace discovery: skipped — last attempt was under {Interval} ago.", _options.MinRefreshInterval);
                return false;
            }
            _lastAttempt = DateTimeOffset.UtcNow;
        }

        try
        {
            var registry = await FetchAsync(ct).ConfigureAwait(false);
            if (registry is { Models.Count: > 0 })
            {
                ModelCatalogue.Announce(registry, "internet");
                return true;
            }

            _logger.LogInformation(
                "HuggingFace discovery: nothing to catalogue from author '{Author}'; keeping the existing catalogue.",
                _options.Author);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Offline, rate-limited, an API shape change — all mean "keep what we
            // have", but never silently: a person debugging "why no new models"
            // needs this line.
            _logger.LogWarning(ex, "HuggingFace discovery failed; keeping the existing catalogue.");
            return false;
        }
    }

    /// <summary>
    /// Query HuggingFace and build a <see cref="ModelRegistry"/>. Per-repo failures
    /// are logged and skipped; a network failure of the listing itself propagates
    /// to the caller (so <see cref="RefreshAsync"/> logs it once).
    /// </summary>
    public async Task<ModelRegistry?> FetchAsync(CancellationToken ct = default)
    {
        var ids = await ListAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("HuggingFace discovery: {Count} candidate model(s) under '{Author}'.", ids.Count, _options.Author);

        var entries = new List<ModelEntry>();
        int skippedLicence = 0, skippedNoFiles = 0, skippedError = 0;

        foreach (var id in ids.Take(Math.Max(1, _options.MaxModels)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (entry, skip) = await BuildEntryAsync(id, ct).ConfigureAwait(false);
                if (entry is not null) { entries.Add(entry); continue; }

                switch (skip)
                {
                    case SkipReason.Licence: skippedLicence++; break;
                    case SkipReason.NoPinnableFiles: skippedNoFiles++; break;
                    default: skippedError++; break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                skippedError++;
                _logger.LogWarning(ex, "HuggingFace discovery: skipped '{Repo}' — unexpected error.", id);
            }
        }

        _logger.LogInformation(
            "HuggingFace discovery: catalogued {Kept}, skipped {SkipLicence} (licence) + {SkipFiles} (no pinnable files) + {SkipErr} (errors).",
            entries.Count, skippedLicence, skippedNoFiles, skippedError);

        return new ModelRegistry(
            RegistryUrl: $"{Trim(_options.BaseUri)}/api/models?author={_options.Author}",
            LastUpdated: DateTime.UtcNow,
            Models: entries);
    }

    private enum SkipReason { None, Licence, NoPinnableFiles, Error }

    private async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct)
    {
        var url = $"{Trim(_options.BaseUri)}/api/models?author={Uri.EscapeDataString(_options.Author)}&limit={_options.MaxModels}";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("HuggingFace listing {Url} returned {Status}.", url, (int)resp.StatusCode);
            resp.EnsureSuccessStatusCode();   // surfaces to RefreshAsync's single warning
        }

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var ids = new List<string>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("HuggingFace listing had an unexpected shape (not an array); no models parsed.");
            return ids;
        }

        foreach (var m in doc.RootElement.EnumerateArray())
            if (m.TryGetProperty("id", out var idEl) && idEl.GetString() is { Length: > 0 } id)
                ids.Add(id);
        return ids;
    }

    private async Task<(ModelEntry? Entry, SkipReason Skip)> BuildEntryAsync(string id, CancellationToken ct)
    {
        var licence = await LicenceAsync(id, ct).ConfigureAwait(false);
        if (!ModelScopeCatalogClient.LicenceAllowed(licence, _options.Licence))
        {
            _logger.LogDebug("HuggingFace discovery: skipped '{Repo}' — licence '{Licence}' not free.", id, licence ?? "(none stated)");
            return (null, SkipReason.Licence);
        }

        var treeUrl = $"{Trim(_options.BaseUri)}/api/models/{id}/tree/main";
        using var resp = await _http.GetAsync(treeUrl, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("HuggingFace discovery: skipped '{Repo}' — file tree returned {Status}.", id, (int)resp.StatusCode);
            return (null, SkipReason.Error);
        }

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("HuggingFace discovery: skipped '{Repo}' — file tree had an unexpected shape.", id);
            return (null, SkipReason.Error);
        }

        var bundle = new List<BundleFile>();
        long total = 0;
        foreach (var f in doc.RootElement.EnumerateArray())
        {
            if (f.TryGetProperty("type", out var t) && !string.Equals(t.GetString(), "file", StringComparison.OrdinalIgnoreCase))
                continue;
            var path = f.TryGetProperty("path", out var p) ? p.GetString() : null;
            if (string.IsNullOrWhiteSpace(path)) continue;
            long size = f.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var s) ? s : 0L;

            string? sha256;
            if (f.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object
                && lfs.TryGetProperty("oid", out var lo) && lo.GetString() is { Length: 64 } lfsSha)
            {
                sha256 = lfsSha.ToLowerInvariant();   // LFS oid IS the content SHA-256
                if (lfs.TryGetProperty("size", out var ls) && ls.TryGetInt64(out var lsz)) size = lsz;
            }
            else
            {
                // Non-LFS: HF gives only a git SHA-1, so we hash the bytes ourselves.
                if (size > _options.MaxInlineHashBytes)
                {
                    _logger.LogWarning(
                        "HuggingFace discovery: skipped '{Repo}' — non-LFS file '{File}' is {Size} bytes, too large to hash for a pin.",
                        id, path, size);
                    return (null, SkipReason.Error);
                }
                sha256 = await HashFileAsync(id, path!, ct).ConfigureAwait(false);
                if (sha256 is null)
                {
                    _logger.LogWarning("HuggingFace discovery: skipped '{Repo}' — could not hash '{File}' to pin it.", id, path);
                    return (null, SkipReason.Error);
                }
            }

            bundle.Add(new BundleFile(path!, sha256, size));
            total += size;
        }

        if (bundle.Count == 0)
        {
            _logger.LogDebug("HuggingFace discovery: skipped '{Repo}' — no pinnable files.", id);
            return (null, SkipReason.NoPinnableFiles);
        }

        var name = id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;
        var modality = name.Contains("VL", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("vision", StringComparison.OrdinalIgnoreCase)
            ? ModelModality.Vision : ModelModality.Chat;
        var memoryHint = (long)(total * 1.4);

        var entry = new ModelEntry(Name: name, Version: string.Empty, Quantization: "MNN")
        {
            Repo = id,
            Source = ModelSource.HuggingFace,
            Modality = modality,
            Engine = ModelEngine.Mnn,          // taobao-mnn ships MNN bundles
            TotalBytes = total,
            BundleFiles = bundle,
            MinRamGb = memoryHint / 1_000_000_000.0,
            MinStorageGb = total / 1_000_000_000.0,
            MemoryHintBytes = memoryHint,
            Capabilities = modality == ModelModality.Vision ? new[] { "Default", "Vision" } : null,
            License = licence,
        };
        // Discovered entries carry no measured quality; derive a size-based rank so
        // they are selectable (same scale the curated ladder uses).
        return (entry with { QualityRank = CatalogueMerge.RankFor(entry) }, SkipReason.None);
    }

    private async Task<string?> LicenceAsync(string id, CancellationToken ct)
    {
        try
        {
            var url = $"{Trim(_options.BaseUri)}/api/models/{id}";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("HuggingFace discovery: '{Repo}' detail returned {Status}; treating licence as unstated.", id, (int)resp.StatusCode);
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("cardData", out var card) && card.ValueKind == JsonValueKind.Object
                && card.TryGetProperty("license", out var lic) && lic.ValueKind == JsonValueKind.String)
                return lic.GetString();

            if (root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                foreach (var tag in tags.EnumerateArray())
                    if (tag.GetString() is { } ts && ts.StartsWith("license:", StringComparison.OrdinalIgnoreCase))
                        return ts["license:".Length..];

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "HuggingFace discovery: '{Repo}' licence lookup failed; treating as unstated.", id);
            return null;
        }
    }

    private async Task<string?> HashFileAsync(string id, string path, CancellationToken ct)
    {
        try
        {
            var url = $"{Trim(_options.BaseUri)}/{id}/resolve/main/{path}";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("HuggingFace discovery: '{Repo}' file '{File}' fetch for hashing returned {Status}.", id, path, (int)resp.StatusCode);
                return null;
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "HuggingFace discovery: hashing '{Repo}'/'{File}' failed.", id, path);
            return null;
        }
    }

    private static string Trim(Uri u) => u.ToString().TrimEnd('/');

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
