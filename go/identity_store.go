// identity_store.go
//
// Ports CircleAI.Identity.InMemoryIdentityStore:
//   InMemoryIdentityStore.cs -> InMemoryIdentityStore
//
// In-memory IIdentityStore for development and testing. Complements the
// identity types + interfaces already declared in identity.go. Thread-safe.

package circleai

import (
	"context"
	"sync"
)

// InMemoryIdentityStore is an in-memory IIdentityStore for development and
// testing. Ports InMemoryIdentityStore.
type InMemoryIdentityStore struct {
	mu         sync.Mutex
	identities map[string]CircleIdentity
	devices    map[string]RegisteredDevice
}

// NewInMemoryIdentityStore creates an empty store.
func NewInMemoryIdentityStore() *InMemoryIdentityStore {
	return &InMemoryIdentityStore{
		identities: make(map[string]CircleIdentity),
		devices:    make(map[string]RegisteredDevice),
	}
}

// Get returns the identity with the given identityID, or nil if not found.
func (s *InMemoryIdentityStore) Get(ctx context.Context, identityID string) (*CircleIdentity, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if id, ok := s.identities[identityID]; ok {
		out := id
		return &out, nil
	}
	return nil, nil
}

// Save persists the given identity, keyed by its IdentityID.
func (s *InMemoryIdentityStore) Save(ctx context.Context, identity CircleIdentity) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.identities[identity.IdentityID] = identity
	return nil
}

// GetDevices returns all devices registered to the given identity.
func (s *InMemoryIdentityStore) GetDevices(ctx context.Context, identityID string) ([]RegisteredDevice, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	var out []RegisteredDevice
	for _, d := range s.devices {
		if d.IdentityID == identityID {
			out = append(out, d)
		}
	}
	return out, nil
}

// RegisterDevice records a device as belonging to an identity, keyed by DeviceID.
func (s *InMemoryIdentityStore) RegisterDevice(ctx context.Context, device RegisteredDevice) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.devices[device.DeviceID] = device
	return nil
}

// GetByDevice returns the identity that owns the given deviceID, or nil.
func (s *InMemoryIdentityStore) GetByDevice(ctx context.Context, deviceID string) (*CircleIdentity, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	device, ok := s.devices[deviceID]
	if !ok {
		return nil, nil
	}
	if id, ok := s.identities[device.IdentityID]; ok {
		out := id
		return &out, nil
	}
	return nil, nil
}

var _ IIdentityStore = (*InMemoryIdentityStore)(nil)
