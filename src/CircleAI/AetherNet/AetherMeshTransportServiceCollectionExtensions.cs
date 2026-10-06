// AetherMeshTransportServiceCollectionExtensions.cs
//
// One call to wire the AetherNet carrier as the mesh's INetworkTransport. DEVICE
// HEAD ONLY — the product links none of this. Requires an ITransportService (the
// byte carrier) and an ISignalProtocolService (sealing/session) already in the
// container; AetherNet's own DI provides those.

using System;
using AetherNet.Security.Services;
using AetherNet.Transport.Abstractions;
using CircleAI.Networking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CircleAI.AetherNet;

/// <summary>DI wiring for <see cref="AetherMeshTransport"/>.</summary>
public static class AetherMeshTransportServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AetherMeshTransport"/> as the singleton
    /// <see cref="INetworkTransport"/> the mesh offload engine rides (replacing any
    /// prior registration). The host must already have registered an
    /// <see cref="ITransportService"/> and an <see cref="ISignalProtocolService"/>.
    /// This device's UHID is read from <paramref name="localUhid"/> so it can come
    /// from identity storage rather than a compile-time constant.
    /// </summary>
    /// <param name="services">The device head's DI container.</param>
    /// <param name="localUhid">Resolves this device's AetherTag / UHID.</param>
    /// <param name="handshakeTimeout">How long to wait for a peer's pre-key bundle. Default 10s.</param>
    public static IServiceCollection AddCircleAiMeshAetherTransport(
        this IServiceCollection services,
        Func<IServiceProvider, string> localUhid,
        TimeSpan? handshakeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(localUhid);

        services.RemoveAll<INetworkTransport>();
        services.AddSingleton<INetworkTransport>(sp => new AetherMeshTransport(
            sp.GetRequiredService<ITransportService>(),
            sp.GetRequiredService<ISignalProtocolService>(),
            localUhid(sp),
            handshakeTimeout));
        return services;
    }
}
