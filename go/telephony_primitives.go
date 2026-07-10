// telephony_primitives.go
//
// Ports CircleAI.Telephony/Primitives.cs — the shared value types for the
// telephony surface:
//   CallDirection    -> CallDirection      (int enum, stable ordinals)
//   CallStatus       -> CallStatus         (int enum, stable ordinals)
//   CallMediaFormat  -> CallMediaFormat    (int enum, stable ordinals)
//   TransferMode     -> TransferMode       (int enum, stable ordinals)
//   CallInfo         -> CallInfo           (immutable value struct)
//   CallSnapshot     -> CallSnapshot       (value struct)
//   AudioFrame       -> AudioFrame         (value struct)
//   DtmfEvent        -> DtmfEvent          (value struct)
//   ProvisionedNumber-> ProvisionedNumber  (value struct)
//
// C# records are value-equal immutable structs; the Go ports are plain structs
// compared by value (all fields comparable except AudioFrame.Pcm, a []byte). The
// C# enums carry no explicit numeric values, so ordinals follow declaration
// order exactly — preserved here so cross-language wire/ordinal comparisons hold.

package circleai

import "time"

// CallDirection is the direction of a call. Ports CallDirection.
type CallDirection int

const (
	// CallDirectionInbound — the remote party initiated (ordinal 0).
	CallDirectionInbound CallDirection = iota
	// CallDirectionOutbound — we initiated (ordinal 1).
	CallDirectionOutbound
)

// String renders the C# enum member name.
func (d CallDirection) String() string {
	switch d {
	case CallDirectionInbound:
		return "Inbound"
	case CallDirectionOutbound:
		return "Outbound"
	default:
		return "CallDirection(" + itoaSmall(int(d)) + ")"
	}
}

// CallStatus is a call lifecycle state. Ports CallStatus. Ordinals follow the
// C# declaration order: Ringing=0, Active=1, EndedByCaller=2, EndedByCallee=3,
// EndedByAgent=4, Voicemail=5, Failed=6, Transferred=7.
type CallStatus int

const (
	// CallStatusRinging — carrier accepted the dial, other end not yet picked up.
	CallStatusRinging CallStatus = iota
	// CallStatusActive — both sides connected; media flowing.
	CallStatusActive
	// CallStatusEndedByCaller — caller hung up.
	CallStatusEndedByCaller
	// CallStatusEndedByCallee — callee hung up.
	CallStatusEndedByCallee
	// CallStatusEndedByAgent — the AI agent (us) ended the call.
	CallStatusEndedByAgent
	// CallStatusVoicemail — carrier-detected voicemail on outbound dial.
	CallStatusVoicemail
	// CallStatusFailed — call did not connect (busy, no answer, network).
	CallStatusFailed
	// CallStatusTransferred — call transferred to a human or different agent.
	CallStatusTransferred
)

// String renders the C# enum member name.
func (s CallStatus) String() string {
	switch s {
	case CallStatusRinging:
		return "Ringing"
	case CallStatusActive:
		return "Active"
	case CallStatusEndedByCaller:
		return "EndedByCaller"
	case CallStatusEndedByCallee:
		return "EndedByCallee"
	case CallStatusEndedByAgent:
		return "EndedByAgent"
	case CallStatusVoicemail:
		return "Voicemail"
	case CallStatusFailed:
		return "Failed"
	case CallStatusTransferred:
		return "Transferred"
	default:
		return "CallStatus(" + itoaSmall(int(s)) + ")"
	}
}

// CallMediaFormat is an audio wire format. Ports CallMediaFormat. Ordinals:
// Mulaw8000=0, Alaw8000=1, Pcm16000=2, Pcm24000=3.
type CallMediaFormat int

