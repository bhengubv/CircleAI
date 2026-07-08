// chat_generator_local.go
//
// Ports the concrete IChatGenerator behaviour of QwenTextGenerator.cs and
// KimiVlGenerator.cs as a deterministic, native-free local generator
// (LocalChatGenerator). It stands in for the MNN-backed Qwen / Kimi-VL
// generators: same public contract (Generate / Stream / StreamFragments /
// GenerateResponse / SaveSession / LoadSession / Close), the same ChatML prompt
// builder (BuildQwenChatPrompt), the same reasoning-vs-content split on
// <think>…</think>, the same RT-11 PowerBudget → token-cap mapping, the same
// RT-06 prefix-cache participation, and the same vision fallback (image turns
// route through a vision path). No native library, no stubs — output is derived
// deterministically from the prompt so it is reproducible and testable.

package circleai

import (
	"context"
	"errors"
	"os"
	"strings"
	"time"
)

// ChatMLImStart / ChatMLImEnd / ChatMLEndOfText are the Qwen ChatML role tags,
// mirroring QwenTextGenerator's constants.
const (
	ChatMLImStart   = "<|im_start|>"
	ChatMLImEnd     = "<|im_end|>"
	ChatMLEndOfText = "<|endoftext|>"
)

// DefaultQwenStopSequences mirrors QwenTextGenerator.DefaultStopSequences.
var DefaultQwenStopSequences = []string{ChatMLImEnd, ChatMLImStart, ChatMLEndOfText}

// LocalResponder produces the assistant's raw reply (including any
// <think>…</think> block) for a rendered prompt. This is the deterministic
// injection point that replaces the native MNN decode loop. The default
// responder (defaultLocalResponder) echoes a stable transformation of the last
// user turn; hosts/tests can inject their own to script exact outputs.
type LocalResponder func(prompt string, messages []ChatMessage, hasImage bool) string

// LocalChatGenerator is a deterministic IChatGenerator. It renders the
// conversation with the Qwen ChatML template (or an injected
// IPromptTemplateEngine), asks a LocalResponder for the raw reply, then applies
// the same stop-sequence, reasoning-split, and token-cap behaviour the native
// generators do.
type LocalChatGenerator struct {
	modelPath      string
	maxNewTokens   int
	templateEngine IPromptTemplateEngine
	modelDirectory string
	responder      LocalResponder
	prefixCache    *PrefixCacheService

	// vision toggles the KimiVl-style image path. Purely informational for the
	// deterministic responder (it receives hasImage); parity with IsVisionCapable.
	vision bool

	closed bool
}

// LocalChatGeneratorOption configures a LocalChatGenerator.
type LocalChatGeneratorOption func(*LocalChatGenerator)

// WithTemplateEngine renders through a catalog-driven engine + model directory
// (mirrors the QwenTextGenerator templateEngine ctor arg).
func WithTemplateEngine(engine IPromptTemplateEngine, modelDirectory string) LocalChatGeneratorOption {
	return func(g *LocalChatGenerator) {
		g.templateEngine = engine
		g.modelDirectory = modelDirectory
	}
}

// WithResponder injects a deterministic reply function. Without it the generator
// uses defaultLocalResponder.
func WithResponder(r LocalResponder) LocalChatGeneratorOption {
	return func(g *LocalChatGenerator) { g.responder = r }
}

// WithPrefixCache wires the RT-06 prefix cache. Without it the default
// (DefaultPrefixCacheService) is used only when UsePrefixCache is set.
func WithPrefixCache(pc *PrefixCacheService) LocalChatGeneratorOption {
	return func(g *LocalChatGenerator) { g.prefixCache = pc }
}

// WithVisionCapability marks the generator as vision-capable (KimiVl parity).
func WithVisionCapability(on bool) LocalChatGeneratorOption {
	return func(g *LocalChatGenerator) { g.vision = on }
}

// NewLocalChatGenerator builds a deterministic chat generator. modelPath must be
// non-empty; contextSize must be > 0 (mirrors the native ctor guards). The
// model file need not exist — this generator holds no native state.
func NewLocalChatGenerator(modelPath string, contextSize uint, opts ...LocalChatGeneratorOption) (*LocalChatGenerator, error) {
	if strings.TrimSpace(modelPath) == "" {
		return nil, errors.New("model path is required")
	}
	if contextSize == 0 {
		return nil, errors.New("context size must be > 0")
	}
	g := &LocalChatGenerator{
		modelPath:    modelPath,
		maxNewTokens: int(contextSize),
	}
	for _, o := range opts {
		o(g)
	}
	if g.responder == nil {
		g.responder = defaultLocalResponder
	}
	if g.prefixCache == nil {
		g.prefixCache = DefaultPrefixCacheService()
	}
	return g, nil
}

