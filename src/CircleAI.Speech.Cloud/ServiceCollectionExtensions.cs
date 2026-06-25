// ServiceCollectionExtensions.cs
//
// (3.2.0) DI helpers — register Whisper recognizer + OpenAI TTS
// synthesizer with their HttpClient and options. Note: registering
// `services.AddSingleton(optionsFactory)` would register the Func<>
// itself, not the options. We unwrap via `sp => optionsFactory(sp)` so
// the constructor's OpenAiVoiceOptions parameter is satisfied.

using System;
using System.Collections.Generic;
using CircleAI.Speech;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Speech.Cloud;

public static class SpeechCloudServiceCollectionExtensions
{
    /// <summary>
    /// (3.2.0) Register <see cref="OpenAiSpeechRecognizer"/> (Whisper)
    /// as the <see cref="ISpeechRecognizer"/> singleton. Options factory
    /// owns API-key sourcing.
    /// </summary>
    public static IServiceCollection AddOpenAiSpeechRecognizer(
        this IServiceCollection                  services,
        Func<IServiceProvider, OpenAiVoiceOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<OpenAiSpeechRecognizer>((sp, client) =>
        {
            var options = sp.GetRequiredService<OpenAiVoiceOptions>();
            client.BaseAddress = options.BaseAddress;
        });
        services.AddSingleton<ISpeechRecognizer>(sp =>
            sp.GetRequiredService<OpenAiSpeechRecognizer>());
        return services;
    }

    /// <summary>
    /// (3.2.0) Register <see cref="OpenAiSpeechSynthesizer"/> (TTS) as
    /// the <see cref="ISpeechSynthesizer"/> singleton.
    /// </summary>
    public static IServiceCollection AddOpenAiSpeechSynthesizer(
        this IServiceCollection                  services,
        Func<IServiceProvider, OpenAiVoiceOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        // If OpenAiVoiceOptions is already registered (e.g. recognizer
        // was added with the same options) we let the first registration
        // win — DI will skip duplicates with the same impl key.
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<OpenAiSpeechSynthesizer>((sp, client) =>
        {
            var options = sp.GetRequiredService<OpenAiVoiceOptions>();
            client.BaseAddress = options.BaseAddress;
        });
        services.AddSingleton<ISpeechSynthesizer>(sp =>
            sp.GetRequiredService<OpenAiSpeechSynthesizer>());
        return services;
    }

    /// <summary>
    /// (3.2.0) Register a <see cref="KeywordVoiceIntentRouter"/> with a
    /// host-supplied list of intents.
    /// </summary>
    public static IServiceCollection AddKeywordVoiceIntentRouter(
        this IServiceCollection            services,
        IEnumerable<VoiceIntent>           intents,
        string                             fallbackIntentName = "ask-ai")
    {
        ArgumentNullException.ThrowIfNull(intents);
        services.AddSingleton<IVoiceIntentRouter>(
            _ => new KeywordVoiceIntentRouter(intents, fallbackIntentName));
        return services;
    }

    // ====================================================================
    // (3.3.0) Five additional cloud STT backends — one of these registers
    // as ISpeechRecognizer (last writer wins) when called solo. Compose
    // with AddOpenAiSpeechRecognizer + a router for multi-vendor failover.
    // ====================================================================

    /// <summary>(3.3.0) Register the Deepgram STT recognizer.</summary>
    public static IServiceCollection AddDeepgramSpeechRecognizer(
        this IServiceCollection                 services,
        Func<IServiceProvider, DeepgramOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<DeepgramSpeechRecognizer>((sp, c) =>
            c.BaseAddress = sp.GetRequiredService<DeepgramOptions>().BaseAddress);
        services.AddSingleton<ISpeechRecognizer>(sp => sp.GetRequiredService<DeepgramSpeechRecognizer>());
        return services;
    }

    /// <summary>(3.3.0) Register the AssemblyAI STT recognizer.</summary>
    public static IServiceCollection AddAssemblyAiSpeechRecognizer(
        this IServiceCollection                  services,
        Func<IServiceProvider, AssemblyAiOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<AssemblyAiSpeechRecognizer>((sp, c) =>
            c.BaseAddress = sp.GetRequiredService<AssemblyAiOptions>().BaseAddress);
        services.AddSingleton<ISpeechRecognizer>(sp => sp.GetRequiredService<AssemblyAiSpeechRecognizer>());
        return services;
    }

    /// <summary>(3.3.0) Register the Google Cloud STT recognizer.</summary>
    public static IServiceCollection AddGoogleSpeechRecognizer(
        this IServiceCollection                    services,
        Func<IServiceProvider, GoogleSpeechOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<GoogleSpeechRecognizer>((sp, c) =>
            c.BaseAddress = sp.GetRequiredService<GoogleSpeechOptions>().BaseAddress);
        services.AddSingleton<ISpeechRecognizer>(sp => sp.GetRequiredService<GoogleSpeechRecognizer>());
        return services;
    }

