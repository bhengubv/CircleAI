// NullImplementations.cs
//
// (3.3.0) No-op fallbacks for the telephony surface. Used when the
// host hasn't wired a real carrier — test runs, dry-runs, or
// "telephony not configured" composition lines.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) Null carrier — fail-soft on every operation.</summary>
public sealed class NullTelephonyCarrier : ITelephonyCarrier
{
    public static readonly NullTelephonyCarrier Instance = new();

    public string CarrierId   => "null";
    public bool   IsConfigured => false;

    public ValueTask<ProvisionedNumber> ProvisionNumberAsync(
        string            countryCode,
        string?           areaCode = null,
        CancellationToken ct       = default)
    {
        throw new InvalidOperationException(
            "Null carrier cannot provision phone numbers. Register a real ITelephonyCarrier (CircleAI.Telephony.Twilio / .Telnyx / .Plivo).");
    }

    public ValueTask ConfigureInboundWebhookAsync(
        string            phoneNumber,
        Uri               inboundWebhook,
        CancellationToken ct = default)
        => ValueTask.CompletedTask;

    public ValueTask<ICallSession> DialAsync(
        string             fromNumber,
        string             toNumber,
        Uri                streamUrl,
        OutboundDialOptions? options = null,
        CancellationToken  ct       = default)
    {
        throw new InvalidOperationException(
            "Null carrier cannot place outbound calls. Register a real ITelephonyCarrier.");
    }

    public ValueTask<IReadOnlyList<ProvisionedNumber>> ListNumbersAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<ProvisionedNumber>>(Array.Empty<ProvisionedNumber>());
}

/// <summary>(3.3.0) Null inbound dispatcher — never fires.</summary>
public sealed class NullInboundCallDispatcher : IInboundCallDispatcher
{
    public static readonly NullInboundCallDispatcher Instance = new();

    public string CarrierId => "null";

    public IDisposable Subscribe(Func<ICallSession, ValueTask> handler) => NoopDisposable.Instance;

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
