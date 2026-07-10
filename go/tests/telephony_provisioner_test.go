// telephony_provisioner_test.go
//
// Verifies CircleAI.Telephony/PhoneNumberProvisioner.cs port: the buy +
// configure-webhook + persist flow, the store-vs-carrier merge on List, and the
// InMemoryProvisionedNumberStore CRUD.

package circleai_test

import (
	"context"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestProvisioner_ProvisionPersistsAndConfigures(t *testing.T) {
	ctx := context.Background()
	carrier := circleai.NewInMemoryTelephonyCarrier("fake", circleai.WithCarrierClock(telephonyClock()))
	carrier.AddAvailableNumber("US", "", "+15551112222", circleai.DecimalFromInt(2))
	store := circleai.NewInMemoryProvisionedNumberStore()
	prov, err := circleai.NewPhoneNumberProvisioner(carrier, store)
	if err != nil {
		t.Fatalf("new provisioner: %v", err)
	}

	wh := mustURL(t, "https://host.example/webhook")
	pn, err := prov.Provision(ctx, "US", wh, "")
	if err != nil {
		t.Fatalf("provision: %v", err)
	}
	if pn.PhoneNumber != "+15551112222" {
		t.Errorf("provisioned = %+v", pn)
	}
	// Webhook was configured on the carrier.
	if _, ok := carrier.WebhookFor(pn.PhoneNumber); !ok {
		t.Error("webhook not configured on carrier")
	}
	// Persisted to the store.
	if got, ok, _ := store.Find(ctx, pn.PhoneNumber); !ok || got.PhoneNumber != pn.PhoneNumber {
		t.Error("number not persisted")
	}

	// List merges store + carrier (single entry, no dupes).
	list, _ := prov.List(ctx)
	if len(list) != 1 || list[0].PhoneNumber != pn.PhoneNumber {
		t.Errorf("list = %+v", list)
	}
}

func TestProvisioner_Validation(t *testing.T) {
	ctx := context.Background()
	carrier := circleai.NewInMemoryTelephonyCarrier("fake")
	prov, _ := circleai.NewPhoneNumberProvisioner(carrier, nil) // nil store => default in-memory

	if _, err := prov.Provision(ctx, "  ", mustURL(t, "https://x/y"), ""); err == nil {
		t.Error("blank country should error")
	}
	if _, err := prov.Provision(ctx, "US", nil, ""); err == nil {
		t.Error("nil webhook should error")
	}
	// Relative webhook.
	rel := mustURL(t, "/relative")
	if _, err := prov.Provision(ctx, "US", rel, ""); err == nil {
		t.Error("relative webhook should error")
	}

	// nil carrier rejected at construction.
	if _, err := circleai.NewPhoneNumberProvisioner(nil, nil); err == nil {
		t.Error("nil carrier should error")
	}
}

func TestInMemoryProvisionedNumberStore_CRUD(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryProvisionedNumberStore()
	n := circleai.ProvisionedNumber{PhoneNumber: "+1000", CarrierID: "fake"}
	if err := store.Save(ctx, n); err != nil {
		t.Fatalf("save: %v", err)
	}
	// Case-insensitive find.
	if _, ok, _ := store.Find(ctx, "+1000"); !ok {
		t.Error("find miss")
	}
	list, _ := store.List(ctx)
	if len(list) != 1 {
		t.Errorf("list len = %d", len(list))
	}
	if err := store.Remove(ctx, "+1000"); err != nil {
		t.Fatalf("remove: %v", err)
	}
	if _, ok, _ := store.Find(ctx, "+1000"); ok {
		t.Error("should be removed")
	}
	// Saving a blank number errors.
	if err := store.Save(ctx, circleai.ProvisionedNumber{}); err == nil {
		t.Error("blank number save should error")
	}
}
