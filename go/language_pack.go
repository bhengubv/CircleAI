// language_pack.go
//
// Ports CircleAI.Languages.Language contracts + helpers:
//   ILanguagePack.cs            -> LanguagePackMetadata, CulturalNote, ILanguagePack
//   ILanguagePackRegistry.cs    -> ILanguagePackRegistry
//   DefaultLanguagePackRegistry.cs -> DefaultLanguagePackRegistry
//   LanguagePackHelpers.cs      -> LanguagePackRegistry (concurrent, BCP-47 match) + LocaleHintMerge
//
// A language pack is a language-specific knowledge bundle: idiomatic
// expressions, cultural context, and prompt tuning so the on-device LLM
// reasons correctly in that language. Packs are mostly data — the concrete
// per-language packs live in language_packs_data.go.

package circleai

import (
	"sort"
	"strings"
	"sync"
)

// ---------------------------------------------------------------------------
// LanguagePackMetadata
// ---------------------------------------------------------------------------

// LanguagePackMetadata is the descriptive metadata for a language pack.
// Ports the LanguagePackMetadata record.
type LanguagePackMetadata struct {
	// BcpTag is the IETF BCP-47 language tag (e.g. "zu", "sw", "ar").
	BcpTag string

	// DisplayName is the English display name of the language.
	DisplayName string

	// NativeName is the name of the language in that language.
	NativeName string

	// PrimaryRegion is the ISO 3166-1 alpha-2 region where the language is
	// primarily spoken.
	PrimaryRegion string

	// SpokenInRegions holds every ISO 3166-1 alpha-2 region the pack targets.
	SpokenInRegions []string

	// PackVersion is the semantic version of the pack, formatted "major.minor".
	PackVersion string
}

// ---------------------------------------------------------------------------
// CulturalNote
// ---------------------------------------------------------------------------

// CulturalNote is a cultural/contextual note for a specific topic.
// Ports the CulturalNote record.
type CulturalNote struct {
	// Context is the topic the note applies to (e.g. "greeting", "business").
	Context string

	// Guidance is the cultural guidance for that context.
	Guidance string

	// Examples holds illustrative example expressions.
	Examples []string
}

// ---------------------------------------------------------------------------
// ILanguagePack
// ---------------------------------------------------------------------------

// ILanguagePack is a language-specific knowledge pack. It provides idiomatic
// expressions, cultural context, and prompt tuning for the on-device LLM to
// reason correctly in this language. Ports the ILanguagePack interface.
type ILanguagePack interface {
	// Metadata returns the pack's descriptive metadata.
	Metadata() LanguagePackMetadata

	// GetIdiomaticExpression returns the idiomatic translation of a common
	// phrase, or nil when the phrase is not mapped. Lookup is case-insensitive.
	GetIdiomaticExpression(phrase string) *string

	// AdaptSystemPrompt adapts a base system prompt for this language and culture.
	AdaptSystemPrompt(basePrompt string) string

	// GetCulturalNotes returns cultural notes for a given context (e.g.
	// "greeting", "business", "medical"). Empty when none are mapped.
	GetCulturalNotes(context string) []CulturalNote

	// GetGreeting returns a locale-appropriate greeting for the given time of day.
	GetGreeting(timeOfDay string) string

	// GetLocaleHints returns locale-specific number/date/currency formatting hints.
	GetLocaleHints() map[string]string
}

// ---------------------------------------------------------------------------
// ILanguagePackRegistry
// ---------------------------------------------------------------------------

// ILanguagePackRegistry is the registry of all installed language packs.
// Ports the ILanguagePackRegistry interface.
type ILanguagePackRegistry interface {
	// Register adds (or replaces) a pack keyed by its BCP-47 tag.
	Register(pack ILanguagePack)

	// GetByBcpTag returns the pack for the given tag, or nil when not present.
	GetByBcpTag(bcpTag string) ILanguagePack

	// GetAvailablePacks returns the metadata of every registered pack.
	GetAvailablePacks() []LanguagePackMetadata

	// HasPack reports whether a pack for the given tag is registered.
	HasPack(bcpTag string) bool
}

// ---------------------------------------------------------------------------
// DefaultLanguagePackRegistry
// ---------------------------------------------------------------------------

// DefaultLanguagePackRegistry is a thread-safe in-memory ILanguagePackRegistry.
// Ports DefaultLanguagePackRegistry. Lookup by exact BCP-47 tag.
type DefaultLanguagePackRegistry struct {
	mu    sync.Mutex
	packs map[string]ILanguagePack
}

// NewDefaultLanguagePackRegistry creates an empty registry.
func NewDefaultLanguagePackRegistry() *DefaultLanguagePackRegistry {
	return &DefaultLanguagePackRegistry{packs: make(map[string]ILanguagePack)}
}

