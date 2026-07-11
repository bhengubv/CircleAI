// domain_board.go
//
// Ports CircleAI.Domain (Contracts.cs / InMemoryDomain.cs / NullImplementations.cs):
//   Ingredient / IFoodEmbeddings / InMemoryFoodEmbeddings / NullFoodEmbeddings
//   FinanceSnippet / IFinanceRetrieval / InMemoryFinanceRetrieval / NullFinanceRetrieval
//   FinanceFinding / IFinancialAgent / MultiPassFinancialAgent / NullFinancialAgent
//   SlideOutline / GeneratedPresentation / IPresentationGenerator /
//     TemplatePresentationGenerator / NullPresentationGenerator
//   JobApplicationDraft / IJobSearchPipeline / TemplateJobSearchPipeline /
//     NullJobSearchPipeline
//   IMemPalaceStore / InMemoryMemPalaceStore / NullMemPalaceStore
//   InMemoryHippoRagStore / NullHippoRagStore   (IHippoRagStore is already
//     declared in memory_graph.go; this file adds the simple 2-hop InMemory /
//     Null impls of that same contract)
//   SwarmPeer / ISwarmCoordinator / InMemorySwarmCoordinator / NullSwarmCoordinator
//   LoRATrainingSummary / LoRAAdapterState / IPersonalLoRA /
//     InMemoryPersonalLoRA / NullPersonalLoRA
//
// MemoryItem + MemoryHit + IHippoRagStore are reused from memory_graph.go
// (they are the Domain contract's shared recall currency, ported there first).

package circleai

import (
	"context"
	"errors"
	"hash/fnv"
	"math"
	"sort"
	"strings"
	"sync"
	"time"
)

// ---------------------------------------------------------------------------
// Food (EPICure)
// ---------------------------------------------------------------------------

// Ingredient is one ingredient with optional canonical form + quantity. Ports
// the Ingredient record; Canonical/Quantity are *string for the nullable slots.
type Ingredient struct {
	Name      string
	Canonical *string
	Quantity  *string
}

// IFoodEmbeddings is the food/ingredient embedding store. Ports IFoodEmbeddings.
type IFoodEmbeddings interface {
	BackendID() string
	Embed(ctx context.Context, ingredient Ingredient) ([]float32, error)
	Substitutes(ctx context.Context, ingredient Ingredient, topK int) ([]Ingredient, error)
}

// InMemoryFoodEmbeddings is a substitute-by-name in-memory store. Ports
// InMemoryFoodEmbeddings.
type InMemoryFoodEmbeddings struct {
	mu     sync.Mutex
	embeds map[string][]float32
	subs   map[string][]Ingredient
}

// NewInMemoryFoodEmbeddings constructs an empty store.
func NewInMemoryFoodEmbeddings() *InMemoryFoodEmbeddings {
	return &InMemoryFoodEmbeddings{
		embeds: make(map[string][]float32),
		subs:   make(map[string][]Ingredient),
	}
}

// BackendID returns "in-memory".
func (f *InMemoryFoodEmbeddings) BackendID() string { return "in-memory" }

// RegisterEmbedding stores a vector for name (case-insensitive). Ports
// RegisterEmbedding.
func (f *InMemoryFoodEmbeddings) RegisterEmbedding(name string, v []float32) {
	if v == nil {
		panic("v must not be nil")
	}
	f.mu.Lock()
	f.embeds[strings.ToLower(name)] = v
	f.mu.Unlock()
}

// RegisterSubstitute appends a substitute for name. Ports RegisterSubstitute.
func (f *InMemoryFoodEmbeddings) RegisterSubstitute(name string, alt Ingredient) {
	f.mu.Lock()
	key := strings.ToLower(name)
	f.subs[key] = append(f.subs[key], alt)
	f.mu.Unlock()
}

// Embed returns the registered vector for the ingredient, or a deterministic
// hash-based 8-dim vector. Ports EmbedAsync.
func (f *InMemoryFoodEmbeddings) Embed(ctx context.Context, i Ingredient) ([]float32, error) {
	f.mu.Lock()
	v, ok := f.embeds[strings.ToLower(i.Name)]
	f.mu.Unlock()
	if ok {
		return v, nil
	}
	v2 := make([]float32, 8)
	h := ordinalIgnoreCaseHash(i.Name)
	for k := 0; k < 8; k++ {
		v2[k] = float32((h>>(uint(k)*4))&0xF) / 15.0
	}
	return v2, nil
}

