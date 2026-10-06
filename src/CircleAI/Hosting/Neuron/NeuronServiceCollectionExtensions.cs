// NeuronServiceCollectionExtensions.cs
//
// DI surface for the Neuron. Composes AddCircleAI (brain + selector + generator
// + memory) and layers the host-neutral NeuronNode plus a warm-on-start loader
// on top. A host that wants the concierge / two-slot behaviour sets
// AIOptions.Router in the options factory it passes here.

using CircleAI.Hosting.Chat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CircleAI.Hosting.Neuron;

/// <summary>
/// Registers the Neuron (brain + host-neutral facade + warm loader) into a
/// <see cref="IServiceCollection"/>.
/// </summary>
public static class NeuronServiceCollectionExtensions
{
    /// <summary>
    /// Register the Neuron using an <see cref="AIOptions"/> factory. Set
    /// <see cref="AIOptions.Router"/> in the factory to enable the concierge +
    /// two-slot behaviour; leave it null for a single-slot generalist Neuron.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="optionsFactory">Factory that returns the configured options.</param>
    /// <param name="warmOnStart">
    /// When <c>true</c> (default), registers <see cref="BackgroundInferenceWorker"/>
    /// so the brain loads + warms on host start. Set <c>false</c> for hosts
    /// (e.g. MAUI) that own their own lifecycle.
    /// </param>
    public static IServiceCollection AddNeuron(
        this IServiceCollection services,
        Func<AIOptions> optionsFactory,
        bool warmOnStart = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionsFactory);

        services.AddCircleAI(optionsFactory);
        RegisterNeuron(services, warmOnStart);
        return services;
    }

    /// <summary>
    /// Register the Neuron using a pre-built <see cref="AIOptions"/> instance.
    /// </summary>
    public static IServiceCollection AddNeuron(
        this IServiceCollection services,
        AIOptions options,
        bool warmOnStart = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddCircleAI(options);
        RegisterNeuron(services, warmOnStart);
        return services;
    }

    private static void RegisterNeuron(IServiceCollection services, bool warmOnStart)
    {
        // The Neuron facade over the brain. Singleton, also exposed host-neutrally
        // as IChatRuntime so any UI / harness can drive it without seeing
        // CircleAI.Inference types.
        services.TryAddSingleton<NeuronNode>(sp =>
            new NeuronNode(sp.GetRequiredService<IAIService>()));
        services.TryAddSingleton<IChatRuntime>(sp => sp.GetRequiredService<NeuronNode>());

        // Warm the brain on host start (model load + optional warm-up).
        // BackgroundInferenceWorker already wraps IAIService.StartAsync.
        if (warmOnStart)
            services.AddHostedService<BackgroundInferenceWorker>();
    }
}
