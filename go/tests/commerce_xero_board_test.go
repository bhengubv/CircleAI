// commerce_xero_board_test.go
//
// Verifies the CircleAI.Commerce.Integration.Xero port (commerce_xero_board.go):
// token store/get, expiry (missing => expired), tenant de-dup + insertion order,
// and reverse-chronological recent-event recall with cap.

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestXero_TokensStoreGetExpiry(t *testing.T) {
	b := circleai.NewInMemoryXeroBoard()
	exp := time.Date(2026, 7, 11, 12, 0, 0, 0, time.UTC)
	b.StoreTokens("u1", circleai.XeroTokens{AccessToken: "at", RefreshToken: "rt", ExpiresAtUtc: exp, IdToken: "id"})
	if tok, ok := b.GetTokens("u1"); !ok || tok.AccessToken != "at" {
		t.Fatalf("get tokens = %+v ok=%v", tok, ok)
	}
	if _, ok := b.GetTokens("nobody"); ok {
		t.Fatalf("missing tokens found")
	}
	// Missing user => expired.
	if !b.TokensExpired("nobody", exp) {
		t.Fatalf("missing user should be expired")
	}
	// Before expiry => not expired.
	if b.TokensExpired("u1", exp.Add(-time.Hour)) {
		t.Fatalf("before expiry should not be expired")
	}
	// At/after expiry => expired (now >= ExpiresAtUtc).
	if !b.TokensExpired("u1", exp) {
		t.Fatalf("at expiry should be expired")
	}
	if !b.TokensExpired("u1", exp.Add(time.Hour)) {
		t.Fatalf("after expiry should be expired")
	}
}

func TestXero_TenantDedupAndOrder(t *testing.T) {
	b := circleai.NewInMemoryXeroBoard()
	b.AddTenant("u1", circleai.XeroTenant{TenantId: "t1", TenantName: "Org One", TenantType: "ORGANISATION"})
	b.AddTenant("u1", circleai.XeroTenant{TenantId: "t2", TenantName: "Org Two", TenantType: "ORGANISATION"})
	b.AddTenant("u1", circleai.XeroTenant{TenantId: "t1", TenantName: "Dup", TenantType: "ORGANISATION"}) // dup ignored
	tenants := b.TenantsFor("u1")
	if len(tenants) != 2 || tenants[0].TenantId != "t1" || tenants[1].TenantId != "t2" {
		t.Fatalf("tenant dedup/order failed: %+v", tenants)
	}
	if tenants[0].TenantName != "Org One" {
		t.Fatalf("duplicate should not overwrite: %q", tenants[0].TenantName)
	}
	if len(b.TenantsFor("nobody")) != 0 {
		t.Fatalf("unknown user tenants should be empty")
	}
}

func TestXero_RecentEvents(t *testing.T) {
	b := circleai.NewInMemoryXeroBoard()
	base := time.Date(2026, 7, 1, 0, 0, 0, 0, time.UTC)
	b.RecordWebhook(circleai.XeroWebhookEvent{TenantId: "t1", ResourceType: "Invoice", ResourceId: "r1", AtUtc: base})
	b.RecordWebhook(circleai.XeroWebhookEvent{TenantId: "t1", ResourceType: "Invoice", ResourceId: "r3", AtUtc: base.Add(2 * time.Hour)})
	b.RecordWebhook(circleai.XeroWebhookEvent{TenantId: "t1", ResourceType: "Invoice", ResourceId: "r2", AtUtc: base.Add(time.Hour)})

	recent := b.RecentEvents(20)
	if len(recent) != 3 || recent[0].ResourceId != "r3" || recent[1].ResourceId != "r2" || recent[2].ResourceId != "r1" {
		t.Fatalf("recent events desc failed: %+v", recent)
	}
	if capped := b.RecentEvents(1); len(capped) != 1 || capped[0].ResourceId != "r3" {
		t.Fatalf("capped recent events failed: %+v", capped)
	}
}