// Substitutes returns up to topK registered substitutes. Ports SubstitutesAsync.
func (f *InMemoryFoodEmbeddings) Substitutes(ctx context.Context, i Ingredient, topK int) ([]Ingredient, error) {
	if topK <= 0 {
		return nil, errors.New("topK out of range")
	}
	f.mu.Lock()
	list, ok := f.subs[strings.ToLower(i.Name)]
	f.mu.Unlock()
	if !ok {
		return []Ingredient{}, nil
	}
	if topK < len(list) {
		list = list[:topK]
	}
	return append([]Ingredient(nil), list...), nil
}

// ordinalIgnoreCaseHash mirrors string.GetHashCode(OrdinalIgnoreCase) closely
// enough for the deterministic 8-dim fallback (a stable 32-bit hash of the
// upper-cased string). The exact .NET hash is process-randomised anyway; this
// is a stable, reproducible substitute.
func ordinalIgnoreCaseHash(s string) uint32 {
	h := fnv.New32a()
	_, _ = h.Write([]byte(strings.ToUpper(s)))
	return h.Sum32()
}

var _ IFoodEmbeddings = (*InMemoryFoodEmbeddings)(nil)

// ---------------------------------------------------------------------------
// Finance (quant-mind + dexter)
// ---------------------------------------------------------------------------

// FinanceSnippet is a scored finance text snippet. Ports FinanceSnippet.
type FinanceSnippet struct {
	Text   string
	Source string
	Score  float32
}

// IFinanceRetrieval is quant-finance RAG retrieval. Ports IFinanceRetrieval.
type IFinanceRetrieval interface {
	BackendID() string
	Retrieve(ctx context.Context, query string, topK int) ([]FinanceSnippet, error)
}

// FinanceFinding is a summarised finance finding with citations. Ports
// FinanceFinding.
type FinanceFinding struct {
	Subject   string
	Summary   string
	Citations []string
}

// IFinancialAgent is an autonomous financial-research agent. Ports
// IFinancialAgent.
type IFinancialAgent interface {
	BackendID() string
	Research(ctx context.Context, question string) ([]FinanceFinding, error)
}

// InMemoryFinanceRetrieval is a substring-scored finance corpus. Ports
// InMemoryFinanceRetrieval.
type InMemoryFinanceRetrieval struct {
	mu     sync.Mutex
	corpus []FinanceSnippet
}

// NewInMemoryFinanceRetrieval constructs an empty corpus.
func NewInMemoryFinanceRetrieval() *InMemoryFinanceRetrieval {
	return &InMemoryFinanceRetrieval{}
}

// BackendID returns "in-memory".
func (r *InMemoryFinanceRetrieval) BackendID() string { return "in-memory" }

// Add appends a snippet. Ports Add.
func (r *InMemoryFinanceRetrieval) Add(s FinanceSnippet) {
	r.mu.Lock()
	r.corpus = append(r.corpus, s)
	r.mu.Unlock()
}

// Retrieve returns up to topK snippets containing query, ordered by descending
// score. Ports RetrieveAsync.
func (r *InMemoryFinanceRetrieval) Retrieve(ctx context.Context, query string, topK int) ([]FinanceSnippet, error) {
	if topK <= 0 {
		return nil, errors.New("topK out of range")
	}
	r.mu.Lock()
	hits := make([]FinanceSnippet, 0)
	for _, s := range r.corpus {
		if strings.Contains(strings.ToLower(s.Text), strings.ToLower(query)) {
			hits = append(hits, s)
		}
	}
	r.mu.Unlock()
	sort.SliceStable(hits, func(i, j int) bool { return hits[i].Score > hits[j].Score })
	if topK < len(hits) {
		hits = hits[:topK]
	}
	return hits, nil
}

var _ IFinanceRetrieval = (*InMemoryFinanceRetrieval)(nil)

// MultiPassFinancialAgent decomposes a question into sub-questions, retrieves
// per sub-question, groups by source and summarises each cluster. Ports
// MultiPassFinancialAgent.
type MultiPassFinancialAgent struct {
	retr IFinanceRetrieval
}

// NewMultiPassFinancialAgent constructs the agent over a retrieval backend.
// Panics if retr is nil.
func NewMultiPassFinancialAgent(retr IFinanceRetrieval) *MultiPassFinancialAgent {
	if retr == nil {
		panic("retrieval must not be nil")
	}
	return &MultiPassFinancialAgent{retr: retr}
}

