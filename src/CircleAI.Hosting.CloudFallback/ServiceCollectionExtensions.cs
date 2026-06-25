// ServiceCollectionExtensions.cs
//
// (3.2.0) DI helpers — register each cloud generator with its
// HttpClient and options factory. Mirrors Concierge's working
// AddOpenAiChat / AddAnthropicChat / AddGeminiChat extensions.

using System;
using CircleAI.Inference;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Hosting.CloudFallback;

public static class CloudFallbackServiceCollectionExtensions
{
    /// <summary>Provider id constants used for keyed DI lookup.</summary>
    public static class ProviderIds
    {
        public const string OpenAi    = "openai";
        public const string Anthropic = "anthropic";
        public const string Gemini    = "gemini";
        public const string Groq      = "groq";
        public const string Cerebras  = "cerebras";
        public const string Together  = "together";
        public const string DeepSeek  = "deepseek";
    }

    /// <summary>
    /// (3.2.0) Register <see cref="OpenAiChatGenerator"/> with its
    /// HttpClient bound to <see cref="OpenAiChatOptions.BaseAddress"/>.
    /// The host owns the options factory so the API key never lives in
    /// source — usually it comes from <c>IConfiguration</c>.
    /// </summary>
    public static IServiceCollection AddOpenAiChatGenerator(
        this IServiceCollection                services,
        Func<IServiceProvider, OpenAiChatOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<OpenAiChatGenerator>((sp, client) =>
        {
            var options = sp.GetRequiredService<OpenAiChatOptions>();
            client.BaseAddress = options.BaseAddress;
        });
        services.AddKeyedSingleton<IChatGenerator>(ProviderIds.OpenAi,
            (sp, _) => sp.GetRequiredService<OpenAiChatGenerator>());
        return services;
    }

    /// <summary>(3.2.0) Register <see cref="AnthropicChatGenerator"/>.</summary>
    public static IServiceCollection AddAnthropicChatGenerator(
        this IServiceCollection                  services,
        Func<IServiceProvider, AnthropicChatOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<AnthropicChatGenerator>((sp, client) =>
        {
            var options = sp.GetRequiredService<AnthropicChatOptions>();
            client.BaseAddress = options.BaseAddress;
        });
        services.AddKeyedSingleton<IChatGenerator>(ProviderIds.Anthropic,
            (sp, _) => sp.GetRequiredService<AnthropicChatGenerator>());
        return services;
    }

    /// <summary>(3.2.0) Register <see cref="GeminiChatGenerator"/>.</summary>
    public static IServiceCollection AddGeminiChatGenerator(
        this IServiceCollection                services,
        Func<IServiceProvider, GeminiChatOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<GeminiChatGenerator>((sp, client) =>
        {
            var options = sp.GetRequiredService<GeminiChatOptions>();
            client.BaseAddress = options.BaseAddress;
        });
        services.AddKeyedSingleton<IChatGenerator>(ProviderIds.Gemini,
            (sp, _) => sp.GetRequiredService<GeminiChatGenerator>());
        return services;
    }

    /// <summary>
    /// (3.2.0) Register a <see cref="CloudFallbackChain"/> built from a
    /// host-supplied factory. The factory receives the
    /// <see cref="IServiceProvider"/> so it can pull individual
    /// generators via <c>GetKeyedService&lt;IChatGenerator&gt;("openai")</c>
    /// and assemble the order it wants (e.g. on-device first, OpenAI
    /// second, Anthropic third).
    /// </summary>
    public static IServiceCollection AddCloudFallbackChain(
        this IServiceCollection                          services,
        Func<IServiceProvider, IEnumerable<IChatGenerator>> chainFactory)
    {
        services.AddSingleton(sp => new CloudFallbackChain(chainFactory(sp)));
        return services;
    }

    /// <summary>(3.3.0) Register Groq (OpenAI-compatible, very low latency).</summary>
    public static IServiceCollection AddGroqChatGenerator(
        this IServiceCollection              services,
        Func<IServiceProvider, GroqChatOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<GroqChatGenerator>((sp, c) =>
            c.BaseAddress = sp.GetRequiredService<GroqChatOptions>().BaseAddress);
        services.AddKeyedSingleton<IChatGenerator>(ProviderIds.Groq,
            (sp, _) => sp.GetRequiredService<GroqChatGenerator>());
        return services;
    }

    /// <summary>(3.3.0) Register Cerebras (OpenAI-compatible).</summary>
    public static IServiceCollection AddCerebrasChatGenerator(
        this IServiceCollection                  services,
        Func<IServiceProvider, CerebrasChatOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<CerebrasChatGenerator>((sp, c) =>
            c.BaseAddress = sp.GetRequiredService<CerebrasChatOptions>().BaseAddress);
        services.AddKeyedSingleton<IChatGenerator>(ProviderIds.Cerebras,
            (sp, _) => sp.GetRequiredService<CerebrasChatGenerator>());
        return services;
    }

    /// <summary>(3.3.0) Register Together AI (OpenAI-compatible).</summary>
    public static IServiceCollection AddTogetherChatGenerator(
        this IServiceCollection                  services,
        Func<IServiceProvider, TogetherChatOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<TogetherChatGenerator>((sp, c) =>
            c.BaseAddress = sp.GetRequiredService<TogetherChatOptions>().BaseAddress);
        services.AddKeyedSingleton<IChatGenerator>(ProviderIds.Together,
            (sp, _) => sp.GetRequiredService<TogetherChatGenerator>());
        return services;
    }

    /// <summary>(3.3.0) Register DeepSeek (OpenAI-compatible).</summary>
    public static IServiceCollection AddDeepSeekChatGenerator(
        this IServiceCollection                  services,
        Func<IServiceProvider, DeepSeekChatOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<DeepSeekChatGenerator>((sp, c) =>
            c.BaseAddress = sp.GetRequiredService<DeepSeekChatOptions>().BaseAddress);
        services.AddKeyedSingleton<IChatGenerator>(ProviderIds.DeepSeek,
            (sp, _) => sp.GetRequiredService<DeepSeekChatGenerator>());
        return services;
    }
}
