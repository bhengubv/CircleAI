// telephony_contracts.go
//
// Ports the CircleAI.Telephony contract surface:
//   Contracts.cs:
//     ITelephonyCarrier        -> ITelephonyCarrier interface
//     OutboundDialOptions      -> OutboundDialOptions value struct
//     ICallSession             -> ICallSession interface (IAsyncDisposable -> Close)
//     IInboundCallDispatcher   -> IInboundCallDispatcher interface
//   IMediaStream.cs:
//     IMediaStream             -> IMediaStream interface (IAsyncDisposable -> Close)
//
// C#→Go stream/event mapping (kept identical across the telephony slice):
//   IAsyncEnumerable<T> ReceiveXAsync(ct)      -> ReceiveX(ctx) <-chan T
//   ValueTask / ValueTask<T>                    -> method returning (…, error)
//   IAsyncDisposable.DisposeAsync()             -> Close(ctx) error
//   event EventHandler<CallStatus> StatusChanged-> OnStatusChanged(func(CallStatus)) (returns an
//                                                  unsubscribe func); fired for every distinct change
//
// A method taking a context.Context replaces the CancellationToken: cancelling
// the context stops a Receive stream (its channel closes) exactly as cancelling
// the CancellationToken ends the IAsyncEnumerable.

package circleai

import (
	"context"
	"net/url"
)

// ITelephonyCarrier is the carrier integration seam — where CircleAI talks to a
// phone-network operator (Twilio, Telnyx, Plivo, or a SIP gateway). Ports
// ITelephonyCarrier.
//
// Inbound: the carrier delivers a call to us and emits an ICallSession via the
// host's webhook plumbing (see IInboundCallDispatcher). Outbound: the caller
// asks us to dial and we call Dial.
type ITelephonyCarrier interface {
	// CarrierID is the stable carrier id — "twilio" / "telnyx" / "plivo" / "null".
	CarrierID() string

	// IsConfigured is true when the carrier has the credentials + base
	// addresses it needs.
	IsConfigured() bool

	// ProvisionNumber buys a new phone number for the given ISO 3166-1 alpha-2
	// country code. areaCode selects an offered area code ("" = any).
	ProvisionNumber(ctx context.Context, countryCode, areaCode string) (ProvisionedNumber, error)

	// ConfigureInboundWebhook configures a number we already own to route
	// inbound calls to our host-provided endpoint.
	ConfigureInboundWebhook(ctx context.Context, phoneNumber string, inboundWebhook *url.URL) error

	// Dial places an outbound call. streamURL is where the carrier should stream
	// the live media (a WebSocket URL on our host). Returns a session the caller
	// can attach an agent to. opts may be nil for defaults.
	Dial(ctx context.Context, fromNumber, toNumber string, streamURL *url.URL, opts *OutboundDialOptions) (ICallSession, error)

	// ListNumbers lists the numbers we own on this carrier.
	ListNumbers(ctx context.Context) ([]ProvisionedNumber, error)
}

// OutboundDialOptions holds optional knobs for an outbound dial. Ports the
// OutboundDialOptions record. The zero value mirrors the C# defaults EXCEPT
// RingTimeoutSeconds, whose C# default is 30 — use NewOutboundDialOptions or set
// it explicitly. A nil *OutboundDialOptions passed to Dial means "all defaults".
type OutboundDialOptions struct {
	// DetectAnsweringMachine — if true, detect voicemail and surface
	// CallStatusVoicemail.
	DetectAnsweringMachine bool

	// RingTimeoutSeconds — how long to ring before treating it as no-answer.
	// C# default 30.
	RingTimeoutSeconds int

	// CallerIDOverride — optional caller-id override (must be a number you own).
	// "" means no override (C# null).
	CallerIDOverride string

	// FollowMeNumbers — optional list of E.164 numbers to also dial if the
	// primary doesn't answer (round-robin). nil = none.
	FollowMeNumbers []string
}

// NewOutboundDialOptions returns options with the C# defaults applied
// (RingTimeoutSeconds = 30). This mirrors `new OutboundDialOptions()`.
func NewOutboundDialOptions() *OutboundDialOptions {
	return &OutboundDialOptions{RingTimeoutSeconds: 30}
}