// BackendID returns "multi-pass".
func (a *MultiPassFinancialAgent) BackendID() string { return "multi-pass" }

// Research runs multi-pass retrieval and clusters findings. Ports ResearchAsync.
func (a *MultiPassFinancialAgent) Research(ctx context.Context, question string) ([]FinanceFinding, error) {
	subQuestions := decomposeFinanceQuestion(question)
	findings := make([]FinanceFinding, 0)
	for _, sub := range subQuestions {
		snippets, err := a.retr.Retrieve(ctx, sub, 5)
		if err != nil {
			return nil, err
		}
		if len(snippets) == 0 {
			continue
		}
		// Group by source, preserving first-seen source order.
		order := make([]string, 0)
		bySource := make(map[string][]FinanceSnippet)
		for _, s := range snippets {
			if _, ok := bySource[s.Source]; !ok {
				order = append(order, s.Source)
			}
			bySource[s.Source] = append(bySource[s.Source], s)
		}
		for _, src := range order {
			grp := append([]FinanceSnippet(nil), bySource[src]...)
			sort.SliceStable(grp, func(i, j int) bool { return grp[i].Score > grp[j].Score })
			if len(grp) > 3 {
				grp = grp[:3]
			}
			texts := make([]string, len(grp))
			for i, s := range grp {
				texts[i] = s.Text
			}
			findings = append(findings, FinanceFinding{
				Subject:   sub,
				Summary:   strings.Join(texts, " | "),
				Citations: []string{src},
			})
		}
	}
	return findings, nil
}

func decomposeFinanceQuestion(question string) []string {
	subs := []string{question}
	if strings.Contains(strings.ToLower(question), " and ") {
		// Split on " and " case-insensitively.
		for _, part := range splitFold(question, " and ") {
			p := strings.TrimSpace(part)
			if len(p) > 6 {
				subs = append(subs, p)
			}
		}
	}
	if len(question) > 60 {
		subs = append(subs, strings.TrimSpace(strings.SplitN(question, ",", 2)[0]))
	}
	// Distinct preserving order.
	seen := make(map[string]struct{})
	out := make([]string, 0, len(subs))
	for _, s := range subs {
		if _, ok := seen[s]; ok {
			continue
		}
		seen[s] = struct{}{}
		out = append(out, s)
	}
	return out
}

// splitFold splits s on a case-insensitive separator, dropping empty segments
// (mirrors StringSplitOptions.RemoveEmptyEntries).
func splitFold(s, sep string) []string {
	out := make([]string, 0)
	lower := strings.ToLower(s)
	lsep := strings.ToLower(sep)
	start := 0
	for {
		idx := strings.Index(lower[start:], lsep)
		if idx < 0 {
			seg := s[start:]
			if seg != "" {
				out = append(out, seg)
			}
			break
		}
		seg := s[start : start+idx]
		if seg != "" {
			out = append(out, seg)
		}
		start += idx + len(sep)
	}
	return out
}

// ---------------------------------------------------------------------------
// Presentations (presenton)
// ---------------------------------------------------------------------------

// SlideOutline is one slide. Ports SlideOutline; Bullets nil == none.
type SlideOutline struct {
	Title   string
	Body    string
	Bullets []string
}

// GeneratedPresentation is a generated deck. Ports GeneratedPresentation.
type GeneratedPresentation struct {
	Slides []SlideOutline
	Theme  string
	Format string
}

// IPresentationGenerator is the presentation generator. Ports
// IPresentationGenerator.
type IPresentationGenerator interface {
	BackendID() string
	Generate(ctx context.Context, topic string, targetSlideCount int, theme *string) (GeneratedPresentation, error)
}

// TemplatePresentationGenerator produces a fixed-shape deck. Ports
// TemplatePresentationGenerator.
type TemplatePresentationGenerator struct{}

// BackendID returns "template".
func (TemplatePresentationGenerator) BackendID() string { return "template" }

