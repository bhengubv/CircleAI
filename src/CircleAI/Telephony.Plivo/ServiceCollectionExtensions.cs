// ServiceCollectionExtensions.cs
//
// (3.3.0) DI helper — register the Plivo carrier.

using System;
using Microsoft.Extensions.DependencyInjection;
using CircleAI.Telephony;

namespace CircleAI.Telephony.Plivo;

public static class PlivoServiceCollectionExtensions
{
    /// <summary>(3.3.0) Register <see cref="PlivoCarrier"/> as the <see cref="ITelephonyCarrier"/> singleton.</summary>
    public static IServiceCollection AddPlivoCarrier(
        this IServiceCollection               services,
        Func<IServiceProvider, PlivoOptions>  optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<PlivoCarrier>((sp, client) =>
        {
            var options = sp.GetRequiredService<PlivoOptions>();
            client.BaseAddress = options.BaseAddress;
        });
        services.AddSingleton<ITelephonyCarrier>(sp => sp.GetRequiredService<PlivoCarrier>());
        return services;
    }
}