// effectiveDialOptions returns opts with the C# default RingTimeoutSeconds (30)
// substituted when the caller passed nil (C# `options ?? new OutboundDialOptions()`).
func effectiveDialOptions(opts *OutboundDialOptions) OutboundDialOptions {
	if opts == nil {
		return OutboundDialOptions{RingTimeoutSeconds: 30}
	}
	return *opts
}

// ICallSession is a live call session. The agent talks to this and does not know
// or care which carrier is on the other side. Ports ICallSession (IAsyncDisposable
// -> Close). Audio in / audio out / hang up / transfer / DTMF.
type ICallSession interface {
	// Info is the stable carrier-supplied metadata captured at call start.
	Info() CallInfo

	// Status is the current lifecycle status.
	Status() CallStatus

	// ReceiveAudio streams audio frames arriving from the caller. Cancel ctx to
	// stop receiving; the returned channel then closes.
	ReceiveAudio(ctx context.Context) <-chan AudioFrame

	// SendAudio sends one audio frame to the caller.
	SendAudio(ctx context.Context, frame AudioFrame) error

	// ReceiveDtmf streams DTMF tones the caller is pressing.
	ReceiveDtmf(ctx context.Context) <-chan DtmfEvent

	// SendDtmf sends DTMF tones from the AI side (for navigating other people's menus).
	SendDtmf(ctx context.Context, digits string) error

	// Transfer transfers the call to targetNumber. Cold = drop and forget.
	// Warm = park the caller, dial the human, brief them, bridge both. briefing
	// may be "" (C# null).
	Transfer(ctx context.Context, targetNumber string, mode TransferMode, briefing string) error

	// HangUp ends the call from our side.
	HangUp(ctx context.Context) error

	// OnStatusChanged subscribes to lifecycle status changes and returns an
	// unsubscribe func. Ports `event EventHandler<CallStatus> StatusChanged`.
	OnStatusChanged(handler func(CallStatus)) (unsubscribe func())

	// Close disposes the session (ports IAsyncDisposable.DisposeAsync).
	Close(ctx context.Context) error
}

// IInboundCallDispatcher materialises inbound ICallSessions. The carrier-provided
// HTTP handler (wired into the host's routing) calls into the dispatcher.
// Ports IInboundCallDispatcher.
type IInboundCallDispatcher interface {
	// CarrierID is the stable id of the carrier feeding inbound calls in.
	CarrierID() string

	// Subscribe subscribes to inbound call sessions. Each new call invokes
	// handler with a session the consumer attaches their agent to. Returns an
	// unsubscribe func (ports IDisposable).
	Subscribe(handler func(context.Context, ICallSession) error) (unsubscribe func())
}

// IMediaStream is a live media channel for one call. The carrier host's
// WebSocket handler implements this; the carrier session consumes it. Ports
// IMediaStream (IAsyncDisposable -> Close). Keeping this carrier-agnostic lets
// the carrier bindings stay transport-free.
type IMediaStream interface {
	// CallInfo is the carrier call id + metadata captured at connect.
	CallInfo() CallInfo

	// ReceiveAudio streams inbound audio frames from the caller.
	ReceiveAudio(ctx context.Context) <-chan AudioFrame

	// SendAudio sends one outbound audio frame to the caller.
	SendAudio(ctx context.Context, frame AudioFrame) error

	// ReceiveDtmf streams inbound DTMF events.
	ReceiveDtmf(ctx context.Context) <-chan DtmfEvent

	// End marks the call ended from our side. Closes the underlying stream.
	End(ctx context.Context) error

	// OnStatusChanged fires when the carrier reports the call status changed and
	// returns an unsubscribe func. Ports `event EventHandler<CallStatus> StatusChanged`.
	OnStatusChanged(handler func(CallStatus)) (unsubscribe func())

	// CurrentStatus is the current lifecycle state.
	CurrentStatus() CallStatus

	// Close disposes the media stream (ports IAsyncDisposable.DisposeAsync).
	Close(ctx context.Context) error
}

// IDtmfSendable is the optional sister interface a media stream can implement to
// support carrier-native out-of-band DTMF (Twilio mark control frame, Telnyx
// send_dtmf, Plivo control event). When the media stream does not implement it,
// the session falls back to in-band tones via the DTMF tone generator. Ports
// IDtmfSendable.
type IDtmfSendable interface {
	// SendDtmf sends the digits out-of-band.
	SendDtmf(ctx context.Context, digits string) error
}
