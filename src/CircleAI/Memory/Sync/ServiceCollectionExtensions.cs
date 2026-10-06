// ServiceCollectionExtensions.cs
//
// DI registration for the companion-state sync layer.
//
// Hosts wire:
//   • An ICompanionStateChannel (loopback for tests / dev, AetherNet for
//     production once the Phase 3.1 channel ships)
//   • A nodeShortId (0..63) per device
// then call:
//   services.AddCompanionStateSync(nodeShortId: 7);
//
// IMPORTANT — the channel is NOT registered here. The host registers a
// concrete channel separately so it can be selected per-deployment.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CircleAI.Memory.Sync;

/// <summary>DI registration for the companion-state sync engine.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers an in-memory store, the HLC, and the engine. Idempotent;
    /// a host that has already registered a custom store wins.
    /// </summary>
    /// <param name="services">DI container.</param>
    /// <param name="nodeShortId">
    /// 0..63 — the device's stable short ID for HLC composition. Pick any
    /// deterministic per-device value.
    /// </param>
    public static IServiceCollection AddCompanionStateSync(
        this IServiceCollection services, long nodeShortId)
    {
        System.ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ISyncableEntryStore, InMemorySyncableEntryStore>();
        services.TryAddSingleton(_ => new HybridLogicalClock(nodeShortId));

        services.TryAddSingleton<ICompanionStateSyncEngine>(sp =>
            new CompanionStateSyncEngine(
                sp.GetRequiredService<ICompanionStateChannel>(),
                sp.GetRequiredService<ISyncableEntryStore>(),
                sp.GetRequiredService<HybridLogicalClock>()));

        services.TryAddSingleton<PersonaStateSyncBridge>();

        return services;
    }
}
