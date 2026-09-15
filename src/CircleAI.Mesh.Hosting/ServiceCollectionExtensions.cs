// ServiceCollectionExtensions.cs
//
// One call that turns a borrow-only node into one that also SERVES.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CircleAI.Mesh.Hosting;

/// <summary>DI wiring for the serve-side offload adapter.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Replace the borrow-only <see cref="NullLocalInferenceFallback"/> that
    /// <c>AddCircleAiMeshOffload</c> registers with <see cref="BridgeLocalInferenceFallback"/>,
    /// so this node answers inbound peer requests through its loaded model.
    /// </summary>
    /// <remarks>
    /// Call AFTER <c>AddCircleAiMeshOffload</c>. That method uses
    /// <c>TryAddSingleton</c> for the fallback (first-registration-wins), so this
    /// removes the null default first and then registers the real one — order
    /// between the two calls no longer matters. The host must also register an
    /// <see cref="CircleAI.Hosting.InferenceBridge.IInferenceBridge"/>.
    /// </remarks>
    public static IServiceCollection AddBrainInferenceFallback(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.RemoveAll<ILocalInferenceFallback>();
        services.AddSingleton<ILocalInferenceFallback, BridgeLocalInferenceFallback>();
        return services;
    }
}
