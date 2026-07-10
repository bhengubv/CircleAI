// network_message_channel.go
//
// Ports CircleAI.Networking.IMessageChannel (IMessageChannel.cs) and its
// mandated working in-memory implementation.
//
// IMessageChannel provides TYPED message delivery over any transport:
//   Task SendAsync<T>(string destinationId, T message, ct) where T : class
//   IAsyncEnumerable<T> ReceiveAsync<T>(ct)                where T : class
//
// Go has no type-parameterised interface methods, so the generic surface is
// modelled as:
//   - IMessageChannel: a NON-generic interface that moves already-encoded
//     objects (SendObject / ReceiveObjects) over an injected INetworkTransport
//     using an injected IMessageCodec. This is the seam a real transport plugs
//     into.
//   - SendMessage[T] / ReceiveMessages[T]: package-level GENERIC helpers that
//     reproduce the C# `SendAsync<T>` / `ReceiveAsync<T>` call sites with full
//     type safety, delegating encode/decode to the channel's codec.
//
// The T:class constraint (reference types) maps to Go struct/pointer payload
// types; the default JSON codec round-trips any such value deterministically.

package circleai

import (
	"context"
	"encoding/json"
	"errors"
)

// ---------------------------------------------------------------------------
// IMessageCodec — the serialisation seam
// ---------------------------------------------------------------------------

// IMessageCodec encodes a typed message into NetworkPayload bytes and decodes
// them back. It is injected behind IMessageChannel so the transport layer stays
// wire-format agnostic. ContentType is stamped onto outbound payloads and lets
// receivers route by media type.
type IMessageCodec interface {
	// ContentType is the media type this codec produces (e.g. application/json).
	ContentType() string
	// Encode serialises message to bytes.
	Encode(message any) ([]byte, error)
	// Decode deserialises data into the value pointed to by target.
	Decode(data []byte, target any) error
}

// JSONMessageCodec is the default IMessageCodec: deterministic JSON.
type JSONMessageCodec struct{}

// ContentType returns "application/json".
func (JSONMessageCodec) ContentType() string { return "application/json" }

// Encode marshals message as JSON.
func (JSONMessageCodec) Encode(message any) ([]byte, error) { return json.Marshal(message) }

// Decode unmarshals JSON data into target.
func (JSONMessageCodec) Decode(data []byte, target any) error { return json.Unmarshal(data, target) }

var _ IMessageCodec = JSONMessageCodec{}

// ---------------------------------------------------------------------------
// IMessageChannel — IMessageChannel.cs
// ---------------------------------------------------------------------------

// IMessageChannel is typed message delivery over any transport, expressed
// non-generically (see SendMessage[T]/ReceiveMessages[T] for the typed sugar).
type IMessageChannel interface {
	// Codec is the serialiser used for encode/decode.
	Codec() IMessageCodec
	// SendObject encodes message and sends it to destinationID.
	SendObject(ctx context.Context, destinationID string, message any) error
	// ReceiveObjects returns a stream of raw inbound payloads carrying encoded
	// messages. The channel closes when ctx is cancelled or the transport stops.
	ReceiveObjects(ctx context.Context) <-chan NetworkPayload
}

// ---------------------------------------------------------------------------
// TransportMessageChannel — working IMessageChannel over an INetworkTransport
// ---------------------------------------------------------------------------

// TransportMessageChannel implements IMessageChannel by encoding messages onto
// NetworkPayloads and shipping them over an injected INetworkTransport. Safe for
// concurrent use (delegates to the transport's own safety).
type TransportMessageChannel struct {
	transport INetworkTransport
	codec     IMessageCodec
	sourceID  string
	priority  MessagePriority
}

// NewTransportMessageChannel builds a channel over transport. Pass nil codec for
// the default JSONMessageCodec. sourceID is stamped as the payload SourceID (may
// be ""). Outbound payloads default to MessagePriorityNormal.
func NewTransportMessageChannel(transport INetworkTransport, codec IMessageCodec, sourceID string) (*TransportMessageChannel, error) {
	if transport == nil {
		return nil, errors.New("transport required")
	}
	if codec == nil {
		codec = JSONMessageCodec{}
	}
	return &TransportMessageChannel{
		transport: transport,
		codec:     codec,
		sourceID:  sourceID,
		priority:  MessagePriorityNormal,
	}, nil
}

// WithPriority returns a shallow copy of the channel that stamps outbound
// payloads with priority. The receiver is left unchanged.
func (c *TransportMessageChannel) WithPriority(priority MessagePriority) *TransportMessageChannel {
	clone := *c
	clone.priority = priority
	return &clone
}

// Codec returns the channel's codec.
func (c *TransportMessageChannel) Codec() IMessageCodec { return c.codec }

// SendObject encodes message with the codec, wraps it in a NetworkPayload
// addressed to destinationID, and sends it over the transport.
func (c *TransportMessageChannel) SendObject(ctx context.Context, destinationID string, message any) error {
	if message == nil {
		return errors.New("message required")
	}
	data, err := c.codec.Encode(message)
	if err != nil {
		return err
	}
	payload := NewNetworkPayloadWith(data, destinationID, c.priority, c.codec.ContentType(), nil)
	if c.sourceID != "" {
		payload.SourceID = c.sourceID
	}
	return c.transport.Send(ctx, payload)
}

// ReceiveObjects returns the transport's inbound payload stream unchanged;
// callers decode with ReceiveMessages[T] or the codec directly.
func (c *TransportMessageChannel) ReceiveObjects(ctx context.Context) <-chan NetworkPayload {
	return c.transport.Receive(ctx)
}

var _ IMessageChannel = (*TransportMessageChannel)(nil)

// ---------------------------------------------------------------------------
// Generic typed sugar — reproduces SendAsync<T> / ReceiveAsync<T>
// ---------------------------------------------------------------------------

// SendMessage is the generic equivalent of IMessageChannel.SendAsync<T>: it
// sends a typed message to destinationID over channel, using channel.Codec.
func SendMessage[T any](ctx context.Context, channel IMessageChannel, destinationID string, message T) error {
	return channel.SendObject(ctx, destinationID, message)
}

// ReceiveMessages is the generic equivalent of IMessageChannel.ReceiveAsync<T>:
// it returns a stream of decoded T values plus a stream carrying at most one
// decode error. Payloads whose ContentType does not match the channel codec are
// skipped (a channel may carry mixed types; a T-typed reader only sees T-shaped
// frames it can decode). The errs channel is closed with the out channel.
//
// Decoding starts a goroutine that reads the channel's raw payload stream. A
// payload that fails to decode into *T is reported once on errs and terminates
// the stream, mirroring a hard deserialisation failure surfacing to the awaiter
// of the C# IAsyncEnumerable<T>.
func ReceiveMessages[T any](ctx context.Context, channel IMessageChannel) (<-chan T, <-chan error) {
	out := make(chan T)
	errs := make(chan error, 1)
	raw := channel.ReceiveObjects(ctx)
	wantType := channel.Codec().ContentType()

	go func() {
		defer close(out)
		defer close(errs)
		for {
			select {
			case <-ctx.Done():
				return
			case payload, ok := <-raw:
				if !ok {
					return
				}
				// Skip frames that clearly belong to another codec/type.
				if payload.ContentType != "" && wantType != "" && payload.ContentType != wantType {
					continue
				}
				var v T
				if err := channel.Codec().Decode(payload.Data, &v); err != nil {
					select {
					case errs <- err:
					default:
					}
					return
				}
				select {
				case out <- v:
				case <-ctx.Done():
					return
				}
			}
		}
	}()

	return out, errs
}
