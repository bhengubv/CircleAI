// inference.go
//
// GenerationOptions, IChatGenerator.
//
// Contract for an on-device chat-style text generator. Implementations own
// native model state and must be closed when done.

package circleai

import "context"

// ---------------------------------------------------------------------------
// GenerationOptions
// ---------------------------------------------------------------------------

// GenerationOptions holds the knobs for a single generation call.
type GenerationOptions struct {
	// MaxTokens is the maximum number of new tokens to produce.
	// Default: 512.
	MaxTokens int

	// Temperature is the sampling temperature. 0 = greedy; higher = more random.
	// Default: 0.7.
	Temperature float32

	// TopP is the nucleus sampling cutoff (top-p). 1.0 disables.
	// Default: 0.9.
	TopP float32

	// TopK is the top-k cutoff. 0 disables.
	// Default: 40.
	TopK int

	// Seed is an optional RNG seed. nil means non-deterministic.
	Seed *int

	// StopSequences holds optional substrings that will end generation when
	// matched in the emitted output.
	StopSequences []string
}

// DefaultGenerationOptions returns a GenerationOptions with sensible defaults.
func DefaultGenerationOptions() GenerationOptions {
	return GenerationOptions{
		MaxTokens:   512,
		Temperature: 0.7,
		TopP:        0.9,
		TopK:        40,
	}
}

// ---------------------------------------------------------------------------
// IChatGenerator
// ---------------------------------------------------------------------------

// IChatGenerator is the contract for an on-device chat-style text generator.
// Implementations own native model state and must be closed when done.
type IChatGenerator interface {
	// Generate produces a complete assistant reply for the given conversation.
	Generate(ctx context.Context, messages []ChatMessage, opts *GenerationOptions) (string, error)

	// Stream streams the assistant reply token-by-token (or piece-by-piece)
	// as it is decoded. Each string received from the channel is the next
	// chunk to append to the output — callers should concatenate them in order.
	// The tokens channel is closed when the stream ends. The errs channel
	// receives at most one error and is then closed.
	Stream(ctx context.Context, messages []ChatMessage, opts *GenerationOptions) (<-chan string, <-chan error)

	// Close releases all native model resources held by this generator.
	Close() error
}
