// ServiceCollectionExtensions.cs
//
// DI glue. Registers the fail-closed defaults (empty catalogue, disabled command
// runner) plus the real loop. The brain (IAIService) is expected to already be
// registered by the host's CircleAI.Hosting wiring; code search is optional.

using System;
using CircleAI.CodeUnderstanding; // ICodeSearch
using CircleAI.DevTools;          // ICodeEditor, FilesystemCodeEditor
using CircleAI.Hosting;           // IAIService
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CircleAI.CodeAgent;

/// <summary>DI registration for the on-device coding agent.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the coding agent and its fail-closed defaults. Uses
    /// <c>TryAdd</c> throughout, so a host that has already bound a real
    /// command runner, catalogue, editor, or search wins.
    /// </summary>
    /// <remarks>
    /// Prerequisite: <see cref="IAIService"/> must be registered (the host's
    /// CircleAI.Hosting setup does this). By default no command runner executes
    /// anything and no coding model is catalogued, so a freshly wired host is
    /// safe and honestly reports Unavailable until a real model is registered.
    /// </remarks>
    public static IServiceCollection AddCircleAICodeAgent(
        this IServiceCollection services,
        CodeAgentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ICodingModelCatalog>(EmptyCodingModelCatalog.Instance);
        services.TryAddSingleton<ICommandRunner>(DisabledCommandRunner.Instance);
        services.TryAddSingleton<ICodeEditor, FilesystemCodeEditor>();

        services.TryAddSingleton<ICodingCapabilityPlanner>(sp =>
            new CodingCapabilityPlanner(
                sp.GetService<ICodingModelCatalog>(),
                options?.Requirements));

        services.TryAddSingleton<ICodeAgent>(sp =>
            new CodeAgentLoop(
                sp.GetRequiredService<IAIService>(),
                sp.GetRequiredService<ICodeEditor>(),
                sp.GetRequiredService<ICommandRunner>(),
                sp.GetRequiredService<ICodingCapabilityPlanner>(),
                sp.GetService<ICodeSearch>(),
                options));

        return services;
    }
}
