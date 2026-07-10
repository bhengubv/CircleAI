// telephony_null_test.go
//
// Verifies CircleAI.Telephony/NullImplementations.cs + CarrierFallback ports:
// null carrier fail-soft behaviour, null dispatcher no-op, and the
// first-configured-wins CarrierFallback.

package circleai_test

import (
	"context"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestNullCarrier(t *testing.T) {
	ctx := context.Background()
	c := circleai.NullTelephonyCarrierInstance
	if c.CarrierID() != "null" || c.IsConfigured() {
		t.Error("null carrier id/configured wrong")
	}
	// Provision / Dial throw.
	if _, err := c.ProvisionNumber(ctx, "ZA", ""); err == nil {
		t.Error("null provision should error")
	}
	if _, err := c.Dial(ctx, "+1", "+2", mustURL(t, "wss://h/s"), nil); err == nil {
		t.Error("null dial should error")
	}
	// ConfigureInboundWebhook is a no-op; ListNumbers is empty.
	if err := c.ConfigureInboundWebhook(ctx, "+1", mustURL(t, "https://h/w")); err != nil {
		t.Errorf("null configure should be no-op, got %v", err)
	}
	nums, _ := c.ListNumbers(ctx)
	if len(nums) != 0 {
		t.Error("null ListNumbers should be empty")
	}
}

func TestNullInboundDispatcher(t *testing.T) {
	d := circleai.NullInboundCallDispatcherInstance
	if d.CarrierID() != "null" {
		t.Error("null dispatcher id wrong")
	}
	called := false
	unsub := d.Subscribe(func(context.Context, circleai.ICallSession) error { called = true; return nil })
	unsub() // no-op
	if called {
		t.Error("null dispatcher should never fire")
	}
}

func TestCarrierFallback_FirstConfiguredWins(t *testing.T) {
	ctx := context.Background()
	unconfigured := circleai.NewInMemoryTelephonyCarrier("a", circleai.WithCarrierUnconfigured())
	configured := circleai.NewInMemoryTelephonyCarrier("b")
	configured.AddAvailableNumber("ZA", "", "+27000", circleai.DecimalFromInt(1))

	fb := circleai.NewCarrierFallback([]circleai.ITelephonyCarrier{unconfigured, configured})
	if fb.CarrierID() != "fallback(2)" {
		t.Errorf("fallback id = %q", fb.CarrierID())
	}
	if !fb.IsConfigured() {
		t.Error("fallback should be configured (b is)")
	}
	// Provision routes to the first configured carrier (b).
	pn, err := fb.ProvisionNumber(ctx, "ZA", "")
	if err != nil {
		t.Fatalf("fallback provision: %v", err)
	}
	if pn.CarrierID != "b" {
		t.Errorf("provisioned via %q, want b", pn.CarrierID)
	}

	// Empty fallback is not configured and delegates to the null carrier.
	empty := circleai.NewCarrierFallback(nil)
	if empty.IsConfigured() {
		t.Error("empty fallback should not be configured")
	}
	if _, err := empty.Dial(ctx, "+1", "+2", mustURL(t, "wss://h/s"), nil); err == nil {
		t.Error("empty fallback dial should error via null carrier")
	}
}
