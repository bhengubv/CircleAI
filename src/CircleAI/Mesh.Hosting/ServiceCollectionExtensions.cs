// ServiceCollectionExtensions.cs
//
// One call that turns a borrow-only node into one that also SERVES.

using CircleAI.Assistant;   // IOffloadGateway, OffloadConsent
using CircleAI.Core;        // ModelModality
using CircleAI.Hosting;     // IAIService
using CircleAI.Inference;   // SelectionQuality
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

    /// <summary>
    /// Wire the borrow side: register <see cref="MeshOffloadGateway"/> as the
    /// <see cref="CircleAI.Assistant.IOffloadGateway"/> the shared turn consults.
    /// <paramref name="consent"/> is read afresh each turn (on/off + chosen node),
    /// so a change in Settings takes effect without a restart. Requires
    /// <c>AddCircleAiMeshOffload</c> (the client) and a registered
    /// <see cref="CircleAI.Hosting.IAIService"/>.
    /// </summary>
    public static IServiceCollection AddMeshOffloadGateway(
        this IServiceCollection services,
        Func<IServiceProvider, OffloadConsent> consent,
        Action<MeshOffloadGatewayOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(consent);

        var options = new MeshOffloadGatewayOptions();
        configure?.Invoke(options);

        services.RemoveAll<IOffloadGateway>();
        services.AddSingleton<IOffloadGateway>(sp =>
        {
            var brain = sp.GetRequiredService<IAIService>();
            return new MeshOffloadGateway(
                sp.GetRequiredService<IMeshOffloadClient>(),
                // Product decision, kept out of the app: this phone can serve chat
                // when PlanFor(Chat) fits well (Good) or at least runs (BelowFloor);
                // the model it would use rides along so the peer knows what was asked.
                () =>
                {
                    var plan = brain.PlanFor(ModelModality.Chat);
                    var canServe = plan.Quality is SelectionQuality.Good or SelectionQuality.BelowFloor;
                    return new LocalChatStatus(canServe, plan.Model?.ModelId);
                },
                () => consent(sp),
                options);
        });
        return services;
    }
}
