// IMediaStream.cs
//
// (3.3.0) Host-supplied media stream abstraction shared across all
// carriers (Twilio, Telnyx, Plivo, etc.). The carrier session reads
// from / writes to this; the ASP.NET host wires the carrier's
// media-streaming WebSocket against it. Keeping this carrier-agnostic
// lets the carrier packages stay framework-free.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Telephony;

/// <summary>
/// (3.3.0) A live media channel for one call. The carrier host's
/// WebSocket handler implements this; the carrier session consumes it.
/// </summary>
public interface IMediaStream : IAsyncDisposable
{
    /// <summary>The carrier call id + metadata captured at connect.</summary>
    CallInfo CallInfo { get; }

    /// <summary>Inbound audio frames from the caller.</summary>
    IAsyncEnumerable<AudioFrame> ReceiveAudioAsync(CancellationToken ct = default);

    /// <summary>Outbound audio frames to the caller.</summary>
    ValueTask SendAudioAsync(AudioFrame frame, CancellationToken ct = default);

    /// <summary>Inbound DTMF events.</summary>
    IAsyncEnumerable<DtmfEvent> ReceiveDtmfAsync(CancellationToken ct = default);

    /// <summary>Mark the call ended from our side. Closes the WebSocket.</summary>
    ValueTask EndAsync(CancellationToken ct = default);

    /// <summary>Fires when the carrier reports the call status changed.</summary>
    event EventHandler<CallStatus>? StatusChanged;

    /// <summary>The current lifecycle state.</summary>
    CallStatus CurrentStatus { get; }
}
