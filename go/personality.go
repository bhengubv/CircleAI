// personality.go
//
// Ports CircleAI.Personality:
//   Persona / FormalityRange / PrivacyLevel      (Persona.cs)
//   IPersonaProvider                             (IPersonaProvider.cs)
//   JsonPersonaProvider                          (JsonPersonaProvider.cs)
//   IPersonaConflictResolver / DeclaredWinsResolver / LearnedWinsResolver (IPersonaConflictResolver.cs)
//   PersonaPromptBuilder.BuildSystemHint         (PersonaPromptBuilder.cs)
//
// PersonaState (the AI's LEARNED model) comes from memory.go — the resolvers
// bridge the declared Persona (here) against it. The prompt builder's
// prompt-injection defence (JSON-encode every user string) is preserved via
// encoding/json string marshalling, which escapes quotes/newlines/control
// characters exactly as the C# JsonSerializer.Serialize(value) does.

package circleai

import (
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"github.com/google/uuid"
)

// PrivacyLevel is the declared privacy posture. Ports the PrivacyLevel enum
// (stable ordinals: Strict=0, Balanced=1, Open=2).
type PrivacyLevel int

const (
	// PrivacyLevelStrict = minimum retention, no proactive surfacing.
	PrivacyLevelStrict PrivacyLevel = 0
	// PrivacyLevelBalanced is the default posture.
	PrivacyLevelBalanced PrivacyLevel = 1
	// PrivacyLevelOpen = maximum retention, share across surfaces.
	PrivacyLevelOpen PrivacyLevel = 2
)

// String renders the C# enum member name (used for JSON round-trip parity).
func (p PrivacyLevel) String() string {
	switch p {
	case PrivacyLevelStrict:
		return "Strict"
	case PrivacyLevelOpen:
		return "Open"
	default:
		return "Balanced"
	}
}

// privacyLevelFromString parses a PrivacyLevel enum member name (JSON form).
func privacyLevelFromString(s string) PrivacyLevel {
	switch s {
	case "Strict":
		return PrivacyLevelStrict
	case "Open":
		return PrivacyLevelOpen
	default:
		return PrivacyLevelBalanced
	}
}

// MarshalJSON emits the enum member name (JsonStringEnumConverter parity).
func (p PrivacyLevel) MarshalJSON() ([]byte, error) { return json.Marshal(p.String()) }

// UnmarshalJSON accepts the enum member name or an integer ordinal.
func (p *PrivacyLevel) UnmarshalJSON(b []byte) error {
	var s string
	if err := json.Unmarshal(b, &s); err == nil {
		*p = privacyLevelFromString(s)
		return nil
	}
	var n int
	if err := json.Unmarshal(b, &n); err != nil {
		return err
	}
	*p = PrivacyLevel(n)
	return nil
}

// FormalityRange declares bounds on conversational formality. Ports
// FormalityRange. Allowed values: "casual", "neutral", "formal".
type FormalityRange struct {
	Floor   string `json:"Floor"`
	Ceiling string `json:"Ceiling"`
}

// Persona is the user-declared persona artefact. Ports the Persona record.
type Persona struct {
	ID              uuid.UUID      `json:"Id"`
	DisplayName     string         `json:"DisplayName"`
	Pronouns        *string        `json:"Pronouns,omitempty"`
	IdentityTags    []string       `json:"IdentityTags"`
	Values          []string       `json:"Values"`
	Taboos          []string       `json:"Taboos"`
	PreferredLocale string         `json:"PreferredLocale"`
	VoicePreference *string        `json:"VoicePreference,omitempty"`
	Formality       FormalityRange `json:"Formality"`
	Privacy         PrivacyLevel   `json:"Privacy"`
	CreatedAt       time.Time      `json:"CreatedAt"`
	UpdatedAt       time.Time      `json:"UpdatedAt"`
}