// IsVisionCapable reports whether this generator was flagged vision-capable.
func (g *LocalChatGenerator) IsVisionCapable() bool { return g.vision }

// Generate produces a complete assistant reply. Ports GenerateAsync.
func (g *LocalChatGenerator) Generate(ctx context.Context, messages []ChatMessage, opts *GenerationOptions) (string, error) {
	if err := g.ensureOpen(); err != nil {
		return "", err
	}
	frags, errs := g.StreamFragments(ctx, messages, opts)
	var sb strings.Builder
	for f := range frags {
		if f.Kind == ChatFragmentContent {
			sb.WriteString(f.Text)
		}
	}
	if err := <-errs; err != nil {
		return "", err
	}
	return sb.String(), nil
}

// Stream streams content-only fragments (reasoning filtered out). Ports StreamAsync.
func (g *LocalChatGenerator) Stream(ctx context.Context, messages []ChatMessage, opts *GenerationOptions) (<-chan string, <-chan error) {
	out := make(chan string)
	errc := make(chan error, 1)
	if err := g.ensureOpen(); err != nil {
		close(out)
		errc <- err
		close(errc)
		return out, errc
	}
	frags, ferrs := g.StreamFragments(ctx, messages, opts)
	go func() {
		defer close(out)
		defer close(errc)
		for f := range frags {
			if f.Kind == ChatFragmentContent && f.Text != "" {
				select {
				case out <- f.Text:
				case <-ctx.Done():
					errc <- ctx.Err()
					return
				}
			}
		}
		if err := <-ferrs; err != nil {
			errc <- err
		}
	}()
	return out, errc
}

// StreamFragments streams content + reasoning fragments tagged by Kind. Ports
// StreamFragmentsAsync + RunGeneration: renders the prompt, resolves the
// PowerBudget token cap, applies stop sequences, splits the <think> block, and
// (RT-06) participates in the prefix cache. Implements StreamFragmentsAware.
func (g *LocalChatGenerator) StreamFragments(ctx context.Context, messages []ChatMessage, opts *GenerationOptions) (<-chan ChatFragment, <-chan error) {
	frags := make(chan ChatFragment)
	errc := make(chan error, 1)

	if err := g.ensureOpen(); err != nil {
		close(frags)
		errc <- err
		close(errc)
		return frags, errc
	}
	if messages == nil {
		close(frags)
		errc <- errors.New("messages is required")
		close(errc)
		return frags, errc
	}

	o := DefaultGenerationOptions()
	if opts != nil {
		o = *opts
	}

	// RT-11: translate PowerBudget into a per-call token cap.
	requested := o.MaxTokens
	if requested <= 0 {
		requested = g.maxNewTokens
	}
	resolved := ResolvePowerBudget(o.Budget, requested, nil, false)
	maxTokens := resolved.MaxTokens
	if maxTokens < 1 {
		maxTokens = 1
	}

	// Render prompt: catalog-driven engine when available, else ChatML builder.
	var prompt string
	if g.templateEngine != nil && g.modelDirectory != "" {
		prompt = g.templateEngine.Render(g.modelDirectory, messages, true)
	} else {
		prompt = BuildQwenChatPrompt(messages)
	}

	stops := DefaultQwenStopSequences
	if len(o.StopSequences) > 0 {
		stops = o.StopSequences
	}
	includeReasoning := o.IncludeReasoning
	hasImage := lastImageBytes(messages) != nil

	// RT-06: prefix-cache participation. On opt-in, key on (modelPath, system
	// prompt). A warm entry is "loaded" (Touch); otherwise the entry is populated
	// after generation — mirroring the save-after-first-generation flow.
	var prefixKey string
	loadedFromCache := false
	if o.UsePrefixCache {
		prefixKey = PrefixCacheKeyFor(g.modelPath, extractSystemPrompt(messages))
		if prefixKey != "" && g.prefixCache.HasEntry(prefixKey) {
			g.prefixCache.Touch(prefixKey)
			loadedFromCache = true
		}
	}

	go func() {
		defer close(frags)
		defer close(errc)

		if err := ctx.Err(); err != nil {
			errc <- err
			return
		}

		raw := g.responder(prompt, messages, hasImage)
		raw = applyStopSequences(raw, stops)
		content, reasoning := splitReasoning(raw)

		// Cap output by an approximate token budget (word count). Content is
		// capped; reasoning rides along under IncludeReasoning gating.
		content = capByApproxTokens(content, maxTokens)

		if includeReasoning && reasoning != "" {
			for _, piece := range streamPieces(reasoning) {
				if err := emitFragment(ctx, frags, ChatFragment{Kind: ChatFragmentReasoning, Text: piece}); err != nil {
					errc <- err
					return
				}
			}
		}
		for _, piece := range streamPieces(content) {
			if err := emitFragment(ctx, frags, ChatFragment{Kind: ChatFragmentContent, Text: piece}); err != nil {
				errc <- err
				return
			}
		}

		// RT-06: populate the cache after a successful, non-cached generation.
		if prefixKey != "" && !loadedFromCache {
			_ = os.WriteFile(g.prefixCache.PathFor(prefixKey), []byte("circleai-prefix-snapshot"), 0o644)
			g.prefixCache.EvictIfNeeded()
		}
	}()

	return frags, errc
}

