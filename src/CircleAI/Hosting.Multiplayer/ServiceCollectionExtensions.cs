// ServiceCollectionExtensions.cs
//
// (3.2.0) DI helpers — register the hub and its peer-identity provider.
// Hosts call AddCircleAiMultiplayer + supply an IMultiplayerPeerIdentity
// (or use the GuestPeerIdentity default).

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CircleAI.Hosting.Multiplayer;

public static class MultiplayerServiceCollectionExtensions
{
    /// <summary>
    /// (3.2.0) Register the SignalR multiplayer hub. Adds a default
    /// <see cref="GuestPeerIdentity"/> if none has been registered.
    /// Hosts wanting real auth call <c>AddScoped&lt;IMultiplayerPeerIdentity, TheirImpl&gt;</c>
    /// before this and the registration here will skip.
    /// </summary>
    public static IServiceCollection AddCircleAiMultiplayer(this IServiceCollection services)
    {
        services.AddSignalR();
        services.TryAddScoped<IMultiplayerPeerIdentity>(_ => new GuestPeerIdentity());
        return services;
    }
}
