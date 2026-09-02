// telephony_inmemory_carrier.go
//
// Deterministic, hermetic ITelephonyCarrier + IInboundCallDispatcher with no
// network. There is no C# 1:1 — the C# carriers all speak a live REST API. Per
// the work-unit note ("port the carrier abstraction + a deterministic in-memory
// fake carrier; the real HTTP carrier is an injected dependency") this is that
// fake: it provisions from a scripted catalogue, tracks owned numbers, dials into
// an InMemoryMediaStream-backed session, and delivers inbound sessions to
// subscribers.
//
// Divergence hooks (endCall / coldTransfer) are satisfied in-process: hang-up
// flips the session's media status to EndedByAgent; cold transfer records the
// target and flips to Transferred. Everything is observable for tests.
//
// CONCURRENCY: the inbound dispatcher registers each subscriber SYNCHRONOUSLY
// under its lock, so a session delivered right after Subscribe returns is never
// lost to a subscribe-vs-publish race (Wave-1 rule).

package circleai

import (
	"context"
	"errors"
	"net/url"
	"strings"
	"sync"
	"time"
)

// InMemoryTelephonyCarrier is a deterministic fake ITelephonyCarrier. It is
// always "configured" (unless explicitly marked otherwise) and performs no I/O.
type InMemoryTelephonyCarrier struct {
	id         string
	configured bool
	now        func() time.Time
	seq        func() string // unique call-id generator

	mu         sync.Mutex
	catalogue  map[string][]catalogueEntry // country code -> available numbers
	owned      map[string]ProvisionedNumber
	webhooks   map[string]*url.URL    // phone number -> configured inbound webhook
	dials      []*InMemoryMediaStream // media streams created by Dial (for inspection)
	dispatcher *InMemoryInboundCallDispatcher
}

// catalogueEntry is one buyable number in the fake carrier's inventory.
type catalogueEntry struct {
	number      string
	areaCode    string
	monthlyCost Decimal
}

// InMemoryTelephonyCarrierOption configures the fake carrier.
type InMemoryTelephonyCarrierOption func(*InMemoryTelephonyCarrier)

// WithCarrierClock injects a deterministic clock (defaults to time.Now).
func WithCarrierClock(now func() time.Time) InMemoryTelephonyCarrierOption {
	return func(c *InMemoryTelephonyCarrier) {
		if now != nil {
			c.now = now
		}
	}
}

// WithCarrierUnconfigured marks the carrier not-configured (IsConfigured=false),
// so provisioning/dialing fail-soft the way a real carrier does without creds.
func WithCarrierUnconfigured() InMemoryTelephonyCarrierOption {
	return func(c *InMemoryTelephonyCarrier) { c.configured = false }
}

// NewInMemoryTelephonyCarrier constructs a fake carrier with the given id (e.g.
// "twilio"/"fake"). It starts configured with an empty catalogue; seed buyable
// numbers with AddAvailableNumber.
func NewInMemoryTelephonyCarrier(id string, opts ...InMemoryTelephonyCarrierOption) *InMemoryTelephonyCarrier {
	c := &InMemoryTelephonyCarrier{
		id:         id,
		configured: true,
		now:        time.Now,
		catalogue:  make(map[string][]catalogueEntry),
		owned:      make(map[string]ProvisionedNumber),
		webhooks:   make(map[string]*url.URL),
	}
	var seq int
	var seqMu sync.Mutex
	c.seq = func() string {
		seqMu.Lock()
		seq++
		n := seq
		seqMu.Unlock()
		return c.id + "-call-" + itoaSmall(n)
	}
	c.dispatcher = newInMemoryInboundCallDispatcher(id)
	for _, o := range opts {
		o(c)
	}
	return c
}

// AddAvailableNumber seeds one buyable number into the catalogue for a country.
// areaCode "" makes it match an any-area-code request only.
func (c *InMemoryTelephonyCarrier) AddAvailableNumber(countryCode, areaCode, number string, monthlyCost Decimal) {
	c.mu.Lock()
	defer c.mu.Unlock()
	cc := strings.ToUpper(countryCode)
	c.catalogue[cc] = append(c.catalogue[cc], catalogueEntry{number: number, areaCode: areaCode, monthlyCost: monthlyCost})
}

// CarrierID returns the id.
func (c *InMemoryTelephonyCarrier) CarrierID() string { return c.id }

// IsConfigured reports whether the carrier is configured.
func (c *InMemoryTelephonyCarrier) IsConfigured() bool { return c.configured }