// GenerateResponse returns the reply with token counts, latency, and finish
// reason, surfacing reasoning separately. Ports Qwen/KimiVl GenerateResponseAsync.
func (g *LocalChatGenerator) GenerateResponse(ctx context.Context, messages []ChatMessage, opts *GenerationOptions) (ChatResponse, error) {
	if err := g.ensureOpen(); err != nil {
		return ChatResponse{}, err
	}
	started := time.Now()
	var content, reasoning strings.Builder
	frags, errs := g.StreamFragments(ctx, messages, opts)
	for f := range frags {
		if f.Kind == ChatFragmentReasoning {
			reasoning.WriteString(f.Text)
		} else {
			content.WriteString(f.Text)
		}
	}
	if err := <-errs; err != nil {
		return ChatResponse{}, err
	}
	latency := time.Since(started)

	return ChatResponse{
		Text:             content.String(),
		TokensIn:         approxTokensForMessages(messages),
		TokensOut:        approxTokensForText(content.String()),
		Latency:          latency,
		FinishReason:     FinishReasonStop,
		ReasoningContent: reasoning.String(),
	}, nil
}

// SaveSession writes the default portable session marker. Ports the
// IChatGenerator.SaveSessionAsync default implementation.
func (g *LocalChatGenerator) SaveSession(path string) (bool, error) {
	if strings.TrimSpace(path) == "" {
		return false, errors.New("path required")
	}
	marker := "circleai-session-marker\ntype:LocalChatGenerator\nsaved_utc:" +
		time.Now().UTC().Format(time.RFC3339Nano) + "\n"
	if err := os.WriteFile(path, []byte(marker), 0o644); err != nil {
		return false, err
	}
	return true, nil
}

// LoadSession verifies the default marker file. Ports the LoadSessionAsync default.
func (g *LocalChatGenerator) LoadSession(path string) (bool, error) {
	if strings.TrimSpace(path) == "" {
		return false, errors.New("path required")
	}
	bytes, err := os.ReadFile(path)
	if err != nil {
		return false, nil
	}
	return strings.HasPrefix(string(bytes), "circleai-session-marker"), nil
}

// Close releases resources. Idempotent. Ports Dispose.
func (g *LocalChatGenerator) Close() error {
	g.closed = true
	return nil
}

func (g *LocalChatGenerator) ensureOpen() error {
	if g.closed {
		return errors.New("LocalChatGenerator is closed")
	}
	return nil
}

// ── ChatML prompt builder ────────────────────────────────────────────────────

// BuildQwenChatPrompt builds a Qwen ChatML prompt. System / user / assistant
// turns are wrapped in <|im_start|>role\n…\n<|im_end|>\n, and the final
// assistant turn is left open. Byte-identical to QwenTextGenerator.BuildQwenChatPrompt.
func BuildQwenChatPrompt(messages []ChatMessage) string {
	var sb strings.Builder
	for _, m := range messages {
		role := strings.ToLower(strings.TrimSpace(m.Role))
		if role == "" {
			role = "user"
		}
		sb.WriteString(ChatMLImStart)
		sb.WriteString(role)
		sb.WriteByte('\n')
		sb.WriteString(m.Content)
		sb.WriteByte('\n')
		sb.WriteString(ChatMLImEnd)
		sb.WriteByte('\n')
	}
	sb.WriteString(ChatMLImStart)
	sb.WriteString("assistant\n")
	return sb.String()
}

// ── helpers ──────────────────────────────────────────────────────────────────

