// network_policy_test.go
//
// Verifies network_policy.go: DefaultNetworkPolicy singleton semantics and the
// NetworkPolicyBuilder fluent surface (mesh-first, no-cloud, allow-list,
// force, disable-queue) plus the nested Policy.Permits rules.

package circleai_test

import (
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestDefaultNetworkPolicy_PermitsEverything(t *testing.T) {
	p := circleai.DefaultNetworkPolicyInstance
	payload := circleai.NewNetworkPayload(nil, "")
	for k := circleai.TransportKindHttp; k <= circleai.TransportKindLocalStore; k++ {
		if !p.Permits(k, payload) {
			t.Errorf("DefaultNetworkPolicy should permit %s", k)
		}
	}
	if p.ForceTransport() != nil {
		t.Error("DefaultNetworkPolicy.ForceTransport should be nil")
	}
	if p.MeshFirst() {
		t.Error("DefaultNetworkPolicy.MeshFirst should be false")
	}
	if !p.OfflineQueueEnabled() {
		t.Error("DefaultNetworkPolicy.OfflineQueueEnabled should be true")
	}
	if !p.AllowCloudTransports() {
		t.Error("DefaultNetworkPolicy.AllowCloudTransports should be true")
	}
}

func TestNetworkPolicyBuilder_Defaults(t *testing.T) {
	p := circleai.NewNetworkPolicyBuilder().Build()
	payload := circleai.NewNetworkPayload(nil, "")
	// Empty allow-list => permit all.
	for k := circleai.TransportKindHttp; k <= circleai.TransportKindLocalStore; k++ {
		if !p.Permits(k, payload) {
			t.Errorf("empty-allow-list policy should permit %s", k)
		}
	}
	if p.MeshFirst() {
		t.Error("default builder MeshFirst should be false")
	}
	if !p.OfflineQueueEnabled() {
		t.Error("default builder queue should be enabled")
	}
	if !p.AllowCloudTransports() {
		t.Error("default builder should allow cloud")
	}
	if p.ForceTransport() != nil {
		t.Error("default builder ForceTransport should be nil")
	}
}

func TestNetworkPolicyBuilder_NoCloud(t *testing.T) {
	p := circleai.NewNetworkPolicyBuilder().NoCloud().Build()
	payload := circleai.NewNetworkPayload(nil, "")
	cloud := []circleai.TransportKind{
		circleai.TransportKindHttp, circleai.TransportKindWebSocket,
		circleai.TransportKindGrpc, circleai.TransportKindMqtt,
	}
	for _, k := range cloud {
		if p.Permits(k, payload) {
			t.Errorf("NoCloud should forbid %s", k)
		}
	}
	// Non-cloud still permitted (no allow-list).
	if !p.Permits(circleai.TransportKindAether, payload) {
		t.Error("NoCloud should still permit Aether")
	}
	if p.AllowCloudTransports() {
		t.Error("NoCloud policy AllowCloudTransports should be false")
	}
}

func TestNetworkPolicyBuilder_AllowList(t *testing.T) {
	p := circleai.NewNetworkPolicyBuilder().
		Allow(circleai.TransportKindWiFi, circleai.TransportKindBluetooth).
		Build()
	payload := circleai.NewNetworkPayload(nil, "")
	if !p.Permits(circleai.TransportKindWiFi, payload) {
		t.Error("allow-list should permit WiFi")
	}
	if !p.Permits(circleai.TransportKindBluetooth, payload) {
		t.Error("allow-list should permit Bluetooth")
	}
	if p.Permits(circleai.TransportKindHttp, payload) {
		t.Error("allow-list should forbid unlisted Http")
	}
	if p.Permits(circleai.TransportKindAether, payload) {
		t.Error("allow-list should forbid unlisted Aether")
	}
}

func TestNetworkPolicyBuilder_AllowListPlusNoCloud(t *testing.T) {
	// Even if a cloud transport is allow-listed, NoCloud wins (matches C#: the
	// no-cloud check runs BEFORE the allow-list check).
	p := circleai.NewNetworkPolicyBuilder().
		Allow(circleai.TransportKindHttp, circleai.TransportKindWiFi).
		NoCloud().
		Build()
	payload := circleai.NewNetworkPayload(nil, "")
	if p.Permits(circleai.TransportKindHttp, payload) {
		t.Error("NoCloud must override an allow-listed cloud transport")
	}
	if !p.Permits(circleai.TransportKindWiFi, payload) {
		t.Error("allow-listed non-cloud WiFi should remain permitted")
	}
}

func TestNetworkPolicyBuilder_ForceAndDisableQueue(t *testing.T) {
	p := circleai.NewNetworkPolicyBuilder().
		Force(circleai.TransportKindAether).
		DisableQueue().
		Build()
	f := p.ForceTransport()
	if f == nil || *f != circleai.TransportKindAether {
		t.Errorf("ForceTransport got %v want Aether", f)
	}
	if p.OfflineQueueEnabled() {
		t.Error("DisableQueue should turn the offline queue off")
	}
}

func TestNetworkPolicyBuilder_MeshFirst(t *testing.T) {
	p := circleai.NewNetworkPolicyBuilder().MeshFirst().Build()
	if !p.MeshFirst() {
		t.Error("MeshFirst() should set the flag")
	}
}
