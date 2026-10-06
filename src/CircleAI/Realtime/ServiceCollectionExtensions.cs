// ServiceCollectionExtensions.cs
//
// (3.3.0) DI helper — register the null realtime service as the default.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CircleAI.Realtime;

public static class RealtimeServiceCollectionExtensions
{
    /// <summary>(3.3.0) Register <see cref="NullRealtimeService"/> as the default <see cref="IRealtimeService"/>.</summary>
    public static IServiceCollection AddCircleAiRealtime(this IServiceCollection services)
    {
        services.TryAddSingleton<IRealtimeService>(NullRealtimeService.Instance);
        return services;
    }
}
