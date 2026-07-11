// distribution_test.go
//
// Verifies the CircleAI.Distribution port (distribution.go +
// distribution_ubiquity.go): the app-store submitter, HMAC-gated delta updater,
// OEM/carrier catalogs, plus a representative sample of the Ubiquity rails
// (onboarding, USSD state machine, offline queue, quiet mode, currency format,
// personality wizard, verifiable wipe).

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestDistribution_AppStoreSubmitter(t *testing.T) {
	s := circleai.NewDefaultAppStoreSubmitter()
	ok, err := s.Submit(context.Background(), circleai.AppStorePackage{StoreName: "PlayStore", PackagePath: "/a.apk", Version: "1.0"})
	if err != nil || !ok {
		t.Fatalf("known store submit = %v err=%v", ok, err)
	}
	// Unknown store -> false, no error.
	if ok, _ := s.Submit(context.Background(), circleai.AppStorePackage{StoreName: "Nope", PackagePath: "/a", Version: "1"}); ok {
		t.Fatalf("unknown store must return false")
	}
	// Missing field -> error.
	if _, err := s.Submit(context.Background(), circleai.AppStorePackage{StoreName: "PlayStore", Version: "1"}); err == nil {
		t.Fatalf("blank package path must error")
	}
	if len(s.Submitted()) != 1 {
		t.Fatalf("submitted count = %d, want 1", len(s.Submitted()))
	}
}

func TestDistribution_SignedDeltaUpdater(t *testing.T) {
	key := []byte("0123456789abcdef")
	u := circleai.NewDefaultSignedDeltaUpdater(key)
	upd := circleai.DeltaUpdate{Channel: "stable", FromVersion: "", ToVersion: "1.0", Payload: []byte("bytes")}
	upd.Signature = circleai.DeltaUpdateSignature(key, upd)
	ok, err := u.Apply(context.Background(), upd)
	if err != nil || !ok {
		t.Fatalf("valid update = %v err=%v", ok, err)
	}
	if v, ok := u.CurrentVersion("stable"); !ok || v != "1.0" {
		t.Fatalf("current version = %q ok=%v", v, ok)
	}
	// Wrong signature -> false.
	bad := circleai.DeltaUpdate{Channel: "stable", FromVersion: "1.0", ToVersion: "2.0", Payload: []byte("x"), Signature: []byte("nope")}
	if ok, _ := u.Apply(context.Background(), bad); ok {
		t.Fatalf("bad signature must return false")
	}
	// Version-chain mismatch -> false.
	mism := circleai.DeltaUpdate{Channel: "stable", FromVersion: "9.9", ToVersion: "2.0", Payload: []byte("x")}
	mism.Signature = circleai.DeltaUpdateSignature(key, mism)
	if ok, _ := u.Apply(context.Background(), mism); ok {
		t.Fatalf("version-chain mismatch must return false")
	}
}

func TestDistribution_Catalogs(t *testing.T) {
	oem := circleai.DefaultOemPreloadCatalog{}
	if len(oem.Partners()) != 5 {
		t.Fatalf("oem partners")
	}
	carriers := circleai.DefaultCarrierPreloadCatalog{}
	if len(carriers.Carriers()) != 6 {
		t.Fatalf("carriers")
	}
	pwa := circleai.DefaultPwaFallback{}
	if pwa.PwaURL() != "https://app.circle.ai" {
		t.Fatalf("pwa url")
	}
	linux := circleai.DefaultLinuxRepoFanout{}
	if len(linux.Repos()) != 6 {
		t.Fatalf("linux repos")
	}
}

func TestUbiquity_OnboardingAndPinVerify(t *testing.T) {
	o := circleai.NewDefaultPhonePinBiometricOnboarding()
	sess, err := o.Start(context.Background(), "+27831234567")
	if err != nil {
		t.Fatalf("start: %v", err)
	}
	if err := o.Complete(context.Background(), sess.SessionID, "1234", true); err != nil {
		t.Fatalf("complete: %v", err)
	}
	if !o.VerifyPin("+27831234567", "1234") || o.VerifyPin("+27831234567", "0000") {
		t.Fatalf("pin verify mismatch")
	}
	// Invalid phone.
	if _, err := o.Start(context.Background(), "abc"); err == nil {
		t.Fatalf("invalid phone must error")
	}
	// Weak pin.
	if err := o.Complete(context.Background(), sess.SessionID, "12", true); err == nil {
		t.Fatalf("weak pin must error")
	}
}

func TestUbiquity_UssdStateMachine(t *testing.T) {
	var u circleai.DefaultUssdFallback
	root, _ := u.Respond(context.Background(), "sess", "") // unknown input at root -> root prompt
	if root == "" {
		t.Fatalf("root prompt empty")
	}
	bal, _ := u.Respond(context.Background(), "sess", "1") // -> balance
	if bal == root {
		t.Fatalf("selecting 1 should change menu")
	}
	back, _ := u.Respond(context.Background(), "sess", "0") // -> root
	if back != root {
		t.Fatalf("0 should return to root")
	}
}

func TestUbiquity_OfflineQueueAndQuietMode(t *testing.T) {
	var q circleai.DefaultOfflineQueuedOperation
	_ = q.Enqueue(context.Background(), "op1")
	_ = q.Enqueue(context.Background(), "op2")
	if len(q.Pending()) != 2 {
		t.Fatalf("pending count")
	}
	if v, ok := q.TryDequeue(); !ok || v != "op1" {
		t.Fatalf("dequeue FIFO = %q ok=%v", v, ok)
	}

	var qm circleai.DefaultQuietMode
	now := time.Now().UTC()
	_ = qm.Engage(context.Background(), "meeting", time.Hour)
	if !qm.IsQuietAt(now.Add(30 * time.Minute)) {
		t.Fatalf("should be quiet inside window")
	}
	if qm.IsQuietAt(now.Add(2 * time.Hour)) {
		t.Fatalf("should not be quiet after window")
	}
	if len(qm.ActiveWindows()) != 1 {
		t.Fatalf("active windows")
	}
}

func TestUbiquity_CurrencyFormatAndPersonality(t *testing.T) {
	f := circleai.DefaultCurrencyFormatter{}
	if got := f.Format(circleai.NewDecimal(19, 500000), "ZAR"); got != "19.50 ZAR" {
		t.Fatalf("currency format = %q, want '19.50 ZAR'", got)
	}
	w := circleai.NewDefaultAiPersonalityWizard()
	if err := w.Select(context.Background(), "s1", circleai.PersonalityChoice{Name: "warm"}); err != nil {
		t.Fatalf("select preset: %v", err)
	}
	if c, ok := w.Selected("s1"); !ok || c.Name != "warm" {
		t.Fatalf("selected = %+v ok=%v", c, ok)
	}
	if err := w.Select(context.Background(), "s1", circleai.PersonalityChoice{Name: "grumpy"}); err == nil {
		t.Fatalf("unknown personality must error")
	}
}

func TestUbiquity_VerifiableWipeDeterministicPhrase(t *testing.T) {
	cert, err := circleai.DefaultVerifiableWipe{}.WipeAndCertify(context.Background(), "owner-1")
	if err != nil || len(cert) != 32 { // SHA-256
		t.Fatalf("wipe cert len = %d err=%v", len(cert), err)
	}
	var mode circleai.DefaultAbusiveEnvironmentMode
	p1 := mode.SafetyPhrase("owner-1")
	p2 := mode.SafetyPhrase("owner-1")
	if p1 != p2 || p1 == "" {
		t.Fatalf("safety phrase must be deterministic per owner: %q vs %q", p1, p2)
	}
}
