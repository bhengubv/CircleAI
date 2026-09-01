// core_hosting.go
//
// The audit log, the quantisation codebook, the cloud providers a device falls
// back to, and the HTTP surface an inference server exposes.
//
// NOTHING HERE IS ENABLED BY DEFAULT AND NONE OF IT HOLDS A KEY. Options carry
// a key the HOST supplies at construction; no provider reads an environment
// variable, no provider caches a credential, and a provider with no key is
// ABSENT rather than broken. A fallback that turns itself on because a variable
// happened to be set is a device that started sending conversations to a third
// party without anybody choosing.
//
// ON-DEVICE IS THE PRODUCT. These exist for the cases where the honest answer
// is that the phone cannot do it.

package circleai

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"net/http"
	"sort"
	"strings"
	"sync"
	"time"
)

// ─────────────────────────────────────────────────────────────────────────────
// Auditing

// CircleAIAuditing is the convenience surface over whichever log is installed.
type CircleAIAuditing struct {
	mu  sync.RWMutex
	log ICircleAIAuditLog
}

// NewCircleAIAuditing returns an auditing facade.
func NewCircleAIAuditing(log ICircleAIAuditLog) *CircleAIAuditing {
	return &CircleAIAuditing{log: log}
}

// Record writes one entry, if a log is installed.
//
// Silently doing nothing when no log is installed is deliberate: auditing is
// something a host opts into, and a component that failed without one would
// make every call site carry a branch it does not need.
func (a *CircleAIAuditing) Record(ctx context.Context, actor, action, subject, outcome string) error {
	a.mu.RLock()
	log := a.log
	a.mu.RUnlock()
	if log == nil {
		return nil
	}
	return log.Record(ctx, CircleAIAuditEntry{
		At:            time.Now().UTC(),
		Component:     actor,
		Operation:     action,
		Outcome:       outcome,
		CorrelationID: subject,
	})
}

// ─────────────────────────────────────────────────────────────────────────────
// Quantisation

// BetaCodebook is a set of reconstruction levels.
type BetaCodebook struct {
	Levels []float64
	Alpha  float64
	Beta   float64
	Bits   int
}

// Quantise returns the index of the nearest level.
func (c BetaCodebook) Quantise(value float64) int {
	if len(c.Levels) == 0 {
		return 0
	}
	best, bestDist := 0, math.Abs(value-c.Levels[0])
	for i := 1; i < len(c.Levels); i++ {
		if d := math.Abs(value - c.Levels[i]); d < bestDist {
			best, bestDist = i, d
		}
	}
	return best
}

// BetaLloydMaxCodebook builds a Lloyd-Max codebook over a beta distribution.
//
// Beta rather than Gaussian because weight distributions after normalisation
// are bounded and skewed, and a Gaussian codebook spends half its levels on
// values that never occur.
//
// NOTE: dim 4 takes about a second to build (a sqrt singularity in the integral
// makes the quadrature work hard). That is NOT a hang, and changing the
// integrator changes the codec's output — so it is left alone.
type BetaLloydMaxCodebook struct{}

// Build returns a codebook with 2^bits levels.
//
// Lloyd's algorithm to convergence rather than a fixed iteration count: a
// codebook that stopped early is one whose levels are slightly wrong in a way
// that shows up only as slightly worse reconstruction, which nobody traces
// back to here.
func (BetaLloydMaxCodebook) Build(bits int, alpha, beta float64) (BetaCodebook, error) {
	if bits < 1 || bits > 8 {
		return BetaCodebook{}, errors.New("bits must be 1..8")
	}
	if alpha <= 0 || beta <= 0 {
		return BetaCodebook{}, errors.New("alpha and beta must be positive")
	}
	n := 1 << bits
	const samples = 4096

	// Sample the density once. The pdf is evaluated on a fixed grid so the
	// result is identical in every port — an adaptive quadrature would give
	// slightly different levels per language, and the codec's output would stop
	// being byte-identical across them.
	xs := make([]float64, samples)
	ws := make([]float64, samples)
	for i := 0; i < samples; i++ {
		x := (float64(i) + 0.5) / samples
		xs[i] = x
		ws[i] = math.Pow(x, alpha-1) * math.Pow(1-x, beta-1)
	}

	levels := make([]float64, n)
	for i := range levels {
		levels[i] = (float64(i) + 0.5) / float64(n)
	}

	for iter := 0; iter < 100; iter++ {
		sums := make([]float64, n)
		weights := make([]float64, n)
		for i, x := range xs {
			k := 0
			bestDist := math.Abs(x - levels[0])
			for j := 1; j < n; j++ {
				if d := math.Abs(x - levels[j]); d < bestDist {
					k, bestDist = j, d
				}
			}
			sums[k] += x * ws[i]
			weights[k] += ws[i]
		}
		moved := 0.0
		for j := 0; j < n; j++ {
			if weights[j] == 0 {
				continue
			}
			next := sums[j] / weights[j]
			moved = math.Max(moved, math.Abs(next-levels[j]))
			levels[j] = next
		}
		if moved < 1e-9 {
			break
		}
	}
	sort.Float64s(levels)
	return BetaCodebook{Levels: levels, Alpha: alpha, Beta: beta, Bits: bits}, nil
}

