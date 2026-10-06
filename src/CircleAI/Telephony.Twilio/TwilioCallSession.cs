// TwilioCallSession.cs
//
// (3.3.0) ICallSession backed by a host-supplied IMediaStream. The
// carrier's responsibility is to terminate calls via REST + adapt
// transfer + DTMF send. Audio in/out delegate to the media stream.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Telephony;

namespace CircleAI.Telephony.Twilio;

/// <summary>
/// (3.3.0) <see cref="ICallSession"/> wrapping a Twilio media stream.
/// </summary>
public sealed class TwilioCallSession : ICallSession
{
    private readonly IMediaStream _media;
    private readonly TwilioCarrier _carrier;
    private readonly BriefingSynthesiser? _briefingTts;
    private readonly Uri? _bridgeStreamUrl;
    private CallStatus _status = CallStatus.Ringing;

    public TwilioCallSession(IMediaStream media, TwilioCarrier carrier)
        : this(media, carrier, briefingTts: null, bridgeStreamUrl: null) { }

    /// <summary>(3.3.0) Construct with warm-transfer support. When <paramref name="briefingTts"/>
    /// and <paramref name="bridgeStreamUrl"/> are supplied, <see cref="TransferAsync"/> with
    /// <see cref="TransferMode.Warm"/> orchestrates a full dial-brief-bridge flow via
    /// <see cref="DefaultWarmTransferOrchestrator"/>.</summary>
    public TwilioCallSession(IMediaStream media, TwilioCarrier carrier, BriefingSynthesiser? briefingTts, Uri? bridgeStreamUrl)
    {
        _media           = media   ?? throw new ArgumentNullException(nameof(media));
        _carrier         = carrier ?? throw new ArgumentNullException(nameof(carrier));
        _briefingTts     = briefingTts;
        _bridgeStreamUrl = bridgeStreamUrl;
        _media.StatusChanged += OnMediaStatusChanged;
    }

    public CallInfo  Info   => _media.CallInfo;
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
        // If the host's media stream supports out-of-band DTMF (Twilio's
        // JSON control frame), prefer that. Otherwise generate in-band
        // tones over the existing audio channel — works for every codec
        // the carrier negotiates.
        if (_media is IDtmfSendable native)
        {
            return native.SendDtmfAsync(digits, ct);
        }
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
            // Warm requested but no briefing pipeline configured — fall through to cold
            // transfer (best-effort) so the caller still reaches a human.
        }

        var transferTwiml = $"<Response><Dial>{System.Net.WebUtility.HtmlEncode(targetNumber)}</Dial></Response>";
        await _carrier.RedirectCallAsync(Info.CallId, transferTwiml, ct).ConfigureAwait(false);
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

    private void OnMediaStatusChanged(object? sender, CallStatus status)
    {
        SetStatus(status);
    }

    private void SetStatus(CallStatus status)
    {
        if (_status == status) return;
        _status = status;
        StatusChanged?.Invoke(this, status);
    }
}

/// <summary>
/// (3.3.0) <see cref="IMediaStream"/> for the moment between
/// "carrier accepted dial" and "host's WebSocket attached." Yields no
/// audio. Calling Send before attach raises a friendly error.
/// </summary>
internal sealed class PendingMediaStream : IMediaStream
{
    public PendingMediaStream(CallInfo info)
    {
        CallInfo = info;
    }

    public CallInfo CallInfo { get; }
    public CallStatus CurrentStatus { get; private set; } = CallStatus.Ringing;
    public event EventHandler<CallStatus>? StatusChanged;

    public async IAsyncEnumerable<AudioFrame> ReceiveAudioAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Yield nothing until the host attaches.
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask SendAudioAsync(AudioFrame frame, CancellationToken ct = default)
        => throw new InvalidOperationException(
            "Cannot send audio before the host's WebSocket has attached its IMediaStream. Wire CircleAI.Hosting.Telephony.Twilio to complete the connection.");

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

// IDtmfSendable moved to CircleAI.Telephony so Telnyx/Plivo can also opt-in.
