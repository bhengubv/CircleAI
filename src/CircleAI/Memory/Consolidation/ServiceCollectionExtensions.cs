// ServiceCollectionExtensions.cs
//
// DI wiring for the memory consolidation engine.
// After AddMemoryConsolidator():
//   • IMemoryConsolidator       → MemoryConsolidator
//   • IMemorySummarizer         → HeuristicSummarizer (host can replace)
//   • IDailyMemoryStore         → InMemoryDailyMemoryStore (host can replace)
//   • ISemanticMemoryStore      → InMemorySemanticMemoryStore (host can replace)
//   • IPersonaDeltaStore        → InMemoryPersonaDeltaStore (host can replace)
//   • ICoreMemoryStore          → InMemoryCoreMemoryStore (host can replace)
// The four consolidation-tier stores default to in-memory; SQLite-backed
// implementations land in a follow-up commit.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CircleAI.Memory.Consolidation;

/// <summary>
/// DI registration for the hierarchical memory consolidator.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Wires the consolidator, the default heuristic summariser, and
    /// in-memory implementations of the four consolidation-tier stores.
    /// Idempotent — uses <c>TryAdd</c> so a host that has already registered
    /// a custom summariser or store wins.
    /// </summary>
    public static IServiceCollection AddMemoryConsolidator(this IServiceCollection services)
    {
        System.ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IDailyMemoryStore, InMemoryDailyMemoryStore>();
        services.TryAddSingleton<ISemanticMemoryStore, InMemorySemanticMemoryStore>();
        services.TryAddSingleton<IPersonaDeltaStore, InMemoryPersonaDeltaStore>();
        services.TryAddSingleton<ICoreMemoryStore, InMemoryCoreMemoryStore>();
        services.TryAddSingleton<IMemorySummarizer, HeuristicSummarizer>();
        services.TryAddSingleton<MemoryConsolidationOptions>();

        services.TryAddSingleton<IMemoryConsolidator>(sp =>
            new MemoryConsolidator(
                sp.GetRequiredService<IEpisodicMemoryStore>(),
                sp.GetRequiredService<IDailyMemoryStore>(),
                sp.GetRequiredService<ISemanticMemoryStore>(),
                sp.GetRequiredService<IPersonaDeltaStore>(),
                sp.GetRequiredService<ICoreMemoryStore>(),
                sp.GetRequiredService<IPersonaStore>(),
                sp.GetRequiredService<IMemorySummarizer>(),
                sp.GetRequiredService<MemoryConsolidationOptions>()));

        return services;
    }
}
