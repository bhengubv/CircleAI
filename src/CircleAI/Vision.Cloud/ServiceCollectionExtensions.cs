// ServiceCollectionExtensions.cs
//
// (3.2.0) DI helpers for the OpenAI + Stability generators and the
// fallback chain. Mirrors CircleAI.Hosting.CloudFallback's keyed
// registration so a host can resolve a specific provider by id.

using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Vision.Cloud;

public static class VisionCloudServiceCollectionExtensions
{
    public static class GeneratorIds
    {
        public const string OpenAi    = "openai-images";
        public const string Stability = "stability";
    }

    /// <summary>(3.2.0) Register <see cref="OpenAiImageGenerator"/>.</summary>
    public static IServiceCollection AddOpenAiImageGenerator(
        this IServiceCollection                  services,
        Func<IServiceProvider, OpenAiImageOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<OpenAiImageGenerator>((sp, client) =>
        {
            var options = sp.GetRequiredService<OpenAiImageOptions>();
            client.BaseAddress = options.BaseAddress;
        });
        services.AddKeyedSingleton<IImageGenerator>(GeneratorIds.OpenAi,
            (sp, _) => sp.GetRequiredService<OpenAiImageGenerator>());
        return services;
    }

    /// <summary>(3.2.0) Register <see cref="StabilityImageGenerator"/>.</summary>
    public static IServiceCollection AddStabilityImageGenerator(
        this IServiceCollection                     services,
        Func<IServiceProvider, StabilityImageOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<StabilityImageGenerator>((sp, client) =>
        {
            var options = sp.GetRequiredService<StabilityImageOptions>();
            client.BaseAddress = options.BaseAddress;
        });
        services.AddKeyedSingleton<IImageGenerator>(GeneratorIds.Stability,
            (sp, _) => sp.GetRequiredService<StabilityImageGenerator>());
        return services;
    }

    /// <summary>
    /// (3.2.0) Register an <see cref="ImageGeneratorFallbackChain"/> built
    /// from a host-supplied factory that pulls keyed generators in the
    /// order it wants.
    /// </summary>
    public static IServiceCollection AddImageGeneratorFallbackChain(
        this IServiceCollection                            services,
        Func<IServiceProvider, IEnumerable<IImageGenerator>> chainFactory)
    {
        ArgumentNullException.ThrowIfNull(chainFactory);
        services.AddSingleton(sp => new ImageGeneratorFallbackChain(chainFactory(sp)));
        return services;
    }
}
