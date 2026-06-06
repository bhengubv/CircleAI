// ServiceCollectionExtensions.cs
//
// DI for the multimodal memory layer. After AddMultimodalMemory():
//   • IMultimodalMemoryStore         → InMemoryMultimodalMemoryStore
//   • IMultimodalCaptioner           → HeuristicMultimodalCaptioner
//                                       (added as the LAST captioner so any
//                                       host-registered richer captioners
//                                       take precedence)
//   • MultimodalMemoryIngester       → singleton wired over all captioners
//                                       + the store
//
// Wire a richer captioner BEFORE calling AddMultimodalMemory():
//   services.AddSingleton<IMultimodalCaptioner, KimiVlCaptioner>();
//   services.AddMultimodalMemory();

using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CircleAI.Memory.Multimodal;

/// <summary>DI registration for the multimodal memory pipeline.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Wires the multimodal memory store, the heuristic captioner fallback,
    /// and the ingester. Idempotent — host-provided richer captioners take
    /// precedence over the heuristic fallback.
    /// </summary>
    public static IServiceCollection AddMultimodalMemory(this IServiceCollection services)
    {
        System.ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IMultimodalMemoryStore, InMemoryMultimodalMemoryStore>();

        // Heuristic captioner is registered with TryAddEnumerable but in a way
        // that places it last in iteration order (TryAddEnumerable preserves
        // registration order). We use Add so it's always present alongside
        // any host-registered captioners.
        services.Add(ServiceDescriptor.Singleton<IMultimodalCaptioner, HeuristicMultimodalCaptioner>());

        services.TryAddSingleton<MultimodalMemoryIngester>(sp =>
        {
            // Order captioners: every non-heuristic FIRST, heuristic LAST.
            var all = sp.GetServices<IMultimodalCaptioner>().ToList();
            var rich = all.Where(c => c is not HeuristicMultimodalCaptioner).ToList();
            var fallback = all.OfType<HeuristicMultimodalCaptioner>().ToList();
            var ordered = rich.Concat(fallback).ToList();
            return new MultimodalMemoryIngester(
                ordered,
                sp.GetRequiredService<IMultimodalMemoryStore>());
        });

        return services;
    }
}
