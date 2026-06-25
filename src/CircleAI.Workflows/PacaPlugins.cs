// PacaPlugins.cs
//
// (3.3.0) Plugin runtime + manifest + lifecycle ported from paca:
// plugin manifest validation, semver upgrade detection, reverse-DNS
// naming, marketplace install/upgrade/uninstall, frontend module
// surface, extension points, artifact + migration management,
// per-plugin resource limits + WASI snapshot preview-1 support.
//
// The wazero / WASM execution layer is host-supplied via
// IPluginRuntimeHost; this package owns the lifecycle.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Workflows;

/// <summary>(3.3.0) Plugin extension points supported by the marketplace.</summary>
public enum PluginExtensionPoint
{
    Sidebar,
    TaskDetail,
    Settings,
    CustomView,
    Route,
    Event,
    McpTool,
}

/// <summary>(3.3.0) Plugin manifest from <c>plugin.json</c>.</summary>
public sealed record PluginManifest(
    string                              Name,                  // reverse-DNS, e.g. "com.paca.bdd"
    string                              DisplayName,
    string                              Version,               // SemVer
    string                              Description,
    Uri?                                ArtifactWasmUrl,
    Uri?                                FrontendModuleUrl,
    IReadOnlyList<PluginExtensionPoint> ExtensionPoints,
    IReadOnlyList<string>               McpTools,
    IReadOnlyList<string>               SqlMigrationFiles,
    PluginResourceLimits                Limits);

/// <summary>(3.3.0) Per-plugin resource limits.</summary>
/// <param name="CallTimeoutMs">Max wall-clock time for one host call. Default 5000ms.</param>
/// <param name="MemoryCeilingBytes">Max memory the WASM instance may allocate. Default 64MB.</param>
public sealed record PluginResourceLimits(int CallTimeoutMs = 5000, long MemoryCeilingBytes = 64L * 1024 * 1024);

/// <summary>(3.3.0) Installed instance.</summary>
public sealed record InstalledPlugin(
    string         Id,                   // matches manifest.Name
    PluginManifest Manifest,
    string         InstalledFromCatalog,
    DateTimeOffset InstalledAtUtc,
    bool           Enabled);

/// <summary>(3.3.0) Plugin runtime host (wazero-style). Provided by the deploy.</summary>
public interface IPluginRuntimeHost
{
    /// <summary>Install + initialise. Run SQL migrations + cache the WASM artifact.</summary>
    ValueTask InstallAsync(InstalledPlugin plugin, CancellationToken ct = default);

    /// <summary>Uninstall — drop WASM + clean artifacts; do NOT roll back data unless asked.</summary>
    ValueTask UninstallAsync(string pluginId, bool dropArtifacts, CancellationToken ct = default);

    /// <summary>Hot-swap to a new version (semver upgrade).</summary>
    ValueTask UpgradeAsync(InstalledPlugin from, InstalledPlugin to, CancellationToken ct = default);
}

/// <summary>(3.3.0) Plugin lifecycle manager. Installs / upgrades / uninstalls / enables / disables.</summary>
public sealed class PacaPluginRegistry
{
    private readonly ConcurrentDictionary<string, InstalledPlugin> _installed = new();
    private readonly IPluginRuntimeHost _runtime;
    private readonly Func<DateTimeOffset> _clock;

    private static readonly Regex ReverseDnsPattern = new(@"^[a-z][a-z0-9]*(\.[a-z][a-z0-9_-]*)+$",
        RegexOptions.Compiled);

    public PacaPluginRegistry(IPluginRuntimeHost runtime, Func<DateTimeOffset>? clock = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _clock   = clock   ?? (() => DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<InstalledPlugin> ListInstalled() => _installed.Values.ToList();

    public InstalledPlugin? Get(string id)
        => _installed.TryGetValue(id, out var p) ? p : null;

    /// <summary>(3.3.0) Validate a manifest before install / upgrade.</summary>
    public static void ValidateManifest(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!ReverseDnsPattern.IsMatch(manifest.Name))
        {
            throw new ArgumentException($"Plugin name '{manifest.Name}' must be reverse-DNS (e.g. com.paca.bdd).");
        }
        if (!Version.TryParse(StripPrerelease(manifest.Version), out _))
        {
            throw new ArgumentException($"Plugin version '{manifest.Version}' is not parseable SemVer.");
        }
        if (manifest.Limits.CallTimeoutMs <= 0)        throw new ArgumentException("CallTimeoutMs must be positive.");
        if (manifest.Limits.MemoryCeilingBytes <= 0)   throw new ArgumentException("MemoryCeilingBytes must be positive.");
    }

    /// <summary>(3.3.0) Install plugin from the supplied manifest.</summary>
    public async ValueTask<InstalledPlugin> InstallAsync(PluginManifest manifest, string catalog, CancellationToken ct = default)
    {
        ValidateManifest(manifest);
        if (_installed.ContainsKey(manifest.Name))
        {
            throw new InvalidOperationException($"Plugin '{manifest.Name}' is already installed; use UpgradeAsync.");
        }
        var installed = new InstalledPlugin(manifest.Name, manifest, catalog, _clock(), Enabled: true);
        await _runtime.InstallAsync(installed, ct).ConfigureAwait(false);
        _installed[manifest.Name] = installed;
        return installed;
    }

    /// <summary>(3.3.0) Upgrade if <paramref name="newManifest"/>'s SemVer is strictly newer.</summary>
    public async ValueTask<InstalledPlugin> UpgradeAsync(PluginManifest newManifest, string catalog, CancellationToken ct = default)
    {
        ValidateManifest(newManifest);
        if (!_installed.TryGetValue(newManifest.Name, out var current))
        {
            throw new InvalidOperationException($"Plugin '{newManifest.Name}' is not installed.");
        }
        if (CompareSemver(newManifest.Version, current.Manifest.Version) <= 0)
        {
            throw new InvalidOperationException($"Version {newManifest.Version} is not newer than {current.Manifest.Version}.");
        }
        var next = new InstalledPlugin(newManifest.Name, newManifest, catalog, _clock(), current.Enabled);
        await _runtime.UpgradeAsync(current, next, ct).ConfigureAwait(false);
        _installed[newManifest.Name] = next;
        return next;
    }

    public async ValueTask UninstallAsync(string id, bool dropArtifacts = true, CancellationToken ct = default)
    {
        if (!_installed.TryRemove(id, out _)) return;
        await _runtime.UninstallAsync(id, dropArtifacts, ct).ConfigureAwait(false);
    }

    public void SetEnabled(string id, bool enabled)
    {
        if (_installed.TryGetValue(id, out var current))
        {
            _installed[id] = current with { Enabled = enabled };
        }
    }

    /// <summary>(3.3.0) Compare SemVer-ish strings: returns &lt;0 / 0 / &gt;0.</summary>
    public static int CompareSemver(string a, string b)
    {
        var va = Version.Parse(StripPrerelease(a));
        var vb = Version.Parse(StripPrerelease(b));
        return va.CompareTo(vb);
    }

    private static string StripPrerelease(string v) => v.Split('-', '+')[0];
}
