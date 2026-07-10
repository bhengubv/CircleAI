// security_checkpoint.go
//
// Ports CircleAI.Security.SecurityCheckpoint (SecurityCheckpoint.cs).
//
// A cryptographically-bound snapshot of trusted local state. When CircleAI
// detects an anomaly, the watchdog may roll back to the last verified
// checkpoint. A checkpoint is:
//   - IMMUTABLE once created (fields set only by NewSecurityCheckpoint)
//   - SELF-VERIFYING (SHA-256 of Payload, verified on restore)
//   - TAGGED with the UHID that created it (identity binding)
//
// The payload is deliberately opaque ([]byte) so any module can checkpoint its
// own serialised state without this package depending on it.

package circleai

import (
	"crypto/sha256"
	"crypto/subtle"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/google/uuid"
)

// SecurityCheckpoint is an immutable, self-verifying snapshot of trusted local
// state. Created before a risky operation; used for rollback if an
// AnomalySignal is confirmed. Ports the SecurityCheckpoint record.
type SecurityCheckpoint struct {
	// ID is the unique checkpoint identifier (UUID string).
	ID string
	// UhidIdentityID is the UHID of the local user whose state is captured,
	// binding the checkpoint to a specific identity.
	UhidIdentityID string
	// ModuleLabel labels the module or subsystem that created this checkpoint
	// (e.g. "CircleAI.Companion", "CircleAI.Memory").
	ModuleLabel string
	// Payload is the opaque serialised state payload.
	Payload []byte
	// PayloadHash is the SHA-256 hash of Payload, computed at creation time and
	// verified by Verify before restoring.
	PayloadHash []byte
	// CreatedAt is the UTC timestamp of checkpoint creation.
	CreatedAt time.Time
}

// NewSecurityCheckpoint creates a new checkpoint, computing PayloadHash
// automatically. Ports SecurityCheckpoint.Create. Returns an error when
// uhidIdentityID or moduleLabel is blank, or payload is nil (mirrors the C#
// argument guards).
func NewSecurityCheckpoint(uhidIdentityID, moduleLabel string, payload []byte) (*SecurityCheckpoint, error) {
	if strings.TrimSpace(uhidIdentityID) == "" {
		return nil, errors.New("uhidIdentityId is required")
	}
	if strings.TrimSpace(moduleLabel) == "" {
		return nil, errors.New("moduleLabel is required")
	}
	if payload == nil {
		return nil, errors.New("payload is required")
	}

	sum := sha256.Sum256(payload)
	hash := make([]byte, len(sum))
	copy(hash, sum[:])

	return &SecurityCheckpoint{
		ID:             uuid.NewString(),
		UhidIdentityID: uhidIdentityID,
		ModuleLabel:    moduleLabel,
		Payload:        payload,
		PayloadHash:    hash,
		CreatedAt:      time.Now().UTC(),
	}, nil
}

// Verify reports whether Payload has not been tampered with since the checkpoint
// was created — i.e. the current SHA-256 of Payload matches PayloadHash. Uses a
// constant-time compare, matching C#'s CryptographicOperations.FixedTimeEquals.
// Ports SecurityCheckpoint.Verify.
func (c *SecurityCheckpoint) Verify() bool {
	current := sha256.Sum256(c.Payload)
	return subtle.ConstantTimeCompare(current[:], c.PayloadHash) == 1
}

// String returns a non-sensitive textual representation — the payload bytes are
// NEVER included in clear. Only the first 16 hex chars (8 bytes) of PayloadHash
// are emitted, sufficient for log correlation without leaking content. Mirrors
// the C# ToString override that prevents structured loggers from serialising
// Payload via reflection.
func (c *SecurityCheckpoint) String() string {
	hashPrefix := "(empty)"
	if len(c.PayloadHash) >= 8 {
		hashPrefix = strings.ToUpper(fmt.Sprintf("%x", c.PayloadHash[:8]))
	}
	return fmt.Sprintf(
		"SecurityCheckpoint(Id=%s, Module=%s, Uhid=%s, PayloadSha256=%s…, PayloadBytes=%d, CreatedAt=%s)",
		c.ID, c.ModuleLabel, c.UhidIdentityID, hashPrefix, len(c.Payload),
		c.CreatedAt.Format("2006-01-02T15:04:05.0000000Z07:00"))
}
