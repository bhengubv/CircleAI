// ServiceCollectionExtensions.cs — CompanionRuntime
//
// AddCompanionRuntime() wires the runtime as an IHostedService. The
// consolidator, sync engine, and ingester are resolved from DI — the
// host registers them via the corresponding Add* extensions on those
// subsystems (AddMemoryConsolidator, AddCompanionStateSync,
// AddMultimodalMemory). Any of them being absent is fine: the runtime
// gracefully skips that subsystem.

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CircleAI.Memory.Runtime;

/// <summary>DI registration for <see cref="CompanionRuntime"/>.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the runtime as both a singleton (for direct access) and as
    /// an <see cref="IHostedService"/> (so Generic Host starts/stops it).
    /// Optional callback to tweak <see cref="CompanionRuntimeOptions"/>.
    /// </summary>
    public static IServiceCollection AddCompanionRuntime(
        this IServiceCollection services,
        Action<CompanionRuntimeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(sp =>
        {
            var opts = new CompanionRuntimeOptions();
            // We can't mutate an immutable record, so the caller must use
            // init-only style — but if a configure callback was passed we
            // honour it by building a fresh instance with the values pushed
            // through a builder pattern. For v0.1 we expose just the
            // options object via init; configure is informational.
            return opts;
        });

        services.TryAddSingleton<CompanionRuntime>();
        services.AddHostedService(sp => sp.GetRequiredService<CompanionRuntime>());

        if (configure is not null)
        {
            // Replace the options registration with a configured copy.
            services.Replace(ServiceDescriptor.Singleton(sp =>
            {
                var o = new CompanionRuntimeOptions();
                // CompanionRuntimeOptions uses init-only properties; the
                // caller may instead override entire options object via a
                // factory delegate. Here we simply expose the default —
                // hosts that need custom values should register their own
                // singleton BEFORE calling AddCompanionRuntime.
                configure(o);
                return o;
            }));
        }

        return services;
    }
}
