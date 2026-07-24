// ServiceCollectionExtensions.cs
//
// One-call wire-up. After:
//     services.AddCircleAiMeshOffload(o => { o.LocalNodeId = myAetherTag; ... });
// the container resolves:
//     IOffloadRouter             -> MeshOffloadRouter
//     IMeshOffloadClient         -> MeshOffloadClient   (also a hosted service - the receive pump)
//     IMeshCapabilityBroadcaster -> AetherMeshCapabilityBroadcaster (REPLACES the v1 no-op)
//     + MeshAdvertisementBeacon  hosted service (periodic re-broadcast)
//
// PREREQUISITES the host must supply in DI:
//   * INetworkTransport  - the reachable transport to ride (hotspot / LAN / Aether).
//                          NOT registered here; the host picks the transport.
//   * ILocalInferenceFallback - the local brain. Defaults to NullLocalInferenceFallback
//                          (borrow-only) unless the host registers its own adapter
//                          over IInferenceBridge / IChatGenerator / a smaller model.
//   * IMeshCapabilityRegistry - defaults to InMemoryMeshCapabilityRegistry (reused
//                          from CircleAI.AetherNet) unless the host registered one.
//
// Discovery of peers is AetherNet's job (aether-protocol repo); this package only
// consumes the transport and the registry.

using CircleAI.AetherNet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CircleAI.Mesh;

/// <summary>DI wiring for the CircleAI mesh offload router family.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the mesh offload router, transport client (+ receive pump), the
    /// real capability broadcaster, and the advert beacon. Idempotent via TryAdd
    /// so it composes with hosts that already wired some pieces (registry,
    /// fallback). The host must additionally register an
    /// <c>INetworkTransport</c>.
    /// </summary>
    /// <param name="services">The host's DI container.</param>
    /// <param name="configure">Optional options configuration.</param>
    public static IServiceCollection AddCircleAiMeshOffload(
        this IServiceCollection services,
        Action<MeshOffloadOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        OptionsBuilderInternalConfigure(services, configure);

        // Reuse the existing in-memory registry as the default sink; the transport
        // client feeds it as peer adverts arrive. Host may register its own first.
        services.TryAddSingleton<IMeshCapabilityRegistry, InMemoryMeshCapabilityRegistry>();

        // Borrow-only by default; host swaps in an adapter over its real engine.
        services.TryAddSingleton<ILocalInferenceFallback, NullLocalInferenceFallback>();

        // The single owner of the transport's receive stream - resolved once and
        // exposed both as the client abstraction and as the hosted-service pump.
        services.TryAddSingleton<MeshOffloadClient>();
        services.TryAddSingleton<IMeshOffloadClient>(sp => sp.GetRequiredService<MeshOffloadClient>());
        services.AddHostedService(sp => sp.GetRequiredService<MeshOffloadClient>());

        services.TryAddSingleton<IOffloadRouter, MeshOffloadRouter>();

        // The real broadcaster REPLACES CircleAI.AetherNet's NullMeshCapabilityBroadcaster.
        services.TryAddSingleton<IMeshCapabilityBroadcaster, AetherMeshCapabilityBroadcaster>();
        services.AddHostedService<MeshAdvertisementBeacon>();

        return services;
    }

    private static void OptionsBuilderInternalConfigure(
        IServiceCollection services, Action<MeshOffloadOptions>? configure)
    {
        var builder = services.AddOptions<MeshOffloadOptions>();
        if (configure is not null)
        {
            builder.Configure(configure);
        }
    }
}
