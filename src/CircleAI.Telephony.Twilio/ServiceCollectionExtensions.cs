// ServiceCollectionExtensions.cs
//
// (3.3.0) DI helper — register the Twilio carrier with its HttpClient
// and options factory.

using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Telephony.Twilio;

public static class TwilioServiceCollectionExtensions
{
    /// <summary>
    /// (3.3.0) Register <see cref="TwilioCarrier"/> as the
    /// <see cref="ITelephonyCarrier"/> singleton.
    /// </summary>
    public static IServiceCollection AddTwilioCarrier(
        this IServiceCollection                services,
        Func<IServiceProvider, TwilioOptions>  optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        services.AddSingleton(sp => optionsFactory(sp));
        services.AddHttpClient<TwilioCarrier>((sp, client) =>
        {
            var options = sp.GetRequiredService<TwilioOptions>();
            client.BaseAddress = options.BaseAddress;
        });
        services.AddSingleton<ITelephonyCarrier>(sp => sp.GetRequiredService<TwilioCarrier>());
        return services;
    }
}
