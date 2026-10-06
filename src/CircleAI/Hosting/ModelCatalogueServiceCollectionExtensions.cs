// ModelCatalogueServiceCollectionExtensions.cs
//
// One call composes the runtime model-catalogue subsystem: the SQLite catalogue,
// the device assessor, the applier (which takes rows from any trusted source —
// mesh peer, self-discovery, side-load — with no central signature), and the
// Wolverine bridge that routes catalogue failures into the self-heal loop.
// Thin-client rule holds — all the logic is in the product; the app just calls
// this and supplies the DB path and the engines it ships.
//
// Conservative by default: CatalogModelSelector is registered as a resolvable
// type but does NOT replace the live IModelSelector unless useAsPrimarySelector
// is set — swapping the selector that loads the model is best flipped once the
// on-device demo confirms the catalogue is seeded, assessed, and selecting.

using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Hosting.SelfHealing;
using CircleAI.Inference;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CircleAI.Hosting;

/// <summary>DI composition for the runtime model catalogue.</summary>
public static class ModelCatalogueServiceCollectionExtensions
{
    /// <summary>
    /// Register the runtime model-catalogue subsystem.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="catalogueDbPath">
    /// Filesystem path for the catalogue SQLite database (a <c>Data Source=</c>
    /// prefix is added). On device, a file under the app's data directory.
    /// </param>
    /// <param name="shippedEngines">
    /// The inference engines this build ships natively. Defaults to
    /// <see cref="ModelEngine.Mnn"/> only — a GGUF model then stays
    /// <c>compatible = 0</c> until a GGUF engine is shipped.
    /// </param>
    /// <param name="useAsPrimarySelector">
    /// When <c>true</c>, <see cref="CatalogModelSelector"/> also becomes the
    /// registered <see cref="IModelSelector"/> (last-wins). Default <c>false</c> —
    /// the catalogue is wired and logging flows, but the live selector is left
    /// unchanged until the on-device demo confirms selection.
    /// </param>
    public static IServiceCollection AddModelCatalogue(
        this IServiceCollection services,
        string catalogueDbPath,
        IEnumerable<ModelEngine>? shippedEngines = null,
        bool useAsPrimarySelector = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogueDbPath);
        var engines = (shippedEngines ?? new[] { ModelEngine.Mnn }).ToArray();

        services.TryAddSingleton<IModelCatalog>(_ =>
            new SqliteModelCatalog("Data Source=" + catalogueDbPath));

        services.TryAddSingleton<IModelAssessor>(sp =>
            new DeviceModelAssessor(sp.GetRequiredService<IModelCatalog>(), engines));

        // The Wolverine bridge: catalogue failures → the self-heal loop when the
        // host has wired one, a no-op otherwise. See [[feedback-logging-not-thin]].
        services.TryAddSingleton<IModelCatalogObserver>(sp =>
        {
            var healer = sp.GetService<ISelfHealer>();
            return healer is null
                ? (IModelCatalogObserver)NullModelCatalogObserver.Instance
                : new SelfHealingCatalogObserver(healer);
        });

        services.TryAddSingleton(sp => new CatalogueFeedWriter(
            sp.GetRequiredService<IModelCatalog>(),
            sp.GetRequiredService<IModelAssessor>(),
            sp.GetService<IModelCatalogObserver>()));

        // The sink that applies arrivals from ANY connection (internet refresh +
        // aethernet offer). Attach() it at startup; a fresh probe per apply.
        services.TryAddSingleton(sp => new CatalogueUpdater(
            sp.GetRequiredService<CatalogueFeedWriter>(),
            () => DeviceProbe.Snapshot()));

        // A SECOND internet discovery host (HuggingFace) so discovery is not
        // hostage to one API — kick RefreshAsync on a cadence; it self-throttles.
        services.TryAddSingleton(sp => new HuggingFaceCatalogClient(
            new HuggingFaceCatalogOptions(),
            httpClient: null,
            logger: sp.GetService<ILogger<HuggingFaceCatalogClient>>()));

        services.TryAddSingleton(sp => new CatalogModelSelector(sp.GetRequiredService<IModelCatalog>()));
        if (useAsPrimarySelector)
            services.AddSingleton<IModelSelector>(sp => sp.GetRequiredService<CatalogModelSelector>());

        return services;
    }
}
