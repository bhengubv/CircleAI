// network_payload_optimiser.go
//
// Ports CircleAI.Networking.IPayloadOptimiser (IPayloadOptimiser.cs) and supplies
// the mandated working deterministic implementation.
//
// IPayloadOptimiser compresses or transforms payloads for low-bandwidth
// transports (BLE, NearLink, LoRa, DTN) so a large frame can survive a
// constrained link. The default GzipPayloadOptimiser deflates the payload Data
// with a fixed compression level (byte-deterministic output), but ONLY when:
//   - the target transport is bandwidth-constrained (BLE / NearLink / DTN — the
//     kinds the C# doc-comment calls out), and
//   - compression actually shrinks the payload (never inflate a tiny frame).
// A marker is stamped into Metadata so Decompress can reverse the transform;
// Decompress is idempotent on payloads that were never optimised.
//
// Go modelling of the C# surface:
//   ValueTask<NetworkPayload> OptimiseAsync(payload, targetTransport, ct)
//                                -> Optimise(ctx, payload, target) (NetworkPayload, error)
//   NetworkPayload Decompress(payload)
//                                -> Decompress(payload) (NetworkPayload, error)
// The C# Decompress is synchronous and never documents a failure mode; the Go
// signature returns an error so a corrupt gzip stream surfaces rather than
// panicking, upholding "no swallowed catches".

package circleai

import (
	"bytes"
	"compress/gzip"
	"context"
	"io"
)

// optimiserCodecKey is the Metadata key marking how a payload was optimised, so
// Decompress knows whether (and how) to reverse the transform.
const optimiserCodecKey = "x-circleai-optimiser"

// optimiserCodecGzip is the Metadata value stamped on gzip-compressed payloads.
const optimiserCodecGzip = "gzip"

// ---------------------------------------------------------------------------
// IPayloadOptimiser — IPayloadOptimiser.cs
// ---------------------------------------------------------------------------

// IPayloadOptimiser compresses or transforms payloads for low-bandwidth
// transports (BLE, NearLink, LoRa, DTN).
type IPayloadOptimiser interface {
	// Optimise returns a (possibly transformed) payload tuned for targetTransport.
	// For bandwidth-rich transports, or when compression would not help, it
	// returns the payload unchanged.
	Optimise(ctx context.Context, payload NetworkPayload, targetTransport TransportKind) (NetworkPayload, error)
	// Decompress reverses a prior Optimise. It is idempotent on payloads that
	// were never optimised (returns them unchanged).
	Decompress(payload NetworkPayload) (NetworkPayload, error)
}

// ---------------------------------------------------------------------------
// GzipPayloadOptimiser — working IPayloadOptimiser
// ---------------------------------------------------------------------------

// GzipPayloadOptimiser is the default IPayloadOptimiser: it gzip-compresses the
// payload Data for low-bandwidth transports when that shrinks the frame. It is
// stateless and safe for concurrent use.
type GzipPayloadOptimiser struct{}

// NewGzipPayloadOptimiser returns the default optimiser.
func NewGzipPayloadOptimiser() GzipPayloadOptimiser { return GzipPayloadOptimiser{} }

// isLowBandwidth reports whether a transport is constrained enough to warrant
// payload compression. Matches the transports the C# doc-comment names: BLE,
// NearLink, LoRa (modelled here as the DTN store-and-forward path plus the
// short-range radios). Cloud/LAN transports keep the payload verbatim.
func isLowBandwidthTransport(k TransportKind) bool {
	switch k {
	case TransportKindBluetooth, TransportKindNearLink, TransportKindDtn:
		return true
	default:
		return false
	}
}

// Optimise gzip-compresses payload.Data when targetTransport is low-bandwidth and
// the compressed form is strictly smaller than the original. Otherwise it returns
// the payload unchanged. An already-optimised payload is returned as-is (never
// double-compressed).
func (GzipPayloadOptimiser) Optimise(ctx context.Context, payload NetworkPayload, targetTransport TransportKind) (NetworkPayload, error) {
	if err := ctx.Err(); err != nil {
		return NetworkPayload{}, err
	}
	if !isLowBandwidthTransport(targetTransport) {
		return payload, nil
	}
	if payload.Metadata != nil {
		if _, already := payload.Metadata[optimiserCodecKey]; already {
			return payload, nil // idempotent — do not re-compress
		}
	}
	if len(payload.Data) == 0 {
		return payload, nil
	}

	compressed, err := gzipCompress(payload.Data)
	if err != nil {
		return NetworkPayload{}, err
	}
	// Never inflate: if gzip did not shrink the frame, keep the original bytes.
	if len(compressed) >= len(payload.Data) {
		return payload, nil
	}

	out := payload
	out.Data = compressed
	out.Metadata = copyStringMap(payload.Metadata)
	if out.Metadata == nil {
		out.Metadata = map[string]string{}
	}
	out.Metadata[optimiserCodecKey] = optimiserCodecGzip
	return out, nil
}

// Decompress reverses a gzip Optimise. A payload without the optimiser marker is
// returned unchanged (idempotent), so it is always safe to call on any inbound
// payload.
func (GzipPayloadOptimiser) Decompress(payload NetworkPayload) (NetworkPayload, error) {
	if payload.Metadata == nil {
		return payload, nil
	}
	codec, ok := payload.Metadata[optimiserCodecKey]
	if !ok || codec != optimiserCodecGzip {
		return payload, nil
	}

	raw, err := gzipDecompress(payload.Data)
	if err != nil {
		return NetworkPayload{}, err
	}

	out := payload
	out.Data = raw
	out.Metadata = copyStringMap(payload.Metadata)
	delete(out.Metadata, optimiserCodecKey)
	return out, nil
}

var _ IPayloadOptimiser = GzipPayloadOptimiser{}

// gzipCompress deflates data with a fixed level so output is deterministic.
func gzipCompress(data []byte) ([]byte, error) {
	var buf bytes.Buffer
	w, err := gzip.NewWriterLevel(&buf, gzip.BestCompression)
	if err != nil {
		return nil, err
	}
	if _, err := w.Write(data); err != nil {
		_ = w.Close()
		return nil, err
	}
	if err := w.Close(); err != nil {
		return nil, err
	}
	return buf.Bytes(), nil
}

// gzipDecompress inflates a gzip stream produced by gzipCompress.
func gzipDecompress(data []byte) ([]byte, error) {
	r, err := gzip.NewReader(bytes.NewReader(data))
	if err != nil {
		return nil, err
	}
	defer r.Close()
	out, err := io.ReadAll(r)
	if err != nil {
		return nil, err
	}
	return out, nil
}
