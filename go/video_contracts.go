// video_contracts.go
//
// Ports the CircleAI.Video contract surface:
//   Primitives.cs -> StyleID, VideoResolution, StyleReferenceFrame,
//                    StyleAttribution, StyleReference, AudioTrack,
//                    VideoGenerationRequest, VideoGenerationResult,
//                    StyleScriptRequest, StyleScriptResult
//   Contracts.cs  -> IVideoGenerator, IStyleScript, IStyleReference
//   NullImplementations.cs -> NullVideoGenerator, NullStyleScript,
//                    InMemoryStyleReference
//
// MAPPING RULES (mirroring the rest of the flat package):
//   - ValueTask<T>          -> synchronous method returning (T, error); ctx first.
//   - ValueTask (no value)  -> method returning error.
//   - IReadOnlyList<T>      -> []T.
//   - ReadOnlyMemory<byte>  -> []byte.  TimeSpan -> time.Duration.
//   - StyleReference? (nullable ref) -> (StyleReference, bool) so a miss is
//     unambiguous (Go structs are not nilable).
//   - readonly record struct StyleId(string) -> StyleID with a String() method;
//     the C# implicit StyleId->string operator maps to StyleID.String().
//
// FLAT-PACKAGE DISAMBIGUATION: the C# StyleId struct is ported as StyleID (Go
// initialism casing). The StyleReference RECORD keeps its name; the catalogue
// interface is IStyleReference (no clash). AudioTrack is unique across the tree.

package circleai

import (
	"context"
	"sort"
	"sync"
	"time"
)

// ---------------------------------------------------------------------------
// Primitives.cs
// ---------------------------------------------------------------------------

// StyleID identifies one registered style (e.g. "pooh-1926", "noir-detective",
// "space-opera"). Ports the StyleId readonly record struct; String() stands in for
// both ToString() and the implicit StyleId->string conversion.
type StyleID struct {
	Value string
}

// String returns the underlying style id value.
func (s StyleID) String() string { return s.Value }

// VideoResolution is the output resolution for a generated video. Ports the
// VideoResolution readonly record struct.
type VideoResolution struct {
	Width  int
	Height int
}

// VideoResolutionP480 is 720x480 (the C# VideoResolution.P480 static property).
func VideoResolutionP480() VideoResolution { return VideoResolution{Width: 720, Height: 480} }

// VideoResolutionP720 is 1280x720 (VideoResolution.P720).
func VideoResolutionP720() VideoResolution { return VideoResolution{Width: 1280, Height: 720} }

// VideoResolutionP1080 is 1920x1080 (VideoResolution.P1080).
func VideoResolutionP1080() VideoResolution { return VideoResolution{Width: 1920, Height: 1080} }

// StyleReferenceFrame is one reference frame the generator can ground style on —
// public-domain illustration, original-character render, etc. Ports the
// StyleReferenceFrame record. Caption is "" when the C# Caption is null.
type StyleReferenceFrame struct {
	ImageBytes []byte
	MimeType   string
	Caption    string
}

// StyleAttribution is attribution + license metadata for one style, letting txtMe
// (and any other consumer) display the source before rendering. Ports the
// StyleAttribution record. Url is "" when the C# Url is null.
type StyleAttribution struct {
	Source  string
	License string
	Url     string
}

// StyleReference is one style the host has registered with the catalogue. Ports the
// StyleReference record. VoicePersonaID is "" when the C# VoicePersonaId is null.
type StyleReference struct {
	ID               StyleID
	DisplayName      string
	ShortDescription string
	Attribution      StyleAttribution
	VoicePersonaID   string
	Frames           []StyleReferenceFrame
}

// AudioTrack is an audio track produced by CircleAI.Speech for the generator to
// embed. Ports the AudioTrack record.
type AudioTrack struct {
	AudioPcm16Mono []byte
	SampleRateHz   int
	Duration       time.Duration
}