// Generate builds a title + body + conclusion deck. Ports GenerateAsync.
func (TemplatePresentationGenerator) Generate(ctx context.Context, topic string, targetSlideCount int, theme *string) (GeneratedPresentation, error) {
	if strings.TrimSpace(topic) == "" {
		return GeneratedPresentation{}, errors.New("topic required")
	}
	if targetSlideCount <= 0 {
		return GeneratedPresentation{}, errors.New("targetSlideCount out of range")
	}
	slides := make([]SlideOutline, 0, targetSlideCount)
	slides = append(slides, SlideOutline{
		Title:   topic,
		Body:    "Overview",
		Bullets: []string{"What is " + topic, "Why it matters", "What we'll cover"},
	})
	for i := 2; i < targetSlideCount; i++ {
		slides = append(slides, SlideOutline{
			Title:   topic + " — Part " + itoa(i-1),
			Body:    "Detail for part " + itoa(i-1),
			Bullets: []string{"Point A", "Point B", "Point C"},
		})
	}
	slides = append(slides, SlideOutline{
		Title:   "Conclusion",
		Body:    "Summary of " + topic,
		Bullets: []string{"Recap", "Next steps", "Questions"},
	})
	th := "default"
	if theme != nil {
		th = *theme
	}
	return GeneratedPresentation{Slides: slides, Theme: th, Format: "markdown"}, nil
}

var _ IPresentationGenerator = TemplatePresentationGenerator{}

// ---------------------------------------------------------------------------
// Job search (career-ops)
// ---------------------------------------------------------------------------

// JobApplicationDraft is a drafted application. Ports JobApplicationDraft.
type JobApplicationDraft struct {
	ResumeText      string
	CoverLetterText string
	KeyMatches      []string
}

// IJobSearchPipeline is the job-search pipeline. Ports IJobSearchPipeline.
type IJobSearchPipeline interface {
	BackendID() string
	DraftApplication(ctx context.Context, roleDescription, candidateProfileText string) (JobApplicationDraft, error)
}

// TemplateJobSearchPipeline intersects role/candidate keywords into a draft.
// Ports TemplateJobSearchPipeline.
type TemplateJobSearchPipeline struct{}

// BackendID returns "template".
func (TemplateJobSearchPipeline) BackendID() string { return "template" }

// DraftApplication produces resume + cover letter + matches. Ports
// DraftApplicationAsync.
func (TemplateJobSearchPipeline) DraftApplication(ctx context.Context, roleDescription, candidateProfileText string) (JobApplicationDraft, error) {
	roleWords := extractKeyWords(roleDescription)
	candWords := extractKeyWords(candidateProfileText)
	candSet := make(map[string]struct{}, len(candWords))
	for _, w := range candWords {
		candSet[w] = struct{}{}
	}
	matches := make([]string, 0)
	for _, w := range roleWords {
		if _, ok := candSet[w]; ok {
			matches = append(matches, w)
			if len(matches) == 10 {
				break
			}
		}
	}
	first3 := matches
	if len(first3) > 3 {
		first3 = first3[:3]
	}
	resume := strings.TrimSpace(candidateProfileText) + "\n\nMatched skills: " + strings.Join(matches, ", ")
	cover := "Dear Hiring Team,\n\nI am applying because my background (" + strings.Join(first3, ", ") + ") fits the role.\n\nRegards."
	return JobApplicationDraft{ResumeText: resume, CoverLetterText: cover, KeyMatches: matches}, nil
}

func extractKeyWords(text string) []string {
	seen := make(map[string]struct{})
	out := make([]string, 0)
	for _, w := range strings.FieldsFunc(text, func(r rune) bool {
		return strings.ContainsRune(" \n\r\t,.;:()", r)
	}) {
		w = strings.ToLower(strings.TrimSpace(w))
		if len(w) > 3 {
			if _, ok := seen[w]; !ok {
				seen[w] = struct{}{}
				out = append(out, w)
			}
		}
	}
	return out
}

var _ IJobSearchPipeline = TemplateJobSearchPipeline{}

// ---------------------------------------------------------------------------
// Memory upgrades (mempalace + HippoRAG)
// ---------------------------------------------------------------------------

// IMemPalaceStore is a MemPalace-pattern long-term memory. Ports IMemPalaceStore.
type IMemPalaceStore interface {
	BackendID() string
	Upsert(ctx context.Context, item MemoryItem) error
	Recall(ctx context.Context, query string, topK int) ([]MemoryHit, error)
}

// InMemoryMemPalaceStore is a substring-scored key-value memory. Ports
// InMemoryMemPalaceStore. (MemoryItem/MemoryHit come from memory_graph.go.)
type InMemoryMemPalaceStore struct {
	mu    sync.Mutex
	items map[string]MemoryItem
}

