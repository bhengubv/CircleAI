// companion_session_factory_test.go
//
// Verifies CompanionSessionFactory (ported from CompanionSessionFactory.cs):
// it resolves display name + preferred language from the identity provider when
// present, falls back to the identity id otherwise, and stamps the requested
// interface onto the session. Uses the capturingGenerator from
// companion_session_test.go.

package circleai_test

import (
	"context"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

// fakeIdentityProvider implements circleai.IIdentityProvider for the factory.
type fakeIdentityProvider struct {
	identity *circleai.CircleIdentity
	err      error
}

func (p fakeIdentityProvider) GetCurrentIdentity(context.Context) (*circleai.CircleIdentity, error) {
	return p.identity, p.err
}
func (p fakeIdentityProvider) IsAuthenticated(context.Context) (bool, error) {
	return p.identity != nil, nil
}
func (p fakeIdentityProvider) CreateIdentity(_ context.Context, displayName string, lang *string) (circleai.CircleIdentity, error) {
	return circleai.CircleIdentity{IdentityID: "new", DisplayName: displayName, PreferredLanguage: lang}, nil
}

func newFactoryDeps() circleai.CompanionSessionFactoryDeps {
	episodic := circleai.NewInMemoryEpisodicStoreDefault()
	recall, _ := circleai.NewFusedRecall(episodic, nil, nil)
	return circleai.CompanionSessionFactoryDeps{
		Generator: &capturingGenerator{reply: "ok"},
		Episodic:  episodic,
		Recall:    recall,
	}
}

func TestCompanionSessionFactory_ResolvesIdentity(t *testing.T) {
	ctx := context.Background()
	lang := "zu"
	prov := fakeIdentityProvider{identity: &circleai.CircleIdentity{
		IdentityID:        "u1",
		DisplayName:       "Thabo",
		PreferredLanguage: &lang,
	}}
	f, err := circleai.NewCompanionSessionFactory(newFactoryDeps(), prov)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}

	sess, err := f.Create(ctx, "u1", circleai.InterfaceKindWearable)
	if err != nil {
		t.Fatalf("Create: %v", err)
	}
	defer sess.Close()

	if sess.IdentityID() != "u1" {
		t.Errorf("identity id: got %q", sess.IdentityID())
	}
	if sess.Interface() != circleai.InterfaceKindWearable {
		t.Errorf("interface: got %v want Wearable", sess.Interface())
	}
	c := sess.GetContext()
	if c.DisplayName != "Thabo" {
		t.Errorf("display name should come from identity: got %q", c.DisplayName)
	}
	if c.PreferredLanguage == nil || *c.PreferredLanguage != "zu" {
		t.Errorf("preferred language should come from identity: got %v", c.PreferredLanguage)
	}
	if sess.SessionID() == "" {
		t.Error("factory should mint a session id")
	}
}

func TestCompanionSessionFactory_FallsBackToIdentityID(t *testing.T) {
	ctx := context.Background()

	// No identity provider → display name defaults to the identity id.
	f, err := circleai.NewCompanionSessionFactory(newFactoryDeps(), nil)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	sess, err := f.Create(ctx, "anon-42", circleai.InterfaceKindWeb)
	if err != nil {
		t.Fatalf("Create: %v", err)
	}
	defer sess.Close()
	if got := sess.GetContext().DisplayName; got != "anon-42" {
		t.Errorf("display name fallback: got %q want anon-42", got)
	}

	// Provider that returns nil identity → also falls back.
	f2, _ := circleai.NewCompanionSessionFactory(newFactoryDeps(), fakeIdentityProvider{identity: nil})
	sess2, err := f2.Create(ctx, "anon-99", circleai.InterfaceKindMobile)
	if err != nil {
		t.Fatalf("Create 2: %v", err)
	}
	defer sess2.Close()
	if got := sess2.GetContext().DisplayName; got != "anon-99" {
		t.Errorf("nil-identity fallback: got %q", got)
	}

	// Distinct sessions get distinct ids.
	a, _ := f.Create(ctx, "x", circleai.InterfaceKindMobile)
	defer a.Close()
	b, _ := f.Create(ctx, "x", circleai.InterfaceKindMobile)
	defer b.Close()
	if a.SessionID() == b.SessionID() {
		t.Errorf("two sessions should have distinct ids: %q", a.SessionID())
	}
}

func TestCompanionSessionFactory_Validation(t *testing.T) {
	ctx := context.Background()
	f, _ := circleai.NewCompanionSessionFactory(newFactoryDeps(), nil)
	if _, err := f.Create(ctx, "", circleai.InterfaceKindMobile); err == nil {
		t.Error("blank identity id should error")
	}

	// Missing required deps error at construction.
	if _, err := circleai.NewCompanionSessionFactory(circleai.CompanionSessionFactoryDeps{}, nil); err == nil {
		t.Error("missing generator/episodic/recall should error")
	}

	// Provider error propagates.
	fErr, _ := circleai.NewCompanionSessionFactory(newFactoryDeps(), fakeIdentityProvider{err: context.DeadlineExceeded})
	if _, err := fErr.Create(ctx, "u1", circleai.InterfaceKindMobile); err == nil {
		t.Error("identity provider error should propagate")
	}
}
