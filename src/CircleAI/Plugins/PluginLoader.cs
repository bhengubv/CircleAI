// PluginLoader.cs
//
// (3.2.0) Discovers + loads IPlugin assemblies from a plugins/ folder.
// Each subfolder is a separate plugin with its own collectible
// AssemblyLoadContext, so two plugins can ship conflicting dep versions
// without crashing each other. Direct lift from CircleUp's PluginLoader.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Plugins;

/// <summary>
/// (3.2.0) Plugin discovery + loading. Layout:
/// <code>
/// plugins/
///   my-plugin/
///     MyPlugin.dll          ← assembly with the IPlugin implementation
///     dependency.dll        ← extra references the plugin needs
///     plugin.json           ← optional manifest (id, version, etc.)
/// </code>
/// </summary>
public sealed class PluginLoader
{
    private readonly ILogger _logger;

    public PluginLoader(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public IReadOnlyList<PluginLoadResult> Discover(string pluginsRoot)
    {
        if (string.IsNullOrEmpty(pluginsRoot) || !Directory.Exists(pluginsRoot))
        {
            return Array.Empty<PluginLoadResult>();
        }

        var results = new List<PluginLoadResult>();
        foreach (var dir in Directory.EnumerateDirectories(pluginsRoot))
        {
            results.Add(LoadDirectory(dir));
        }
        return results;
    }

    private PluginLoadResult LoadDirectory(string directory)
    {
        var id = Path.GetFileName(directory);
        try
        {
            // Pick the entry assembly: prefer the .dll whose name matches
            // the folder; else first .dll alphabetically.
            var entryDll = Directory.GetFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
                .OrderBy(p => string.Equals(Path.GetFileNameWithoutExtension(p), id, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (entryDll is null)
            {
                return new PluginLoadResult(id, null, $"No .dll found in {directory}.");
            }

            var context = new AssemblyLoadContext($"plugin:{id}", isCollectible: true);
            // Resolve referenced assemblies from the plugin's folder
            // first; fall back to the host AssemblyLoadContext.
            context.Resolving += (ctx, name) =>
            {
                var candidate = Path.Combine(directory, name.Name + ".dll");
                return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
            };

            var asm = context.LoadFromAssemblyPath(entryDll);
            var pluginType = asm.GetTypes()
                .FirstOrDefault(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
            if (pluginType is null)
            {
                return new PluginLoadResult(id, null, $"No IPlugin implementation in {entryDll}.");
            }

            if (Activator.CreateInstance(pluginType) is not IPlugin plugin)
            {
                return new PluginLoadResult(id, null, $"Failed to instantiate {pluginType.FullName}.");
            }

            return new PluginLoadResult(id, plugin, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load plugin from {Directory}", directory);
            return new PluginLoadResult(id, null, ex.Message);
        }
    }
}

/// <summary>(3.2.0) Outcome of loading one plugin directory.</summary>
public sealed record PluginLoadResult(string Id, IPlugin? Plugin, string? Error);
