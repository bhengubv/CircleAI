// network_types_test.go
//
// Verifies the CircleAI.Networking vocabulary port (network_types.go):
//   - enum ordinals (stable, matching C# declaration order)
//   - String() renders the C# member names
//   - IsCloudTransport grouping
//   - NewNetworkPayload == NetworkPayload.Create semantics (Guid "N" id,
//     defaults, empty metadata, immutability of the returned envelope)
//   - NewNetworkContextOffline == NetworkContext.Offline
//   - PeerInfo shape

package circleai_test

import (
	"regexp"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestTransportKind_Ordinals(t *testing.T) {
	cases := []struct {
		got  circleai.TransportKind
		want int
		name string
	}{
		{circleai.TransportKindHttp, 0, "Http"},
		{circleai.TransportKindWebSocket, 1, "WebSocket"},
		{circleai.TransportKindGrpc, 2, "Grpc"},
		{circleai.TransportKindMqtt, 3, "Mqtt"},
		{circleai.TransportKindTcp, 4, "Tcp"},
		{circleai.TransportKindUdp, 5, "Udp"},
		{circleai.TransportKindWiFi, 6, "WiFi"},
		{circleai.TransportKindBluetooth, 7, "Bluetooth"},
		{circleai.TransportKindNearLink, 8, "NearLink"},
		{circleai.TransportKindAether, 9, "Aether"},
		{circleai.TransportKindDtn, 10, "Dtn"},
		{circleai.TransportKindLocalStore, 11, "LocalStore"},
	}
	for _, c := range cases {
		if int(c.got) != c.want {
			t.Errorf("TransportKind %s ordinal got %d want %d", c.name, int(c.got), c.want)
		}
		if c.got.String() != c.name {
			t.Errorf("TransportKind.String got %q want %q", c.got.String(), c.name)
		}
	}
}

func TestConnectivityState_Ordinals(t *testing.T) {
	cases := []struct {
		got  circleai.ConnectivityState
		want int
		name string
	}{
		{circleai.ConnectivityStateOnline, 0, "Online"},
		{circleai.ConnectivityStateLocalOnly, 1, "LocalOnly"},
		{circleai.ConnectivityStateMeshOnly, 2, "MeshOnly"},
		{circleai.ConnectivityStateOffline, 3, "Offline"},
	}
	for _, c := range cases {
		if int(c.got) != c.want {
			t.Errorf("ConnectivityState %s ordinal got %d want %d", c.name, int(c.got), c.want)
		}
		if c.got.String() != c.name {
			t.Errorf("ConnectivityState.String got %q want %q", c.got.String(), c.name)
		}
	}
}

func TestMessagePriority_Ordinals(t *testing.T) {
	cases := []struct {
		got  circleai.MessagePriority
		want int
		name string
	}{
		{circleai.MessagePriorityLow, 0, "Low"},
		{circleai.MessagePriorityNormal, 1, "Normal"},
		{circleai.MessagePriorityHigh, 2, "High"},
		{circleai.MessagePriorityUrgent, 3, "Urgent"},
		{circleai.MessagePriorityEmergency, 4, "Emergency"},
	}
	for _, c := range cases {
		if int(c.got) != c.want {
			t.Errorf("MessagePriority %s ordinal got %d want %d", c.name, int(c.got), c.want)
		}
		if c.got.String() != c.name {
			t.Errorf("MessagePriority.String got %q want %q", c.got.String(), c.name)
		}
	}
}

func TestPeerRole_Ordinals(t *testing.T) {
	cases := []struct {
		got  circleai.PeerRole
		want int
		name string
	}{
		{circleai.PeerRolePeer, 0, "Peer"},
		{circleai.PeerRoleRelay, 1, "Relay"},
		{circleai.PeerRoleBridge, 2, "Bridge"},
		{circleai.PeerRoleSink, 3, "Sink"},
	}
	for _, c := range cases {
		if int(c.got) != c.want {
			t.Errorf("PeerRole %s ordinal got %d want %d", c.name, int(c.got), c.want)
		}
		if c.got.String() != c.name {
			t.Errorf("PeerRole.String got %q want %q", c.got.String(), c.name)
		}
	}
}

func TestSyncDeliveryMode_Reused(t *testing.T) {
	// SyncDeliveryMode is reused from sync.go; confirm the networking work unit
	// sees the same ordinals it expects.
	if int(circleai.SyncDeliveryModeBestEffort) != 0 ||
		int(circleai.SyncDeliveryModeGuaranteed) != 1 ||
		int(circleai.SyncDeliveryModeUrgent) != 2 {
		t.Error("SyncDeliveryMode ordinals drifted from BestEffort=0,Guaranteed=1,Urgent=2")
	}
}

func TestTransportKind_IsCloudTransport(t *testing.T) {
	cloud := []circleai.TransportKind{
		circleai.TransportKindHttp, circleai.TransportKindWebSocket,
		circleai.TransportKindGrpc, circleai.TransportKindMqtt,
	}
	for _, k := range cloud {
		if !k.IsCloudTransport() {
			t.Errorf("%s should be a cloud transport", k)
		}
	}
	nonCloud := []circleai.TransportKind{
		circleai.TransportKindTcp, circleai.TransportKindUdp, circleai.TransportKindWiFi,
		circleai.TransportKindBluetooth, circleai.TransportKindNearLink,
		circleai.TransportKindAether, circleai.TransportKindDtn, circleai.TransportKindLocalStore,
	}
	for _, k := range nonCloud {
		if k.IsCloudTransport() {
			t.Errorf("%s should NOT be a cloud transport", k)
		}
	}
}

var guidNPattern = regexp.MustCompile(`^[0-9a-f]{32}$`)

func TestNewNetworkPayload_CreateDefaults(t *testing.T) {
	before := time.Now().UTC().Add(-time.Second)
	p := circleai.NewNetworkPayload([]byte("hello"), "dest-1")

	if !guidNPattern.MatchString(p.ID) {
		t.Errorf("payload ID %q is not a 32-char lowercase hex Guid-N", p.ID)
	}
	if p.SourceID != "" {
		t.Errorf("Create leaves SourceID empty, got %q", p.SourceID)
	}
	if p.DestinationID != "dest-1" {
		t.Errorf("DestinationID got %q want dest-1", p.DestinationID)
	}
	if string(p.Data) != "hello" {
		t.Errorf("Data got %q want hello", string(p.Data))
	}
	if p.Priority != circleai.MessagePriorityNormal {
		t.Errorf("default Priority got %v want Normal", p.Priority)
	}
	if p.ContentType != "application/octet-stream" {
		t.Errorf("default ContentType got %q", p.ContentType)
	}
	if p.TTL != nil {
		t.Error("default TTL should be nil")
	}
	if p.Metadata == nil || len(p.Metadata) != 0 {
		t.Errorf("Create should give an empty non-nil Metadata, got %v", p.Metadata)
	}
	if p.CreatedAt.Before(before) {
		t.Errorf("CreatedAt %v is implausibly old", p.CreatedAt)
	}
}

func TestNewNetworkPayload_UniqueIDs(t *testing.T) {
	seen := map[string]struct{}{}
	for i := 0; i < 1000; i++ {
		id := circleai.NewNetworkPayload(nil, "").ID
		if _, dup := seen[id]; dup {
			t.Fatalf("duplicate payload ID generated: %q", id)
		}
		seen[id] = struct{}{}
	}
}

func TestNewNetworkPayloadWith_EmptyContentTypeNormalised(t *testing.T) {
	ttl := 5 * time.Second
	p := circleai.NewNetworkPayloadWith([]byte{1, 2}, "d", circleai.MessagePriorityUrgent, "", &ttl)
	if p.ContentType != "application/octet-stream" {
		t.Errorf("empty ContentType should normalise to octet-stream, got %q", p.ContentType)
	}
	if p.Priority != circleai.MessagePriorityUrgent {
		t.Errorf("Priority got %v want Urgent", p.Priority)
	}
	if p.TTL == nil || *p.TTL != ttl {
		t.Errorf("TTL got %v want %v", p.TTL, ttl)
	}
}

func TestNetworkPayload_ImmutableBacking(t *testing.T) {
	src := []byte("abc")
	p := circleai.NewNetworkPayload(src, "d")
	src[0] = 'X' // mutate the caller's slice
	if string(p.Data) != "abc" {
		t.Error("payload must defensively copy Data so caller mutations do not leak in")
	}

	// WithMetadata must not mutate the original.
	p2 := p.WithMetadata("k", "v")
	if _, ok := p.Metadata["k"]; ok {
		t.Error("WithMetadata mutated the original payload's Metadata")
	}
	if p2.Metadata["k"] != "v" {
		t.Error("WithMetadata did not set the key on the copy")
	}

	// WithSource must not alias metadata.
	p3 := p2.WithSource("node-A")
	if p3.SourceID != "node-A" {
		t.Errorf("WithSource SourceID got %q", p3.SourceID)
	}
	p3.Metadata["only3"] = "1"
	if _, ok := p2.Metadata["only3"]; ok {
		t.Error("WithSource shared the Metadata map with the source payload")
	}
}

func TestNewNetworkContextOffline(t *testing.T) {
	c := circleai.NewNetworkContextOffline()
	if c.State != circleai.ConnectivityStateOffline {
		t.Errorf("Offline State got %v", c.State)
	}
	if c.PreferredTransport != circleai.TransportKindLocalStore {
		t.Errorf("Offline PreferredTransport got %v want LocalStore", c.PreferredTransport)
	}
	if len(c.AvailableTransports) != 0 {
		t.Errorf("Offline AvailableTransports should be empty, got %v", c.AvailableTransports)
	}
	if c.SignalStrengthDbm != nil || c.EstimatedBandwidthBps != nil || c.LatencyMs != nil {
		t.Error("Offline radio metrics should all be nil")
	}
	if c.NearbyPeerCount != 0 {
		t.Errorf("Offline NearbyPeerCount got %d", c.NearbyPeerCount)
	}
	if c.SnapshotAt.IsZero() {
		t.Error("Offline SnapshotAt should be stamped")
	}
}