// VideoGenerationRequest is one generation request — text + optional style +
// optional grounding image + optional audio. Ports the VideoGenerationRequest
// record. Nullable value/ref members use pointers so "unset" is distinguishable:
// StyleID *StyleID, ReferenceImage *StyleReferenceFrame, Track *AudioTrack,
// Seed *int64. FrameRate defaults to 24.
type VideoGenerationRequest struct {
	Prompt         string
	Duration       time.Duration
	Resolution     VideoResolution
	FrameRate      int
	StyleID        *StyleID
	ReferenceImage *StyleReferenceFrame
	AudioTrack     *AudioTrack
	Seed           *int64
}

// NewVideoGenerationRequest builds a request applying the C# FrameRate default (24)
// when frameRate is left 0.
func NewVideoGenerationRequest(prompt string, duration time.Duration, resolution VideoResolution) VideoGenerationRequest {
	return VideoGenerationRequest{Prompt: prompt, Duration: duration, Resolution: resolution, FrameRate: 24}
}

// VideoGenerationResult is one generation outcome. Ports the VideoGenerationResult
// record.
type VideoGenerationResult struct {
	VideoBytes []byte
	MimeType   string
	Duration   time.Duration
	FrameCount int
	Resolution VideoResolution
	BackendID  string
}

// StyleScriptRequest is one style-script request — raw user message + chosen voice.
// Ports the StyleScriptRequest record. SpeakerHint / LanguageHint are "" when the C#
// values are null.
type StyleScriptRequest struct {
	SourceMessage string
	Style         StyleID
	SpeakerHint   string
	LanguageHint  string
}

// StyleScriptResult is one style-script outcome — the rewritten line + voice +
// estimated duration. Ports the StyleScriptResult record. VoicePersonaID is "" when
// the C# VoicePersonaId is null.
type StyleScriptResult struct {
	RewrittenText           string
	Style                   StyleID
	VoicePersonaID          string
	EstimatedSpokenDuration time.Duration
}

// ---------------------------------------------------------------------------
// Contracts.cs
// ---------------------------------------------------------------------------

// IVideoGenerator generates a short video from a text prompt (and optional style +
// reference frame + audio track). Ports IVideoGenerator.
type IVideoGenerator interface {
	// BackendID is the backend self-identification — "cogvideox-2b",
	// "ltx-video-2b-distilled", "null".
	BackendID() string
	// GenerateAsync synthesises the requested video.
	GenerateAsync(ctx context.Context, request VideoGenerationRequest) (VideoGenerationResult, error)
}

// IStyleScript rewrites a user message in a chosen style's voice. Ports IStyleScript.
type IStyleScript interface {
	// BackendID is the backend self-identification — "circleai-llm", "null".
	BackendID() string
	// RewriteAsync rewrites the source message in the requested style.
	RewriteAsync(ctx context.Context, request StyleScriptRequest) (StyleScriptResult, error)
}

// IStyleReference is the catalogue of registered styles. Ports IStyleReference.
type IStyleReference interface {
	// BackendID is the backend self-identification — "in-memory",
	// "embedded-defaults", "null".
	BackendID() string
	// RegisterAsync registers a style (typically at host startup).
	RegisterAsync(ctx context.Context, style StyleReference) error
	// GetAsync looks up one style by id; ok is false on a miss (the C# nullable).
	GetAsync(ctx context.Context, id StyleID) (style StyleReference, ok bool, err error)
	// ListAsync enumerates every registered style — drives picker UIs.
	ListAsync(ctx context.Context) ([]StyleReference, error)
}

// ---------------------------------------------------------------------------
// NullImplementations.cs
// ---------------------------------------------------------------------------

// NullVideoGenerator returns an empty video — zero bytes, mime "video/mp4". Ports
// NullVideoGenerator.
type NullVideoGenerator struct{}

// NullVideoGeneratorInstance mirrors NullVideoGenerator.Instance.
var NullVideoGeneratorInstance = NullVideoGenerator{}

// BackendID returns "null".
func (NullVideoGenerator) BackendID() string { return "null" }

