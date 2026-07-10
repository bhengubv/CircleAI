// telephony_provisioner.go
//
// Ports CircleAI.Telephony/PhoneNumberProvisioner.cs:
//   PhoneNumberProvisioner        -> PhoneNumberProvisioner
//   IProvisionedNumberStore       -> IProvisionedNumberStore interface
//   InMemoryProvisionedNumberStore-> InMemoryProvisionedNumberStore (thread-safe)
//
// The provisioner orchestrates buy + configure-webhook + persist over any
// ITelephonyCarrier, and List() merges the local store with the carrier's
// authoritative list (carrier wins on conflict). Number keys are matched
// case-insensitively, mirroring the C# StringComparer.OrdinalIgnoreCase.

package circleai

import (
	"context"
	"errors"
	"net/url"
	"sort"
	"strings"
	"sync"
)

// IProvisionedNumberStore is the persistence contract for assigned numbers.
// Ports IProvisionedNumberStore. The default in-memory implementation is fine
// for dev; production hosts plug in a database-backed store.
type IProvisionedNumberStore interface {
	// Save upserts a number.
	Save(ctx context.Context, number ProvisionedNumber) error
	// List returns all stored numbers.
	List(ctx context.Context) ([]ProvisionedNumber, error)
	// Find returns the number and true, or (zero, false) if absent (C# null).
	Find(ctx context.Context, phoneNumber string) (ProvisionedNumber, bool, error)
	// Remove deletes a number (no-op if absent).
	Remove(ctx context.Context, phoneNumber string) error
}

// InMemoryProvisionedNumberStore is the default thread-safe in-memory store.
// Ports InMemoryProvisionedNumberStore. Keyed case-insensitively by phone number.
type InMemoryProvisionedNumberStore struct {
	mu       sync.Mutex
	byNumber map[string]ProvisionedNumber // key = lower-cased phone number
}

// NewInMemoryProvisionedNumberStore constructs an empty store.
func NewInMemoryProvisionedNumberStore() *InMemoryProvisionedNumberStore {
	return &InMemoryProvisionedNumberStore{byNumber: make(map[string]ProvisionedNumber)}
}

// Save upserts number. Ports SaveAsync (requires a non-empty number).
func (s *InMemoryProvisionedNumberStore) Save(_ context.Context, number ProvisionedNumber) error {
	if number.PhoneNumber == "" {
		return errors.New("number is required")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.byNumber[strings.ToLower(number.PhoneNumber)] = number
	return nil
}

// List returns all stored numbers. Ports ListAsync. Order is by phone number so
// the result is deterministic (the C# returns dictionary-values order, which is
// unspecified; sorting keeps tests stable without changing set semantics).
func (s *InMemoryProvisionedNumberStore) List(_ context.Context) ([]ProvisionedNumber, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]ProvisionedNumber, 0, len(s.byNumber))
	for _, v := range s.byNumber {
		out = append(out, v)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].PhoneNumber < out[j].PhoneNumber })
	return out, nil
}

// Find returns the number and true, or (zero,false) if absent. Ports FindAsync.
func (s *InMemoryProvisionedNumberStore) Find(_ context.Context, phoneNumber string) (ProvisionedNumber, bool, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	v, ok := s.byNumber[strings.ToLower(phoneNumber)]
	return v, ok, nil
}

// Remove deletes a number. Ports RemoveAsync (no-op when absent).
func (s *InMemoryProvisionedNumberStore) Remove(_ context.Context, phoneNumber string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	delete(s.byNumber, strings.ToLower(phoneNumber))
	return nil
}

// PhoneNumberProvisioner buys + configures + persists numbers from any carrier
// behind ITelephonyCarrier. Ports PhoneNumberProvisioner.
type PhoneNumberProvisioner struct {
	carrier ITelephonyCarrier
	store   IProvisionedNumberStore
}

// NewPhoneNumberProvisioner constructs the provisioner. carrier is required; a
// nil store defaults to a fresh InMemoryProvisionedNumberStore (ports the C#
// constructor's `store ?? new InMemoryProvisionedNumberStore()`).
func NewPhoneNumberProvisioner(carrier ITelephonyCarrier, store IProvisionedNumberStore) (*PhoneNumberProvisioner, error) {
	if carrier == nil {
		return nil, errors.New("carrier is required")
	}
	if store == nil {
		store = NewInMemoryProvisionedNumberStore()
	}
	return &PhoneNumberProvisioner{carrier: carrier, store: store}, nil
}

// Provision buys a number, wires its inbound webhook, persists it, and returns
// the metadata. Ports ProvisionAsync. areaCode "" = any. inboundWebhook must be
// absolute. A webhook-configuration failure propagates (the number is NOT
// persisted), matching the C# which rethrows before SaveAsync.
func (p *PhoneNumberProvisioner) Provision(ctx context.Context, countryCode string, inboundWebhook *url.URL, areaCode string) (ProvisionedNumber, error) {
	if strings.TrimSpace(countryCode) == "" {
		return ProvisionedNumber{}, errors.New("countryCode is required")
	}
	if inboundWebhook == nil {
		return ProvisionedNumber{}, errors.New("inboundWebhook is required")
	}
	if !inboundWebhook.IsAbs() {
		return ProvisionedNumber{}, errors.New("inboundWebhook must be an absolute URI")
	}

	provisioned, err := p.carrier.ProvisionNumber(ctx, countryCode, areaCode)
	if err != nil {
		return ProvisionedNumber{}, err
	}

	if err := p.carrier.ConfigureInboundWebhook(ctx, provisioned.PhoneNumber, inboundWebhook); err != nil {
		return ProvisionedNumber{}, err
	}

	if err := p.store.Save(ctx, provisioned); err != nil {
		return ProvisionedNumber{}, err
	}
	return provisioned, nil
}

// List returns the provisioned numbers we know about, merging the store with the
// carrier's authoritative list (carrier wins on conflict). Ports ListAsync.
func (p *PhoneNumberProvisioner) List(ctx context.Context) ([]ProvisionedNumber, error) {
	stored, err := p.store.List(ctx)
	if err != nil {
		return nil, err
	}
	carrierNumbers, err := p.carrier.ListNumbers(ctx)
	if err != nil {
		return nil, err
	}
	merged := make(map[string]ProvisionedNumber, len(stored)+len(carrierNumbers))
	for _, n := range stored {
		merged[strings.ToLower(n.PhoneNumber)] = n
	}
	for _, n := range carrierNumbers {
		merged[strings.ToLower(n.PhoneNumber)] = n // carrier authoritative
	}
	out := make([]ProvisionedNumber, 0, len(merged))
	for _, v := range merged {
		out = append(out, v)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].PhoneNumber < out[j].PhoneNumber })
	return out, nil
}

var _ IProvisionedNumberStore = (*InMemoryProvisionedNumberStore)(nil)