    /// <summary>(3.3.0) Register the Azure STT recognizer.</summary>
    public static IServiceCollection AddAzureSpeechRecognizer(
        this IServiceCollection                   services,
        Func<IServiceProvider, AzureSpeechOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<AzureSpeechRecognizer>((sp, c) =>
        {
            var o = sp.GetRequiredService<AzureSpeechOptions>();
            if (o.BaseAddress is not null) c.BaseAddress = o.BaseAddress;
        });
        services.AddSingleton<ISpeechRecognizer>(sp => sp.GetRequiredService<AzureSpeechRecognizer>());
        return services;
    }

    /// <summary>(3.3.0) Register the Cartesia STT recognizer.</summary>
    public static IServiceCollection AddCartesiaSpeechRecognizer(
        this IServiceCollection                   services,
        Func<IServiceProvider, CartesiaSttOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<CartesiaSpeechRecognizer>((sp, c) =>
            c.BaseAddress = sp.GetRequiredService<CartesiaSttOptions>().BaseAddress);
        services.AddSingleton<ISpeechRecognizer>(sp => sp.GetRequiredService<CartesiaSpeechRecognizer>());
        return services;
    }

    // ====================================================================
    // (3.3.0) Six additional cloud TTS backends. Combine with the existing
    // OpenAI TTS to make 7. Last-write-wins on ISpeechSynthesizer.
    // ====================================================================

    /// <summary>(3.3.0) Register the ElevenLabs TTS synthesizer.</summary>
    public static IServiceCollection AddElevenLabsSpeechSynthesizer(
        this IServiceCollection                  services,
        Func<IServiceProvider, ElevenLabsOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<ElevenLabsSpeechSynthesizer>((sp, c) =>
            c.BaseAddress = sp.GetRequiredService<ElevenLabsOptions>().BaseAddress);
        services.AddSingleton<ISpeechSynthesizer>(sp => sp.GetRequiredService<ElevenLabsSpeechSynthesizer>());
        return services;
    }

    /// <summary>(3.3.0) Register the Cartesia Sonic TTS synthesizer.</summary>
    public static IServiceCollection AddCartesiaSpeechSynthesizer(
        this IServiceCollection                   services,
        Func<IServiceProvider, CartesiaTtsOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<CartesiaSpeechSynthesizer>((sp, c) =>
            c.BaseAddress = sp.GetRequiredService<CartesiaTtsOptions>().BaseAddress);
        services.AddSingleton<ISpeechSynthesizer>(sp => sp.GetRequiredService<CartesiaSpeechSynthesizer>());
        return services;
    }

    /// <summary>(3.3.0) Register the Deepgram Aura TTS synthesizer.</summary>
    public static IServiceCollection AddDeepgramSpeechSynthesizer(
        this IServiceCollection                   services,
        Func<IServiceProvider, DeepgramTtsOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<DeepgramSpeechSynthesizer>((sp, c) =>
            c.BaseAddress = sp.GetRequiredService<DeepgramTtsOptions>().BaseAddress);
        services.AddSingleton<ISpeechSynthesizer>(sp => sp.GetRequiredService<DeepgramSpeechSynthesizer>());
        return services;
    }

    /// <summary>(3.3.0) Register the Azure TTS synthesizer.</summary>
    public static IServiceCollection AddAzureSpeechSynthesizer(
        this IServiceCollection                services,
        Func<IServiceProvider, AzureTtsOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<AzureSpeechSynthesizer>((sp, c) =>
        {
            var o = sp.GetRequiredService<AzureTtsOptions>();
            if (o.BaseAddress is not null) c.BaseAddress = o.BaseAddress;
        });
        services.AddSingleton<ISpeechSynthesizer>(sp => sp.GetRequiredService<AzureSpeechSynthesizer>());
        return services;
    }

    /// <summary>(3.3.0) Register the Google Cloud TTS synthesizer.</summary>
    public static IServiceCollection AddGoogleSpeechSynthesizer(
        this IServiceCollection                services,
        Func<IServiceProvider, GoogleTtsOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<GoogleSpeechSynthesizer>((sp, c) =>
            c.BaseAddress = sp.GetRequiredService<GoogleTtsOptions>().BaseAddress);
        services.AddSingleton<ISpeechSynthesizer>(sp => sp.GetRequiredService<GoogleSpeechSynthesizer>());
        return services;
    }

    /// <summary>(3.3.0) Register the Play.HT TTS synthesizer.</summary>
    public static IServiceCollection AddPlayHtSpeechSynthesizer(
        this IServiceCollection             services,
        Func<IServiceProvider, PlayHtOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<PlayHtSpeechSynthesizer>((sp, c) =>
            c.BaseAddress = sp.GetRequiredService<PlayHtOptions>().BaseAddress);
        services.AddSingleton<ISpeechSynthesizer>(sp => sp.GetRequiredService<PlayHtSpeechSynthesizer>());
        return services;
    }
}