// NewInMemoryMemPalaceStore constructs an empty store.
func NewInMemoryMemPalaceStore() *InMemoryMemPalaceStore {
	return &InMemoryMemPalaceStore{items: make(map[string]MemoryItem)}
}

// BackendID returns "in-memory".
func (s *InMemoryMemPalaceStore) BackendID() string { return "in-memory" }

// Upsert inserts or replaces an item by Id. Ports UpsertAsync.
func (s *InMemoryMemPalaceStore) Upsert(ctx context.Context, item MemoryItem) error {
	if strings.TrimSpace(item.ID) == "" {
		return errors.New("Id required")
	}
	s.mu.Lock()
	s.items[item.ID] = item
	s.mu.Unlock()
	return nil
}

// Recall returns up to topK items scoring > 0 by earliest-match position. Ports
// RecallAsync.
func (s *InMemoryMemPalaceStore) Recall(ctx context.Context, query string, topK int) ([]MemoryHit, error) {
	if topK <= 0 {
		return nil, errors.New("topK out of range")
	}
	s.mu.Lock()
	hits := make([]MemoryHit, 0)
	for _, i := range s.items {
		if sc := memPalaceScore(i.Text, query); sc > 0 {
			hits = append(hits, MemoryHit{Item: i, Score: float64(sc)})
		}
	}
	s.mu.Unlock()
	sort.SliceStable(hits, func(i, j int) bool { return hits[i].Score > hits[j].Score })
	if topK < len(hits) {
		hits = hits[:topK]
	}
	return hits, nil
}

func memPalaceScore(body, query string) float32 {
	if body == "" || query == "" {
		return 0
	}
	q := strings.TrimSpace(query)
	idx := strings.Index(strings.ToLower(body), strings.ToLower(q))
	if idx < 0 {
		return 0
	}
	return 1.0 / (1.0 + float32(idx))
}

var _ IMemPalaceStore = (*InMemoryMemPalaceStore)(nil)

// InMemoryHippoRagStore is the simple 2-hop HippoRAG impl over a MemPalace
// store. Ports InMemoryDomain.InMemoryHippoRagStore (distinct from the
// Personalised-PageRank HippoRagStore in memory_graph.go). Satisfies the
// IHippoRagStore contract declared in memory_graph.go.
type InMemoryHippoRagStore struct {
	base *InMemoryMemPalaceStore
}

// NewInMemoryHippoRagStore constructs an empty 2-hop store.
func NewInMemoryHippoRagStore() *InMemoryHippoRagStore {
	return &InMemoryHippoRagStore{base: NewInMemoryMemPalaceStore()}
}

// BackendId returns "in-memory".
func (h *InMemoryHippoRagStore) BackendId() string { return "in-memory" }

// Index upserts the item. Ports IndexAsync.
func (h *InMemoryHippoRagStore) Index(ctx context.Context, item MemoryItem) error {
	return h.base.Upsert(ctx, item)
}

// MultiHopRecall does a first hop on the query then a second hop seeded by the
// top hit's text. Ports MultiHopRecallAsync.
func (h *InMemoryHippoRagStore) MultiHopRecall(ctx context.Context, query string, topK int) ([]MemoryHit, error) {
	first, err := h.base.Recall(ctx, query, topK)
	if err != nil {
		return nil, err
	}
	if len(first) == 0 {
		return first, nil
	}
	seed := first[0].Item.Text
	second, err := h.base.Recall(ctx, seed, topK)
	if err != nil {
		return nil, err
	}
	// Concat + distinct by Item.ID (first occurrence wins), then top-K by score.
	seen := make(map[string]struct{})
	merged := make([]MemoryHit, 0, len(first)+len(second))
	for _, hit := range append(first, second...) {
		if _, ok := seen[hit.Item.ID]; ok {
			continue
		}
		seen[hit.Item.ID] = struct{}{}
		merged = append(merged, hit)
	}
	sort.SliceStable(merged, func(i, j int) bool { return merged[i].Score > merged[j].Score })
	if topK < len(merged) {
		merged = merged[:topK]
	}
	return merged, nil
}

var _ IHippoRagStore = (*InMemoryHippoRagStore)(nil)

// ---------------------------------------------------------------------------
// Swarm (MiroFish)
// ---------------------------------------------------------------------------

