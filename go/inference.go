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

	// IncludeReasoning controls whether the model's reasoning trace (Qwen3
	// <think>…</think>) is surfaced on the call. Default true.
	//
	// When true the generator separates reasoning from the final answer:
	// ChatResponse.ReasoningContent gets the reasoning, ChatResponse.Text
	// gets the answer. Streaming callers see fragments tagged with
	// ChatFragmentReasoning.
	//
	// When false the generator still RUNS reasoning (this is per-call output
	// gating, NOT a thinking disable) but the reasoning text is dropped —
	// only the final answer reaches the caller. Use this for JSON-strict
	// consumers.
	IncludeReasoning bool

	// Budget (RT-11) is the declarative per-call power budget. The runtime
	// maps it to a max-tokens cap and (eventually) model size. Default
	// PowerBudgetNormal auto-downgrades to PowerBudgetLow below 15% battery.
	Budget PowerBudget

	// UsePrefixCache (RT-06): whether the runtime should consult the
	// cross-session prefix cache for a warm (modelId, systemPrompt) snapshot
	// before resetting the model handle. Default false.
	UsePrefixCache bool
}

// PowerBudget — per-call power budget. The runtime maps it to a max-tokens
// cap and (when fallback chains are configured) into a model-size pick.
type PowerBudget int

const (
	// PowerBudgetNone — opt out of automatic budget control. Honour MaxTokens literally.
	PowerBudgetNone PowerBudget = 0
	// PowerBudgetLow — ~64 token cap, prefers TQ4 KV, smaller model in chain.
	PowerBudgetLow PowerBudget = 1
	// PowerBudgetNormal — default. ~512 token cap. Auto-downgrades to Low below 15% battery.
	PowerBudgetNormal PowerBudget = 2
	// PowerBudgetHigh — ~2048 token cap, full FP16 KV. Auto-throttles on thermal warnings.
	PowerBudgetHigh PowerBudget = 3
)

// DefaultGenerationOptions returns a GenerationOptions with sensible defaults.
func DefaultGenerationOptions() GenerationOptions {
	return GenerationOptions{
		MaxTokens:        512,
		Temperature:      0.7,
		TopP:             0.9,
		TopK:             40,
		IncludeReasoning: true,
		Budget:           PowerBudgetNormal,
		UsePrefixCache:   false,
	}
}

// SessionPersistence is an optional extension implemented by generators
// (typically MNN-backed) that can snapshot their KV state to disk. RT-02
// surface — used to survive Android/iOS OOM kills.
type SessionPersistence interface {
	SaveSession(path string) (bool, error)
	LoadSession(path string) (bool, error)
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
	// receives at most one error and is then closed. Content only — any
	// reasoning emitted inside <think>…</think> is filtered out. Use the
	// StreamFragmentsAware extension when you also need the reasoning stream.
	Stream(ctx context.Context, messages []ChatMessage, opts *GenerationOptions) (<-chan string, <-chan error)

	// Close releases all native model resources held by this generator.
	Close() error
}

// StreamFragmentsAware is an optional extension that surfaces both content
// and reasoning fragments tagged by Kind. Generators that produce a
// <think>…</think> reasoning trace should implement it so the caller can
// route the trace into a separate reasoning_content field.
type StreamFragmentsAware interface {
	StreamFragments(ctx context.Context, messages []ChatMessage, opts *GenerationOptions) (<-chan ChatFragment, <-chan error)
}

// StreamFragments is the default-implementation helper. If the generator
// implements StreamFragmentsAware it is used directly; otherwise the
// generator's Stream output is wrapped and every chunk is tagged as
// ChatFragmentContent. This helper does NOT split <think> tags — that
// requires generator-level token routing.
func StreamFragments(ctx context.Context, g IChatGenerator, messages []ChatMessage, opts *GenerationOptions) (<-chan ChatFragment, <-chan error) {
	if aware, ok := g.(StreamFragmentsAware); ok {
		return aware.StreamFragments(ctx, messages, opts)
	}

	chunks, errs := g.Stream(ctx, messages, opts)
	frags := make(chan ChatFragment)
	out := make(chan error, 1)

	go func() {
		defer close(frags)
		defer close(out)
		for {
			select {
			case <-ctx.Done():
				out <- ctx.Err()
				return
			case chunk, ok := <-chunks:
				if !ok {
					// drain the error channel before exiting
					if err, ok := <-errs; ok && err != nil {
						out <- err
					}
					return
				}
				select {
				case frags <- ChatFragment{Kind: ChatFragmentContent, Text: chunk}:
				case <-ctx.Done():
					out <- ctx.Err()
					return
				}
			case err := <-errs:
				if err != nil {
					out <- err
				}
				return
			}
		}
	}()

	return frags, out
}