const (
	// CallMediaFormatMulaw8000 — µ-law 8 kHz mono (Twilio/Plivo default).
	CallMediaFormatMulaw8000 CallMediaFormat = iota
	// CallMediaFormatAlaw8000 — A-law 8 kHz mono.
	CallMediaFormatAlaw8000
	// CallMediaFormatPcm16000 — linear PCM 16-bit 16 kHz mono (Telnyx).
	CallMediaFormatPcm16000
	// CallMediaFormatPcm24000 — linear PCM 16-bit 24 kHz mono (WebRTC/OpenAI).
	CallMediaFormatPcm24000
)

// String renders the C# enum member name.
func (f CallMediaFormat) String() string {
	switch f {
	case CallMediaFormatMulaw8000:
		return "Mulaw8000"
	case CallMediaFormatAlaw8000:
		return "Alaw8000"
	case CallMediaFormatPcm16000:
		return "Pcm16000"
	case CallMediaFormatPcm24000:
		return "Pcm24000"
	default:
		return "CallMediaFormat(" + itoaSmall(int(f)) + ")"
	}
}

// TransferMode is the transfer style the AI requests. Ports TransferMode.
// Cold=0, Warm=1.
type TransferMode int

const (
	// TransferModeCold — drop the caller into the new line and hang up.
	TransferModeCold TransferMode = iota
	// TransferModeWarm — park caller, dial human, brief, then bridge both.
	TransferModeWarm
)

// String renders the C# enum member name.
func (m TransferMode) String() string {
	switch m {
	case TransferModeCold:
		return "Cold"
	case TransferModeWarm:
		return "Warm"
	default:
		return "TransferMode(" + itoaSmall(int(m)) + ")"
	}
}

// CallInfo is immutable metadata about one call, captured once at start.
// Ports the CallInfo record.
type CallInfo struct {
	CallID       string          // carrier-supplied unique id (Twilio CallSid, Telnyx call_control_id, ...)
	Direction    CallDirection   // who initiated
	From         string          // caller E.164 (e.g. +27821234567)
	To           string          // called party E.164
	CarrierID    string          // "twilio" / "telnyx" / "plivo" / ...
	MediaFormat  CallMediaFormat // wire format being streamed
	StartedAtUTC time.Time       // when the call started
}

// CallSnapshot is a snapshot of a call's current state. Ports CallSnapshot.
// CostSoFar mirrors the C# decimal (money) — modelled as Decimal (see
// telephony_decimal.go) so per-second cost arithmetic is exact.
type CallSnapshot struct {
	Info           CallInfo
	Status         CallStatus
	Duration       time.Duration
	CostSoFar      Decimal
	TransferTarget string // set when Status == Transferred; "" otherwise (C# null)
}

// AudioFrame is an audio chunk flowing caller<->AI. Ports AudioFrame.
// Pcm holds the raw samples (C# ReadOnlyMemory<byte>).
type AudioFrame struct {
	Pcm    []byte
	Format CallMediaFormat
	Offset time.Duration
}

// DtmfEvent is a DTMF tone from the caller. Ports DtmfEvent.
type DtmfEvent struct {
	Digit    rune          // 0-9, *, #
	Duration time.Duration // how long the caller held it
	Offset   time.Duration // when, relative to call start
}

// ProvisionedNumber is the result of a number-provisioning request. Ports
// ProvisionedNumber. MonthlyRecurringCost is a Decimal (C# decimal).
type ProvisionedNumber struct {
	PhoneNumber          string
	CarrierID            string
	ProvisionedAtUTC     time.Time
	MonthlyRecurringCost Decimal
}

// itoaSmall renders a small non-negative-or-negative int without importing
// strconv into every enum String() (kept local to avoid a fmt dependency here).
func itoaSmall(v int) string {
	if v == 0 {
		return "0"
	}
	neg := v < 0
	if neg {
		v = -v
	}
	var buf [20]byte
	i := len(buf)
	for v > 0 {
		i--
		buf[i] = byte('0' + v%10)
		v /= 10
	}
	if neg {
		i--
		buf[i] = '-'
	}
	return string(buf[i:])
}