// SwarmPeer is a swarm participant. Ports SwarmPeer.
type SwarmPeer struct {
	PeerID     string
	Capability string
	Health     float32
}

// ISwarmCoordinator is multi-device coordination. Ports ISwarmCoordinator.
type ISwarmCoordinator interface {
	BackendID() string
	ListPeers(ctx context.Context) ([]SwarmPeer, error)
	// ChooseDelegate returns the healthiest peer id for capability, or (nil).
	ChooseDelegate(ctx context.Context, capability string) (*string, error)
}

// InMemorySwarmCoordinator tracks peers and picks the healthiest per capability.
// Ports InMemorySwarmCoordinator.
type InMemorySwarmCoordinator struct {
	mu    sync.Mutex
	peers map[string]SwarmPeer
}

// NewInMemorySwarmCoordinator constructs an empty coordinator.
func NewInMemorySwarmCoordinator() *InMemorySwarmCoordinator {
	return &InMemorySwarmCoordinator{peers: make(map[string]SwarmPeer)}
}

// BackendID returns "in-memory".
func (c *InMemorySwarmCoordinator) BackendID() string { return "in-memory" }

// Register stores (or replaces by PeerId) a peer. Ports Register.
func (c *InMemorySwarmCoordinator) Register(p SwarmPeer) {
	c.mu.Lock()
	c.peers[p.PeerID] = p
	c.mu.Unlock()
}

// ListPeers returns all peers. Ports ListPeersAsync.
func (c *InMemorySwarmCoordinator) ListPeers(ctx context.Context) ([]SwarmPeer, error) {
	c.mu.Lock()
	out := make([]SwarmPeer, 0, len(c.peers))
	for _, p := range c.peers {
		out = append(out, p)
	}
	c.mu.Unlock()
	return out, nil
}

// ChooseDelegate returns the healthiest peer for capability. Ports
// ChooseDelegateAsync.
func (c *InMemorySwarmCoordinator) ChooseDelegate(ctx context.Context, capability string) (*string, error) {
	if strings.TrimSpace(capability) == "" {
		return nil, errors.New("capability required")
	}
	c.mu.Lock()
	var best *SwarmPeer
	for _, p := range c.peers {
		if !strings.EqualFold(p.Capability, capability) {
			continue
		}
		if best == nil || p.Health > best.Health {
			pc := p
			best = &pc
		}
	}
	c.mu.Unlock()
	if best == nil {
		return nil, nil
	}
	id := best.PeerID
	return &id, nil
}

var _ ISwarmCoordinator = (*InMemorySwarmCoordinator)(nil)

// ---------------------------------------------------------------------------
// Personal LoRA
// ---------------------------------------------------------------------------

// LoRATrainingSummary summarises a training run. Ports LoRATrainingSummary.
type LoRATrainingSummary struct {
	AdapterID    string
	StepsTrained int
	FinalLoss    float32
}

// LoRAAdapterState is the persisted adapter state. Ports LoRAAdapterState.
type LoRAAdapterState struct {
	AdapterID    string
	Steps        int
	FinalLoss    float32
	TrainedAtUTC time.Time
}

// IPersonalLoRA is on-device LoRA personalisation. Ports IPersonalLoRA.
type IPersonalLoRA interface {
	BackendID() string
	Train(ctx context.Context, adapterID string, conversationSamples []string) (LoRATrainingSummary, error)
	LoadAdapter(ctx context.Context, adapterID string) error
	UnloadAdapter(ctx context.Context, adapterID string) error
}

// InMemoryPersonalLoRA is an in-memory adapter manager with a simulated
// training loop. Ports InMemoryPersonalLoRA.
type InMemoryPersonalLoRA struct {
	mu       sync.Mutex
	adapters map[string]LoRAAdapterState
	loaded   map[string]struct{}
}

// NewInMemoryPersonalLoRA constructs an empty manager.
func NewInMemoryPersonalLoRA() *InMemoryPersonalLoRA {
	return &InMemoryPersonalLoRA{
		adapters: make(map[string]LoRAAdapterState),
		loaded:   make(map[string]struct{}),
	}
}

// BackendID returns "in-memory".
func (l *InMemoryPersonalLoRA) BackendID() string { return "in-memory" }

