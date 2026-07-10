// telephony_null.go
//
// Ports CircleAI.Telephony/NullImplementations.cs:
//   NullTelephonyCarrier      -> NullTelephonyCarrier (fail-soft carrier)
//   NullInboundCallDispatcher -> NullInboundCallDispatcher (never fires)
// and CircleAI.Telephony/ServiceCollectionExtensions.cs:
//   CarrierFallback           -> CarrierFallback (first-configured-wins failover)
//
// The DI extension methods (AddCircleAiTelephony / AddTwilioCarrier / …) have no
// Go analogue in a flat package with no DI container; the composable pieces they
// wire (null carrier, null dispatcher, fallback, provisioner, in-memory store)
// are all exported so a host constructs them directly. AddCarrierFallback's core
// value type — CarrierFallback — is ported below.

package circleai

import (
	"context"
	"errors"
	"net/url"
)

// NullTelephonyCarrier is the null carrier — fail-soft on every operation. Ports
// NullTelephonyCarrier. Use NullTelephonyCarrierInstance for the shared singleton.
type NullTelephonyCarrier struct{}

// NullTelephonyCarrierInstance is the shared singleton (ports the C# static
// readonly Instance).
var NullTelephonyCarrierInstance = NullTelephonyCarrier{}

// CarrierID is "null".
func (NullTelephonyCarrier) CarrierID() string { return "null" }

// IsConfigured is always false.
func (NullTelephonyCarrier) IsConfigured() bool { return false }

// ProvisionNumber always errors — a real carrier must be registered. Ports the
// InvalidOperationException.
func (NullTelephonyCarrier) ProvisionNumber(_ context.Context, _, _ string) (ProvisionedNumber, error) {
	return ProvisionedNumber{}, errors.New(
		"Null carrier cannot provision phone numbers. Register a real ITelephonyCarrier (CircleAI.Telephony.Twilio / .Telnyx / .Plivo).")
}

// ConfigureInboundWebhook is a no-op (fail-soft). Ports the ValueTask.CompletedTask return.
func (NullTelephonyCarrier) ConfigureInboundWebhook(_ context.Context, _ string, _ *url.URL) error {
	return nil
}

// Dial always errors — a real carrier must be registered. Ports the
// InvalidOperationException.
func (NullTelephonyCarrier) Dial(_ context.Context, _, _ string, _ *url.URL, _ *OutboundDialOptions) (ICallSession, error) {
	return nil, errors.New("Null carrier cannot place outbound calls. Register a real ITelephonyCarrier.")
}

// ListNumbers returns an empty list. Ports Array.Empty<ProvisionedNumber>().
func (NullTelephonyCarrier) ListNumbers(_ context.Context) ([]ProvisionedNumber, error) {
	return []ProvisionedNumber{}, nil
}

// NullInboundCallDispatcher is the null inbound dispatcher — never fires. Ports
// NullInboundCallDispatcher. Use NullInboundCallDispatcherInstance for the singleton.
type NullInboundCallDispatcher struct{}

// NullInboundCallDispatcherInstance is the shared singleton.
var NullInboundCallDispatcherInstance = NullInboundCallDispatcher{}

// CarrierID is "null".
func (NullInboundCallDispatcher) CarrierID() string { return "null" }

// Subscribe returns a no-op unsubscribe; the handler is never invoked. Ports the
// NoopDisposable return.
func (NullInboundCallDispatcher) Subscribe(_ func(context.Context, ICallSession) error) func() {
	return func() {}
}

// CarrierFallback is a multi-carrier failover that picks the first configured
// carrier. Ports the internal CarrierFallback (materialised by AddCarrierFallback).
type CarrierFallback struct {
	carriers []ITelephonyCarrier
}

// NewCarrierFallback constructs a fallback over the given carriers, in order. A
// nil slice yields an empty fallback (Pick then returns the null carrier).
func NewCarrierFallback(carriers []ITelephonyCarrier) *CarrierFallback {
	cp := append([]ITelephonyCarrier(nil), carriers...)
	return &CarrierFallback{carriers: cp}
}

// CarrierID is "fallback(N)" where N is the carrier count. Ports the C# format.
func (f *CarrierFallback) CarrierID() string {
	return "fallback(" + itoaSmall(len(f.carriers)) + ")"
}

// IsConfigured is true when any wrapped carrier is configured. Ports Any(...).
func (f *CarrierFallback) IsConfigured() bool {
	for _, c := range f.carriers {
		if c.IsConfigured() {
			return true
		}
	}
	return false
}

// pick returns the first configured carrier, or the null carrier. Ports Pick().
func (f *CarrierFallback) pick() ITelephonyCarrier {
	for _, c := range f.carriers {
		if c.IsConfigured() {
			return c
		}
	}
	return NullTelephonyCarrierInstance
}

// ProvisionNumber delegates to the picked carrier.
func (f *CarrierFallback) ProvisionNumber(ctx context.Context, countryCode, areaCode string) (ProvisionedNumber, error) {
	return f.pick().ProvisionNumber(ctx, countryCode, areaCode)
}

// ConfigureInboundWebhook delegates to the picked carrier.
func (f *CarrierFallback) ConfigureInboundWebhook(ctx context.Context, phoneNumber string, inboundWebhook *url.URL) error {
	return f.pick().ConfigureInboundWebhook(ctx, phoneNumber, inboundWebhook)
}

// Dial delegates to the picked carrier.
func (f *CarrierFallback) Dial(ctx context.Context, fromNumber, toNumber string, streamURL *url.URL, opts *OutboundDialOptions) (ICallSession, error) {
	return f.pick().Dial(ctx, fromNumber, toNumber, streamURL, opts)
}

// ListNumbers delegates to the picked carrier.
func (f *CarrierFallback) ListNumbers(ctx context.Context) ([]ProvisionedNumber, error) {
	return f.pick().ListNumbers(ctx)
}

// parseAbsoluteURL parses s and requires it to be absolute, mirroring the way the
// warm-transfer orchestrator and carriers treat a required stream/webhook URL.
func parseAbsoluteURL(s string) (*url.URL, error) {
	u, err := url.Parse(s)
	if err != nil {
		return nil, err
	}
	if !u.IsAbs() {
		return nil, errors.New("URL must be absolute: " + s)
	}
	return u, nil
}

// Interface guards.
var (
	_ ITelephonyCarrier      = NullTelephonyCarrier{}
	_ ITelephonyCarrier      = (*CarrierFallback)(nil)
	_ IInboundCallDispatcher = NullInboundCallDispatcher{}
)
