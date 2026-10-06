// TestCallSession.cs
//
// (3.3.0) Build voice loops without paying for a real carrier minute.
// TestCallSession is an in-memory ICallSession that lets a test
// harness inject inbound audio + DTMF, capture outbound audio, and
// drive lifecycle events on demand.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) In-memory ICallSession for harnesses + unit tests.</summary>
public sealed class TestCallSession : ICallSession
{
    private readonly Channel<AudioFrame> _inboundAudio = Channel.CreateUnbounded<AudioFrame>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Channel<DtmfEvent> _inboundDtmf = Channel.CreateUnbounded<DtmfEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly List<AudioFrame> _outboundAudio = new();
    private readonly List<string> _outboundDtmf = new();
    private readonly object _gate = new();
    private CallStatus _status = CallStatus.Active;

    public TestCallSession(CallInfo? info = null)
    {
        Info = info ?? new CallInfo(
            CallId:        Guid.NewGuid().ToString("n"),
            Direction:     CallDirection.Inbound,
            From:          "+15555550100",
            To:            "+15555550200",
            CarrierId:     "test",
            MediaFormat:   CallMediaFormat.Pcm16000,
            StartedAtUtc:  DateTimeOffset.UtcNow);
    }

    public CallInfo  Info   { get; }
    public CallStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public event EventHandler<CallStatus>? StatusChanged;

    /// <summary>(3.3.0) Outbound audio frames the AI has emitted, captured for assertions.</summary>
    public IReadOnlyList<AudioFrame> SentAudioFrames
    {
        get { lock (_outboundAudio) return _outboundAudio.ToArray(); }
    }

    /// <summary>(3.3.0) Outbound DTMF strings the AI has emitted.</summary>
    public IReadOnlyList<string> SentDtmf
    {
        get { lock (_outboundDtmf) return _outboundDtmf.ToArray(); }
    }

    /// <summary>(3.3.0) Inject one inbound audio frame for the AI to consume via ReceiveAudioAsync.</summary>
    public void InjectInboundAudio(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _inboundAudio.Writer.TryWrite(frame);
    }

    /// <summary>(3.3.0) Inject one inbound DTMF event.</summary>
    public void InjectInboundDtmf(DtmfEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        _inboundDtmf.Writer.TryWrite(ev);
    }

    /// <summary>(3.3.0) Stop the inbound streams cleanly.</summary>
    public void EndInboundStreams()
    {
        _inboundAudio.Writer.TryComplete();
        _inboundDtmf.Writer.TryComplete();
    }

    /// <summary>(3.3.0) Trigger a status change (e.g. caller hangs up).</summary>
    public void TriggerStatusChange(CallStatus newStatus)
    {
        EventHandler<CallStatus>? handler;
        lock (_gate)
        {
            _status = newStatus;
            handler = StatusChanged;
        }
        handler?.Invoke(this, newStatus);
    }

    public IAsyncEnumerable<AudioFrame> ReceiveAudioAsync(CancellationToken ct = default)
        => _inboundAudio.Reader.ReadAllAsync(ct);

    public IAsyncEnumerable<DtmfEvent> ReceiveDtmfAsync(CancellationToken ct = default)
        => _inboundDtmf.Reader.ReadAllAsync(ct);

    public ValueTask SendAudioAsync(AudioFrame frame, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_outboundAudio) _outboundAudio.Add(frame);
        return ValueTask.CompletedTask;
    }

    public ValueTask SendDtmfAsync(string digits, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(digits);
        lock (_outboundDtmf) _outboundDtmf.Add(digits);
        return ValueTask.CompletedTask;
    }

    public ValueTask TransferAsync(string target, TransferMode mode, string? briefing = null, CancellationToken ct = default)
    {
        TriggerStatusChange(CallStatus.Transferred);
        return ValueTask.CompletedTask;
    }

    public ValueTask HangUpAsync(CancellationToken ct = default)
    {
        TriggerStatusChange(CallStatus.EndedByAgent);
        EndInboundStreams();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        EndInboundStreams();
        return ValueTask.CompletedTask;
    }
}