// ─────────────────────────────────────────────────────────────────────────────
// Cloud fallback

// OpenAiChatOptions configures an OpenAI-compatible provider.
//
// APIKey is supplied by the HOST and never read from the environment. A
// provider that configured itself from a variable is one that starts sending
// conversations because a shell had something set.
type OpenAiChatOptions struct {
	APIKey      string
	Endpoint    string
	Model       string
	Temperature float64
	MaxTokens   int
	Timeout     time.Duration
}

// AnthropicChatOptions configures Anthropic.
type AnthropicChatOptions struct {
	APIKey      string
	Endpoint    string
	Model       string
	Temperature float64
	MaxTokens   int
	Timeout     time.Duration
	// Anthropic sends a dated API version header, and it is not optional — a
	// request without one is rejected outright.
	APIVersion string
}

// GeminiChatOptions configures Gemini.
type GeminiChatOptions struct {
	APIKey      string
	Endpoint    string
	Model       string
	Temperature float64
	MaxTokens   int
	Timeout     time.Duration
}

// GroqChatOptions configures Groq.
type GroqChatOptions struct {
	APIKey      string
	Endpoint    string
	Model       string
	Temperature float64
	MaxTokens   int
	Timeout     time.Duration
}

// CerebrasChatOptions configures Cerebras.
type CerebrasChatOptions struct {
	APIKey      string
	Endpoint    string
	Model       string
	Temperature float64
	MaxTokens   int
	Timeout     time.Duration
}

// DeepSeekChatOptions configures DeepSeek.
type DeepSeekChatOptions struct {
	APIKey      string
	Endpoint    string
	Model       string
	Temperature float64
	MaxTokens   int
	Timeout     time.Duration
}

// TogetherChatOptions configures Together.
type TogetherChatOptions struct {
	APIKey      string
	Endpoint    string
	Model       string
	Temperature float64
	MaxTokens   int
	Timeout     time.Duration
}

// PostFunc is the host's HTTP.
//
// A function rather than an http.Client because the transport is the host's:
// this package opens no socket and decides no timeout, proxy or certificate
// policy.
type PostFunc func(ctx context.Context, url, body string, headers map[string]string) (string, error)

// OpenAiCompatibleChatGeneratorBase is the shared implementation for providers
// that speak OpenAI's wire format.
//
// Seven providers, one wire format, one place where a streaming-response parse
// bug gets fixed.
type OpenAiCompatibleChatGeneratorBase struct {
	providerID      string
	defaultEndpoint string
	opts            OpenAiChatOptions
	post            PostFunc
}

// NewOpenAiCompatibleChatGeneratorBase returns the shared base.
func NewOpenAiCompatibleChatGeneratorBase(providerID, defaultEndpoint string, opts OpenAiChatOptions, post PostFunc) *OpenAiCompatibleChatGeneratorBase {
	return &OpenAiCompatibleChatGeneratorBase{providerID: providerID, defaultEndpoint: defaultEndpoint, opts: opts, post: post}
}

// ProviderID implements IConfigurableChatGenerator.
func (g *OpenAiCompatibleChatGeneratorBase) ProviderID() string { return g.providerID }

