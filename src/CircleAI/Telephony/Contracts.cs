// Contracts.cs
//
// (3.3.0) The CircleAI.Telephony contract surface — carrier-agnostic.
// Any consumer (txtMe, Panik, salon receptionist) talks to this; the
// real Twilio / Telnyx / Plivo adapters ship as sibling packages.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Telephony;

/// <summary>
/// (3.3.0) Carrier integration — the place where CircleAI talks to a
/// phone-network operator (Twilio, Telnyx, Plivo, or a SIP gateway).
/// Inbound: carrier delivers a call to us → carrier emits
/// <see cref="ICallSession"/> via the host's webhook plumbing.
/// Outbound: caller asks us to dial → we call <see cref="DialAsync"/>.
/// </summary>
public interface ITelephonyCarrier
{
    /// <summary>Stable carrier id — "twilio" / "telnyx" / "plivo" / "null".</summary>
    string CarrierId { get; }

    /// <summary>True when the carrier has the credentials + base addresses it needs.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Buy a new phone number from this carrier for the given country code
    /// (ISO 3166-1 alpha-2, e.g. "ZA"). Caller chooses one of the offered
    /// area codes via <paramref name="areaCode"/>; pass null for "any".
    /// </summary>
    ValueTask<ProvisionedNumber> ProvisionNumberAsync(
        string             countryCode,
        string?            areaCode = null,
        CancellationToken  ct       = default);

    /// <summary>
    /// Configure a number we already own to route inbound calls to our
    /// host-provided WebSocket endpoint.
    /// </summary>
    ValueTask ConfigureInboundWebhookAsync(
        string             phoneNumber,
        Uri                inboundWebhook,
        CancellationToken  ct = default);

    /// <summary>
    /// Place an outbound call. <paramref name="streamUrl"/> is where the
    /// carrier should stream the live media (WebSocket URL on our host).
    /// Returns a session the caller can attach an agent to.
    /// </summary>
    ValueTask<ICallSession> DialAsync(
        string             fromNumber,
        string             toNumber,
        Uri                streamUrl,
        OutboundDialOptions? options = null,
        CancellationToken  ct       = default);

    /// <summary>List the numbers we own on this carrier.</summary>
    ValueTask<IReadOnlyList<ProvisionedNumber>> ListNumbersAsync(CancellationToken ct = default);
}

/// <summary>(3.3.0) Optional knobs for an outbound dial.</summary>
public sealed record OutboundDialOptions
{
    /// <summary>If true, detect voicemail and surface <see cref="CallStatus.Voicemail"/>.</summary>
    public bool DetectAnsweringMachine { get; init; }

    /// <summary>How long to ring before treating it as no-answer. Default 30 s.</summary>
    public int RingTimeoutSeconds { get; init; } = 30;

    /// <summary>Optional caller-id override (must be a number you own).</summary>
    public string? CallerIdOverride { get; init; }

    /// <summary>Optional list of E.164 numbers to also dial if the primary doesn't answer (round-robin).</summary>
    public IReadOnlyList<string>? FollowMeNumbers { get; init; }
}

/// <summary>
/// (3.3.0) Live call session. The agent talks to this — it doesn't know
/// or care which carrier is on the other side. Audio in / audio out /
/// hang up / transfer / DTMF.
/// </summary>
public interface ICallSession : IAsyncDisposable
{
    /// <summary>Stable carrier-supplied info captured at call start.</summary>
    CallInfo Info { get; }

    /// <summary>Current lifecycle status (Active / EndedByCaller / Transferred / ...).</summary>
    CallStatus Status { get; }

    /// <summary>
    /// Audio frames arriving from the caller. Cancel the token to stop
    /// receiving.
    /// </summary>
    IAsyncEnumerable<AudioFrame> ReceiveAudioAsync(CancellationToken ct = default);

    /// <summary>Send an audio frame to the caller.</summary>
    ValueTask SendAudioAsync(AudioFrame frame, CancellationToken ct = default);

    /// <summary>DTMF tones the caller is pressing.</summary>
    IAsyncEnumerable<DtmfEvent> ReceiveDtmfAsync(CancellationToken ct = default);

    /// <summary>Send DTMF tones from the AI side (for navigating other people's menus).</summary>
    ValueTask SendDtmfAsync(string digits, CancellationToken ct = default);

    /// <summary>
    /// Transfer the call to <paramref name="targetNumber"/>. Cold = drop and
    /// forget. Warm = park the caller, dial the human, brief them,
    /// bridge both.
    /// </summary>
    ValueTask TransferAsync(
        string             targetNumber,
        TransferMode       mode,
        string?            briefing = null,
        CancellationToken  ct       = default);

    /// <summary>End the call from our side.</summary>
    ValueTask HangUpAsync(CancellationToken ct = default);

    /// <summary>Subscribe to lifecycle status changes.</summary>
    event EventHandler<CallStatus>? StatusChanged;
}

/// <summary>
/// (3.3.0) Inbound webhook dispatcher — the carrier-provided HTTP
/// handler (host wires this into ASP.NET routing) calls into the
/// dispatcher to materialise an <see cref="ICallSession"/> the agent
/// can attach to.
/// </summary>
public interface IInboundCallDispatcher
{
    /// <summary>Stable id of the carrier feeding inbound calls into this dispatcher.</summary>
    string CarrierId { get; }

    /// <summary>
    /// Subscribe to inbound call sessions. Each new call yields a session
    /// the consumer attaches their agent to.
    /// </summary>
    IDisposable Subscribe(Func<ICallSession, ValueTask> handler);
}
