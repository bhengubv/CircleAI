// ServiceCollectionExtensions.cs
//
// (3.3.0) DI helpers — register each of the 5 vendor connectors.

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CircleAI.Realtime.Cloud;

public static class RealtimeCloudServiceCollectionExtensions
{
    private static void EnsureTransport(IServiceCollection services)
        => services.TryAddSingleton<IRealtimeTransportFactory>(NullRealtimeTransportFactory.Instance);

    public static IServiceCollection AddOpenAiRealtime(
        this IServiceCollection                       services,
        Func<IServiceProvider, OpenAiRealtimeOptions>  optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        EnsureTransport(services);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddSingleton<OpenAiRealtimeService>();
        services.AddSingleton<IRealtimeService>(sp => sp.GetRequiredService<OpenAiRealtimeService>());
        return services;
    }

    public static IServiceCollection AddGeminiLive(
        this IServiceCollection                services,
        Func<IServiceProvider, GeminiLiveOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        EnsureTransport(services);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddSingleton<GeminiLiveService>();
        services.AddSingleton<IRealtimeService>(sp => sp.GetRequiredService<GeminiLiveService>());
        return services;
    }

    public static IServiceCollection AddNovaSonic(
        this IServiceCollection                services,
        Func<IServiceProvider, NovaSonicOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        EnsureTransport(services);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddSingleton<NovaSonicService>();
        services.AddSingleton<IRealtimeService>(sp => sp.GetRequiredService<NovaSonicService>());
        return services;
    }

    public static IServiceCollection AddElevenLabsConv(
        this IServiceCollection                     services,
        Func<IServiceProvider, ElevenLabsConvOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        EnsureTransport(services);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddSingleton<ElevenLabsConvService>();
        services.AddSingleton<IRealtimeService>(sp => sp.GetRequiredService<ElevenLabsConvService>());
        return services;
    }

    public static IServiceCollection AddUltravox(
        this IServiceCollection              services,
        Func<IServiceProvider, UltravoxOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        EnsureTransport(services);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<UltravoxService>((sp, c) =>
        {
            var o = sp.GetRequiredService<UltravoxOptions>();
            c.BaseAddress = o.ApiEndpoint;
        });
        services.AddSingleton<IRealtimeService>(sp => sp.GetRequiredService<UltravoxService>());
        return services;
    }
}
