// Primitives.cs
//
// (3.3.0) Shared value types for the telephony surface. Direction +
// call lifecycle states + media format negotiation primitives, kept
// minimal so a real-world inbound or outbound call needs nothing else
// in scope.

using System;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) Call direction.</summary>
public enum CallDirection
{
    Inbound,
    Outbound,
}

/// <summary>(3.3.0) Call lifecycle states.</summary>
public enum CallStatus
{
    /// <summary>Carrier accepted the dial but the other end has not picked up yet.</summary>
    Ringing,

    /// <summary>Both sides connected; media flowing.</summary>
    Active,

    /// <summary>Caller hung up.</summary>
    EndedByCaller,

    /// <summary>Callee hung up.</summary>
    EndedByCallee,

    /// <summary>AI agent (us) ended the call.</summary>
    EndedByAgent,

    /// <summary>Carrier-detected voicemail / answering machine on outbound dial.</summary>
    Voicemail,

    /// <summary>Call did not connect (busy, no answer, network).</summary>
    Failed,

    /// <summary>Call transferred to a human or a different agent.</summary>
    Transferred,
}

/// <summary>(3.3.0) Audio wire formats supported across carriers.</summary>
public enum CallMediaFormat
{
    /// <summary>µ-law 8 kHz mono — Twilio default, Plivo default, fallback Telnyx.</summary>
    Mulaw8000,

    /// <summary>A-law 8 kHz mono — some European carriers.</summary>
    Alaw8000,

    /// <summary>Linear PCM 16-bit 16 kHz mono — Telnyx negotiated path.</summary>
    Pcm16000,

    /// <summary>Linear PCM 16-bit 24 kHz mono — high-quality WebRTC, OpenAI Realtime.</summary>
    Pcm24000,
}

/// <summary>(3.3.0) Transfer mode the AI requests from the carrier.</summary>
public enum TransferMode
{
    /// <summary>Drop the caller into the new line and hang up — fast, no context handover.</summary>
    Cold,

    /// <summary>Park caller, dial human, brief human verbally, then bridge both — context preserved.</summary>
    Warm,
}

/// <summary>
/// (3.3.0) Information about one call. Captured once at call start, immutable.
/// </summary>
/// <param name="CallId">Carrier-supplied unique id (Twilio CallSid, Telnyx call_control_id, etc.).</param>
/// <param name="Direction">Direction — who initiated.</param>
/// <param name="From">Caller's phone number in E.164 format (e.g. +27821234567).</param>
/// <param name="To">Called party's phone number in E.164 format.</param>
/// <param name="CarrierId">Carrier id (e.g. "twilio", "telnyx", "plivo").</param>
/// <param name="MediaFormat">Audio wire format the carrier is streaming.</param>
/// <param name="StartedAtUtc">When the call started.</param>
public sealed record CallInfo(
    string         CallId,
    CallDirection  Direction,
    string         From,
    string         To,
    string         CarrierId,
    CallMediaFormat MediaFormat,
    DateTimeOffset StartedAtUtc);

/// <summary>
/// (3.3.0) A snapshot of a call's current state. Returned by lifecycle queries.
/// </summary>
/// <param name="Info">Carrier-captured call metadata.</param>
/// <param name="Status">Current lifecycle state.</param>
/// <param name="Duration">How long since the call connected.</param>
/// <param name="CostSoFar">Per-second cost so far (carrier minutes + any LLM/STT/TTS attached).</param>
/// <param name="TransferTarget">If <see cref="CallStatus.Transferred"/>, the E.164 number we transferred to.</param>
public sealed record CallSnapshot(
    CallInfo       Info,
    CallStatus     Status,
    TimeSpan       Duration,
    decimal        CostSoFar,
    string?        TransferTarget = null);

/// <summary>(3.3.0) Audio chunk flowing from caller → AI or AI → caller.</summary>
public sealed record AudioFrame(
    ReadOnlyMemory<byte> Pcm,
    CallMediaFormat      Format,
    TimeSpan             Offset);

/// <summary>
/// (3.3.0) DTMF tone from the caller.
/// </summary>
/// <param name="Digit">The digit (0-9, *, #).</param>
/// <param name="Duration">How long the caller held it.</param>
/// <param name="Offset">When (relative to call start).</param>
public sealed record DtmfEvent(
    char           Digit,
    TimeSpan       Duration,
    TimeSpan       Offset);

/// <summary>(3.3.0) Result of a number-provisioning request.</summary>
public sealed record ProvisionedNumber(
    string         PhoneNumber,
    string         CarrierId,
    DateTimeOffset ProvisionedAtUtc,
    decimal        MonthlyRecurringCost);