// Train runs a simulated training loop and stores adapter state. Ports
// TrainAsync.
func (l *InMemoryPersonalLoRA) Train(ctx context.Context, adapterID string, samples []string) (LoRATrainingSummary, error) {
	if strings.TrimSpace(adapterID) == "" {
		return LoRATrainingSummary{}, errors.New("adapterId required")
	}
	if len(samples) == 0 {
		return LoRATrainingSummary{}, errors.New("at least one sample required")
	}
	steps := len(samples)
	totalChars := 0
	for _, s := range samples {
		totalChars += len(s)
	}
	finalLoss := float32(1.0/(1.0+math.Log(1+float64(steps))) + 1.0/(1.0+float64(totalChars)/1000.0))
	state := LoRAAdapterState{AdapterID: adapterID, Steps: steps, FinalLoss: finalLoss, TrainedAtUTC: time.Now().UTC()}
	l.mu.Lock()
	l.adapters[adapterID] = state
	l.mu.Unlock()
	return LoRATrainingSummary{AdapterID: adapterID, StepsTrained: steps, FinalLoss: finalLoss}, nil
}

// LoadAdapter marks a trained adapter loaded. Ports LoadAdapterAsync.
func (l *InMemoryPersonalLoRA) LoadAdapter(ctx context.Context, adapterID string) error {
	if strings.TrimSpace(adapterID) == "" {
		return errors.New("adapterId required")
	}
	l.mu.Lock()
	defer l.mu.Unlock()
	if _, ok := l.adapters[adapterID]; !ok {
		return errors.New("adapter '" + adapterID + "' not trained")
	}
	l.loaded[adapterID] = struct{}{}
	return nil
}

// UnloadAdapter clears the loaded flag. Ports UnloadAdapterAsync.
func (l *InMemoryPersonalLoRA) UnloadAdapter(ctx context.Context, adapterID string) error {
	if strings.TrimSpace(adapterID) == "" {
		return errors.New("adapterId required")
	}
	l.mu.Lock()
	delete(l.loaded, adapterID)
	l.mu.Unlock()
	return nil
}

// IsLoaded reports whether adapterID is currently loaded. Ports IsLoaded.
func (l *InMemoryPersonalLoRA) IsLoaded(adapterID string) bool {
	l.mu.Lock()
	_, ok := l.loaded[adapterID]
	l.mu.Unlock()
	return ok
}

// StateOf returns the adapter state, or (zero,false). Ports StateOf.
func (l *InMemoryPersonalLoRA) StateOf(adapterID string) (LoRAAdapterState, bool) {
	l.mu.Lock()
	s, ok := l.adapters[adapterID]
	l.mu.Unlock()
	return s, ok
}

var _ IPersonalLoRA = (*InMemoryPersonalLoRA)(nil)

// ---------------------------------------------------------------------------
// Null implementations
// ---------------------------------------------------------------------------

// NullFoodEmbeddings is a fail-safe food store. Ports NullFoodEmbeddings.
type NullFoodEmbeddings struct{}

// NullFoodEmbeddingsInstance is the shared singleton.
var NullFoodEmbeddingsInstance = NullFoodEmbeddings{}

func (NullFoodEmbeddings) BackendID() string { return "null" }
func (NullFoodEmbeddings) Embed(context.Context, Ingredient) ([]float32, error) {
	return make([]float32, 300), nil
}
func (NullFoodEmbeddings) Substitutes(context.Context, Ingredient, int) ([]Ingredient, error) {
	return []Ingredient{}, nil
}

// NullFinanceRetrieval is a fail-safe finance retrieval. Ports NullFinanceRetrieval.
type NullFinanceRetrieval struct{}

// NullFinanceRetrievalInstance is the shared singleton.
var NullFinanceRetrievalInstance = NullFinanceRetrieval{}

func (NullFinanceRetrieval) BackendID() string { return "null" }
func (NullFinanceRetrieval) Retrieve(context.Context, string, int) ([]FinanceSnippet, error) {
	return []FinanceSnippet{}, nil
}

// NullFinancialAgent is a fail-safe financial agent. Ports NullFinancialAgent.
type NullFinancialAgent struct{}

// NullFinancialAgentInstance is the shared singleton.
var NullFinancialAgentInstance = NullFinancialAgent{}

func (NullFinancialAgent) BackendID() string { return "null" }
func (NullFinancialAgent) Research(context.Context, string) ([]FinanceFinding, error) {
	return []FinanceFinding{}, nil
}

