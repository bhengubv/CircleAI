// SkillPackAutoImporter.cs
//
// (2.0.2) Downloads each enabled SkillPackSource from GitHub as a tarball,
// extracts it to the local cache, and feeds the SKILL.md files through
// SkillPackLoader.ImportAsync. Caller controls when this runs — typically
// once on host start.
//
// Network calls go through IPackDownloader so tests substitute a fake
// downloader that copies pre-staged content from a temp directory.

using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Skills;

/// <summary>
/// (2.0.2) Settings for <see cref="SkillPackAutoImporter"/>.
/// </summary>
public sealed class SkillPackSourcesOptions
{
    /// <summary>
    /// All packs the host knows about. Defaults to <see cref="KnownSkillPacks.All"/>.
    /// </summary>
    public IList<SkillPackSource> Sources { get; set; } = new List<SkillPackSource>(KnownSkillPacks.All);

    /// <summary>
    /// Root directory for cached pack downloads. Defaults to
    /// <c>%LOCALAPPDATA%/CircleAI/skill-packs/</c> on Windows /
    /// <c>~/.local/share/CircleAI/skill-packs/</c> on Linux/macOS.
    /// </summary>
    public string CacheDirectory { get; set; } = DefaultCacheDirectory();

    /// <summary>
    /// When <c>true</c>, <see cref="SkillPackAutoImporter.ImportEnabledAsync"/>
    /// pulls every source where <see cref="SkillPackSource.IsDefaultEnabled"/>
    /// is set. When <c>false</c>, only sources named in
    /// <see cref="ExplicitlyEnabled"/> are imported.
    /// </summary>
    public bool ImportDefaultEnabledPacks { get; set; } = true;

    /// <summary>
    /// Pack names the host wants to opt in beyond the default-enabled set.
    /// Useful for enabling <c>career-ops</c> or <c>build-your-own-x</c>
    /// once their adapters are wired.
    /// </summary>
    public IList<string> ExplicitlyEnabled { get; set; } = new List<string>();

    /// <summary>
    /// When set, the importer reuses cached extractions older than this
    /// without re-downloading. Default 7 days.
    /// </summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromDays(7);

    private static string DefaultCacheDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(string.IsNullOrEmpty(root) ? Path.GetTempPath() : root,
            "CircleAI", "skill-packs");
    }
}

/// <summary>
/// Strategy for materialising a remote pack into a local directory.
/// Default: <see cref="HttpPackDownloader"/>. Tests substitute a fake.
/// </summary>
public interface IPackDownloader
{
    /// <summary>
    /// Ensure <paramref name="source"/> is materialised under
    /// <paramref name="cacheRoot"/>. Returns the local path containing the
    /// extracted repo (so the caller can append <c>SkillSubdir</c>).
    /// </summary>
    Task<string> EnsureAsync(
        SkillPackSource   source,
        string            cacheRoot,
        TimeSpan          cacheTtl,
        CancellationToken ct);
}

/// <summary>
/// Default downloader — fetches
/// <c>https://github.com/&lt;owner/repo&gt;/archive/&lt;ref&gt;.tar.gz</c>
/// and extracts it via <see cref="System.Formats.Tar.TarFile"/>.
/// </summary>
public sealed class HttpPackDownloader : IPackDownloader
{
    private readonly HttpClient _http;

    public HttpPackDownloader(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.Add(
                new System.Net.Http.Headers.ProductInfoHeaderValue("CircleAI", "2.0.2"));
    }

    /// <inheritdoc/>
    public async Task<string> EnsureAsync(
        SkillPackSource source, string cacheRoot, TimeSpan cacheTtl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);

        var slug      = Sanitize(source.Name);
        var packDir   = Path.Combine(cacheRoot, slug);
        var stamp     = Path.Combine(packDir, ".stamp");

        if (File.Exists(stamp))
        {
            var age = DateTimeOffset.UtcNow - new DateTimeOffset(File.GetLastWriteTimeUtc(stamp), TimeSpan.Zero);
            if (age <= cacheTtl) return packDir;
        }