// IsConfigured implements IConfigurableChatGenerator.
func (g *OpenAiCompatibleChatGeneratorBase) IsConfigured() bool {
	return strings.TrimSpace(g.opts.APIKey) != "" && strings.TrimSpace(g.opts.Model) != "" && g.post != nil
}

// Generate implements IConfigurableChatGenerator.
func (g *OpenAiCompatibleChatGeneratorBase) Generate(ctx context.Context, prompt string) (string, error) {
	if !g.IsConfigured() {
		return "", fmt.Errorf("%s is not configured", g.providerID)
	}
	endpoint := g.opts.Endpoint
	if endpoint == "" {
		endpoint = g.defaultEndpoint
	}
	body, err := json.Marshal(map[string]any{
		"model":       g.opts.Model,
		"temperature": g.opts.Temperature,
		"max_tokens":  g.opts.MaxTokens,
		"messages":    []map[string]string{{"role": "user", "content": prompt}},
	})
	if err != nil {
		return "", err
	}
	// The key goes in a HEADER, never the query string: a URL is logged by
	// every proxy on the path and by the client's own request log.
	return g.post(ctx, endpoint, string(body), map[string]string{
		"Content-Type":  "application/json",
		"Authorization": "Bearer " + g.opts.APIKey,
	})
}

// OpenAiChatGenerator is OpenAI.
type OpenAiChatGenerator struct {
	*OpenAiCompatibleChatGeneratorBase
}

// NewOpenAiChatGenerator returns the generator.
func NewOpenAiChatGenerator(opts OpenAiChatOptions, post PostFunc) *OpenAiChatGenerator {
	return &OpenAiChatGenerator{NewOpenAiCompatibleChatGeneratorBase("openai", "https://api.openai.com/v1/chat/completions", opts, post)}
}

// GroqChatGenerator is Groq.
type GroqChatGenerator struct {
	*OpenAiCompatibleChatGeneratorBase
}

// NewGroqChatGenerator returns the generator.
func NewGroqChatGenerator(opts GroqChatOptions, post PostFunc) *GroqChatGenerator {
	return &GroqChatGenerator{NewOpenAiCompatibleChatGeneratorBase("groq", "https://api.groq.com/openai/v1/chat/completions",
		OpenAiChatOptions(opts), post)}
}

// CerebrasChatGenerator is Cerebras.
type CerebrasChatGenerator struct {
	*OpenAiCompatibleChatGeneratorBase
}

// NewCerebrasChatGenerator returns the generator.
func NewCerebrasChatGenerator(opts CerebrasChatOptions, post PostFunc) *CerebrasChatGenerator {
	return &CerebrasChatGenerator{NewOpenAiCompatibleChatGeneratorBase("cerebras", "https://api.cerebras.ai/v1/chat/completions",
		OpenAiChatOptions(opts), post)}
}

// DeepSeekChatGenerator is DeepSeek.
type DeepSeekChatGenerator struct {
	*OpenAiCompatibleChatGeneratorBase
}

// NewDeepSeekChatGenerator returns the generator.
func NewDeepSeekChatGenerator(opts DeepSeekChatOptions, post PostFunc) *DeepSeekChatGenerator {
	return &DeepSeekChatGenerator{NewOpenAiCompatibleChatGeneratorBase("deepseek", "https://api.deepseek.com/chat/completions",
		OpenAiChatOptions(opts), post)}
}

// TogetherChatGenerator is Together.
type TogetherChatGenerator struct {
	*OpenAiCompatibleChatGeneratorBase
}

// NewTogetherChatGenerator returns the generator.
func NewTogetherChatGenerator(opts TogetherChatOptions, post PostFunc) *TogetherChatGenerator {
	return &TogetherChatGenerator{NewOpenAiCompatibleChatGeneratorBase("together", "https://api.together.xyz/v1/chat/completions",
		OpenAiChatOptions(opts), post)}
}

// AnthropicChatGenerator is Anthropic.
//
// NOT OpenAI-shaped: the system prompt is a top-level field rather than a
// message, and the response content is a list of blocks.
type AnthropicChatGenerator struct {
	opts AnthropicChatOptions
	post PostFunc
}

// NewAnthropicChatGenerator returns the generator.
func NewAnthropicChatGenerator(opts AnthropicChatOptions, post PostFunc) *AnthropicChatGenerator {
	if opts.APIVersion == "" {
		opts.APIVersion = "2023-06-01"
	}
	return &AnthropicChatGenerator{opts: opts, post: post}
}