// NullPresentationGenerator is a fail-safe generator. Ports NullPresentationGenerator.
type NullPresentationGenerator struct{}

// NullPresentationGeneratorInstance is the shared singleton.
var NullPresentationGeneratorInstance = NullPresentationGenerator{}

func (NullPresentationGenerator) BackendID() string { return "null" }
func (NullPresentationGenerator) Generate(_ context.Context, _ string, _ int, theme *string) (GeneratedPresentation, error) {
	th := "default"
	if theme != nil {
		th = *theme
	}
	return GeneratedPresentation{Slides: []SlideOutline{}, Theme: th, Format: "json"}, nil
}

// NullJobSearchPipeline is a fail-safe pipeline. Ports NullJobSearchPipeline.
type NullJobSearchPipeline struct{}

// NullJobSearchPipelineInstance is the shared singleton.
var NullJobSearchPipelineInstance = NullJobSearchPipeline{}

func (NullJobSearchPipeline) BackendID() string { return "null" }
func (NullJobSearchPipeline) DraftApplication(context.Context, string, string) (JobApplicationDraft, error) {
	return JobApplicationDraft{ResumeText: "", CoverLetterText: "", KeyMatches: []string{}}, nil
}

// NullMemPalaceStore is a fail-safe MemPalace store. Ports NullMemPalaceStore.
type NullMemPalaceStore struct{}

// NullMemPalaceStoreInstance is the shared singleton.
var NullMemPalaceStoreInstance = NullMemPalaceStore{}

func (NullMemPalaceStore) BackendID() string                            { return "null" }
func (NullMemPalaceStore) Upsert(context.Context, MemoryItem) error     { return nil }
func (NullMemPalaceStore) Recall(context.Context, string, int) ([]MemoryHit, error) {
	return []MemoryHit{}, nil
}

// NullHippoRagStore is a fail-safe HippoRAG store. Ports NullHippoRagStore.
type NullHippoRagStore struct{}

// NullHippoRagStoreInstance is the shared singleton.
var NullHippoRagStoreInstance = NullHippoRagStore{}

func (NullHippoRagStore) BackendId() string                        { return "null" }
func (NullHippoRagStore) Index(context.Context, MemoryItem) error  { return nil }
func (NullHippoRagStore) MultiHopRecall(context.Context, string, int) ([]MemoryHit, error) {
	return []MemoryHit{}, nil
}

// NullSwarmCoordinator is a fail-safe coordinator. Ports NullSwarmCoordinator.
type NullSwarmCoordinator struct{}

// NullSwarmCoordinatorInstance is the shared singleton.
var NullSwarmCoordinatorInstance = NullSwarmCoordinator{}

func (NullSwarmCoordinator) BackendID() string { return "null" }
func (NullSwarmCoordinator) ListPeers(context.Context) ([]SwarmPeer, error) {
	return []SwarmPeer{}, nil
}
func (NullSwarmCoordinator) ChooseDelegate(context.Context, string) (*string, error) {
	return nil, nil
}

// NullPersonalLoRA is a fail-safe LoRA manager. Ports NullPersonalLoRA.
type NullPersonalLoRA struct{}

// NullPersonalLoRAInstance is the shared singleton.
var NullPersonalLoRAInstance = NullPersonalLoRA{}

func (NullPersonalLoRA) BackendID() string { return "null" }
func (NullPersonalLoRA) Train(_ context.Context, id string, _ []string) (LoRATrainingSummary, error) {
	return LoRATrainingSummary{AdapterID: id, StepsTrained: 0, FinalLoss: 0}, nil
}
func (NullPersonalLoRA) LoadAdapter(context.Context, string) error   { return nil }
func (NullPersonalLoRA) UnloadAdapter(context.Context, string) error { return nil }

var (
	_ IFoodEmbeddings        = NullFoodEmbeddings{}
	_ IFinanceRetrieval      = NullFinanceRetrieval{}
	_ IFinancialAgent        = NullFinancialAgent{}
	_ IPresentationGenerator = NullPresentationGenerator{}
	_ IJobSearchPipeline     = NullJobSearchPipeline{}
	_ IMemPalaceStore        = NullMemPalaceStore{}
	_ IHippoRagStore         = NullHippoRagStore{}
	_ ISwarmCoordinator      = NullSwarmCoordinator{}
	_ IPersonalLoRA          = NullPersonalLoRA{}
)
