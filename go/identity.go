// identity.go
//
// IdentityTier, CircleIdentity, RegisteredDevice, BiometricProfile,
// BiometricMatcher (CosineSimilarity, IsMatch), IIdentityStore,
// IIdentityProvider, IBiometricStore.
//
// A Circle AI identity is the unified persona key that travels with the
// person. Phone → Watch → Desktop → Smart Speaker → Car: same identity,
// same memory.

package circleai

import (
	"context"
	"math"
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
// BiometricProfile
// ---------------------------------------------------------------------------

// BiometricProfile stores the enrolled biometric embedding for an identity.
// The EmbeddingVector must be L2-normalised before storage.
type BiometricProfile struct {
	// IdentityID is the identity this profile belongs to.
	IdentityID string

	// EmbeddingVector is the L2-normalised face/voice embedding.
	EmbeddingVector []float32

	// MatchThreshold is the minimum cosine similarity for a positive match.
	// Default: 0.85.
	MatchThreshold float32

	// EnrolledAt is the UTC time when this profile was first enrolled.
	EnrolledAt time.Time

	// LastMatchAt is the UTC time of the most recent successful match.
	// nil if never matched since enrolment.
	LastMatchAt *time.Time
}

// EmbeddingDimension returns the dimensionality of the stored embedding.
func (p BiometricProfile) EmbeddingDimension() int { return len(p.EmbeddingVector) }

// ---------------------------------------------------------------------------
// BiometricMatcher
// ---------------------------------------------------------------------------

// CosineSimilarity computes the cosine similarity between two float32 slices.
// Uses float64 accumulators for cross-platform reproducibility.
// Do NOT use SIMD/vector intrinsics here.
// Returns 0 when slices are of different lengths or empty.
func CosineSimilarity(a, b []float32) float64 {
	if len(a) != len(b) || len(a) == 0 {
		return 0
	}
	var dot, magA, magB float64
	for i := range a {
		ai := float64(a[i])
		bi := float64(b[i])
		dot += ai * bi
		magA += ai * ai
		magB += bi * bi
	}
	magA = math.Sqrt(magA)
	magB = math.Sqrt(magB)
	if magA < 1e-10 || magB < 1e-10 {
		return 0
	}
	sim := dot / (magA * magB)
	if sim > 1 {
		return 1
	}
	if sim < -1 {
		return -1
	}
	return sim
}

// IsMatch returns true when the cosine similarity between candidate and the
// stored embedding meets or exceeds the profile's MatchThreshold.
func IsMatch(candidate []float32, stored BiometricProfile) bool {
	return CosineSimilarity(candidate, stored.EmbeddingVector) >= float64(stored.MatchThreshold)
}

// ---------------------------------------------------------------------------
// IBiometricStore
// ---------------------------------------------------------------------------

// IBiometricStore is a persistent store for BiometricProfiles.
type IBiometricStore interface {
	// Get returns the biometric profile for identityID, or nil if not found.
	Get(ctx context.Context, identityID string) (*BiometricProfile, error)

	// Save persists the biometric profile.
	Save(ctx context.Context, profile BiometricProfile) error

	// Delete removes the biometric profile for identityID.
	Delete(ctx context.Context, identityID string) error

	// Exists reports whether a biometric profile exists for identityID.
	Exists(ctx context.Context, identityID string) (bool, error)
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
