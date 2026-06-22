// PluginRegistry.cs
//
// (3.2.0) Installed-plugin registry + marketplace catalog. Direct lift
// from CircleUp's PluginRegistry — JSON-backed, atomic save (tmp +
// rename), thread-safe, opt-in permissions per plugin.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Plugins;

/// <summary>
/// (3.2.0) Tracks installed plugins. Hot-reload pattern: ShutdownAsync
/// every loaded plugin, drop the AssemblyLoadContext (collectible),
/// rescan the plugins/ folder, initialise everything fresh. Permissions
/// are declarative — users audit before trusting.
/// </summary>
public sealed class PluginRegistry
{
    private readonly string _pluginsRoot;
    private readonly string _manifestPath;
    private readonly object _gate = new();
    private readonly ILogger _logger;
    private readonly List<RegisteredPlugin> _installed = new();

    public PluginRegistry(string pluginsRoot, ILogger? logger = null)
    {
        _pluginsRoot = pluginsRoot ?? throw new ArgumentNullException(nameof(pluginsRoot));
        _logger = logger ?? NullLogger.Instance;
        Directory.CreateDirectory(_pluginsRoot);
        _manifestPath = Path.Combine(_pluginsRoot, "registry.json");
        Load();
    }

    public IReadOnlyList<RegisteredPlugin> Installed
    {
        get { lock (_gate) { return _installed.ToList(); } }
    }

    public RegisteredPlugin? Get(string id)
    {
        lock (_gate)
        {
            return _installed.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public RegisteredPlugin Register(string id, string displayName, string version, IEnumerable<string> permissions)
    {
        var entry = new RegisteredPlugin
        {
            Id          = id,
            DisplayName = displayName,
            Version     = version,
            Permissions = permissions.ToList(),
            Enabled     = false,
            InstalledAt = DateTimeOffset.UtcNow,
        };
        lock (_gate)
        {
            _installed.RemoveAll(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            _installed.Add(entry);
            Save();
        }
        return entry;
    }

    public bool SetEnabled(string id, bool enabled)
    {
        lock (_gate)
        {
            var p = _installed.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (p is null) return false;
            p.Enabled = enabled;
            Save();
            return true;
        }
    }

    public bool GrantPermission(string id, string permission)
    {
        lock (_gate)
        {
            var p = _installed.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (p is null) return false;
            if (!p.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase))
            {
                p.Permissions.Add(permission);
                Save();
            }
            return true;
        }
    }

    public bool RevokePermission(string id, string permission)
    {
        lock (_gate)
        {
            var p = _installed.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (p is null) return false;
            var removed = p.Permissions.RemoveAll(perm => string.Equals(perm, permission, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) Save();
            return removed > 0;
        }
    }

    public bool Uninstall(string id)
    {
        lock (_gate)
        {
            var removed = _installed.RemoveAll(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                Save();
                // Best-effort: delete the plugin folder too.
                var dir = Path.Combine(_pluginsRoot, id);
                if (Directory.Exists(dir))
                {
                    try { Directory.Delete(dir, recursive: true); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete plugin folder {Folder}", dir); }
                }
            }
            return removed;
        }
    }

    private void Load()
    {
        if (!File.Exists(_manifestPath)) return;
        try
        {
            var json = File.ReadAllText(_manifestPath);
            var loaded = JsonSerializer.Deserialize<List<RegisteredPlugin>>(json);
            if (loaded is not null)
            {
                _installed.Clear();
                _installed.AddRange(loaded);
            }
        }
        catch { /* corrupt — start fresh */ }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_installed, new JsonSerializerOptions { WriteIndented = true });
            var tmp = _manifestPath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(_manifestPath)) File.Delete(_manifestPath);
            File.Move(tmp, _manifestPath);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to save plugin registry."); }
    }
}

/// <summary>(3.2.0) One installed plugin entry.</summary>
public sealed class RegisteredPlugin
{
    public string Id          { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version     { get; set; } = "0.0.0";
    public List<string> Permissions { get; set; } = new();
    public bool Enabled     { get; set; }
    public DateTimeOffset InstalledAt { get; set; }
}

/// <summary>
/// (3.2.0) Marketplace catalog. Backed by a JSON file the operator
/// publishes (typically <c>plugins/marketplace.json</c>). Catalog is
/// metadata only — install downloads the plugin into
/// <c>plugins/{id}/</c>.
/// </summary>
public sealed class PluginMarketplace
{
    private readonly string _catalogPath;

    public PluginMarketplace(string catalogPath)
    {
        _catalogPath = catalogPath ?? throw new ArgumentNullException(nameof(catalogPath));
    }

    private static readonly JsonSerializerOptions CatalogJson = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<MarketplaceEntry> List()
    {
        if (!File.Exists(_catalogPath)) return Array.Empty<MarketplaceEntry>();
        try
        {
            var json = File.ReadAllText(_catalogPath);
            return JsonSerializer.Deserialize<List<MarketplaceEntry>>(json, CatalogJson) ?? new();
        }
        catch
        {
            return Array.Empty<MarketplaceEntry>();
        }
    }
}

/// <summary>(3.2.0) One marketplace catalog entry.</summary>
public sealed class MarketplaceEntry
{
    public string Id          { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version     { get; set; } = "0.0.0";
    public string Description { get; set; } = "";
    public string Author      { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public List<string> Permissions { get; set; } = new();
}
