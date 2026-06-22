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
}
