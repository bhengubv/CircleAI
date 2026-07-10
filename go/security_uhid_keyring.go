// security_uhid_keyring.go
//
// Ports CircleAI.Security.UhidKeyRing (UhidKeyRing.cs).
//
// Ephemeral session key management bound to a UHID identity. Each UHID session
// gets a fresh P-256 (NIST) key pair for ECDSA signing. When an anomaly is
// confirmed the watchdog calls Rotate() — the old key is revoked and a new key
// ring is issued. All in-flight requests signed with the revoked key are
// rejected.
//
// Uses crypto/ecdsa + crypto/elliptic (P-256) — no external dependency beyond
// google/uuid (already a permitted dependency). Signatures are ASN.1 DER
// (SignASN1/VerifyASN1); PublicKeyDer is the SubjectPublicKeyInfo DER encoding,
// matching the C# ExportSubjectPublicKeyInfo. Signatures round-trip within a
// ring (the contract: sign with this ring, verify with this ring).

package circleai

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"errors"
	"fmt"
	"strings"
	"sync"
	"time"

	"github.com/google/uuid"
)

// UhidKeyRing is an ephemeral ECDSA (P-256) session key ring bound to a UHID
// identity. Generate a fresh ring at session start or on anomaly confirmation.
// Once revoked, the ring cannot sign; generate a new one. Ports UhidKeyRing.
type UhidKeyRing struct {
	mu      sync.Mutex
	key     *ecdsa.PrivateKey
	revoked bool

	ringID         string
	uhidIdentityID string
	generatedAt    time.Time
	revokedAt      *time.Time
	publicKeyDer   []byte
}

// GenerateFreshUhidKeyRing creates a new UhidKeyRing for uhidIdentityID with a
// freshly generated P-256 key pair. Ports UhidKeyRing.GenerateFresh. Returns an
// error when uhidIdentityID is blank or key generation fails (mirrors the C#
// argument guard; C# key generation cannot fail, but Go's rand can surface one).
func GenerateFreshUhidKeyRing(uhidIdentityID string) (*UhidKeyRing, error) {
	if strings.TrimSpace(uhidIdentityID) == "" {
		return nil, errors.New("uhidIdentityId is required")
	}
	r := &UhidKeyRing{uhidIdentityID: uhidIdentityID}
	if err := r.regenerateKey(); err != nil {
		return nil, err
	}
	return r, nil
}

// RingID returns the unique ring identifier. It changes on every regeneration.
func (r *UhidKeyRing) RingID() string {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.ringID
}

// UhidIdentityID returns the UHID identity this ring is bound to.
func (r *UhidKeyRing) UhidIdentityID() string { return r.uhidIdentityID }

// GeneratedAt returns the UTC timestamp when this ring was generated.
func (r *UhidKeyRing) GeneratedAt() time.Time {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.generatedAt
}

// RevokedAt returns the UTC timestamp when this ring was revoked, or nil if
// still active.
func (r *UhidKeyRing) RevokedAt() *time.Time {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.revokedAt == nil {
		return nil
	}
	t := *r.revokedAt
	return &t
}

// IsRevoked reports whether this ring has been explicitly revoked. Ports
// UhidKeyRing.IsRevoked.
func (r *UhidKeyRing) IsRevoked() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.revoked
}

// PublicKeyDer returns the DER-encoded (SubjectPublicKeyInfo) public key for this
// ring. Safe to share; corresponds to the private signing key. A copy is
// returned so callers cannot mutate the ring's stored bytes.
func (r *UhidKeyRing) PublicKeyDer() []byte {
	r.mu.Lock()
	defer r.mu.Unlock()
	out := make([]byte, len(r.publicKeyDer))
	copy(out, r.publicKeyDer)
	return out
}

// Rotate revokes the current key and generates a replacement, returning a NEW
// UhidKeyRing. This instance remains revoked. Prefer this over mutating in place
// so call sites holding a reference to the old ring cannot accidentally sign
// with a rotated key. Ports UhidKeyRing.Rotate.
func (r *UhidKeyRing) Rotate() (*UhidKeyRing, error) {
	r.Revoke()
	return GenerateFreshUhidKeyRing(r.uhidIdentityID)
}

// Sign signs data with the current private key using ECDSA-SHA256 (ASN.1 DER
// signature). Returns an error if the ring is disposed or revoked. Ports
// UhidKeyRing.Sign.
func (r *UhidKeyRing) Sign(data []byte) ([]byte, error) {
	if data == nil {
		return nil, errors.New("data must not be nil")
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.key == nil {
		return nil, errors.New("UhidKeyRing has been disposed")
	}
	if r.revoked {
		return nil, fmt.Errorf("UhidKeyRing %s has been revoked — call Rotate() to get a fresh ring", r.ringID)
	}
	digest := sha256.Sum256(data)
	return ecdsa.SignASN1(rand.Reader, r.key, digest[:])
}

// Verify verifies an ECDSA-SHA256 signature (ASN.1 DER) against data using this
// ring's public key. Works even after revocation, so prior signatures can still
// be validated. Ports UhidKeyRing.Verify.
func (r *UhidKeyRing) Verify(data, signature []byte) bool {
	if data == nil || signature == nil {
		return false
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.key == nil {
		return false
	}
	digest := sha256.Sum256(data)
	return ecdsa.VerifyASN1(&r.key.PublicKey, digest[:], signature)
}

// Revoke revokes this ring. After revocation Sign errors; Verify continues to
// work for historical validation. Idempotent. Ports UhidKeyRing.Revoke.
func (r *UhidKeyRing) Revoke() {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.revoked {
		return
	}
	r.revoked = true
	now := time.Now().UTC()
	r.revokedAt = &now
}

// Dispose clears the private key. After disposal Sign and Verify fail. Idempotent.
// Ports UhidKeyRing.Dispose.
func (r *UhidKeyRing) Dispose() {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.key = nil
}

func (r *UhidKeyRing) regenerateKey() error {
	r.mu.Lock()
	defer r.mu.Unlock()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return err
	}
	der, err := x509.MarshalPKIXPublicKey(&key.PublicKey)
	if err != nil {
		return err
	}
	r.key = key
	r.ringID = uuid.NewString()
	r.generatedAt = time.Now().UTC()
	r.revokedAt = nil
	r.revoked = false
	r.publicKeyDer = der
	return nil
}
