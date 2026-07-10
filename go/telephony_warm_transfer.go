// telephony_warm_transfer.go
//
// Ports CircleAI.Telephony/WarmTransferOrchestrator.cs:
//   WarmTransferRequest           -> WarmTransferRequest value struct
//   WarmTransferResult            -> WarmTransferResult value struct
//   IWarmTransferOrchestrator     -> IWarmTransferOrchestrator interface
//   BriefingSynthesiser           -> BriefingSynthesiser func type
//   DefaultWarmTransferOrchestrator-> DefaultWarmTransferOrchestrator
//
// Flow (unchanged): dial the target on a fresh leg, speak the briefing TTS to
// the target, cold-transfer the caller onto the target (the bridge moment), then
// hang up the AI's bridge leg. Any step failing hangs up the bridge leg (once
// dialled) and returns Succeeded=false with the reason — the orchestrator itself
// never returns a non-nil error, matching the C# which encodes failure in the
// result rather than throwing.

package circleai

import (
	"context"
	"strings"
)

// WarmTransferRequest is one warm-transfer request. Ports WarmTransferRequest.
type WarmTransferRequest struct {
	SourceSession   ICallSession // the active call we want to transfer
	TargetNumber    string       // E.164 number of the person we're transferring to
	BriefingText    string       // what the AI should say to the target before the bridge
	BridgeStreamURL string       // WSS endpoint the carrier will hand the target leg to
}

// WarmTransferResult is the outcome of a warm transfer. Ports WarmTransferResult.
// FailureReason is "" on success (C# null); BridgeSession is the AI leg (nil on
// early failure).
type WarmTransferResult struct {
	Succeeded     bool
	FailureReason string
	BridgeSession ICallSession
}

// IWarmTransferOrchestrator parks the caller, dials the target, briefs, and
// bridges. Ports IWarmTransferOrchestrator.
type IWarmTransferOrchestrator interface {
	Execute(ctx context.Context, request WarmTransferRequest) (WarmTransferResult, error)
}

// BriefingSynthesiser synthesises the briefing text to PCM-16 mono. Ports the
// BriefingSynthesiser delegate.
type BriefingSynthesiser func(ctx context.Context, text string) ([]byte, error)

// DefaultWarmTransferOrchestrator is the carrier-agnostic warm-transfer driver.
// Ports DefaultWarmTransferOrchestrator.
type DefaultWarmTransferOrchestrator struct {
	carrier     ITelephonyCarrier
	briefingTts BriefingSynthesiser
}

// NewDefaultWarmTransferOrchestrator constructs the driver. carrier and
// briefingTts are both required (the C# constructor throws on either being null).
func NewDefaultWarmTransferOrchestrator(carrier ITelephonyCarrier, briefingTts BriefingSynthesiser) *DefaultWarmTransferOrchestrator {
	if carrier == nil {
		panic("carrier is required")
	}
	if briefingTts == nil {
		panic("briefingTts is required")
	}
	return &DefaultWarmTransferOrchestrator{carrier: carrier, briefingTts: briefingTts}
}

// Execute runs the warm transfer. Ports ExecuteAsync exactly, including the
// step-by-step failure handling and the parseURL used for the bridge stream (an
// unparseable BridgeStreamURL is treated as a dial failure).
func (o *DefaultWarmTransferOrchestrator) Execute(ctx context.Context, request WarmTransferRequest) (WarmTransferResult, error) {
	if request.SourceSession == nil {
		return WarmTransferResult{Succeeded: false, FailureReason: "SourceSession is required"}, nil
	}
	if strings.TrimSpace(request.TargetNumber) == "" {
		return WarmTransferResult{Succeeded: false, FailureReason: "TargetNumber is required"}, nil
	}

	// 1) Dial target on a fresh leg.
	bridgeURL, perr := parseAbsoluteURL(request.BridgeStreamURL)
	if perr != nil {
		return WarmTransferResult{Succeeded: false, FailureReason: "Failed to dial target: " + perr.Error()}, nil
	}
	bridgeLeg, err := o.carrier.Dial(ctx, request.SourceSession.Info().To, request.TargetNumber, bridgeURL, nil)
	if err != nil {
		return WarmTransferResult{Succeeded: false, FailureReason: "Failed to dial target: " + err.Error()}, nil
	}

	// 2) Speak briefing to target.
	briefingAudio, err := o.briefingTts(ctx, request.BriefingText)
	if err != nil {
		_ = bridgeLeg.HangUp(ctx)
		return WarmTransferResult{Succeeded: false, FailureReason: "Failed to brief target: " + err.Error()}, nil
	}
	if len(briefingAudio) > 0 {
		if err := bridgeLeg.SendAudio(ctx, AudioFrame{Pcm: briefingAudio, Format: CallMediaFormatPcm24000, Offset: 0}); err != nil {
			_ = bridgeLeg.HangUp(ctx)
			return WarmTransferResult{Succeeded: false, FailureReason: "Failed to brief target: " + err.Error()}, nil
		}
	}

	// 3) Hand caller off to target — the bridge moment (cold transfer, no briefing).
	if err := request.SourceSession.Transfer(ctx, request.TargetNumber, TransferModeCold, ""); err != nil {
		_ = bridgeLeg.HangUp(ctx)
		return WarmTransferResult{Succeeded: false, FailureReason: "Failed to bridge caller: " + err.Error()}, nil
	}

	// 4) AI leg ends; caller and target stay connected.
	_ = bridgeLeg.HangUp(ctx)
	return WarmTransferResult{Succeeded: true, BridgeSession: bridgeLeg}, nil
}

var _ IWarmTransferOrchestrator = (*DefaultWarmTransferOrchestrator)(nil)