        Directory.CreateDirectory(packDir);
        var tarballUrl = BuildTarballUrl(source);
        using var resp = await _http.GetAsync(tarballUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        // Extract gz-tar into a temp staging dir, then rename atomically.
        var stage = packDir + ".stage";
        if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        Directory.CreateDirectory(stage);

        await using (var net  = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var gz   = new GZipStream(net, CompressionMode.Decompress))
        {
            await TarFile.ExtractToDirectoryAsync(gz, stage, overwriteFiles: true, ct).ConfigureAwait(false);
        }

        // GitHub tarballs nest the content under <repo>-<ref>/. Flatten if so.
        var inner = Directory.EnumerateDirectories(stage).FirstOrDefault();
        var staged = inner is not null && Directory.GetFiles(stage).Length == 0 ? inner : stage;

        if (Directory.Exists(packDir)) Directory.Delete(packDir, recursive: true);
        Directory.Move(staged, packDir);
        if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        File.WriteAllText(stamp, DateTimeOffset.UtcNow.ToString("O"));
        return packDir;
    }

    private static string BuildTarballUrl(SkillPackSource source)
    {
        // GitHub: https://github.com/<owner>/<repo>/archive/<ref>.tar.gz
        var url = source.RepoUrl.TrimEnd('/');
        return $"{url}/archive/{source.GitRef}.tar.gz";
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}

/// <summary>
/// (2.0.2) Orchestrates download + import for every enabled pack.
/// </summary>
public sealed class SkillPackAutoImporter
{
    private readonly IPackDownloader _downloader;
    private readonly ISkillStore     _store;
    private readonly SkillPackSourcesOptions _options;

    public SkillPackAutoImporter(
        ISkillStore             store,
        SkillPackSourcesOptions options,
        IPackDownloader?        downloader = null)
    {
        _store      = store      ?? throw new ArgumentNullException(nameof(store));
        _options    = options    ?? throw new ArgumentNullException(nameof(options));
        _downloader = downloader ?? new HttpPackDownloader();
    }

    /// <summary>
    /// Resolve which packs to import based on the options, then download
    /// and import each. Continues on per-pack failure; returns one
    /// manifest per successfully-imported pack.
    /// </summary>
    public async Task<IReadOnlyList<SkillPackManifest>> ImportEnabledAsync(
        Action<string, Exception>? onError = null,
        CancellationToken          ct      = default)
    {
        var results = new List<SkillPackManifest>();
        Directory.CreateDirectory(_options.CacheDirectory);

        foreach (var source in EnumerateEnabled())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var packDir = await _downloader
                    .EnsureAsync(source, _options.CacheDirectory, _options.CacheTtl, ct)
                    .ConfigureAwait(false);
                var skillRoot = string.IsNullOrEmpty(source.SkillSubdir)
                    ? packDir
                    : Path.Combine(packDir, source.SkillSubdir);
                if (!Directory.Exists(skillRoot))
                {
                    onError?.Invoke(source.Name,
                        new DirectoryNotFoundException(
                            $"Skill subdir '{source.SkillSubdir}' not found in pack '{source.Name}'."));
                    continue;
                }

                var manifest = await SkillPackLoader.ImportAsync(
                    _store, skillRoot,
                    packName:    source.Name,
                    packVersion: source.GitRef,
                    sourceUrl:   source.RepoUrl,
                    license:     source.License,
                    onWarning:   (path, ex) => onError?.Invoke($"{source.Name}: {path}", ex),
                    ct:          ct).ConfigureAwait(false);
                results.Add(manifest);
            }
            catch (Exception ex)
            {
                onError?.Invoke(source.Name, ex);
            }
        }

        return results;
    }

    private IEnumerable<SkillPackSource> EnumerateEnabled()
    {
        var byName = _options.Sources.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_options.ImportDefaultEnabledPacks)
        {
            foreach (var s in _options.Sources)
                if (s.IsDefaultEnabled && seen.Add(s.Name)) yield return s;
        }

        foreach (var name in _options.ExplicitlyEnabled)
        {
            if (byName.TryGetValue(name, out var src) && seen.Add(src.Name))
                yield return src;
        }
    }
}