// NewPersona creates a Persona with balanced defaults, an unconstrained
// formality range ("casual".."formal"), and now timestamps. Ports
// Persona.Create. Returns an error on empty displayName/locale.
func NewPersona(displayName, locale string) (Persona, error) {
	if strings.TrimSpace(displayName) == "" {
		return Persona{}, errors.New("displayName required")
	}
	if strings.TrimSpace(locale) == "" {
		return Persona{}, errors.New("locale required")
	}
	now := time.Now().UTC()
	return Persona{
		ID:              uuid.New(),
		DisplayName:     displayName,
		Pronouns:        nil,
		IdentityTags:    []string{},
		Values:          []string{},
		Taboos:          []string{},
		PreferredLocale: locale,
		VoicePreference: nil,
		Formality:       FormalityRange{Floor: "casual", Ceiling: "formal"},
		Privacy:         PrivacyLevelBalanced,
		CreatedAt:       now,
		UpdatedAt:       now,
	}, nil
}

// ---------------------------------------------------------------------------
// IPersonaConflictResolver (IPersonaConflictResolver.cs)
// ---------------------------------------------------------------------------

// IPersonaConflictResolver reconciles a declared Persona with the learned
// PersonaState. Ports IPersonaConflictResolver. Implementations are
// deterministic and never mutate their inputs.
type IPersonaConflictResolver interface {
	Resolve(declared Persona, learned PersonaState) Persona
}

// DeclaredWinsResolver clamps learned formality into the declared range; the
// declared record is otherwise authoritative. Ports DeclaredWinsResolver.
type DeclaredWinsResolver struct{}

// Resolve applies the declared-wins policy. Ports Resolve.
func (DeclaredWinsResolver) Resolve(declared Persona, learned PersonaState) Persona {
	clamped := clampFormality(learned.Formality, declared.Formality)
	if clamped == learned.Formality {
		return declared
	}
	var rng FormalityRange
	switch clamped {
	case "casual":
		rng = FormalityRange{Floor: "casual", Ceiling: declared.Formality.Ceiling}
	case "formal":
		rng = FormalityRange{Floor: declared.Formality.Floor, Ceiling: "formal"}
	default:
		rng = declared.Formality
	}
	out := declared
	out.Formality = rng
	return out
}

func clampFormality(learned string, rng FormalityRange) string {
	learnedRank := formalityRank(learned)
	floorRank := formalityRank(rng.Floor)
	ceilingRank := formalityRank(rng.Ceiling)
	if floorRank > ceilingRank {
		return rng.Floor
	}
	if learnedRank < floorRank {
		return rng.Floor
	}
	if learnedRank > ceilingRank {
		return rng.Ceiling
	}
	return learned
}

func formalityRank(formality string) int {
	switch formality {
	case "casual":
		return 0
	case "formal":
		return 2
	default:
		return 1 // neutral / unknown
	}
}

var _ IPersonaConflictResolver = DeclaredWinsResolver{}

// LearnedWinsResolver passes the declared persona through so identity, taboos,
// and values stay intact; the learned formality/locale/verbosity are applied
// separately by the prompt builder. Ports LearnedWinsResolver.
type LearnedWinsResolver struct{}

// Resolve returns the declared persona unchanged. Ports Resolve.
func (LearnedWinsResolver) Resolve(declared Persona, _ PersonaState) Persona {
	return declared
}

var _ IPersonaConflictResolver = LearnedWinsResolver{}

// ---------------------------------------------------------------------------
// IPersonaProvider (IPersonaProvider.cs)
// ---------------------------------------------------------------------------

// IPersonaProvider persists and retrieves user-declared Persona documents.
// Ports IPersonaProvider. ExportAllAsync (IAsyncEnumerable) is a slice-returner.
type IPersonaProvider interface {
	Get(ctx context.Context, userID string) (Persona, bool, error)
	Save(ctx context.Context, userID string, persona Persona) (Persona, error)
	Exists(ctx context.Context, userID string) (bool, error)
	ExportAll(ctx context.Context) ([]Persona, error)
}

// ---------------------------------------------------------------------------
// JsonPersonaProvider (JsonPersonaProvider.cs)
// ---------------------------------------------------------------------------

// JsonPersonaProvider stores each persona as {root}/{userId}.persona.json.
// Atomic write-then-rename; per-userId locking. Ports JsonPersonaProvider.
type JsonPersonaProvider struct {
	rootDirectory string
	locksMu       sync.Mutex
	locks         map[string]*sync.Mutex
}

