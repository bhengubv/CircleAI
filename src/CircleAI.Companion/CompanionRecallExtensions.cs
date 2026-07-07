// CompanionRecallExtensions.cs
//
// (M1) Opt-in registration for fused associative recall. Off by default:
// without AddFusedRecall, the companion uses flat episodic recall — so fused
// vs flat is a clean A/B (the presence of the IRecall registration is the flag,
// matching the codebase idiom where every capability is an optional service).

using CircleAI.Domain;
using CircleAI.Hosting;
using CircleAI.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CircleAI.Companion;

/// <summary>(M1) DI helpers for wiring <see cref="FusedRecall"/> as <see cref="IRecall"/>.</summary>
public static class CompanionRecallExtensions
{
    /// <summary>
    /// Registers <see cref="IRecall"/> → <see cref="FusedRecall"/> over the episodic store
    /// and, when present, the graph store (<see cref="IHippoRagStore"/>). Opt-in: absent this
    /// call the companion falls back to flat episodic recall, so it doubles as the eval's
    /// fused-vs-flat switch.
    /// </summary>
    public static IServiceCollection AddFusedRecall(
        this IServiceCollection services, FusedRecallOptions? options = null)
    {
        services.TryAddSingleton<IRecall>(sp => new FusedRecall(
            sp.GetRequiredService<IEpisodicMemoryStore>(),
            sp.GetService<IHippoRagStore>(),
            options));
        return services;
    }

    /// <summary>
    /// (M1) Wire the whole graph-memory brain in one call: a durable knowledge graph,
    /// the connector that fills it from conversation (the model-based one when an
    /// <see cref="IAIService"/> is registered, else the model-free one), multi-hop
    /// recall over it, fused recall, and the background encoder.
    /// </summary>
    public static IServiceCollection AddCompanionMemoryGraph(
        this IServiceCollection services, string graphConnectionString)
    {
        services.TryAddSingleton(_ => new SqliteKnowledgeGraph(graphConnectionString));

        // Smart connector when a model is present; the model-free one otherwise.
        services.TryAddSingleton<IKnowledgeGraphExtractor>(sp =>
        {
            var ai = sp.GetService<IAIService>();
            return ai is not null
                ? new LlmKnowledgeGraphExtractor(ai)
                : new HeuristicKnowledgeGraphExtractor();
        });

        services.TryAddSingleton<IHippoRagStore>(sp =>
            new SqliteHippoRagStore(sp.GetRequiredService<SqliteKnowledgeGraph>()));

        // Memory integrity: attributed beliefs formed as the user talks.
        services.TryAddSingleton<IBeliefExtractor>(_ => new HeuristicBeliefExtractor());
        services.TryAddSingleton<SelfBeliefStore>();

        services.TryAddSingleton(sp => new CompanionMemoryEncoder(
            sp.GetRequiredService<IKnowledgeGraphExtractor>(),
            sp.GetRequiredService<SqliteKnowledgeGraph>(),
            sp.GetService<IBeliefExtractor>(),
            sp.GetService<SelfBeliefStore>()));

        return AddFusedRecall(services);
    }
}