// ProvisionNumber buys the first matching catalogue number for the country/area,
// moves it to owned, and returns its metadata. Errors when unconfigured (like a
// real carrier missing creds) or when nothing matches.
func (c *InMemoryTelephonyCarrier) ProvisionNumber(_ context.Context, countryCode, areaCode string) (ProvisionedNumber, error) {
	if !c.configured {
		return ProvisionedNumber{}, errors.New(c.id + " carrier is not configured.")
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	cc := strings.ToUpper(countryCode)
	list := c.catalogue[cc]
	idx := -1
	for i, e := range list {
		if areaCode == "" || e.areaCode == areaCode {
			idx = i
			break
		}
	}
	if idx < 0 {
		return ProvisionedNumber{}, errors.New(c.id + " has no available numbers in country='" + countryCode + "', areaCode='" + areaCode + "'.")
	}
	entry := list[idx]
	// Consume it from the catalogue.
	c.catalogue[cc] = append(append([]catalogueEntry(nil), list[:idx]...), list[idx+1:]...)

	pn := ProvisionedNumber{
		PhoneNumber:          entry.number,
		CarrierID:            c.id,
		ProvisionedAtUTC:     c.now().UTC(),
		MonthlyRecurringCost: entry.monthlyCost,
	}
	c.owned[strings.ToLower(entry.number)] = pn
	return pn, nil
}

// ConfigureInboundWebhook records the webhook for an owned number. Errors when
// the number is not owned (mirrors the real carriers rejecting a foreign number).
func (c *InMemoryTelephonyCarrier) ConfigureInboundWebhook(_ context.Context, phoneNumber string, inboundWebhook *url.URL) error {
	if !c.configured {
		return errors.New(c.id + " carrier is not configured.")
	}
	if inboundWebhook == nil {
		return errors.New("inboundWebhook is required")
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	if _, ok := c.owned[strings.ToLower(phoneNumber)]; !ok {
		return errors.New("Phone number '" + phoneNumber + "' is not owned on this carrier.")
	}
	c.webhooks[strings.ToLower(phoneNumber)] = inboundWebhook
	return nil
}

// Dial creates an in-memory outbound call session. The returned session wraps an
// InMemoryMediaStream (retrievable via LastDial) that starts Active — the fake
// "picks up" immediately so tests can push/pull audio without a socket. Errors
// when unconfigured.
func (c *InMemoryTelephonyCarrier) Dial(_ context.Context, fromNumber, toNumber string, streamURL *url.URL, opts *OutboundDialOptions) (ICallSession, error) {
	if !c.configured {
		return nil, errors.New(c.id + " carrier is not configured.")
	}
	if strings.TrimSpace(toNumber) == "" {
		return nil, errors.New("toNumber is required")
	}
	o := effectiveDialOptions(opts)
	from := fromNumber
	if o.CallerIDOverride != "" {
		from = o.CallerIDOverride
	}
	info := CallInfo{
		CallID:       c.seq(),
		Direction:    CallDirectionOutbound,
		From:         from,
		To:           toNumber,
		CarrierID:    c.id,
		MediaFormat:  CallMediaFormatMulaw8000,
		StartedAtUTC: c.now().UTC(),
	}
	media := NewInMemoryMediaStream(info, CallStatusActive)
	c.mu.Lock()
	c.dials = append(c.dials, media)
	c.mu.Unlock()
	return newCarrierCallSession(media, &inMemoryCarrierOps{carrier: c, media: media}, warmTransferConfig{carrier: c}), nil
}

// ListNumbers returns the owned numbers. Ports ListNumbersAsync (empty when
// unconfigured).
func (c *InMemoryTelephonyCarrier) ListNumbers(_ context.Context) ([]ProvisionedNumber, error) {
	if !c.configured {
		return []ProvisionedNumber{}, nil
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	out := make([]ProvisionedNumber, 0, len(c.owned))
	for _, v := range c.owned {
		out = append(out, v)
	}
	return out, nil
}

// Dispatcher returns the carrier's inbound dispatcher, so a host/test can deliver
// simulated inbound calls to subscribers.
func (c *InMemoryTelephonyCarrier) Dispatcher() *InMemoryInboundCallDispatcher { return c.dispatcher }

// LastDial returns the media stream of the most recent Dial, and true, or
// (nil,false) if Dial was never called. Lets a test drive the "far end."
func (c *InMemoryTelephonyCarrier) LastDial() (*InMemoryMediaStream, bool) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if len(c.dials) == 0 {
		return nil, false
	}
	return c.dials[len(c.dials)-1], true
}

// WebhookFor returns the configured inbound webhook for an owned number.
func (c *InMemoryTelephonyCarrier) WebhookFor(phoneNumber string) (*url.URL, bool) {
	c.mu.Lock()
	defer c.mu.Unlock()
	u, ok := c.webhooks[strings.ToLower(phoneNumber)]
	return u, ok
}

// DeliverInbound builds an inbound call session (backed by a fresh Active
// InMemoryMediaStream) for a call from->to and hands it to every subscriber of
// the dispatcher, returning the media stream so the caller can drive the far end.
// This is how a test simulates an incoming call.
func (c *InMemoryTelephonyCarrier) DeliverInbound(ctx context.Context, from, to string) (*InMemoryMediaStream, error) {
	info := CallInfo{
		CallID:       c.seq(),
		Direction:    CallDirectionInbound,
		From:         from,
		To:           to,
		CarrierID:    c.id,
		MediaFormat:  CallMediaFormatMulaw8000,
		StartedAtUTC: c.now().UTC(),
	}
	media := NewInMemoryMediaStream(info, CallStatusActive)
	session := newCarrierCallSession(media, &inMemoryCarrierOps{carrier: c, media: media}, warmTransferConfig{carrier: c})
	if err := c.dispatcher.deliver(ctx, session); err != nil {
		return nil, err
	}
	return media, nil
}

// inMemoryCarrierOps satisfies carrierSessionOps for the fake carrier, driving
// the paired InMemoryMediaStream directly (no network).
type inMemoryCarrierOps struct {
	carrier *InMemoryTelephonyCarrier
	media   *InMemoryMediaStream
}

// endCall flips the media stream to EndedByAgent.
func (o *inMemoryCarrierOps) endCall(ctx context.Context, _ string) error {
	return o.media.End(ctx)
}

// coldTransfer records the target and flips the media stream to Transferred.
func (o *inMemoryCarrierOps) coldTransfer(_ context.Context, _ string, targetNumber string) error {
	if strings.TrimSpace(targetNumber) == "" {
		return errors.New("targetNumber is required")
	}
	o.media.SetStatus(CallStatusTransferred)
	return nil
}

// ---------------------------------------------------------------------------
// InMemoryInboundCallDispatcher
// ---------------------------------------------------------------------------

// InMemoryInboundCallDispatcher is a deterministic IInboundCallDispatcher. It
// fans each delivered session out to every current subscriber. Ports the
// dispatcher contract with a real (in-memory) delivery path.
type InMemoryInboundCallDispatcher struct {
	carrierID string

	mu     sync.Mutex
	subs   map[int]func(context.Context, ICallSession) error
	nextID int
}

// newInMemoryInboundCallDispatcher constructs an empty dispatcher.
func newInMemoryInboundCallDispatcher(carrierID string) *InMemoryInboundCallDispatcher {
	return &InMemoryInboundCallDispatcher{carrierID: carrierID, subs: make(map[int]func(context.Context, ICallSession) error)}
}

// CarrierID returns the carrier id.
func (d *InMemoryInboundCallDispatcher) CarrierID() string { return d.carrierID }

// Subscribe registers handler synchronously and returns an unsubscribe func.
func (d *InMemoryInboundCallDispatcher) Subscribe(handler func(context.Context, ICallSession) error) func() {
	if handler == nil {
		return func() {}
	}
	d.mu.Lock()
	id := d.nextID
	d.nextID++
	d.subs[id] = handler
	d.mu.Unlock()
	return func() {
		d.mu.Lock()
		delete(d.subs, id)
		d.mu.Unlock()
	}
}

// deliver hands session to every current subscriber. Handlers are snapshotted
// under the lock, then invoked after release (so a handler that subscribes/
// unsubscribes cannot deadlock). The first handler error is returned after all
// have been invoked.
func (d *InMemoryInboundCallDispatcher) deliver(ctx context.Context, session ICallSession) error {
	d.mu.Lock()
	snapshot := make([]func(context.Context, ICallSession) error, 0, len(d.subs))
	for _, h := range d.subs {
		snapshot = append(snapshot, h)
	}
	d.mu.Unlock()
	var firstErr error
	for _, h := range snapshot {
		if err := h(ctx, session); err != nil && firstErr == nil {
			firstErr = err
		}
	}
	return firstErr
}

// Interface guards.
var (
	_ ITelephonyCarrier      = (*InMemoryTelephonyCarrier)(nil)
	_ IInboundCallDispatcher = (*InMemoryInboundCallDispatcher)(nil)
	_ carrierSessionOps      = (*inMemoryCarrierOps)(nil)
)
