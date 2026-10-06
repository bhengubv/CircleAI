// PlivoCallSession.cs
//
// (3.3.0) ICallSession backed by a host-supplied IMediaStream wired
// to Plivo's Audio Streaming WebSocket.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Telephony;

namespace CircleAI.Telephony.Plivo;

/// <summary>(3.3.0) <see cref="ICallSession"/> wrapping a Plivo media stream.</summary>
public sealed class PlivoCallSession : ICallSession
{
    private readonly IMediaStream _media;
    private readonly PlivoCarrier _carrier;
    private readonly BriefingSynthesiser? _briefingTts;
    private readonly Uri? _bridgeStreamUrl;
    private CallStatus _status = CallStatus.Ringing;

    public PlivoCallSession(IMediaStream media, PlivoCarrier carrier)
        : this(media, carrier, briefingTts: null, bridgeStreamUrl: null) { }

    /// <summary>(3.3.0) Construct with warm-transfer support — see TwilioCallSession for semantics.</summary>
    public PlivoCallSession(IMediaStream media, PlivoCarrier carrier, BriefingSynthesiser? briefingTts, Uri? bridgeStreamUrl)
    {
        _media           = media   ?? throw new ArgumentNullException(nameof(media));
        _carrier         = carrier ?? throw new ArgumentNullException(nameof(carrier));
        _briefingTts     = briefingTts;
        _bridgeStreamUrl = bridgeStreamUrl;
        _media.StatusChanged += OnMediaStatusChanged;
    }

    public CallInfo Info => _media.CallInfo;

    public CallStatus Status => _media.CurrentStatus == CallStatus.Ringing && _status != CallStatus.Ringing
        ? _status
        : _media.CurrentStatus;

    public event EventHandler<CallStatus>? StatusChanged;

    public IAsyncEnumerable<AudioFrame> ReceiveAudioAsync(CancellationToken ct = default)
        => _media.ReceiveAudioAsync(ct);

    public ValueTask SendAudioAsync(AudioFrame frame, CancellationToken ct = default)
        => _media.SendAudioAsync(frame, ct);

    public IAsyncEnumerable<DtmfEvent> ReceiveDtmfAsync(CancellationToken ct = default)
        => _media.ReceiveDtmfAsync(ct);

    public ValueTask SendDtmfAsync(string digits, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(digits)) return ValueTask.CompletedTask;
        if (_media is IDtmfSendable native) return native.SendDtmfAsync(digits, ct);
        var sampleRate = Info.MediaFormat switch
        {
            CallMediaFormat.Pcm16000  => 16000,
            CallMediaFormat.Pcm24000  => 24000,
            CallMediaFormat.Mulaw8000 => 8000,
            _                         => 8000,
        };
        return DtmfToneGenerator.SendThroughSessionAsync(this, digits, sampleRate, ct: ct);
    }

    public async ValueTask TransferAsync(
        string             targetNumber,
        TransferMode       mode,
        string?            briefing = null,
        CancellationToken  ct       = default)
    {
        if (mode == TransferMode.Warm)
        {
            if (_briefingTts is not null && _bridgeStreamUrl is not null && !string.IsNullOrWhiteSpace(briefing))
            {
                var orchestrator = new DefaultWarmTransferOrchestrator(_carrier, _briefingTts);
                var result = await orchestrator.ExecuteAsync(
                    new WarmTransferRequest(this, targetNumber, briefing, _bridgeStreamUrl), ct).ConfigureAwait(false);
                if (!result.Succeeded)
                    throw new InvalidOperationException($"Warm transfer failed: {result.FailureReason}");
                return;
            }
        }

        await _carrier.TransferCallAsync(Info.CallId, targetNumber, ct).ConfigureAwait(false);
        SetStatus(CallStatus.Transferred);
    }

    public async ValueTask HangUpAsync(CancellationToken ct = default)
    {
        SetStatus(CallStatus.EndedByAgent);
        try
        {
            await _media.EndAsync(ct).ConfigureAwait(false);
        }
        catch { /* media may already be closed */ }
        await _carrier.EndCallAsync(Info.CallId, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _media.StatusChanged -= OnMediaStatusChanged;
        await _media.DisposeAsync().ConfigureAwait(false);
    }

    private void OnMediaStatusChanged(object? sender, CallStatus status) => SetStatus(status);

    private void SetStatus(CallStatus status)
    {
        if (_status == status) return;
        _status = status;
        StatusChanged?.Invoke(this, status);
    }
}

/// <summary>(3.3.0) Pending stream returned while the host's WebSocket attaches.</summary>
internal sealed class PlivoPendingMediaStream : IMediaStream
{
    public PlivoPendingMediaStream(CallInfo info) { CallInfo = info; }

    public CallInfo  CallInfo      { get; }
    public CallStatus CurrentStatus { get; private set; } = CallStatus.Ringing;
    public event EventHandler<CallStatus>? StatusChanged;

    public async IAsyncEnumerable<AudioFrame> ReceiveAudioAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask SendAudioAsync(AudioFrame frame, CancellationToken ct = default)
        => throw new InvalidOperationException(
            "Cannot send audio before the host's WebSocket has attached its IMediaStream.");

    public async IAsyncEnumerable<DtmfEvent> ReceiveDtmfAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask EndAsync(CancellationToken ct = default)
    {
        CurrentStatus = CallStatus.EndedByAgent;
        StatusChanged?.Invoke(this, CurrentStatus);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
