// SelectingTransportServiceCollectionExtensions.cs
//
// One call that turns every registered transport into one that chooses (OPEN-GAPS A10).
//
// CALL IT AFTER THE TRANSPORTS ARE REGISTERED. It takes each INetworkTransport registration
// that is there, puts them behind one SelectingTransport, and registers the cascade selector -
// so whatever rides INetworkTransport (the mesh offload engine) gets every transport the
// device has, tried in the stated order, instead of the last one registered.

using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CircleAI.Networking;

/// <summary>Registers a <see cref="SelectingTransport"/> over the transports already registered.</summary>
public static class SelectingTransportServiceCollectionExtensions
{
    /// <summary>
    /// Replaces the registered <see cref="INetworkTransport"/>s with one
    /// <see cref="SelectingTransport"/> over all of them, routed by a
    /// <see cref="CascadeTransportSelector"/> under <paramref name="policy"/>.
    /// </summary>
    public static IServiceCollection AddSelectingTransport(this IServiceCollection services, INetworkPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var registered = services
            .Where(d => d.ServiceType == typeof(INetworkTransport) && !d.IsKeyedService)
            .ToList();
        foreach (var d in registered) services.Remove(d);

        services.TryAddSingleton<ITransportSelector>(_ => new CascadeTransportSelector(policy));
        services.AddSingleton<INetworkTransport>(sp => new SelectingTransport(
            registered.Select(d => (INetworkTransport)(
                d.ImplementationInstance
                ?? d.ImplementationFactory?.Invoke(sp)
                ?? ActivatorUtilities.CreateInstance(sp, d.ImplementationType!))),
            sp.GetRequiredService<ITransportSelector>()));
        return services;
    }
}
