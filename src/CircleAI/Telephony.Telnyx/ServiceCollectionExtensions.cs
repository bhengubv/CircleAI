// ServiceCollectionExtensions.cs
//
// (3.3.0) DI helper — register the Telnyx carrier with its HttpClient
// and options factory.

using System;
using Microsoft.Extensions.DependencyInjection;
using CircleAI.Telephony;

namespace CircleAI.Telephony.Telnyx;

public static class TelnyxServiceCollectionExtensions
{
    /// <summary>(3.3.0) Register <see cref="TelnyxCarrier"/> as the <see cref="ITelephonyCarrier"/> singleton.</summary>
    public static IServiceCollection AddTelnyxCarrier(
        this IServiceCollection                services,
        Func<IServiceProvider, TelnyxOptions>  optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<TelnyxCarrier>((sp, client) =>
        {
            var options = sp.GetRequiredService<TelnyxOptions>();
            client.BaseAddress = options.BaseAddress;
        });
        services.AddSingleton<ITelephonyCarrier>(sp => sp.GetRequiredService<TelnyxCarrier>());
        return services;
    }
}
