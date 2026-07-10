// security_uhid_keyring_test.go
//
// Verifies UhidKeyRing (ported from UhidKeyRing.cs):
//   - Sign/Verify round-trips within a ring; a foreign signature fails.
//   - Rotate revokes the old ring, returns a fresh one with a new RingID.
//   - Verify still works after revocation; Sign fails after revocation.
//   - Dispose clears the key. Blank identity is rejected.

package circleai_test

import (
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestKeyRing_SignVerifyRoundTrip(t *testing.T) {
	r, err := circleai.GenerateFreshUhidKeyRing("uhid-1")
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	data := []byte("authorize-this")
	sig, err := r.Sign(data)
	if err != nil {
		t.Fatalf("sign: %v", err)
	}
	if !r.Verify(data, sig) {
		t.Error("Verify should accept a signature this ring produced")
	}
	if r.Verify([]byte("tampered"), sig) {
		t.Error("Verify should reject a signature over different data")
	}
	if len(r.PublicKeyDer()) == 0 {
		t.Error("PublicKeyDer should be populated")
	}
}

func TestKeyRing_ForeignSignatureRejected(t *testing.T) {
	r1, _ := circleai.GenerateFreshUhidKeyRing("uhid-1")
	r2, _ := circleai.GenerateFreshUhidKeyRing("uhid-1")
	data := []byte("msg")
	sig, _ := r2.Sign(data)
	if r1.Verify(data, sig) {
		t.Error("ring should reject a signature made by a different ring")
	}
}

func TestKeyRing_RotateRevokesAndIssuesFresh(t *testing.T) {
	r, _ := circleai.GenerateFreshUhidKeyRing("uhid-1")
	oldID := r.RingID()
	fresh, err := r.Rotate()
	if err != nil {
		t.Fatalf("rotate: %v", err)
	}
	if !r.IsRevoked() {
		t.Error("original ring should be revoked after Rotate")
	}
	if fresh.IsRevoked() {
		t.Error("fresh ring should not be revoked")
	}
	if fresh.RingID() == oldID {
		t.Error("fresh ring should have a new RingID")
	}
	if fresh.UhidIdentityID() != "uhid-1" {
		t.Errorf("fresh ring identity: got %q", fresh.UhidIdentityID())
	}
}

func TestKeyRing_SignFailsAfterRevoke_VerifyStillWorks(t *testing.T) {
	r, _ := circleai.GenerateFreshUhidKeyRing("uhid-1")
	data := []byte("payload")
	sig, _ := r.Sign(data)

	r.Revoke()
	if r.RevokedAt() == nil {
		t.Error("RevokedAt should be set after Revoke")
	}
	if _, err := r.Sign(data); err == nil {
		t.Error("Sign should fail after revocation")
	}
	// Historical validation must still succeed.
	if !r.Verify(data, sig) {
		t.Error("Verify should still work after revocation")
	}
}

func TestKeyRing_RevokeIdempotent(t *testing.T) {
	r, _ := circleai.GenerateFreshUhidKeyRing("uhid-1")
	r.Revoke()
	first := r.RevokedAt()
	r.Revoke() // no-op
	second := r.RevokedAt()
	if first == nil || second == nil || !first.Equal(*second) {
		t.Error("second Revoke should not change RevokedAt")
	}
}

func TestKeyRing_DisposeClearsKey(t *testing.T) {
	r, _ := circleai.GenerateFreshUhidKeyRing("uhid-1")
	data := []byte("x")
	sig, _ := r.Sign(data)
	r.Dispose()
	if _, err := r.Sign(data); err == nil {
		t.Error("Sign should fail after Dispose")
	}
	if r.Verify(data, sig) {
		t.Error("Verify should fail after Dispose (key cleared)")
	}
	r.Dispose() // idempotent
}

func TestKeyRing_BlankIdentityRejected(t *testing.T) {
	if _, err := circleai.GenerateFreshUhidKeyRing("   "); err == nil {
		t.Error("blank identity should error")
	}
}

func TestKeyRing_SignNilDataRejected(t *testing.T) {
	r, _ := circleai.GenerateFreshUhidKeyRing("uhid-1")
	if _, err := r.Sign(nil); err == nil {
		t.Error("nil data should error")
	}
}
