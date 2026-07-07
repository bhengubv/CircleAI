// companion_session.go
//
// The conscious loop: a concrete ICompanionSession that recalls from fused
// memory, persists each turn, and encodes it into the graph off the hot path.
// Ported from CircleAI.Companion (CompanionSession) — the C# reference — and
// mirrors the TypeScript pilot (companion/session.ts) 1:1.
//
// On every turn it (1) recalls the most relevant memories + the user's own facts
// and injects them into the system prompt, (2) calls the generator, (3) persists
// the exchange to episodic memory, and (4) hands it to the background encoder so
// the knowledge graph fills for future associative recall.

package circleai

import (
	"context"
	"errors"
	"strings"
	"sync"
	"time"

	"github.com/google/uuid"
)

// EmbedderFunc computes an embedding for the given text. It returns nil when no
// embedding is available (→ episodic recency recall). Optional.
type EmbedderFunc func(ctx context.Context, text string) ([]float32, error)

// CompanionSessionOptions is construction-time configuration for a
// CompanionSession.
type CompanionSessionOptions struct {
	SessionID         string
	IdentityID        string
	Interface         InterfaceKind
	DisplayName       string
	PreferredLanguage *string
	// PersonaHints is a static persona hint block prepended to the system prompt.
	PersonaHints string
	// AffectSummary is a static affect hint block prepended to the system prompt.
	AffectSummary string
	ActiveGoals   []string
	// RecallTopK is how many memories to recall per turn. Default 5.
	RecallTopK int
	// AppContext is an optional app context stamped onto persisted episodes.
	AppContext *string
	// Encoder is the background graph/belief encoder. When nil, turns are not encoded.
	Encoder *CompanionMemoryEncoder
	// Beliefs holds the user's own facts, surfaced into the system prompt.
	Beliefs *SelfBeliefStore
	// Embedder is an optional embedder for associative episodic recall; nil → recency.
	Embedder EmbedderFunc
}

// CompanionSession is a companion session that thinks with fused memory and
// remembers what it learns.
type CompanionSession struct {
	generator IChatGenerator
	episodic  IEpisodicMemoryStore
	recall    IRecall
	opts      CompanionSessionOptions

	mu        sync.Mutex
	history   []CompanionTurn
	context   CompanionContext
	proactive chan CompanionProactiveEvent
	closeOnce sync.Once
}

// NewCompanionSession creates a session. generator, episodic and recall are
// required.
func NewCompanionSession(
	generator IChatGenerator,
	episodic IEpisodicMemoryStore,
	recall IRecall,
	opts CompanionSessionOptions,
) (*CompanionSession, error) {
	if generator == nil {
		return nil, errors.New("generator required")
	}
	if episodic == nil {
		return nil, errors.New("episodic required")
	}
	if recall == nil {
		return nil, errors.New("recall required")
	}
	s := &CompanionSession{
		generator: generator,
		episodic:  episodic,
		recall:    recall,
		opts:      opts,
		proactive: make(chan CompanionProactiveEvent),
	}
	s.context = s.buildContext(nil)
	return s, nil
}

// SessionID returns the stable unique identifier for this session.
func (s *CompanionSession) SessionID() string { return s.opts.SessionID }

// IdentityID returns the authenticated identity driving this session.
func (s *CompanionSession) IdentityID() string { return s.opts.IdentityID }

// Interface returns the surface on which this session is running.
func (s *CompanionSession) Interface() InterfaceKind { return s.opts.Interface }

// Send sends a message to the Companion and receives a complete reply.
func (s *CompanionSession) Send(ctx context.Context, message string) (string, error) {
	prepared, err := s.prepare(ctx, message)
	if err != nil {
		return "", err
	}
	reply, err := s.generator.Generate(ctx, prepared.messages, nil)
	if err != nil {
		return "", err
	}
	if err := s.recordTurn(ctx, message, reply, prepared.queryEmbedding, prepared.snippets); err != nil {
		return "", err
	}
	return reply, nil
}