// NewJsonPersonaProvider creates a provider rooted at rootDirectory, creating
// the directory if needed. Errors on empty root / mkdir failure.
func NewJsonPersonaProvider(rootDirectory string) (*JsonPersonaProvider, error) {
	if strings.TrimSpace(rootDirectory) == "" {
		return nil, errors.New("rootDirectory required")
	}
	if err := os.MkdirAll(rootDirectory, 0o755); err != nil {
		return nil, err
	}
	return &JsonPersonaProvider{rootDirectory: rootDirectory, locks: make(map[string]*sync.Mutex)}, nil
}

func (p *JsonPersonaProvider) lockFor(userID string) *sync.Mutex {
	p.locksMu.Lock()
	defer p.locksMu.Unlock()
	g, ok := p.locks[userID]
	if !ok {
		g = &sync.Mutex{}
		p.locks[userID] = g
	}
	return g
}

func (p *JsonPersonaProvider) personaPath(userID string) string {
	safe := sanitizeFileName(userID)
	if strings.TrimSpace(safe) == "" {
		safe = "default"
	}
	return filepath.Join(p.rootDirectory, safe+".persona.json")
}

// Get loads the persona for userID. Ports GetAsync.
func (p *JsonPersonaProvider) Get(ctx context.Context, userID string) (Persona, bool, error) {
	if strings.TrimSpace(userID) == "" {
		return Persona{}, false, errors.New("userId required")
	}
	path := p.personaPath(userID)
	if _, err := os.Stat(path); errors.Is(err, os.ErrNotExist) {
		return Persona{}, false, nil
	}
	gate := p.lockFor(userID)
	gate.Lock()
	defer gate.Unlock()
	data, err := os.ReadFile(path)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return Persona{}, false, nil
		}
		return Persona{}, false, err
	}
	var persona Persona
	if err := json.Unmarshal(data, &persona); err != nil {
		return Persona{}, false, err
	}
	return persona, true, nil
}

// Save persists persona (refreshing UpdatedAt) atomically. Ports SaveAsync.
func (p *JsonPersonaProvider) Save(ctx context.Context, userID string, persona Persona) (Persona, error) {
	if strings.TrimSpace(userID) == "" {
		return Persona{}, errors.New("userId required")
	}
	refreshed := persona
	refreshed.UpdatedAt = time.Now().UTC()
	target := p.personaPath(userID)
	tmp := target + "." + uuidNoDashes(uuid.New()) + ".tmp"

	data, err := json.MarshalIndent(refreshed, "", "  ")
	if err != nil {
		return Persona{}, err
	}

	gate := p.lockFor(userID)
	gate.Lock()
	defer gate.Unlock()
	if err := os.WriteFile(tmp, data, 0o644); err != nil {
		_ = os.Remove(tmp)
		return Persona{}, err
	}
	if err := os.Rename(tmp, target); err != nil {
		_ = os.Remove(tmp)
		return Persona{}, err
	}
	return refreshed, nil
}

// Exists reports whether a persona is stored for userID. Ports ExistsAsync.
func (p *JsonPersonaProvider) Exists(ctx context.Context, userID string) (bool, error) {
	if strings.TrimSpace(userID) == "" {
		return false, errors.New("userId required")
	}
	_, err := os.Stat(p.personaPath(userID))
	if errors.Is(err, os.ErrNotExist) {
		return false, nil
	}
	if err != nil {
		return false, err
	}
	return true, nil
}

// ExportAll streams every stored persona, skipping corrupt records. Ports
// ExportAllAsync.
func (p *JsonPersonaProvider) ExportAll(ctx context.Context) ([]Persona, error) {
	out := make([]Persona, 0)
	entries, err := os.ReadDir(p.rootDirectory)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return out, nil
		}
		return nil, err
	}
	for _, entry := range entries {
		if ctx.Err() != nil {
			return nil, ctx.Err()
		}
		if entry.IsDir() || !strings.HasSuffix(entry.Name(), ".persona.json") {
			continue
		}
		data, rerr := os.ReadFile(filepath.Join(p.rootDirectory, entry.Name()))
		if rerr != nil {
			continue
		}
		var persona Persona
		if json.Unmarshal(data, &persona) != nil {
			continue
		}
		out = append(out, persona)
	}
	return out, nil
}

