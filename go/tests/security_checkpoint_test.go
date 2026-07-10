// security_checkpoint_test.go
//
// Verifies SecurityCheckpoint (ported from SecurityCheckpoint.cs):
//   - NewSecurityCheckpoint computes the SHA-256 hash and stamps id/time.
//   - Verify returns true for an intact payload, false after tampering.
//   - String never leaks payload bytes and shows only a hash prefix.
//   - Argument guards reject blank identity/label and nil payload.

package circleai_test

import (
	"crypto/sha256"
	"strings"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestSecurityCheckpoint_CreateComputesHash(t *testing.T) {
	payload := []byte("trusted-state-blob")
	cp, err := circleai.NewSecurityCheckpoint("uhid-123", "CircleAI.Companion", payload)
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	if cp.ID == "" {
		t.Error("ID empty")
	}
	if cp.UhidIdentityID != "uhid-123" || cp.ModuleLabel != "CircleAI.Companion" {
		t.Errorf("identity/label mismatch: %q / %q", cp.UhidIdentityID, cp.ModuleLabel)
	}
	sum := sha256.Sum256(payload)
	if len(cp.PayloadHash) != len(sum) {
		t.Fatalf("hash length: got %d, want %d", len(cp.PayloadHash), len(sum))
	}
	for i := range sum {
		if cp.PayloadHash[i] != sum[i] {
			t.Fatalf("hash mismatch at byte %d", i)
		}
	}
}

func TestSecurityCheckpoint_VerifyIntact(t *testing.T) {
	cp, err := circleai.NewSecurityCheckpoint("u", "m", []byte("abc123"))
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	if !cp.Verify() {
		t.Error("Verify should be true for an intact checkpoint")
	}
}

func TestSecurityCheckpoint_VerifyDetectsTampering(t *testing.T) {
	cp, err := circleai.NewSecurityCheckpoint("u", "m", []byte("abc123"))
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	// Mutate the payload in place; the stored hash no longer matches.
	cp.Payload[0] ^= 0xFF
	if cp.Verify() {
		t.Error("Verify should be false after payload tampering")
	}
}

func TestSecurityCheckpoint_EmptyPayloadVerifies(t *testing.T) {
	cp, err := circleai.NewSecurityCheckpoint("u", "m", []byte{})
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	if !cp.Verify() {
		t.Error("empty (non-nil) payload should verify")
	}
}

func TestSecurityCheckpoint_StringHidesPayload(t *testing.T) {
	secret := []byte("SUPER-SECRET-PAYLOAD-VALUE")
	cp, err := circleai.NewSecurityCheckpoint("uhid-x", "CircleAI.Memory", secret)
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	s := cp.String()
	if strings.Contains(s, "SUPER-SECRET") {
		t.Errorf("String leaked payload content: %q", s)
	}
	if !strings.Contains(s, "SecurityCheckpoint(") {
		t.Errorf("String missing type tag: %q", s)
	}
	if !strings.Contains(s, "CircleAI.Memory") {
		t.Errorf("String missing module label: %q", s)
	}
	if !strings.Contains(s, "PayloadBytes=26") {
		t.Errorf("String should report payload byte count 26: %q", s)
	}
}

func TestSecurityCheckpoint_ArgumentGuards(t *testing.T) {
	if _, err := circleai.NewSecurityCheckpoint("  ", "m", []byte("x")); err == nil {
		t.Error("blank uhid should error")
	}
	if _, err := circleai.NewSecurityCheckpoint("u", "  ", []byte("x")); err == nil {
		t.Error("blank module label should error")
	}
	if _, err := circleai.NewSecurityCheckpoint("u", "m", nil); err == nil {
		t.Error("nil payload should error")
	}
}
