// telephony_call_session.go
//
// Ports the shared ICallSession behaviour that TwilioCallSession,
// TelnyxCallSession, and PlivoCallSession implement identically, plus the
// per-carrier hooks where they diverge. The three C# classes have byte-identical
// audio/DTMF/status/hang-up bodies and differ only in the cold-transfer REST
// call and the hang-up REST call, so the Go port factors the common body into
// carrierCallSession and injects the two carrier-specific actions via
// carrierSessionOps. Each carrier file constructs a carrierCallSession with its
// own ops (see telephony_twilio.go / _telnyx.go / _plivo.go).
//
// Status semantics reproduce the C# getter exactly:
//   Status = (media.CurrentStatus == Ringing && local != Ringing) ? local : media.CurrentStatus
// i.e. a locally-set terminal status (EndedByAgent / Transferred) shows through
// while the media stream is still Ringing, otherwise the media status wins.

package circleai

import (
	"context"
	"errors"
	"sync"
)

// carrierSessionOps are the two carrier-specific actions a call session needs:
// end the call and perform a cold transfer, both via the carrier's control
// surface. Ports the divergent bodies of TransferAsync (cold path) and HangUpAsync.
type carrierSessionOps interface {
	// endCall terminates the call by its carrier id (Twilio EndCallAsync, Telnyx
	// hangup, Plivo hangup).
	endCall(ctx context.Context, callID string) error
	// coldTransfer redirects/transfers the in-progress call to targetNumber
	// (Twilio RedirectCall with <Dial> TwiML, Telnyx/Plivo transfer action).
	coldTransfer(ctx context.Context, callID, targetNumber string) error
}

// warmTransferConfig carries the optional warm-transfer pipeline. When both TTS
// and BridgeStreamURL are set, a Warm transfer with a non-blank briefing runs the
// full dial-brief-bridge via DefaultWarmTransferOrchestrator; otherwise Warm
// falls through to a cold transfer (best-effort). Ports the (briefingTts,
// bridgeStreamUrl) constructor pair.
type warmTransferConfig struct {
	carrier         ITelephonyCarrier
	briefingTts     BriefingSynthesiser
	bridgeStreamURL string // "" when warm transfer is not configured
}

// carrierCallSession is the shared ICallSession implementation. Ports the common
// body of the three carrier session classes.
type carrierCallSession struct {
	media IMediaStream
	ops   carrierSessionOps
	warm  warmTransferConfig

	notifier   statusNotifier
	unsubMedia func()

	mu     sync.Mutex
	status CallStatus // the locally-tracked status (C# _status), starts Ringing
}

// newCarrierCallSession wires a session over a media stream + carrier ops. It
// subscribes to the media stream's status changes SYNCHRONOUSLY (Wave-1 rule:
// subscribe before anything can publish) and re-publishes them through its own
// dedupe + notifier. media and ops are required.
func newCarrierCallSession(media IMediaStream, ops carrierSessionOps, warm warmTransferConfig) *carrierCallSession {
	if media == nil {
		panic("media is required")
	}
	if ops == nil {
		panic("ops is required")
	}
	s := &carrierCallSession{media: media, ops: ops, warm: warm, status: CallStatusRinging}
	s.unsubMedia = media.OnStatusChanged(s.onMediaStatusChanged)
	return s
}

// Info returns the call metadata from the media stream. Ports Info => _media.CallInfo.
func (s *carrierCallSession) Info() CallInfo { return s.media.CallInfo() }

// Status ports the C# getter blending local + media status.
func (s *carrierCallSession) Status() CallStatus {
	s.mu.Lock()
	local := s.status
	s.mu.Unlock()
	mediaStatus := s.media.CurrentStatus()
	if mediaStatus == CallStatusRinging && local != CallStatusRinging {
		return local
	}
	return mediaStatus
}

// ReceiveAudio delegates to the media stream.
func (s *carrierCallSession) ReceiveAudio(ctx context.Context) <-chan AudioFrame {
	return s.media.ReceiveAudio(ctx)
}

// SendAudio delegates to the media stream.
func (s *carrierCallSession) SendAudio(ctx context.Context, frame AudioFrame) error {
	return s.media.SendAudio(ctx, frame)
}

