// ──────────────────────────────────────────────────────────────────────────
// ServiceCollectionExtensions
//
// One-call wire-up. After:
//     services.AddCircleAiAetherMeshAdapter();
// the DI container resolves:
//     IAetherContext              → AetherMeshContextAdapter
//     IAetherTelemetry            → AetherMeshTelemetryAdapter
//     IAetherMeshAiProvider       → CircleAiAetherMeshAiProvider
//     AetherMeshDirectiveSink     → singleton (the CircleAI → mesh forwarder)
//
// The CircleAI-side ISecurityDirectiveConsumer (inbound directive store) is
// owned by CircleAI.Security.Aether's AddCircleAiMeshSecurity() — call both
// extensions when wiring a host that wants both directions of the pipe.
//
// Prerequisites in DI:
//   • AetherMesh.Extensibility.IAetherMeshTelemetry     (from AetherMesh.Core)
//   • AetherMesh.Extensibility.ISecurityDirectiveConsumer (mesh policy engine)
//   • CircleAI.Aether.IAetherIntelligence               (CircleAI's brain)
// ──────────────────────────────────────────────────────────────────────────

using AetherMesh.Extensibility;
using CircleAI.Aether;
using Microsoft.Extensions.DependencyInjection;
using MeshConsumer = AetherMesh.Extensibility.ISecurityDirectiveConsumer;

namespace CircleAI.AetherMesh;

/// <summary>
/// DI wiring for the CircleAI ↔ AetherMesh adapter family.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all CircleAI ↔ AetherMesh bridges in one call.
    /// Idempotent — every registration uses TryAdd semantics so it composes
    /// with hosts that already wired some of the pieces.
    /// </summary>
    /// <param name="services">The host's DI container.</param>
    /// <param name="minimumAetherMeshVersion">
    /// Minimum AetherMesh protocol version the host requires; null means any.
    /// </param>
    /// <param name="isEnabled">
    /// Whether AetherMesh should report as enabled in this process.
    /// Default true.
    /// </param>
    public static IServiceCollection AddCircleAiAetherMeshAdapter(
        this IServiceCollection services,
        Version? minimumAetherMeshVersion = null,
        bool isEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(services);

        // IAetherContext — presence + version
        services.AddSingleton<IAetherContext>(
            _ => new AetherMeshContextAdapter(minimumAetherMeshVersion, isEnabled));

        // IAetherTelemetry — fan AetherMesh events into CircleAI shape
        services.AddSingleton<IAetherTelemetry>(sp =>
            new AetherMeshTelemetryAdapter(sp.GetRequiredService<IAetherMeshTelemetry>()));

        // The CircleAI → mesh forwarder. Registered as the concrete type so it
        // does not claim the ISecurityDirectiveConsumer DI slot — that slot is
        // owned by MeshDirectiveStore (the inbound store) when the host also
        // calls AddCircleAiMeshSecurity().
        services.AddSingleton<AetherMeshDirectiveSink>(sp =>
            new AetherMeshDirectiveSink(sp.GetRequiredService<MeshConsumer>()));

        // AetherMesh's AI seat — plug CircleAI's brain in
        services.AddSingleton<IAetherMeshAiProvider>(sp =>
            new CircleAiAetherMeshAiProvider(sp.GetRequiredService<IAetherIntelligence>()));

        return services;
    }
}