// sanitizeFileName replaces filesystem-invalid characters with '_' (mirrors the
// C# Path.GetInvalidFileNameChars join).
func sanitizeFileName(name string) string {
	invalid := "\x00<>:\"/\\|?*"
	return strings.Map(func(r rune) rune {
		if r < 32 || strings.ContainsRune(invalid, r) {
			return '_'
		}
		return r
	}, name)
}

var _ IPersonaProvider = (*JsonPersonaProvider)(nil)

// ---------------------------------------------------------------------------
// PersonaPromptBuilder (PersonaPromptBuilder.cs)
// ---------------------------------------------------------------------------

// BuildPersonaSystemHint renders persona into a compact system-prompt hint, or
// returns "" when the persona is effectively default. Every user string is
// JSON-quoted as a prompt-injection defence. Ports
// PersonaPromptBuilder.BuildSystemHint.
func BuildPersonaSystemHint(persona Persona) string {
	if isPersonaEffectivelyDefault(persona) {
		return ""
	}
	var sb strings.Builder
	sb.WriteString("[Persona]")
	sb.WriteString("\nYou are speaking with ")
	sb.WriteString(personaQuote(persona.DisplayName))
	sb.WriteByte('.')

	if persona.Pronouns != nil && strings.TrimSpace(*persona.Pronouns) != "" {
		sb.WriteString(" They identify as ")
		sb.WriteString(personaQuote(*persona.Pronouns))
		sb.WriteByte('.')
	}

	sb.WriteString("\nThey prefer responses in ")
	sb.WriteString(personaQuote(persona.PreferredLocale))
	sb.WriteString(", tone between ")
	sb.WriteString(personaQuote(persona.Formality.Floor))
	sb.WriteString(" and ")
	sb.WriteString(personaQuote(persona.Formality.Ceiling))
	sb.WriteByte('.')

	if len(persona.IdentityTags) > 0 {
		sb.WriteString("\nIdentity tags: ")
		sb.WriteString(personaQuoteList(persona.IdentityTags))
		sb.WriteByte('.')
	}
	if len(persona.Values) > 0 {
		sb.WriteString("\nTheir declared values: ")
		sb.WriteString(personaQuoteList(persona.Values))
		sb.WriteByte('.')
	}
	if len(persona.Taboos) > 0 {
		sb.WriteString("\nAvoid: ")
		sb.WriteString(personaQuoteList(persona.Taboos))
		sb.WriteByte('.')
	}
	if persona.VoicePreference != nil && strings.TrimSpace(*persona.VoicePreference) != "" {
		sb.WriteString("\nPreferred voice tag: ")
		sb.WriteString(personaQuote(*persona.VoicePreference))
		sb.WriteByte('.')
	}
	if persona.Privacy == PrivacyLevelStrict {
		sb.WriteString("\nPrivacy: strict — minimize stored signals, do not surface personal context proactively, and never share personal context across surfaces without explicit prompt.")
	} else if persona.Privacy == PrivacyLevelOpen {
		sb.WriteString("\nPrivacy: open — the user has authorised broader retention and proactive surfacing.")
	}
	return sb.String()
}

func isPersonaEffectivelyDefault(p Persona) bool {
	pronounsEmpty := p.Pronouns == nil || strings.TrimSpace(*p.Pronouns) == ""
	voiceEmpty := p.VoicePreference == nil || strings.TrimSpace(*p.VoicePreference) == ""
	return pronounsEmpty &&
		len(p.IdentityTags) == 0 &&
		len(p.Values) == 0 &&
		len(p.Taboos) == 0 &&
		voiceEmpty &&
		p.Privacy == PrivacyLevelBalanced &&
		p.Formality.Floor == "casual" &&
		p.Formality.Ceiling == "formal"
}

// personaQuote JSON-encodes value into a quoted literal (prompt-injection
// defence). Uses a non-HTML-escaping encoder to match UnsafeRelaxedJsonEscaping.
func personaQuote(value string) string {
	var sb strings.Builder
	enc := json.NewEncoder(&sb)
	enc.SetEscapeHTML(false)
	_ = enc.Encode(value)
	// Encode appends a trailing newline; trim it.
	return strings.TrimRight(sb.String(), "\n")
}

func personaQuoteList(items []string) string {
	if len(items) == 0 {
		return ""
	}
	parts := make([]string, len(items))
	for i, it := range items {
		parts[i] = personaQuote(it)
	}
	return strings.Join(parts, ", ")
}