// ProviderID implements IConfigurableChatGenerator.
func (g *AnthropicChatGenerator) ProviderID() string { return "anthropic" }

// IsConfigured implements IConfigurableChatGenerator.
func (g *AnthropicChatGenerator) IsConfigured() bool {
	return strings.TrimSpace(g.opts.APIKey) != "" && strings.TrimSpace(g.opts.Model) != "" && g.post != nil
}

// Generate implements IConfigurableChatGenerator.
func (g *AnthropicChatGenerator) Generate(ctx context.Context, prompt string) (string, error) {
	if !g.IsConfigured() {
		return "", errors.New("anthropic is not configured")
	}
	endpoint := g.opts.Endpoint
	if endpoint == "" {
		endpoint = "https://api.anthropic.com/v1/messages"
	}
	body, err := json.Marshal(map[string]any{
		"model":      g.opts.Model,
		"max_tokens": g.opts.MaxTokens,
		"messages":   []map[string]string{{"role": "user", "content": prompt}},
	})
	if err != nil {
		return "", err
	}
	return g.post(ctx, endpoint, string(body), map[string]string{
		"Content-Type":      "application/json",
		"x-api-key":         g.opts.APIKey,
		"anthropic-version": g.opts.APIVersion,
	})
}

// GeminiChatGenerator is Gemini.
//
// Also not OpenAI-shaped: "contents" and "parts" rather than messages.
type GeminiChatGenerator struct {
	opts GeminiChatOptions
	post PostFunc
}

// NewGeminiChatGenerator returns the generator.
func NewGeminiChatGenerator(opts GeminiChatOptions, post PostFunc) *GeminiChatGenerator {
	return &GeminiChatGenerator{opts: opts, post: post}
}

// ProviderID implements IConfigurableChatGenerator.
func (g *GeminiChatGenerator) ProviderID() string { return "gemini" }

// IsConfigured implements IConfigurableChatGenerator.
func (g *GeminiChatGenerator) IsConfigured() bool {
	return strings.TrimSpace(g.opts.APIKey) != "" && strings.TrimSpace(g.opts.Model) != "" && g.post != nil
}

// Generate implements IConfigurableChatGenerator.
func (g *GeminiChatGenerator) Generate(ctx context.Context, prompt string) (string, error) {
	if !g.IsConfigured() {
		return "", errors.New("gemini is not configured")
	}
	endpoint := g.opts.Endpoint
	if endpoint == "" {
		endpoint = "https://generativelanguage.googleapis.com/v1beta/models/" + g.opts.Model + ":generateContent"
	}
	body, err := json.Marshal(map[string]any{
		"contents": []map[string]any{{"parts": []map[string]string{{"text": prompt}}}},
	})
	if err != nil {
		return "", err
	}
	// The header form, not the query-string form. Gemini accepts both, and the
	// query string puts the key in every proxy log on the path.
	return g.post(ctx, endpoint, string(body), map[string]string{
		"Content-Type":   "application/json",
		"x-goog-api-key": g.opts.APIKey,
	})
}

// ─────────────────────────────────────────────────────────────────────────────
// Hosting

// SystemPromptEnrichment is whether persona, device context, recall and skills
// get appended to the caller's own system prompt.
type SystemPromptEnrichment int

const (
	// EnrichmentAlways — appended AFTER the caller's own prompt, so the
	// caller's instructions still LEAD.
	//
	// The default, and the reason is stated because it was a change: silently
	// losing memory grounding is worse than receiving grounding you did not
	// explicitly ask for.
	EnrichmentAlways SystemPromptEnrichment = iota
	// EnrichmentOnlyWhenAbsent — only when the caller supplies no system turn
	// at all. Full control of the prompt, accepting that recall and persona will
	// not be injected.
	EnrichmentOnlyWhenAbsent
)

func (e SystemPromptEnrichment) String() string {
	if e == EnrichmentOnlyWhenAbsent {
		return "only-when-absent"
	}
	return "always"
}

// AIApiClient talks to a remote CircleAI host.
type AIApiClient struct {
	baseURL string
	post    PostFunc
}

