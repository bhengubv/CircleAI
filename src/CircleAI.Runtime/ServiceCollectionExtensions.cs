// ServiceCollectionExtensions.cs
//
// DI registration helpers. Hosts wire all three Runtime services with a
// single call: services.AddCircleAIRuntime(cacheRoot).

using Microsoft.Extensions.DependencyInjection;
using CircleAI.Runtime.Backends;
using CircleAI.Runtime.Capabilities;
using CircleAI.Runtime.NativeRuntimes;

namespace CircleAI.Runtime;

/// <summary>
/// DI extensions for the CircleAI.Runtime package.
/// </summary>
public static class CircleAIRuntimeServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="ICapabilityProbe"/> (auto-platform-selected),
    /// <see cref="IBackendSelector"/>, and <see cref="INativeRuntimeFetcher"/>
    /// (rooted at <paramref name="runtimeCacheRoot"/>) as singletons.
    /// </summary>
    public static IServiceCollection AddCircleAIRuntime(
        this IServiceCollection services, string runtimeCacheRoot)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(runtimeCacheRoot))
            throw new ArgumentException("Runtime cache root must not be empty.", nameof(runtimeCacheRoot));

        services.AddSingleton<ICapabilityProbe>(_ => new CapabilityProbe());
        services.AddSingleton<IBackendSelector, BackendSelector>();
        services.AddSingleton<INativeRuntimeFetcher>(_ => new NativeRuntimeFetcher(runtimeCacheRoot));
        return services;
    }
}
