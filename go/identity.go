// identity.go
//
// IdentityTier, CircleIdentity, RegisteredDevice, IIdentityStore,
// IIdentityProvider.
//
// A Circle AI identity is the unified persona key that travels with the
// person. Phone → Watch → Desktop → Smart Speaker → Car: same identity,
// same memory.

package circleai

import (
	"context"
	"time"
)

// ---------------------------------------------------------------------------
// IdentityTier
// ---------------------------------------------------------------------------

// IdentityTier is the verification level of a CircleIdentity.
type IdentityTier int

const (
	// IdentityTierAnonymous means no verification has been performed.
	IdentityTierAnonymous IdentityTier = iota

	// IdentityTierPseudonymous means the identity is self-asserted but not verified.
	IdentityTierPseudonymous

	// IdentityTierVerified means the identity has been verified (e.g. via phone OTP + biometrics).
	IdentityTierVerified
)

// ---------------------------------------------------------------------------
// CircleIdentity
// ---------------------------------------------------------------------------

// CircleIdentity is a Circle AI identity — the unified persona key that
// travels with the person across all devices and surfaces.
type CircleIdentity struct {
	// IdentityID is the stable GUID — never changes.
	IdentityID string

	// DisplayName is the user's display name.
	DisplayName string

	// PreferredLanguage is the IETF BCP-47 language tag preferred by this user.
	// nil means no preference.
	PreferredLanguage *string

	// Tier is the verification level of this identity.
	Tier IdentityTier

	// DeviceIDs holds the device IDs registered to this identity.
	DeviceIDs []string

	// CreatedAt is when this identity was first created (UTC).
	CreatedAt time.Time

	// LastSeenAt is when this identity was last active (UTC).
	LastSeenAt time.Time
}

// ---------------------------------------------------------------------------
// RegisteredDevice
// ---------------------------------------------------------------------------

// RegisteredDevice is a device registered to an identity.
type RegisteredDevice struct {
	// DeviceID is the unique identifier for this device.
	DeviceID string

	// IdentityID is the identity this device belongs to.
	IdentityID string

	// Platform is the device platform: "android" | "ios" | "windows" |
	// "macos" | "linux" | "web" | "watch" | "iot".
	Platform string

	// DeviceName is a human-readable name for the device.
	// nil when unknown.
	DeviceName *string

	// RegisteredAt is when this device was first registered (UTC).
	RegisteredAt time.Time

	// LastActiveAt is when this device was last seen active (UTC).
	LastActiveAt time.Time
}

// ---------------------------------------------------------------------------
// IIdentityStore
// ---------------------------------------------------------------------------

// IIdentityStore is a persistent store for Circle AI identities and device
// registrations.
type IIdentityStore interface {
	// Get returns the identity with the given identityID, or nil if not found.
	Get(ctx context.Context, identityID string) (*CircleIdentity, error)

	// Save persists the given identity.
	Save(ctx context.Context, identity CircleIdentity) error

	// GetDevices returns all devices registered to the given identity.
	GetDevices(ctx context.Context, identityID string) ([]RegisteredDevice, error)

	// RegisterDevice records a device as belonging to an identity.
	RegisterDevice(ctx context.Context, device RegisteredDevice) error

	// GetByDevice returns the identity that owns the given deviceID,
	// or nil if no matching identity is found.
	GetByDevice(ctx context.Context, deviceID string) (*CircleIdentity, error)
}

// ---------------------------------------------------------------------------
// IIdentityProvider
// ---------------------------------------------------------------------------

// IIdentityProvider resolves the active identity for the current
// device/session. Implementations may use local storage, biometrics, or
// mesh-distributed keys.
type IIdentityProvider interface {
	// GetCurrentIdentity returns the currently authenticated identity,
	// or nil if the session is not authenticated.
	GetCurrentIdentity(ctx context.Context) (*CircleIdentity, error)

	// IsAuthenticated reports whether the current session is authenticated.
	IsAuthenticated(ctx context.Context) (bool, error)

	// CreateIdentity creates a new identity with the given display name and
	// optional preferred language. Returns the newly created identity.
	CreateIdentity(ctx context.Context, displayName string, preferredLanguage *string) (CircleIdentity, error)
}
