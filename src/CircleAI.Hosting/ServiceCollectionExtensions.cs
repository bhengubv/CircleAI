// ServiceCollectionExtensions.cs
//
// DI surface for Circle AI services. Provides AddCircleAI entry points
// that register AIOptions, AIService (as both singleton and IAIService),
// IChatGenerator, and optional subsystems (RAG) based on the caller's
// configuration.
//
// AIOptions uses init-only setters, so callers construct it via object
// initializers and pass the finished instance (or a factory that builds one).

using System;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using CircleAI.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CircleAI.Hosting;

/// <summary>
/// Extension methods for registering Circle AI services into a
/// <see cref="IServiceCollection"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Circle AI services into the DI container using a factory
    /// that produces the <see cref="AIOptions"/> configuration.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="optionsFactory">
    /// Factory that returns a fully configured <see cref="AIOptions"/> instance.
    /// Called once when the DI container first resolves <see cref="AIOptions"/>.
    /// </param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <example>
    /// <code>
    /// services.AddCircleAI(() =&gt; new AIOptions
    /// {
    ///     ModelPath = "/path/to/qwen3-0.6b.gguf",
    ///     SystemPrompt = "You are a helpful assistant.",
    ///     ContextSize = 8192,
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddCircleAI(
        this IServiceCollection services,
        Func<AIOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionsFactory);

        // ---------------------------------------------------------------
        // AIOptions — singleton, built by the caller's factory
        // ---------------------------------------------------------------
        services.AddSingleton(_ =>
        {
            var options = optionsFactory();
            return options ?? throw new InvalidOperationException(
                "AIOptions factory returned null.");
        });

        RegisterCoreServices(services);
        return services;
    }

    /// <summary>
    /// Registers Circle AI services into the DI container using a
    /// pre-built <see cref="AIOptions"/> instance.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="options">
    /// A fully configured <see cref="AIOptions"/> instance. Registered as a
    /// singleton directly.
    /// </param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <example>
    /// <code>
    /// services.AddCircleAI(new AIOptions
    /// {
    ///     ModelPath = "/path/to/qwen3-0.6b.gguf",
    ///     SystemPrompt = "You are a helpful assistant.",
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddCircleAI(
        this IServiceCollection services,
        AIOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        RegisterCoreServices(services);
        return services;
    }

    /// <summary>
    /// Registers Circle AI services with default <see cref="AIOptions"/>.
    /// The caller must ensure <see cref="AIOptions.ModelId"/> resolves via a
    /// registered <see cref="IModelLoader"/>.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddCircleAI(this IServiceCollection services)
        => services.AddCircleAI(new AIOptions());

    /// <summary>
    /// Registers Circle AI services with a specific model file path.
    /// All other <see cref="AIOptions"/> properties use their defaults.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="modelPath">Absolute path to a GGUF model file.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddCircleAI(
        this IServiceCollection services,
        string modelPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelPath);
        return services.AddCircleAI(new AIOptions { ModelPath = modelPath });
    }

    // ------------------------------------------------------------------
    // Private — shared service registrations
    // ------------------------------------------------------------------

    /// <summary>
    /// Registers all core services that depend on <see cref="AIOptions"/>
    /// being already registered in the container.
    /// </summary>
    private static void RegisterCoreServices(IServiceCollection services)
    {
        // ---------------------------------------------------------------
        // IDeviceContext — DefaultDeviceContext.Instance probes the host
        // (RAM, storage, connectivity, locale, timezone). Consumer can
        // override either by supplying AIOptions.DeviceContext directly
        // or by registering their own IDeviceContext before AddCircleAI.
        // ---------------------------------------------------------------
        services.TryAddSingleton<IDeviceContext>(sp =>
        {
            var opts = sp.GetRequiredService<AIOptions>();
            return opts.DeviceContext ?? DefaultDeviceContext.Instance;
        });

        // ---------------------------------------------------------------
        // ICatalogSignatureVerifier — fail-closed Null verifier by default.
        // Consumer wires Ed25519 verification by registering their own
        // before AddCircleAI.
        // ---------------------------------------------------------------
        services.TryAddSingleton<ICatalogSignatureVerifier>(_ => NullCatalogSignatureVerifier.Instance);

        // ---------------------------------------------------------------
        // ModelRegistryService — shared singleton. When AIOptions.CatalogClient
        // is supplied, the registry primes from its disk cache at construction.
        // ---------------------------------------------------------------
        services.TryAddSingleton<ModelRegistryService>(sp =>
        {
            var opts = sp.GetRequiredService<AIOptions>();
            return new ModelRegistryService(opts.CatalogClient, registryUrl: null);
        });

        // ---------------------------------------------------------------
        // IModelSelector — DeviceAwareModelSelector reads the registry,
        // filters by capability + device fit, ranks by QualityRank.
        // ---------------------------------------------------------------
        services.TryAddSingleton<IModelSelector>(sp =>
            new DeviceAwareModelSelector(sp.GetRequiredService<ModelRegistryService>()));

        // ---------------------------------------------------------------
        // IPromptTemplateEngine — Scriban-backed Jinja2 renderer. Reads
        // each model's chat_template from its tokenizer_config.json so
        // the SDK never hardcodes ChatML format.
        // ---------------------------------------------------------------
        services.TryAddSingleton<IPromptTemplateEngine>(_ => new PromptTemplateEngine());

        // ---------------------------------------------------------------
        // IChatGenerator — vision-capable Kimi-VL when RequiredCapabilities
        // declares Vision, otherwise QwenTextGenerator. Both share the
        // PromptTemplateEngine (catalog-driven chat_template). Context
        // size derives from device tier when AIOptions.ContextSize is null.
        // ---------------------------------------------------------------
        services.AddSingleton<IChatGenerator>(sp =>
        {
            var opts            = sp.GetRequiredService<AIOptions>();
            var modelPath       = ResolveModelPath(opts, sp);
            var templateEngine  = sp.GetService<IPromptTemplateEngine>();
            var deviceCtx       = sp.GetService<IDeviceContext>();
            var contextSize     = (uint)ResolveContextSize(opts, deviceCtx);

            if (opts.RequiredCapabilities.HasFlag(ChatCapability.Vision))
            {
                return new KimiVlGenerator(
                    modelPath,
                    contextSize:    contextSize,
                    threads:        opts.ThreadCount,
                    templateEngine: templateEngine);
            }

            return new QwenTextGenerator(
                modelPath,
                contextSize:    contextSize,
                threads:        opts.ThreadCount,
                templateEngine: templateEngine);
        });

        // ---------------------------------------------------------------
        // AIService — singleton, also exposed as IAIService. Receives the
        // selector so it can auto-resolve ModelId at StartAsync time when
        // the consumer leaves AIOptions.ModelId null.
        // ---------------------------------------------------------------
        services.AddSingleton<AIService>(sp =>
        {
            var opts          = sp.GetRequiredService<AIOptions>();
            var modelLoader   = sp.GetService<IModelLoader>();
            var modelSelector = sp.GetService<IModelSelector>();
            var templateEngine= sp.GetService<IPromptTemplateEngine>();
            var deviceCtx     = sp.GetService<IDeviceContext>();
            var logger        = sp.GetService<ILogger<AIService>>();

            // Generator factory so AIService can lazy-load via DI with the
            // resolved model path. Context size resolved per-call so a
            // device tier change between Start cycles is honoured.
            IChatGenerator GeneratorFactory(string path) =>
                new QwenTextGenerator(
                    path,
                    contextSize:    (uint)ResolveContextSize(opts, deviceCtx),
                    threads:        opts.ThreadCount,
                    templateEngine: templateEngine);

            return new AIService(opts, modelLoader, GeneratorFactory, modelSelector, logger);
        });
        services.AddSingleton<IAIService>(sp => sp.GetRequiredService<AIService>());

        // ---------------------------------------------------------------
        // RagContextBuilder — always resolvable; uses episodic memory when
        // configured, falls back to an empty in-memory store otherwise.
        // ---------------------------------------------------------------
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<AIOptions>();

            // Caller-supplied builder takes precedence.
            if (opts.RagBuilder is not null)
                return opts.RagBuilder;

            // Build from episodic store if present.
            if (opts.EpisodicMemory is not null)
                return new RagContextBuilder(opts.EpisodicMemory, embedder: null, topK: opts.RagTopK);

            // Fallback: in-memory store so RagContextBuilder is always resolvable.
            // CIRCLEAI_MEM_CAP_001 gates the store's 1000-entry FIFO default
            // on the public surface; this hosting fallback is deliberate and
            // documented, so the suppression is narrowly scoped to the call.
#pragma warning disable CIRCLEAI_MEM_CAP_001
            return new RagContextBuilder(new InMemoryEpisodicStore());
#pragma warning restore CIRCLEAI_MEM_CAP_001
        });
    }

    // ------------------------------------------------------------------
    // Private helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Resolves the model path from <see cref="AIOptions.ModelPath"/> (explicit)
    /// or falls back to <see cref="IModelLoader.GetModelPath"/> using
    /// <see cref="AIOptions.ModelId"/>. Returns a non-null path or throws.
    /// When both are null AIService takes over selector-driven resolution at
    /// StartAsync time, so this helper is only invoked for the standalone
    /// IChatGenerator registration (which needs a path up front).
    /// </summary>
    private static string ResolveModelPath(AIOptions opts, IServiceProvider sp)
    {
        // Explicit path takes precedence.
        if (!string.IsNullOrWhiteSpace(opts.ModelPath))
            return opts.ModelPath!;

        // Pinned ModelId via loader.
        var loader = sp.GetService<IModelLoader>();
        if (loader is not null && !string.IsNullOrWhiteSpace(opts.ModelId))
        {
            var path = loader.GetModelPath(opts.ModelId);
            if (!string.IsNullOrEmpty(path))
                return path;
        }

        // Auto-select via IModelSelector (preserves SDK-knows-nothing default).
        var selector = sp.GetService<IModelSelector>();
        if (selector is not null && loader is not null)
        {
            var deviceCtx = sp.GetService<IDeviceContext>() ?? DefaultDeviceContext.Instance;
            var probe     = deviceCtx is DefaultDeviceContext ddc
                ? ddc.BuildProbe()
                : DeviceProbe.Snapshot();
            var selection = selector.BestFit(probe, opts.RequiredCapabilities);
            var path      = loader.GetModelPath(selection.ModelId);
            if (!string.IsNullOrEmpty(path))
                return path;
        }

        throw new InvalidOperationException(
            "Cannot resolve model path. Set AIOptions.ModelPath, pin AIOptions.ModelId " +
            "with a registered IModelLoader, or rely on the default IModelSelector + " +
            "IModelLoader pair (registered by AddCircleAI).");
    }

    /// <summary>
    /// Resolves the context window: explicit <see cref="AIOptions.ContextSize"/>
    /// wins; otherwise falls back to <see cref="DeviceTierDefaults.ContextWindow"/>
    /// using the registered <see cref="IDeviceContext"/>.
    /// </summary>
    private static int ResolveContextSize(AIOptions opts, IDeviceContext? deviceCtx)
    {
        if (opts.ContextSize is int explicitSize and > 0)
            return explicitSize;

        var probe = deviceCtx is DefaultDeviceContext ddc
            ? ddc.BuildProbe()
            : DeviceProbe.Snapshot();
        return DeviceTierDefaults.ContextWindow(probe.Classify());
    }
}