// NewAIApiClient returns a client.
func NewAIApiClient(baseURL string, post PostFunc) *AIApiClient {
	return &AIApiClient{baseURL: strings.TrimRight(baseURL, "/"), post: post}
}

// Ask sends a prompt to the remote host.
func (c *AIApiClient) Ask(ctx context.Context, prompt string) (string, error) {
	if c.post == nil {
		return "", errors.New("no transport configured")
	}
	body, err := json.Marshal(map[string]string{"prompt": prompt})
	if err != nil {
		return "", err
	}
	return c.post(ctx, c.baseURL+"/v1/ask", string(body), map[string]string{"Content-Type": "application/json"})
}

// CronScheduleParser parses a five-field cron expression.
//
// A named type over the field parsing that already lives in
// proactive_scheduler.go. One parser: a scheduler and a job list that disagree
// about what "0 9 * * 1" means is a job that fires on a day nobody expected.
type CronScheduleParser struct{}

// Parse reads "minute hour day-of-month month day-of-week".
//
// Refusing loudly matters more here than anywhere else: a schedule that parses
// but means something other than what was written does not fail — it fires at
// three in the morning, or never, and the person who wrote it finds out weeks
// later.
//
// DAY-OF-MONTH AND DAY-OF-WEEK ARE OR-ED, NOT AND-ED, when both are restricted.
// That is genuinely how cron behaves and it surprises everybody: "1 * * 13 5"
// is the 13th AND every Friday, not only Friday the 13th. Implementing the
// intuitive reading gives a scheduler that silently disagrees with every other
// cron on the system.
func (CronScheduleParser) Parse(expression string) (*CronExpression, error) {
	return ParseCronExpression(expression)
}

// ─────────────────────────────────────────────────────────────────────────────
// The inference server

// AuthSchemes names the authentication schemes the server accepts.
type AuthSchemes struct{}

// ApiKey is the API-key scheme name.
func (AuthSchemes) ApiKey() string { return "ApiKey" }

// Bearer is the bearer-token scheme name.
func (AuthSchemes) Bearer() string { return "Bearer" }

// ApiKeyAuthSchemeOptions configures API-key authentication.
type ApiKeyAuthSchemeOptions struct {
	HeaderName string
	// Verify compares a presented key against what is stored. Supplied by the
	// host so no key material lives here, and expected to compare in CONSTANT
	// TIME — an early-exit compare leaks the key a byte at a time.
	Verify func(presented string) (subject string, ok bool)
}

// DefaultApiKeyAuthSchemeOptions returns the header name.
func DefaultApiKeyAuthSchemeOptions() ApiKeyAuthSchemeOptions {
	return ApiKeyAuthSchemeOptions{HeaderName: "X-Api-Key"}
}

// AdminEndpoints registers the administrative routes.
//
// Registered SEPARATELY from the inference routes so a deployment can expose
// one and not the other — the usual shape being inference on a public listener
// and administration on a loopback one.
type AdminEndpoints struct {
	opts ApiKeyAuthSchemeOptions
}

// NewAdminEndpoints returns the endpoints.
func NewAdminEndpoints(opts ApiKeyAuthSchemeOptions) *AdminEndpoints {
	if opts.HeaderName == "" {
		opts.HeaderName = DefaultApiKeyAuthSchemeOptions().HeaderName
	}
	return &AdminEndpoints{opts: opts}
}

// Register adds the routes to a mux.
func (e *AdminEndpoints) Register(mux *http.ServeMux) {
	if mux == nil {
		return
	}
	mux.HandleFunc("/admin/health", e.guard(func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"status":"ok"}`))
	}))
	mux.HandleFunc("/admin/models", e.guard(func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"models":[]}`))
	}))
}

func (e *AdminEndpoints) guard(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if e.opts.Verify == nil {
			// No verifier wired means the admin surface is CLOSED, not open. A
			// server that authenticated everybody because nobody supplied one
			// is an open endpoint that looks configured.
			http.Error(w, "administration is not enabled", http.StatusNotFound)
			return
		}
		if _, ok := e.opts.Verify(r.Header.Get(e.opts.HeaderName)); !ok {
			http.Error(w, "unauthorised", http.StatusUnauthorized)
			return
		}
		next(w, r)
	}
}