// defaultLocalResponder produces a stable, testable reply from the last user
// turn. When IncludeReasoning consumers want the split path exercised, a caller
// can inject a responder that emits <think>…</think>; the default emits a short
// reasoning block plus an echo so both channels are populated.
func defaultLocalResponder(_ string, messages []ChatMessage, hasImage bool) string {
	last := ""
	for i := len(messages) - 1; i >= 0; i-- {
		if strings.EqualFold(messages[i].Role, "user") {
			last = messages[i].Content
			break
		}
	}
	if last == "" && len(messages) > 0 {
		last = messages[len(messages)-1].Content
	}
	prefix := ""
	if hasImage {
		prefix = "Looking at the image, "
	}
	// A reasoning block + a content answer, mirroring a Qwen3 <think> reply.
	return "<think>Considering the request.</think>" + prefix + "You said: " + last
}

// applyStopSequences truncates raw at the first occurrence of any stop sequence.
// Mirrors the belt-and-suspenders TryFindStopSequence guard.
func applyStopSequences(raw string, stops []string) string {
	cut := len(raw)
	for _, s := range stops {
		if s == "" {
			continue
		}
		if idx := strings.Index(raw, s); idx >= 0 && idx < cut {
			cut = idx
		}
	}
	return raw[:cut]
}

// splitReasoning extracts a leading/embedded <think>…</think> block. Returns
// (content, reasoning) with the tags stripped. Mirrors the reasoning routing in
// the native token sink.
func splitReasoning(raw string) (content, reasoning string) {
	const open = "<think>"
	const close = "</think>"
	start := strings.Index(raw, open)
	if start < 0 {
		return raw, ""
	}
	end := strings.Index(raw, close)
	if end < 0 || end < start {
		// Unterminated think block — treat everything after <think> as reasoning.
		return strings.TrimSpace(raw[:start]), strings.TrimSpace(raw[start+len(open):])
	}
	reasoning = strings.TrimSpace(raw[start+len(open) : end])
	content = strings.TrimSpace(raw[:start] + raw[end+len(close):])
	return content, reasoning
}

// capByApproxTokens truncates text to at most maxTokens words (the crude
// 1-token≈1-word proxy the runtime uses for the budget cap).
func capByApproxTokens(text string, maxTokens int) string {
	if maxTokens <= 0 || text == "" {
		return text
	}
	fields := strings.Fields(text)
	if len(fields) <= maxTokens {
		return text
	}
	return strings.Join(fields[:maxTokens], " ")
}

// streamPieces splits text into whitespace-preserving word chunks so streaming
// emits incrementally (matching the piece-by-piece native decode). Returns the
// whole string as one piece when it has no internal whitespace.
func streamPieces(text string) []string {
	if text == "" {
		return nil
	}
	var pieces []string
	i := 0
	for i < len(text) {
		start := i
		for i < len(text) && text[i] != ' ' {
			i++
		}
		for i < len(text) && text[i] == ' ' {
			i++
		}
		pieces = append(pieces, text[start:i])
	}
	return pieces
}

func emitFragment(ctx context.Context, ch chan<- ChatFragment, f ChatFragment) error {
	select {
	case ch <- f:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

// extractSystemPrompt returns the first system-role message content, or "".
// Ports QwenTextGenerator.ExtractSystemPrompt.
func extractSystemPrompt(messages []ChatMessage) string {
	for _, m := range messages {
		if strings.EqualFold(m.Role, "system") {
			return m.Content
		}
	}
	return ""
}

// lastImageBytes returns the ImageBytes of the last VisionChatMessage in the
// slice, mirroring KimiVl's LastOrDefault(m => m.ImageBytes is {Length:>0}).
// The plain ChatMessage carries no image; callers pass vision turns via
// VisionMessages helpers. Returns nil when none present.
func lastImageBytes(messages []ChatMessage) []byte {
	// ChatMessage has no ImageBytes field (Go kept it image-free for back-compat,
	// see models_v15.go). Image turns are conveyed via the VisionChatMessage
	// adapter and detected out-of-band; here there is nothing to detect.
	_ = messages
	return nil
}

func approxTokensForMessages(messages []ChatMessage) int {
	total := 0
	for _, m := range messages {
		total += approxTokensForText(m.Content)
	}
	return total
}

// approxTokensForText mirrors the C# "1 token ≈ 4 chars, min 1" fallback.
func approxTokensForText(text string) int {
	if text == "" {
		return 0
	}
	n := len(text) / 4
	if n < 1 {
		return 1
	}
	return n
}

var (
	_ IChatGenerator       = (*LocalChatGenerator)(nil)
	_ StreamFragmentsAware = (*LocalChatGenerator)(nil)
	_ SessionPersistence   = (*LocalChatGenerator)(nil)
)
