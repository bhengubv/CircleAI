// network_selector_test.go
//
// Verifies network_selector.go DefaultTransportSelector against the C#
// documented cascade and the priority/policy biasing rules, including the
// invariant that every cascade terminates with LocalStore.

package circleai_test

import (
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func ctxWith(transports ...circleai.TransportKind) circleai.NetworkContext {
	c := circleai.NewNetworkContextOffline()
	c.AvailableTransports = transports
	c.State = circleai.ConnectivityStateOnline
	return c
}

func TestSelector_DefaultCascadeOrder(t *testing.T) {
	sel := circleai.NewDefaultTransportSelector(nil)
	// Everything available, Normal priority => canonical cloud-first order.
	ctx := ctxWith(
		circleai.TransportKindHttp, circleai.TransportKindWebSocket, circleai.TransportKindGrpc,
		circleai.TransportKindMqtt, circleai.TransportKindTcp, circleai.TransportKindWiFi,
		circleai.TransportKindBluetooth, circleai.TransportKindNearLink, circleai.TransportKindAether,
		circleai.TransportKindDtn,
	)
	payload := circleai.NewNetworkPayload(nil, "")
	got := sel.GetCascade(payload, ctx)
	want := []circleai.TransportKind{
		circleai.TransportKindGrpc, circleai.TransportKindWebSocket, circleai.TransportKindHttp,
		circleai.TransportKindMqtt, circleai.TransportKindTcp, circleai.TransportKindWiFi,
		circleai.TransportKindBluetooth, circleai.TransportKindNearLink, circleai.TransportKindAether,
		circleai.TransportKindDtn, circleai.TransportKindLocalStore,
	}
	assertCascade(t, got, want)
	if sel.SelectBest(payload, ctx) != circleai.TransportKindGrpc {
		t.Errorf("SelectBest got %v want Grpc", sel.SelectBest(payload, ctx))
	}
}

func TestSelector_FiltersUnavailable(t *testing.T) {
	sel := circleai.NewDefaultTransportSelector(nil)
	ctx := ctxWith(circleai.TransportKindHttp, circleai.TransportKindWiFi)
	payload := circleai.NewNetworkPayload(nil, "")
	got := sel.GetCascade(payload, ctx)
	// Only Http and WiFi are available; order Http (before WiFi) then LocalStore.
	assertCascade(t, got, []circleai.TransportKind{
		circleai.TransportKindHttp, circleai.TransportKindWiFi, circleai.TransportKindLocalStore,
	})
}

func TestSelector_TerminatesWithLocalStore_EvenWhenNothingAvailable(t *testing.T) {
	sel := circleai.NewDefaultTransportSelector(nil)
	ctx := ctxWith() // no transports at all
	payload := circleai.NewNetworkPayload(nil, "")
	got := sel.GetCascade(payload, ctx)
	assertCascade(t, got, []circleai.TransportKind{circleai.TransportKindLocalStore})
	if sel.SelectBest(payload, ctx) != circleai.TransportKindLocalStore {
		t.Error("with nothing available SelectBest must fall back to LocalStore")
	}
}

func TestSelector_EmergencyPriorityPrefersMesh(t *testing.T) {
	sel := circleai.NewDefaultTransportSelector(nil)
	ctx := ctxWith(
		circleai.TransportKindHttp, circleai.TransportKindGrpc,
		circleai.TransportKindAether, circleai.TransportKindBluetooth,
	)
	payload := circleai.NewNetworkPayloadWith(nil, "", circleai.MessagePriorityEmergency, "", nil)
	got := sel.GetCascade(payload, ctx)
	// Mesh-preferred order floats Aether/Bluetooth ahead of Grpc/Http.
	assertCascade(t, got, []circleai.TransportKind{
		circleai.TransportKindAether, circleai.TransportKindBluetooth,
		circleai.TransportKindGrpc, circleai.TransportKindHttp,
		circleai.TransportKindLocalStore,
	})
	if sel.SelectBest(payload, ctx) != circleai.TransportKindAether {
		t.Error("Emergency SelectBest should prefer Aether")
	}
}

func TestSelector_MeshFirstPolicyBias(t *testing.T) {
	pol := circleai.NewNetworkPolicyBuilder().MeshFirst().Build()
	sel := circleai.NewDefaultTransportSelector(pol)
	ctx := ctxWith(circleai.TransportKindHttp, circleai.TransportKindAether)
	payload := circleai.NewNetworkPayload(nil, "") // Normal priority
	got := sel.GetCascade(payload, ctx)
	// MeshFirst policy => mesh order even at Normal priority: Aether before Http.
	assertCascade(t, got, []circleai.TransportKind{
		circleai.TransportKindAether, circleai.TransportKindHttp, circleai.TransportKindLocalStore,
	})
}

func TestSelector_NoCloudPolicyFiltersCloud(t *testing.T) {
	pol := circleai.NewNetworkPolicyBuilder().NoCloud().Build()
	sel := circleai.NewDefaultTransportSelector(pol)
	ctx := ctxWith(circleai.TransportKindGrpc, circleai.TransportKindHttp, circleai.TransportKindWiFi)
	payload := circleai.NewNetworkPayload(nil, "")
	got := sel.GetCascade(payload, ctx)
	// Grpc + Http removed by NoCloud; only WiFi then LocalStore.
	assertCascade(t, got, []circleai.TransportKind{
		circleai.TransportKindWiFi, circleai.TransportKindLocalStore,
	})
}

func TestSelector_ForcedTransportLeads(t *testing.T) {
	pol := circleai.NewNetworkPolicyBuilder().Force(circleai.TransportKindNearLink).Build()
	sel := circleai.NewDefaultTransportSelector(pol)
	// NearLink is not even listed as available, but a forced transport is honoured.
	ctx := ctxWith(circleai.TransportKindHttp)
	payload := circleai.NewNetworkPayload(nil, "")
	got := sel.GetCascade(payload, ctx)
	assertCascade(t, got, []circleai.TransportKind{
		circleai.TransportKindNearLink, circleai.TransportKindLocalStore,
	})
	if sel.SelectBest(payload, ctx) != circleai.TransportKindNearLink {
		t.Error("forced transport should be SelectBest")
	}
}

func TestSelector_ForcedLocalStoreNotDuplicated(t *testing.T) {
	pol := circleai.NewNetworkPolicyBuilder().Force(circleai.TransportKindLocalStore).Build()
	sel := circleai.NewDefaultTransportSelector(pol)
	ctx := ctxWith(circleai.TransportKindHttp)
	payload := circleai.NewNetworkPayload(nil, "")
	got := sel.GetCascade(payload, ctx)
	// Forcing LocalStore must not yield [LocalStore, LocalStore].
	assertCascade(t, got, []circleai.TransportKind{circleai.TransportKindLocalStore})
}

func assertCascade(t *testing.T, got, want []circleai.TransportKind) {
	t.Helper()
	if len(got) != len(want) {
		t.Fatalf("cascade length got %d want %d\n got=%v\nwant=%v", len(got), len(want), got, want)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("cascade[%d] got %v want %v\n got=%v\nwant=%v", i, got[i], want[i], got, want)
		}
	}
	if len(got) == 0 || got[len(got)-1] != circleai.TransportKindLocalStore {
		t.Fatalf("cascade must terminate with LocalStore, got %v", got)
	}
}
