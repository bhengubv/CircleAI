// ServiceCollectionExtensions.cs
//
// (3.3.0) DI helpers for the carrier-agnostic surface. Real carrier
// adapters add themselves via CircleAI.Telephony.Twilio /
// .Telephony.Telnyx / .Telephony.Plivo extension methods.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CircleAI.Telephony;

public static class TelephonyServiceCollectionExtensions
{
    /// <summary>
    /// (3.3.0) Register the null carrier as the default
    /// <see cref="ITelephonyCarrier"/>. Real carrier registrations
    /// override via their own Add* extensions.
    /// </summary>
    public static IServiceCollection AddCircleAiTelephony(this IServiceCollection services)
    {
        services.TryAddSingleton<ITelephonyCarrier>(NullTelephonyCarrier.Instance);
        services.TryAddSingleton<IInboundCallDispatcher>(NullInboundCallDispatcher.Instance);
        services.TryAddSingleton<IProvisionedNumberStore, InMemoryProvisionedNumberStore>();
        services.TryAddSingleton<PhoneNumberProvisioner>();
        return services;
    }

    /// <summary>
    /// (3.3.0) Register a multi-carrier fallback that walks carriers in
    /// order and uses the first configured one. Useful when the host
    /// wires more than one carrier and wants automatic failover.
    /// </summary>
    public static IServiceCollection AddCarrierFallback(
        this IServiceCollection services,
        params Func<IServiceProvider, ITelephonyCarrier>[] carrierFactories)
    {
        ArgumentNullException.ThrowIfNull(carrierFactories);
        services.AddSingleton<ITelephonyCarrier>(sp =>
            new CarrierFallback(carrierFactories.Select(f => f(sp))));
        return services;
    }
}

/// <summary>(3.3.0) Multi-carrier failover — picks the first configured carrier.</summary>
internal sealed class CarrierFallback : ITelephonyCarrier
{
    private readonly IReadOnlyList<ITelephonyCarrier> _carriers;

    public CarrierFallback(IEnumerable<ITelephonyCarrier> carriers)
    {
        _carriers = carriers?.ToList() ?? new List<ITelephonyCarrier>();
    }

    public string CarrierId   => $"fallback({_carriers.Count})";
    public bool   IsConfigured => _carriers.Any(c => c.IsConfigured);

    private ITelephonyCarrier Pick() =>
        _carriers.FirstOrDefault(c => c.IsConfigured) ?? NullTelephonyCarrier.Instance;

    public System.Threading.Tasks.ValueTask<ProvisionedNumber> ProvisionNumberAsync(
        string countryCode, string? areaCode = null, System.Threading.CancellationToken ct = default)
        => Pick().ProvisionNumberAsync(countryCode, areaCode, ct);

    public System.Threading.Tasks.ValueTask ConfigureInboundWebhookAsync(
        string phoneNumber, Uri inboundWebhook, System.Threading.CancellationToken ct = default)
        => Pick().ConfigureInboundWebhookAsync(phoneNumber, inboundWebhook, ct);

    public System.Threading.Tasks.ValueTask<ICallSession> DialAsync(
        string fromNumber, string toNumber, Uri streamUrl,
        OutboundDialOptions? options = null, System.Threading.CancellationToken ct = default)
        => Pick().DialAsync(fromNumber, toNumber, streamUrl, options, ct);

    public System.Threading.Tasks.ValueTask<IReadOnlyList<ProvisionedNumber>> ListNumbersAsync(
        System.Threading.CancellationToken ct = default)
        => Pick().ListNumbersAsync(ct);
}
