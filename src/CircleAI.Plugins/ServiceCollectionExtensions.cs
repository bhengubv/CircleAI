// ServiceCollectionExtensions.cs
//
// (3.2.0) DI helpers for the plugin host. Registers PluginEvents +
// PluginRegistry + PluginLifecycleService as singletons; consumers can
// then optionally register IWorkspacePathProvider /
// IPluginsRootResolver.

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CircleAI.Plugins;

public static class PluginsServiceCollectionExtensions
{
    /// <summary>
    /// (3.2.0) Register the plugin host. Reads
    /// <c>CircleAI:PluginsPath</c> from configuration (defaults to
    /// <c>{ContentRoot}/plugins</c>).
    /// </summary>
    public static IServiceCollection AddCircleAiPlugins(this IServiceCollection services)
    {
        services.AddSingleton<IPluginEvents, PluginEvents>();
        services.AddSingleton(sp =>
        {
            var resolver = sp.GetService<IPluginsRootResolver>();
            if (resolver is not null) return new PluginRegistry(resolver.ResolveRoot());
            var env = sp.GetService<IHostEnvironment>();
            var root = env is not null ? System.IO.Path.Combine(env.ContentRootPath, "plugins") : "plugins";
            return new PluginRegistry(root);
        });
        services.AddSingleton<PluginLifecycleService>();
        services.AddHostedService(sp => sp.GetRequiredService<PluginLifecycleService>());
        return services;
    }

    /// <summary>Register a host-supplied plugins-root resolver.</summary>
    public static IServiceCollection AddPluginsRootResolver<T>(this IServiceCollection services)
        where T : class, IPluginsRootResolver
    {
        services.AddSingleton<IPluginsRootResolver, T>();
        return services;
    }

    /// <summary>Register a host-supplied workspace-path provider.</summary>
    public static IServiceCollection AddWorkspacePathProvider<T>(this IServiceCollection services)
        where T : class, IWorkspacePathProvider
    {
        services.AddSingleton<IWorkspacePathProvider, T>();
        return services;
    }
}