// Stream streams the Companion's reply token-by-token. The tokens channel is
// closed when the stream ends; the error channel receives at most one error and
// is then closed. The full reply is persisted once the stream completes.
func (s *CompanionSession) Stream(ctx context.Context, message string) (<-chan string, <-chan error) {
	tokens := make(chan string)
	errs := make(chan error, 1)

	go func() {
		defer close(tokens)
		defer close(errs)

		prepared, err := s.prepare(ctx, message)
		if err != nil {
			errs <- err
			return
		}

		genTokens, genErrs := s.generator.Stream(ctx, prepared.messages, nil)
		var b strings.Builder
		for {
			select {
			case <-ctx.Done():
				errs <- ctx.Err()
				return
			case chunk, ok := <-genTokens:
				if !ok {
					// Drain a trailing generator error before persisting.
					if gerr, ok := <-genErrs; ok && gerr != nil {
						errs <- gerr
						return
					}
					if rerr := s.recordTurn(ctx, message, b.String(), prepared.queryEmbedding, prepared.snippets); rerr != nil {
						errs <- rerr
					}
					return
				}
				b.WriteString(chunk)
				select {
				case tokens <- chunk:
				case <-ctx.Done():
					errs <- ctx.Err()
					return
				}
			case gerr := <-genErrs:
				if gerr != nil {
					errs <- gerr
					return
				}
			}
		}
	}()

	return tokens, errs
}

// Agent runs in agentic mode. Pilot: no tool-execution loop yet — agentic tool
// calling is a later slice. Falls back to a plain reply so the surface is complete.
func (s *CompanionSession) Agent(ctx context.Context, instruction string) (string, error) {
	return s.Send(ctx, instruction)
}

// GetContext returns the most recent CompanionContext snapshot.
func (s *CompanionSession) GetContext() CompanionContext {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.context
}

// RefreshContext refreshes the context from backing stores (recency recall).
func (s *CompanionSession) RefreshContext(ctx context.Context) error {
	hits, err := s.recall.Recall(ctx, "", nil, s.recallTopK())
	if err != nil {
		return err
	}
	snippets := snippetsFromHits(hits)
	s.mu.Lock()
	s.context = s.buildContext(snippets)
	s.mu.Unlock()
	return nil
}

// History returns the in-session conversation history (not persisted).
func (s *CompanionSession) History() []CompanionTurn {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]CompanionTurn, len(s.history))
	copy(out, s.history)
	return out
}

// SignalFeedback signals satisfaction with the last reply. Pilot: accepted but
// not yet routed to a feedback store / affect update. Wired in a later slice.
func (s *CompanionSession) SignalFeedback(_ context.Context, _ bool, _ *string) error {
	return nil
}

// ProactiveEvents returns a channel on which proactive events are delivered. The
// channel is never nil; it is closed when the session is closed.
func (s *CompanionSession) ProactiveEvents() <-chan CompanionProactiveEvent {
	return s.proactive
}

// Close releases all resources held by the session.
func (s *CompanionSession) Close() error {
	s.closeOnce.Do(func() {
		close(s.proactive)
	})
	return nil
}

// ── internals ──────────────────────────────────────────────────────────────

type preparedTurn struct {
	messages       []ChatMessage
	queryEmbedding []float32
	snippets       []string
}

// prepare recalls before the current turn is persisted, so recall draws on prior
// memory and never echoes the message back.
func (s *CompanionSession) prepare(ctx context.Context, message string) (preparedTurn, error) {
	var queryEmbedding []float32
	if s.opts.Embedder != nil {
		emb, err := s.opts.Embedder(ctx, message)
		if err != nil {
			return preparedTurn{}, err
		}
		queryEmbedding = emb
	}

	hits, err := s.recall.Recall(ctx, message, queryEmbedding, s.recallTopK())
	if err != nil {
		return preparedTurn{}, err
	}
	snippets := snippetsFromHits(hits)

	messages := []ChatMessage{{Role: "system", Content: s.buildSystemPrompt(snippets)}}
	s.mu.Lock()
	for _, turn := range s.history {
		messages = append(messages, ChatMessage{Role: turn.Role, Content: turn.Content})
	}
	s.mu.Unlock()
	messages = append(messages, ChatMessage{Role: "user", Content: message})

	return preparedTurn{messages: messages, queryEmbedding: queryEmbedding, snippets: snippets}, nil
}

