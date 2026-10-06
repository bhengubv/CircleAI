// PhoneNumberProvisioner.cs
//
// (3.3.0) Orchestrates the "buy + configure + persist" loop across any
// carrier that implements ITelephonyCarrier. Single call: pick a
// country, supply your inbound webhook, get back a ProvisionedNumber
// that's ready to take calls.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Telephony;

/// <summary>
/// (3.3.0) Service that buys + configures + persists phone numbers
/// from any carrier behind <see cref="ITelephonyCarrier"/>.
/// </summary>
public sealed class PhoneNumberProvisioner
{
    private readonly ITelephonyCarrier _carrier;
    private readonly IProvisionedNumberStore _store;
    private readonly ILogger _logger;

    public PhoneNumberProvisioner(
        ITelephonyCarrier        carrier,
        IProvisionedNumberStore? store  = null,
        ILogger<PhoneNumberProvisioner>? logger = null)
    {
        _carrier = carrier ?? throw new ArgumentNullException(nameof(carrier));
        _store   = store   ?? new InMemoryProvisionedNumberStore();
        _logger  = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// (3.3.0) Buy a number, wire its inbound webhook, persist it,
    /// return the metadata.
    /// </summary>
    /// <param name="countryCode">ISO country code (e.g. "US", "ZA", "NG").</param>
    /// <param name="inboundWebhook">HTTPS URL the carrier will hit when the number rings.</param>
    /// <param name="areaCode">Optional area code / prefix preference.</param>
    public async ValueTask<ProvisionedNumber> ProvisionAsync(
        string             countryCode,
        Uri                inboundWebhook,
        string?            areaCode = null,
        CancellationToken  ct       = default)
    {
        if (string.IsNullOrWhiteSpace(countryCode)) throw new ArgumentException("countryCode is required", nameof(countryCode));
        ArgumentNullException.ThrowIfNull(inboundWebhook);
        if (!inboundWebhook.IsAbsoluteUri)
        {
            throw new ArgumentException("inboundWebhook must be an absolute URI", nameof(inboundWebhook));
        }

        _logger.LogInformation("Provisioning number on {Carrier} for {Country}/{Area}",
            _carrier.CarrierId, countryCode, areaCode ?? "(any)");

        var provisioned = await _carrier.ProvisionNumberAsync(countryCode, areaCode, ct).ConfigureAwait(false);

        try
        {
            await _carrier.ConfigureInboundWebhookAsync(provisioned.PhoneNumber, inboundWebhook, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Webhook configuration failed for {Number} on {Carrier}",
                provisioned.PhoneNumber, _carrier.CarrierId);
            throw;
        }

        await _store.SaveAsync(provisioned, ct).ConfigureAwait(false);
        return provisioned;
    }

    /// <summary>(3.3.0) The provisioned numbers we know about, locally + via the carrier.</summary>
    public async ValueTask<IReadOnlyList<ProvisionedNumber>> ListAsync(CancellationToken ct = default)
    {
        var stored = await _store.ListAsync(ct).ConfigureAwait(false);
        // Merge with carrier authoritative list — store may be stale.
        var carrierNumbers = await _carrier.ListNumbersAsync(ct).ConfigureAwait(false);
        var merged = new Dictionary<string, ProvisionedNumber>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in stored)       merged[n.PhoneNumber] = n;
        foreach (var n in carrierNumbers) merged[n.PhoneNumber] = n;
        return merged.Values.ToList();
    }
}

/// <summary>
/// (3.3.0) Persistence contract for assigned numbers. Default in-memory
/// implementation is fine for dev; production hosts should plug in a
/// database-backed store.
/// </summary>
public interface IProvisionedNumberStore
{
    ValueTask SaveAsync(ProvisionedNumber number, CancellationToken ct = default);
    ValueTask<IReadOnlyList<ProvisionedNumber>> ListAsync(CancellationToken ct = default);
    ValueTask<ProvisionedNumber?> FindAsync(string phoneNumber, CancellationToken ct = default);
    ValueTask RemoveAsync(string phoneNumber, CancellationToken ct = default);
}

/// <summary>(3.3.0) Default in-memory store. Thread-safe.</summary>
public sealed class InMemoryProvisionedNumberStore : IProvisionedNumberStore
{
    private readonly Dictionary<string, ProvisionedNumber> _byNumber = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public ValueTask SaveAsync(ProvisionedNumber number, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(number);
        lock (_gate) { _byNumber[number.PhoneNumber] = number; }
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<ProvisionedNumber>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<ProvisionedNumber>>(_byNumber.Values.ToList());
        }
    }

    public ValueTask<ProvisionedNumber?> FindAsync(string phoneNumber, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _byNumber.TryGetValue(phoneNumber, out var found);
            return ValueTask.FromResult<ProvisionedNumber?>(found);
        }
    }

    public ValueTask RemoveAsync(string phoneNumber, CancellationToken ct = default)
    {
        lock (_gate) { _byNumber.Remove(phoneNumber); }
        return ValueTask.CompletedTask;
    }
}