// Register adds or replaces a pack. A nil pack is ignored (the C# original
// throws ArgumentNullException; the Go port fails closed by ignoring nil).
func (r *DefaultLanguagePackRegistry) Register(pack ILanguagePack) {
	if pack == nil {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	r.packs[pack.Metadata().BcpTag] = pack
}

// GetByBcpTag returns the pack for the given tag, or nil.
func (r *DefaultLanguagePackRegistry) GetByBcpTag(bcpTag string) ILanguagePack {
	r.mu.Lock()
	defer r.mu.Unlock()
	if p, ok := r.packs[bcpTag]; ok {
		return p
	}
	return nil
}

// GetAvailablePacks returns the metadata of every registered pack.
func (r *DefaultLanguagePackRegistry) GetAvailablePacks() []LanguagePackMetadata {
	r.mu.Lock()
	defer r.mu.Unlock()
	out := make([]LanguagePackMetadata, 0, len(r.packs))
	for _, p := range r.packs {
		out = append(out, p.Metadata())
	}
	return out
}

// HasPack reports whether a pack for the given tag is registered.
func (r *DefaultLanguagePackRegistry) HasPack(bcpTag string) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	_, ok := r.packs[bcpTag]
	return ok
}

var _ ILanguagePackRegistry = (*DefaultLanguagePackRegistry)(nil)

// ---------------------------------------------------------------------------
// LanguagePackRegistry (LanguagePackHelpers.cs)
// ---------------------------------------------------------------------------

// LanguagePackRegistry is a concurrent registry with richer BCP-47 matching
// (exact tag, language-prefix, region). Ports LanguagePackRegistry from
// LanguagePackHelpers.cs. Tag lookups are case-insensitive.
type LanguagePackRegistry struct {
	mu    sync.RWMutex
	byTag map[string]ILanguagePack // key = lower-cased BCP tag
}

// NewLanguagePackRegistry creates an empty registry.
func NewLanguagePackRegistry() *LanguagePackRegistry {
	return &LanguagePackRegistry{byTag: make(map[string]ILanguagePack)}
}

// Register adds or replaces a pack keyed by its (case-folded) BCP tag. A nil
// pack is ignored.
func (r *LanguagePackRegistry) Register(pack ILanguagePack) {
	if pack == nil {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	r.byTag[strings.ToLower(pack.Metadata().BcpTag)] = pack
}

// GetByExactTag returns the pack whose BCP tag exactly (case-insensitively)
// matches bcpTag, or nil.
func (r *LanguagePackRegistry) GetByExactTag(bcpTag string) ILanguagePack {
	if strings.TrimSpace(bcpTag) == "" {
		return nil
	}
	r.mu.RLock()
	defer r.mu.RUnlock()
	if p, ok := r.byTag[strings.ToLower(bcpTag)]; ok {
		return p
	}
	return nil
}

// GetByLanguage returns the first pack whose BCP tag starts with the language
// portion (before the first '-') of langPrefix, or nil.
func (r *LanguagePackRegistry) GetByLanguage(langPrefix string) ILanguagePack {
	if strings.TrimSpace(langPrefix) == "" {
		return nil
	}
	prefix := strings.ToLower(strings.SplitN(langPrefix, "-", 2)[0])
	r.mu.RLock()
	defer r.mu.RUnlock()
	// Deterministic order (C# FirstOrDefault over a ConcurrentDictionary is
	// unordered; we sort keys so the Go port is reproducible).
	keys := make([]string, 0, len(r.byTag))
	for k := range r.byTag {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	for _, k := range keys {
		p := r.byTag[k]
		if strings.HasPrefix(strings.ToLower(p.Metadata().BcpTag), prefix) {
			return p
		}
	}
	return nil
}

// ForRegion returns every pack that lists region in its SpokenInRegions
// (case-insensitive). region must be non-empty.
func (r *LanguagePackRegistry) ForRegion(region string) []ILanguagePack {
	if strings.TrimSpace(region) == "" {
		panic("region required")
	}
	r.mu.RLock()
	defer r.mu.RUnlock()
	var out []ILanguagePack
	keys := make([]string, 0, len(r.byTag))
	for k := range r.byTag {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	for _, k := range keys {
		p := r.byTag[k]
		for _, rg := range p.Metadata().SpokenInRegions {
			if strings.EqualFold(rg, region) {
				out = append(out, p)
				break
			}
		}
	}
	return out
}

// AllTags returns every registered BCP tag, sorted ascending.
func (r *LanguagePackRegistry) AllTags() []string {
	r.mu.RLock()
	defer r.mu.RUnlock()
	out := make([]string, 0, len(r.byTag))
	for _, p := range r.byTag {
		out = append(out, p.Metadata().BcpTag)
	}
	sort.Strings(out)
	return out
}

// ---------------------------------------------------------------------------
// LocaleHintMerge (LanguagePackHelpers.cs)
// ---------------------------------------------------------------------------

// MergeLocaleHints merges two locale-hint maps: secondary provides the base,
// primary overrides. Keys are compared case-insensitively (primary wins).
// Ports LocaleHintMerge.Merge. The returned map's keys preserve the casing of
// whichever source last wrote them, mirroring the C# case-insensitive
// dictionary semantics.
func MergeLocaleHints(primary, secondary map[string]string) map[string]string {
	if primary == nil || secondary == nil {
		panic("primary and secondary required")
	}
	// Track keys case-insensitively so a primary key overrides a secondary key
	// that differs only by case.
	merged := make(map[string]string, len(primary)+len(secondary))
	lowerToKey := make(map[string]string, len(primary)+len(secondary))
	for k, v := range secondary {
		lk := strings.ToLower(k)
		lowerToKey[lk] = k
		merged[k] = v
	}
	for k, v := range primary {
		lk := strings.ToLower(k)
		if existing, ok := lowerToKey[lk]; ok && existing != k {
			delete(merged, existing)
		}
		lowerToKey[lk] = k
		merged[k] = v
	}
	return merged
}