// ReceiveDtmf delegates to the media stream.
func (s *carrierCallSession) ReceiveDtmf(ctx context.Context) <-chan DtmfEvent {
	return s.media.ReceiveDtmf(ctx)
}

// SendDtmf sends DTMF out-of-band when the media stream supports IDtmfSendable,
// else falls back to in-band tones via the DTMF generator at the sample rate for
// the negotiated media format. Ports the SendDtmfAsync body (identical across
// carriers).
func (s *carrierCallSession) SendDtmf(ctx context.Context, digits string) error {
	if digits == "" {
		return nil
	}
	if native, ok := s.media.(IDtmfSendable); ok {
		return native.SendDtmf(ctx, digits)
	}
	sampleRate := dtmfSampleRateForFormat(s.Info().MediaFormat)
	return DtmfSendThroughSession(ctx, s, digits, sampleRate, dtmfDefaultDurationMs, dtmfDefaultInterDigitGapMs)
}

// Transfer ports the shared TransferAsync: a Warm request with a configured
// briefing pipeline + non-blank briefing runs the orchestrator; otherwise it
// (and every Cold request) performs the carrier's cold transfer and sets
// Transferred.
func (s *carrierCallSession) Transfer(ctx context.Context, targetNumber string, mode TransferMode, briefing string) error {
	if mode == TransferModeWarm {
		if s.warm.briefingTts != nil && s.warm.bridgeStreamURL != "" && stringsTrimSpaceNonEmpty(briefing) {
			orch := NewDefaultWarmTransferOrchestrator(s.warm.carrier, s.warm.briefingTts)
			result, err := orch.Execute(ctx, WarmTransferRequest{
				SourceSession:   s,
				TargetNumber:    targetNumber,
				BriefingText:    briefing,
				BridgeStreamURL: s.warm.bridgeStreamURL,
			})
			if err != nil {
				return err
			}
			if !result.Succeeded {
				return errors.New("Warm transfer failed: " + result.FailureReason)
			}
			return nil
		}
		// Warm requested but no briefing pipeline — fall through to cold transfer.
	}

	if err := s.ops.coldTransfer(ctx, s.Info().CallID, targetNumber); err != nil {
		return err
	}
	s.setStatus(CallStatusTransferred)
	return nil
}

// HangUp ends the call: set EndedByAgent, end the media stream (swallow errors —
// it may already be closed), then terminate via the carrier. Ports HangUpAsync.
func (s *carrierCallSession) HangUp(ctx context.Context) error {
	s.setStatus(CallStatusEndedByAgent)
	_ = s.media.End(ctx) // media may already be closed
	return s.ops.endCall(ctx, s.Info().CallID)
}

// OnStatusChanged subscribes to lifecycle status changes.
func (s *carrierCallSession) OnStatusChanged(handler func(CallStatus)) func() {
	return s.notifier.subscribe(handler)
}

// Close unsubscribes from the media stream and disposes it. Ports DisposeAsync.
func (s *carrierCallSession) Close(ctx context.Context) error {
	if s.unsubMedia != nil {
		s.unsubMedia()
	}
	return s.media.Close(ctx)
}

// onMediaStatusChanged re-publishes a media status change through the session.
// Ports OnMediaStatusChanged => SetStatus(status).
func (s *carrierCallSession) onMediaStatusChanged(status CallStatus) {
	s.setStatus(status)
}

// setStatus dedupes and fires. Ports SetStatus: no-op when unchanged, else set +
// invoke StatusChanged. The notifier fires after releasing the lock.
func (s *carrierCallSession) setStatus(status CallStatus) {
	s.mu.Lock()
	if s.status == status {
		s.mu.Unlock()
		return
	}
	s.status = status
	s.mu.Unlock()
	s.notifier.fire(status)
}

// stringsTrimSpaceNonEmpty reports whether s is non-blank after trimming, i.e.
// !string.IsNullOrWhiteSpace(s).
func stringsTrimSpaceNonEmpty(s string) bool {
	for _, r := range s {
		if r != ' ' && r != '\t' && r != '\n' && r != '\r' && r != '\v' && r != '\f' {
			return true
		}
	}
	return false
}

var _ ICallSession = (*carrierCallSession)(nil)
