// ──────────────────────────────────────────────────────────────────────────
// ServiceCollectionExtensions
//
// One-call wire-up. After:
//     services.AddCircleAiAetherNetAdapter();
// the DI container resolves:
//     IAetherContext              → AetherNetContextAdapter
//     IAetherTelemetry            → AetherNetTelemetryAdapter
//     IAetherNetAiProvider       → CircleAiAetherNetAiProvider
//     AetherNetDirectiveSink     → singleton (the CircleAI → mesh forwarder)
//
// The CircleAI-side ISecurityDirectiveConsumer (inbound directive store) is
// owned by CircleAI.Security.Aether's AddCircleAiMeshSecurity() — call both
// extensions when wiring a host that wants both directions of the pipe.
//
// Prerequisites in DI:
//   • AetherNet.Extensibility.IAetherNetTelemetry     (from AetherNet.Core)
//   • AetherNet.Extensibility.ISecurityDirectiveConsumer (mesh policy engine)
//   • CircleAI.Aether.IAetherIntelligence               (CircleAI's brain)
// ──────────────────────────────────────────────────────────────────────────

using AetherNet.Extensibility;
using CircleAI.Aether;
using Microsoft.Extensions.DependencyInjection;
using MeshConsumer = AetherNet.Extensibility.ISecurityDirectiveConsumer;

namespace CircleAI.AetherNet;

/// <summary>
/// DI wiring for the CircleAI ↔ AetherNet adapter family.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all CircleAI ↔ AetherNet bridges in one call.
    /// Idempotent — every registration uses TryAdd semantics so it composes
    /// with hosts that already wired some of the pieces.
    /// </summary>
    /// <param name="services">The host's DI container.</param>
    /// <param name="minimumAetherNetVersion">
    /// Minimum AetherNet protocol version the host requires; null means any.
    /// </param>
    /// <param name="isEnabled">
    /// Whether AetherNet should report as enabled in this process.
    /// Default true.
    /// </param>
    public static IServiceCollection AddCircleAiAetherNetAdapter(
        this IServiceCollection services,
        Version? minimumAetherNetVersion = null,
        bool isEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(services);

        // IAetherContext — presence + version
        services.AddSingleton<IAetherContext>(
            _ => new AetherNetContextAdapter(minimumAetherNetVersion, isEnabled));

        // IAetherTelemetry — fan AetherNet events into CircleAI shape
        services.AddSingleton<IAetherTelemetry>(sp =>
            new AetherNetTelemetryAdapter(sp.GetRequiredService<IAetherNetTelemetry>()));

        // The CircleAI → mesh forwarder. Registered as the concrete type so it
        // does not claim the ISecurityDirectiveConsumer DI slot — that slot is
        // owned by MeshDirectiveStore (the inbound store) when the host also
        // calls AddCircleAiMeshSecurity().
        services.AddSingleton<AetherNetDirectiveSink>(sp =>
            new AetherNetDirectiveSink(sp.GetRequiredService<MeshConsumer>()));

        // AetherNet's AI seat — plug CircleAI's brain in
        services.AddSingleton<IAetherNetAiProvider>(sp =>
            new CircleAiAetherNetAiProvider(sp.GetRequiredService<IAetherIntelligence>()));

        return services;
    }

    /// <summary>
    /// Adds the inbound directive bridge — when AetherNet publishes a
    /// SecurityDirective (locally authored or received over the mesh) it gets
    /// translated and forwarded to the CircleAI <see cref="global::CircleAI.Aether.ISecurityDirectiveConsumer"/>
    /// (typically <c>MeshDirectiveStore</c>).
    /// <para>
    /// Call this AFTER both <see cref="AddCircleAiAetherNetAdapter"/> and
    /// <c>AddCircleAiMeshSecurity()</c> have been called.
    /// </para>
    /// </summary>
    public static IServiceCollection AddCircleAiAetherNetInboundDirectives(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Concrete type — host wires it as an additional mesh-side consumer
        // (the AetherNet runtime fires every registered consumer when a
        // directive arrives). The constructor takes the CircleAI-side
        // ISecurityDirectiveConsumer; the type alias below avoids ambiguity
        // with the mesh-side interface of the same simple name.
        services.AddSingleton(sp =>
            new AetherNetInboundDirectiveBridge(
                sp.GetRequiredService<CircleAI.Aether.ISecurityDirectiveConsumer>()));

        // Also register under the mesh-side interface so AetherNet's DI
        // discovery picks it up. Sequence: outbound sink keeps its concrete
        // registration; inbound bridge claims the IAetherNet ISecurityDirective
        // Consumer slot for mesh → CircleAI delivery.
        services.AddSingleton<MeshConsumer>(sp =>
            sp.GetRequiredService<AetherNetInboundDirectiveBridge>());

        return services;
    }
}