// GenerateAsync returns an empty video echoing the request's resolution.
func (NullVideoGenerator) GenerateAsync(_ context.Context, request VideoGenerationRequest) (VideoGenerationResult, error) {
	return VideoGenerationResult{
		VideoBytes: []byte{},
		MimeType:   "video/mp4",
		Duration:   0,
		FrameCount: 0,
		Resolution: request.Resolution,
		BackendID:  "null",
	}, nil
}

// NullStyleScript returns the source message unchanged with a zero estimated
// duration. Ports NullStyleScript.
type NullStyleScript struct{}

// NullStyleScriptInstance mirrors NullStyleScript.Instance.
var NullStyleScriptInstance = NullStyleScript{}

// BackendID returns "null".
func (NullStyleScript) BackendID() string { return "null" }

// RewriteAsync echoes SourceMessage with the request's Style, no voice, zero
// duration.
func (NullStyleScript) RewriteAsync(_ context.Context, request StyleScriptRequest) (StyleScriptResult, error) {
	return StyleScriptResult{
		RewrittenText:           request.SourceMessage,
		Style:                   request.Style,
		VoicePersonaID:          "",
		EstimatedSpokenDuration: 0,
	}, nil
}

// InMemoryStyleReference is a thread-safe in-memory style catalogue — the default
// implementation. Hosting layers register their style packs on startup and the
// picker reads from here. Ports InMemoryStyleReference. Lookups are
// case-insensitive on the style id (StringComparer.OrdinalIgnoreCase).
type InMemoryStyleReference struct {
	mu   sync.Mutex
	byID map[string]StyleReference
}

// NewInMemoryStyleReference constructs an empty catalogue.
func NewInMemoryStyleReference() *InMemoryStyleReference {
	return &InMemoryStyleReference{byID: make(map[string]StyleReference)}
}

// BackendID returns "in-memory".
func (r *InMemoryStyleReference) BackendID() string { return "in-memory" }

// RegisterAsync registers (or replaces) a style keyed on its id (case-insensitive).
func (r *InMemoryStyleReference) RegisterAsync(ctx context.Context, style StyleReference) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	r.mu.Lock()
	r.byID[styleKey(style.ID.Value)] = style
	r.mu.Unlock()
	return nil
}

// GetAsync looks up one style by id (case-insensitive); ok is false on a miss.
func (r *InMemoryStyleReference) GetAsync(ctx context.Context, id StyleID) (StyleReference, bool, error) {
	if err := ctx.Err(); err != nil {
		return StyleReference{}, false, err
	}
	r.mu.Lock()
	s, ok := r.byID[styleKey(id.Value)]
	r.mu.Unlock()
	return s, ok, nil
}

// ListAsync returns every registered style. The C# returns a snapshot copy of the
// dictionary values (order unspecified); this port sorts by id so enumeration is
// deterministic for callers and tests.
func (r *InMemoryStyleReference) ListAsync(ctx context.Context) ([]StyleReference, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	r.mu.Lock()
	copySlice := make([]StyleReference, 0, len(r.byID))
	for _, s := range r.byID {
		copySlice = append(copySlice, s)
	}
	r.mu.Unlock()
	sort.SliceStable(copySlice, func(i, j int) bool {
		return copySlice[i].ID.Value < copySlice[j].ID.Value
	})
	return copySlice, nil
}

// styleKey folds the style id to lower-case ASCII for case-insensitive lookup,
// matching StringComparer.OrdinalIgnoreCase's byte-wise ASCII folding for the slug
// ids styles use (e.g. "Pooh-1926" == "pooh-1926"). Ordinal (not culture-aware)
// folding is deliberate — strings.ToLower would apply Unicode special-casing the C#
// comparer does not.
func styleKey(id string) string {
	b := []byte(id)
	for i := 0; i < len(b); i++ {
		if b[i] >= 'A' && b[i] <= 'Z' {
			b[i] += 'a' - 'A'
		}
	}
	return string(b)
}

// Interface guards.
var (
	_ IVideoGenerator = NullVideoGenerator{}
	_ IStyleScript    = NullStyleScript{}
	_ IStyleReference = (*InMemoryStyleReference)(nil)
)
