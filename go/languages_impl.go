// languages_impl.go
//
// Concrete CircleAI.Languages implementations that complement the types +
// interfaces in languages.go:
//   NullLanguageDetector.cs    -> NullLanguageDetector / NullLanguageDetectorInstance
//   DefaultLanguageRegistry.cs -> DefaultLanguageRegistry
//
// These are the "no ML model available" and "backed by KnownLanguagesAll"
// defaults. The IScriptNormaliser interface has no concrete implementation in
// the C# source (only the contract ships), so none is added here.

package circleai

import (
	"context"
	"strings"
)

// ---------------------------------------------------------------------------
// NullLanguageDetector
// ---------------------------------------------------------------------------

// NullLanguageDetector is a no-op ILanguageDetector used when no ML model is
// available. It always returns Unknown / 0-confidence — callers must treat this
// as "undetected". Ports NullLanguageDetector.
type NullLanguageDetector struct{}

// NullLanguageDetectorInstance is the shared singleton, mirroring the C#
// NullLanguageDetector.Instance field.
var NullLanguageDetectorInstance = NullLanguageDetector{}

// Detect always returns Unknown with confidence 0 and IsReliable false.
func (NullLanguageDetector) Detect(ctx context.Context, text string) (DetectionResult, error) {
	return DetectionResult{Language: LanguageTagUnknown, Confidence: 0, IsReliable: false}, nil
}

// DetectMultiple returns a single Unknown candidate, matching the C# original
// which ignores maxResults and always yields one entry.
func (NullLanguageDetector) DetectMultiple(ctx context.Context, text string, maxResults int) ([]DetectionResult, error) {
	return []DetectionResult{{Language: LanguageTagUnknown, Confidence: 0, IsReliable: false}}, nil
}

var _ ILanguageDetector = NullLanguageDetector{}

// ---------------------------------------------------------------------------
// DefaultLanguageRegistry
// ---------------------------------------------------------------------------

// DefaultLanguageRegistry is a thread-safe ILanguageRegistry backed by
// KnownLanguagesAll. Tag and region lookups are case-insensitive. Ports
// DefaultLanguageRegistry. The backing maps are built once at construction and
// only read afterwards, so no lock is required for lookups.
type DefaultLanguageRegistry struct {
	byTag    map[string]LanguageTag   // key = lower-cased BCP tag
	byRegion map[string][]LanguageTag // key = lower-cased ISO region
}

// NewDefaultLanguageRegistry builds a registry over KnownLanguagesAll.
func NewDefaultLanguageRegistry() *DefaultLanguageRegistry {
	r := &DefaultLanguageRegistry{
		byTag:    make(map[string]LanguageTag, len(KnownLanguagesAll)),
		byRegion: make(map[string][]LanguageTag),
	}
	for _, t := range KnownLanguagesAll {
		r.byTag[strings.ToLower(t.BcpTag)] = t
		rk := strings.ToLower(t.PrimaryRegion)
		r.byRegion[rk] = append(r.byRegion[rk], t)
	}
	return r
}

// GetByBcpTag returns the LanguageTag for the given tag, or nil.
func (r *DefaultLanguageRegistry) GetByBcpTag(bcpTag string) *LanguageTag {
	if t, ok := r.byTag[strings.ToLower(bcpTag)]; ok {
		out := t
		return &out
	}
	return nil
}

// GetAll returns all known language tags in declaration order.
func (r *DefaultLanguageRegistry) GetAll() []LanguageTag {
	return KnownLanguagesAll
}

// GetForRegion returns every language whose primary region matches isoRegion.
func (r *DefaultLanguageRegistry) GetForRegion(isoRegion string) []LanguageTag {
	src := r.byRegion[strings.ToLower(isoRegion)]
	out := make([]LanguageTag, len(src))
	copy(out, src)
	return out
}

// IsSupported reports whether the given BCP tag is in the registry.
func (r *DefaultLanguageRegistry) IsSupported(bcpTag string) bool {
	_, ok := r.byTag[strings.ToLower(bcpTag)]
	return ok
}

var _ ILanguageRegistry = (*DefaultLanguageRegistry)(nil)
