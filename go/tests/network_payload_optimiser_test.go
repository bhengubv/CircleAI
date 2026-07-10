// network_payload_optimiser_test.go
//
// Verifies network_payload_optimiser.go GzipPayloadOptimiser (IPayloadOptimiser):
//   - Optimise compresses Data for low-bandwidth transports (BLE/NearLink/DTN)
//     and stamps the codec marker; Decompress round-trips it byte-for-byte
//   - cloud/LAN targets leave the payload verbatim (no marker, no transform)
//   - an incompressible / tiny payload is never inflated
//   - Optimise is idempotent (never double-compresses)
//   - Decompress on an un-optimised payload is a safe no-op
//   - a cancelled ctx is honoured; a corrupt gzip stream surfaces an error

package circleai_test

import (
	"bytes"
	"context"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestOptimiser_RoundTripLowBandwidth(t *testing.T) {
	opt := circleai.NewGzipPayloadOptimiser()
	// Highly compressible data so gzip is guaranteed to shrink it.
	original := bytes.Repeat([]byte("circleai-mesh-payload "), 64)
	p := circleai.NewNetworkPayload(original, "peer-1")

	for _, target := range []circleai.TransportKind{
		circleai.TransportKindBluetooth,
		circleai.TransportKindNearLink,
		circleai.TransportKindDtn,
	} {
		comp, err := opt.Optimise(context.Background(), p, target)
		if err != nil {
			t.Fatalf("Optimise(%s) error: %v", target, err)
		}
		if len(comp.Data) >= len(original) {
			t.Errorf("Optimise(%s) did not shrink data: %d >= %d", target, len(comp.Data), len(original))
		}
		if comp.Metadata["x-circleai-optimiser"] != "gzip" {
			t.Errorf("Optimise(%s) missing gzip marker, metadata=%v", target, comp.Metadata)
		}

		back, err := opt.Decompress(comp)
		if err != nil {
			t.Fatalf("Decompress(%s) error: %v", target, err)
		}
		if !bytes.Equal(back.Data, original) {
			t.Errorf("Decompress(%s) did not restore original bytes", target)
		}
		if _, still := back.Metadata["x-circleai-optimiser"]; still {
			t.Errorf("Decompress(%s) left the codec marker behind", target)
		}
	}
}

func TestOptimiser_CloudTargetUnchanged(t *testing.T) {
	opt := circleai.NewGzipPayloadOptimiser()
	original := bytes.Repeat([]byte("x"), 500)
	p := circleai.NewNetworkPayload(original, "d")

	for _, target := range []circleai.TransportKind{
		circleai.TransportKindHttp, circleai.TransportKindWebSocket,
		circleai.TransportKindGrpc, circleai.TransportKindMqtt,
		circleai.TransportKindTcp, circleai.TransportKindWiFi,
		circleai.TransportKindAether, circleai.TransportKindLocalStore,
	} {
		out, err := opt.Optimise(context.Background(), p, target)
		if err != nil {
			t.Fatalf("Optimise(%s) error: %v", target, err)
		}
		if !bytes.Equal(out.Data, original) {
			t.Errorf("Optimise(%s) altered data for a non-low-bandwidth target", target)
		}
		if _, marked := out.Metadata["x-circleai-optimiser"]; marked {
			t.Errorf("Optimise(%s) stamped a codec marker on a verbatim payload", target)
		}
	}
}

func TestOptimiser_NeverInflatesIncompressible(t *testing.T) {
	opt := circleai.NewGzipPayloadOptimiser()
	// Tiny payload — gzip framing overhead exceeds the savings, so it must be
	// left verbatim rather than inflated.
	p := circleai.NewNetworkPayload([]byte("hi"), "d")
	out, err := opt.Optimise(context.Background(), p, circleai.TransportKindBluetooth)
	if err != nil {
		t.Fatalf("Optimise error: %v", err)
	}
	if !bytes.Equal(out.Data, []byte("hi")) {
		t.Error("tiny payload should not be transformed (would inflate)")
	}
	if _, marked := out.Metadata["x-circleai-optimiser"]; marked {
		t.Error("tiny payload should carry no codec marker")
	}
}

func TestOptimiser_Idempotent(t *testing.T) {
	opt := circleai.NewGzipPayloadOptimiser()
	original := bytes.Repeat([]byte("abc123"), 128)
	p := circleai.NewNetworkPayload(original, "d")

	once, err := opt.Optimise(context.Background(), p, circleai.TransportKindDtn)
	if err != nil {
		t.Fatalf("first Optimise error: %v", err)
	}
	twice, err := opt.Optimise(context.Background(), once, circleai.TransportKindDtn)
	if err != nil {
		t.Fatalf("second Optimise error: %v", err)
	}
	if !bytes.Equal(twice.Data, once.Data) {
		t.Error("Optimise must be idempotent — a second pass re-compressed the payload")
	}
	// And it still round-trips after the redundant pass.
	back, err := opt.Decompress(twice)
	if err != nil {
		t.Fatalf("Decompress error: %v", err)
	}
	if !bytes.Equal(back.Data, original) {
		t.Error("double-optimised payload did not restore original bytes")
	}
}

func TestOptimiser_DecompressPassthrough(t *testing.T) {
	opt := circleai.NewGzipPayloadOptimiser()
	p := circleai.NewNetworkPayload([]byte("plain"), "d")
	out, err := opt.Decompress(p)
	if err != nil {
		t.Fatalf("Decompress error: %v", err)
	}
	if !bytes.Equal(out.Data, []byte("plain")) {
		t.Error("Decompress on an un-optimised payload must be a no-op")
	}
}

func TestOptimiser_ContextCancelled(t *testing.T) {
	opt := circleai.NewGzipPayloadOptimiser()
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	p := circleai.NewNetworkPayload([]byte("data"), "d")
	if _, err := opt.Optimise(ctx, p, circleai.TransportKindBluetooth); err == nil {
		t.Error("Optimise should honour a cancelled context")
	}
}

func TestOptimiser_CorruptStreamErrors(t *testing.T) {
	opt := circleai.NewGzipPayloadOptimiser()
	// A payload marked gzip but carrying non-gzip bytes must surface an error,
	// not panic or silently pass garbage through.
	p := circleai.NewNetworkPayload([]byte("not-gzip-bytes"), "d").
		WithMetadata("x-circleai-optimiser", "gzip")
	if _, err := opt.Decompress(p); err == nil {
		t.Error("Decompress of a corrupt gzip stream should return an error")
	}
}
