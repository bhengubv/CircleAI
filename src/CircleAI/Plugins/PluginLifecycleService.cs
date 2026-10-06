// PluginLifecycleService.cs
//
// (3.2.0) IHostedService that discovers + initialises plugins at host
// startup, shuts them down on stop, and supports ReloadAsync() for
// hot-reload. Direct lift from CircleUp's PluginLifecycleService;
// vault-specific IVaultPathProvider replaced with a host-supplied
// Func<string?> workspace accessor (configurable via DI).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CircleAI.Plugins;

/// <summary>
/// (3.2.0) Plugin lifecycle host. Configure the plugins root through
/// <c>CircleAI:PluginsPath</c> in <see cref="IConfiguration"/>, or
/// register an <see cref="IPluginsRootResolver"/> in DI to control it
/// programmatically.
/// </summary>
public sealed class PluginLifecycleService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PluginLifecycleService> _logger;
    private readonly List<IPlugin> _initialised = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _pluginsPath = "";

    public PluginLifecycleService(
        IServiceProvider                  services,
        IConfiguration                    configuration,
        ILogger<PluginLifecycleService>   logger)
    {
        _services      = services;
        _configuration = configuration;
        _logger        = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var resolver = _services.GetService<IPluginsRootResolver>();
        if (resolver is not null)
        {
            _pluginsPath = resolver.ResolveRoot();
        }
        else
        {
            var env = _services.GetService<IHostEnvironment>();
            _pluginsPath = _configuration["CircleAI:PluginsPath"]
                ?? (env is not null ? Path.Combine(env.ContentRootPath, "plugins") : "plugins");
        }
        await LoadAllAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await UnloadAllAsync(cancellationToken);
    }

    /// <summary>Drop every loaded plugin and re-scan the plugins/
    /// folder. Use after installing/updating a plugin so changes apply
    /// without a host restart.</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await UnloadAllAsync(cancellationToken).ConfigureAwait(false);
            await LoadAllAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public IReadOnlyList<IPlugin> Active
    {
        get { lock (_initialised) return _initialised.ToList(); }
    }

    private async Task LoadAllAsync(CancellationToken cancellationToken)
    {
        var loader = new PluginLoader(_logger);
        var results = loader.Discover(_pluginsPath);

        var events        = _services.GetRequiredService<IPluginEvents>();
        var workspaceFn   = _services.GetService<IWorkspacePathProvider>();
        var loggerFactory = _services.GetRequiredService<ILoggerFactory>();
        var registry      = _services.GetRequiredService<PluginRegistry>();

        foreach (var result in results)
        {
            if (result.Plugin is null)
            {
                _logger.LogWarning("Plugin '{Id}' failed to load: {Error}", result.Id, result.Error);
                continue;
            }
            try
            {
                // Look up declared permissions from the registry — if
                // the plugin isn't registered yet (first-run discovery),
                // auto-register with empty permissions so it loads but
                // can't touch anything until the user grants.
                var registered  = registry.Get(result.Plugin.Id);
                var permissions = registered?.Permissions ?? new List<string>();
                if (registered is null)
                {
                    registry.Register(result.Plugin.Id, result.Plugin.DisplayName, result.Plugin.Version, permissions);
                }
                var baseCtx = new PluginContext(
                    workspacePathAccessor: () => workspaceFn?.WorkspacePath,
                    events:                events,
                    logger:                loggerFactory.CreateLogger($"plugin:{result.Plugin.Id}"));
                var ctx = new PermissionedPluginContext(baseCtx, permissions);
                await result.Plugin.InitializeAsync(ctx, cancellationToken).ConfigureAwait(false);
                lock (_initialised) _initialised.Add(result.Plugin);
                _logger.LogInformation("Plugin '{Id}' v{Version} initialised (permissions: {Perms}).",
                    result.Plugin.Id, result.Plugin.Version,
                    permissions.Count == 0 ? "none" : string.Join(", ", permissions));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Plugin '{Id}' threw during initialisation.", result.Id);
            }
        }
    }

    private async Task UnloadAllAsync(CancellationToken cancellationToken)
    {
        List<IPlugin> snapshot;
        lock (_initialised)
        {
            snapshot = _initialised.ToList();
            _initialised.Clear();
        }
        foreach (var plugin in snapshot)
        {
            try { await plugin.ShutdownAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Plugin '{Id}' threw during shutdown.", plugin.Id); }
        }
    }
}

/// <summary>(3.2.0) Optional host hook for resolving the plugins root.</summary>
public interface IPluginsRootResolver
{
    string ResolveRoot();
}

/// <summary>(3.2.0) Optional host hook for the workspace path plugins see.</summary>
public interface IWorkspacePathProvider
{
    string? WorkspacePath { get; }
}
