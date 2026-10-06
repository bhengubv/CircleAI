// WarmTransferOrchestrator.cs
//
// (3.3.0) Warm call transfer: park caller, dial target, speak the
// briefing to target via TTS, then bridge by issuing a cold transfer
// of the caller leg to the target. The AI's bridge-leg call ends once
// the caller is connected.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) One warm-transfer request.</summary>
/// <param name="SourceSession">The active call we want to transfer.</param>
/// <param name="TargetNumber">E.164 number of the person we're transferring to.</param>
/// <param name="BriefingText">What the AI should say to the target before the bridge.</param>
/// <param name="BridgeStreamUrl">WSS endpoint the carrier will hand the target leg to.</param>
public sealed record WarmTransferRequest(
    ICallSession SourceSession,
    string       TargetNumber,
    string       BriefingText,
    Uri          BridgeStreamUrl);

/// <summary>(3.3.0) Outcome of a warm transfer.</summary>
public sealed record WarmTransferResult(
    bool         Succeeded,
    string?      FailureReason,
    ICallSession? BridgeSession);

/// <summary>(3.3.0) Park caller, dial target, brief, bridge.</summary>
public interface IWarmTransferOrchestrator
{
    ValueTask<WarmTransferResult> ExecuteAsync(WarmTransferRequest request, CancellationToken ct = default);
}

/// <summary>(3.3.0) Synthesise the briefing text to PCM-16 mono.</summary>
public delegate ValueTask<ReadOnlyMemory<byte>> BriefingSynthesiser(string text, CancellationToken ct);

/// <summary>(3.3.0) Carrier-agnostic warm-transfer driver.</summary>
public sealed class DefaultWarmTransferOrchestrator : IWarmTransferOrchestrator
{
    private readonly ITelephonyCarrier _carrier;
    private readonly BriefingSynthesiser _briefingTts;
    private readonly ILogger _logger;

    public DefaultWarmTransferOrchestrator(
        ITelephonyCarrier   carrier,
        BriefingSynthesiser briefingTts,
        ILogger<DefaultWarmTransferOrchestrator>? logger = null)
    {
        _carrier     = carrier     ?? throw new ArgumentNullException(nameof(carrier));
        _briefingTts = briefingTts ?? throw new ArgumentNullException(nameof(briefingTts));
        _logger      = (ILogger?)logger ?? NullLogger.Instance;
    }

    public async ValueTask<WarmTransferResult> ExecuteAsync(
        WarmTransferRequest request,
        CancellationToken   ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SourceSession is null)
        {
            return new WarmTransferResult(false, "SourceSession is required", null);
        }
        if (string.IsNullOrWhiteSpace(request.TargetNumber))
        {
            return new WarmTransferResult(false, "TargetNumber is required", null);
        }

        // 1) Dial target on a fresh leg.
        ICallSession bridgeLeg;
        try
        {
            bridgeLeg = await _carrier.DialAsync(
                fromNumber: request.SourceSession.Info.To,
                toNumber:   request.TargetNumber,
                streamUrl:  request.BridgeStreamUrl,
                ct:         ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Warm-transfer dial to {Target} failed", request.TargetNumber);
            return new WarmTransferResult(false, $"Failed to dial target: {ex.Message}", null);
        }

        // 2) Speak briefing to target.
        try
        {
            var briefingAudio = await _briefingTts(request.BriefingText, ct).ConfigureAwait(false);
            if (!briefingAudio.IsEmpty)
            {
                await bridgeLeg.SendAudioAsync(
                    new AudioFrame(briefingAudio, CallMediaFormat.Pcm24000, TimeSpan.Zero), ct)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Warm-transfer briefing failed; hanging up bridge leg");
            await bridgeLeg.HangUpAsync(ct).ConfigureAwait(false);
            return new WarmTransferResult(false, $"Failed to brief target: {ex.Message}", null);
        }

        // 3) Hand caller off to target — this is the bridge moment.
        try
        {
            await request.SourceSession.TransferAsync(
                request.TargetNumber, TransferMode.Cold, briefing: null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Warm-transfer bridge step failed");
            await bridgeLeg.HangUpAsync(ct).ConfigureAwait(false);
            return new WarmTransferResult(false, $"Failed to bridge caller: {ex.Message}", null);
        }

        // 4) AI leg ends; caller and target stay connected.
        await bridgeLeg.HangUpAsync(ct).ConfigureAwait(false);
        return new WarmTransferResult(true, null, bridgeLeg);
    }
}