func (s *CompanionSession) recordTurn(ctx context.Context, userText, reply string, queryEmbedding []float32, snippets []string) error {
	episodeID := uuid.New()
	entry := EpisodicMemoryEntry{
		ID:            episodeID,
		RecordedAtUTC: time.Now().UTC(),
		UserText:      userText,
		AssistantText: reply,
		AppContext:    s.opts.AppContext,
		Embedding:     queryEmbedding,
	}
	if err := s.episodic.Add(ctx, entry); err != nil {
		return err
	}

	// Off the hot path: fill the graph + form attributed beliefs for next time.
	if s.opts.Encoder != nil {
		s.opts.Encoder.Enqueue(userText, reply, episodeID.String())
	}

	now := time.Now().UTC()
	s.mu.Lock()
	s.history = append(s.history,
		CompanionTurn{Role: "user", Content: userText, Timestamp: now},
		CompanionTurn{Role: "assistant", Content: reply, Timestamp: now},
	)
	s.context = s.buildContext(snippets)
	s.mu.Unlock()
	return nil
}

func (s *CompanionSession) buildSystemPrompt(snippets []string) string {
	var parts []string
	if strings.TrimSpace(s.opts.PersonaHints) != "" {
		parts = append(parts, strings.TrimSpace(s.opts.PersonaHints))
	}
	if strings.TrimSpace(s.opts.AffectSummary) != "" {
		parts = append(parts, strings.TrimSpace(s.opts.AffectSummary))
	}

	facts := s.userFacts()
	if len(facts) > 0 {
		var b strings.Builder
		b.WriteString("[What you know about the user]")
		for _, f := range facts {
			b.WriteString("\n- ")
			b.WriteString(f)
		}
		parts = append(parts, b.String())
	}
	if len(snippets) > 0 {
		var b strings.Builder
		b.WriteString("[Relevant memories]")
		for _, snip := range snippets {
			b.WriteString("\n- ")
			b.WriteString(snip)
		}
		parts = append(parts, b.String())
	}
	return strings.Join(parts, "\n\n")
}

func (s *CompanionSession) userFacts() []string {
	if s.opts.Beliefs == nil {
		return nil
	}
	facts := s.opts.Beliefs.SelfFacts()
	out := make([]string, 0, len(facts))
	for _, f := range facts {
		out = append(out, f.Object)
	}
	return out
}

func (s *CompanionSession) buildContext(snippets []string) CompanionContext {
	displayName := s.opts.DisplayName
	goals := s.opts.ActiveGoals
	if goals == nil {
		goals = []string{}
	}
	if snippets == nil {
		snippets = []string{}
	}
	return CompanionContext{
		IdentityID:           s.opts.IdentityID,
		DisplayName:          displayName,
		PreferredLanguage:    s.opts.PreferredLanguage,
		Interface:            s.opts.Interface,
		PersonaHints:         s.opts.PersonaHints,
		AffectSummary:        s.opts.AffectSummary,
		RecentMemorySnippets: snippets,
		ActiveGoals:          goals,
		ContextBuiltAt:       time.Now().UTC(),
	}
}

func (s *CompanionSession) recallTopK() int {
	if s.opts.RecallTopK <= 0 {
		return 5
	}
	return s.opts.RecallTopK
}

func snippetsFromHits(hits []MemoryHit) []string {
	out := make([]string, 0, len(hits))
	for _, h := range hits {
		out = append(out, h.Item.Text)
	}
	return out
}

// Compile-time assertion that the concrete session satisfies the interface.
var _ ICompanionSession = (*CompanionSession)(nil)
