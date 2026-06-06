// ──────────────────────────────────────────────────────────────────────────
// ServiceCollectionExtensions — CircleAI.Security.Aether
//
// Wires the mesh-directive subscription pipeline:
//
//   AetherMesh issues SecurityDirective
//     → (CircleAI.Aether.AetherMesh adapter translates)
//     → CircleAI.Aether.ISecurityDirectiveConsumer
//     → MeshDirectiveStore (records + expires)
//     → MeshSecurityGate (queries: "is this user blocked?")
//
// After AddCircleAiMeshSecurity():
//   • MeshDirectiveStore is the registered ISecurityDirectiveConsumer
//   • MeshSecurityGate is available for chat / API / moderation code
//   • Both are singletons (state is process-wide)
// ──────────────────────────────────────────────────────────────────────────

using CircleAI.Aether;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CircleAI.Security.AetherMesh;

/// <summary>
/// DI wiring for the CircleAI mesh-security directive pipeline.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="MeshDirectiveStore"/> as the sink for
    /// CircleAI.Aether.<see cref="ISecurityDirectiveConsumer"/> notifications,
    /// plus <see cref="MeshSecurityGate"/> as the read-only query view.
    /// Idempotent — uses TryAdd semantics so re-wiring is safe.
    /// </summary>
    public static IServiceCollection AddCircleAiMeshSecurity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<MeshDirectiveStore>();
        services.TryAddSingleton<MeshSecurityGate>();

        // The store IS the directive sink. Registering it under both faces
        // means CircleAI.Aether-aware code can resolve either type and
        // observe the same state.
        services.TryAddSingleton<ISecurityDirectiveConsumer>(
            sp => sp.GetRequiredService<MeshDirectiveStore>());

        return services;
    }
}
