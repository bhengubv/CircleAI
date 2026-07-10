// speech_cloud_intent_router.go
//
// Ports CircleAI.Speech.Cloud.KeywordVoiceIntentRouter.cs — the generic
// regex-based voice intent router (and its VoiceIntent / VoiceIntentMatch
// records + IVoiceIntentRouter interface + NullVoiceIntentRouter).
//
// The router matches a host-supplied ordered list of intents against the trimmed
// transcript; the first hit wins; it falls through to a caller-defined fallback
// intent (typically "ask-ai") when nothing matches. On a hit, every NAMED
// capture group is surfaced (numeric/implicit groups skipped), value-trimmed,
// empties dropped — matching the C# exactly.
//
// Go's regexp uses (?P<name>...) for named groups; SubexpNames() returns "" for
// the implicit whole-match group and for unnamed groups, so those are skipped
// just as the C# int.TryParse guard skips the "0" group.

package circleai

import (
	"context"
	"errors"
	"regexp"
	"strings"
)

// VoiceIntent is one named intent the router recognises. Pattern is matched
// against the trimmed transcript; on a hit, every named group is exposed in
// VoiceIntentMatch.Captures. Ports the VoiceIntent record.
type VoiceIntent struct {
	// Name is the intent identifier surfaced on a match.
	Name string
	// Pattern is the compiled regex tried against the transcript.
	Pattern *regexp.Regexp
}

// VoiceIntentMatch is one match outcome. Ports the VoiceIntentMatch record.
type VoiceIntentMatch struct {
	// IntentName is the matched intent (or the fallback name).
	IntentName string
	// Transcript is the trimmed transcript that was matched.
	Transcript string
	// Captures maps each named group to its trimmed value (empty on fallback).
	Captures map[string]string
}

// IVoiceIntentRouter maps a transcript to one of a host-supplied set of intents.
// Rule-based, sub-millisecond per attempt, hermetic. Ports IVoiceIntentRouter.
type IVoiceIntentRouter interface {
	// BackendID is the backend self-identification — "keyword" / "null".
	BackendID() string
	// Route matches the transcript against the configured intents, returning a
	// match for the first hitting intent, or for the fallback intent when nothing
	// matches (whose Captures is empty).
	Route(ctx context.Context, transcript string) (VoiceIntentMatch, error)
}

// KeywordVoiceIntentRouter is the default IVoiceIntentRouter. It takes an ordered
// list of intents plus a fallback name and tries each pattern in order. Ports
// KeywordVoiceIntentRouter.
type KeywordVoiceIntentRouter struct {
	intents          []VoiceIntent
	fallbackIntentID string
}

// NewKeywordVoiceIntentRouter constructs a router over intents. fallbackIntentName
// defaults to "ask-ai" when empty (mirroring the C# default argument), and must
// not be whitespace-only. Ports the KeywordVoiceIntentRouter constructor.
func NewKeywordVoiceIntentRouter(intents []VoiceIntent, fallbackIntentName string) (*KeywordVoiceIntentRouter, error) {
	if intents == nil {
		return nil, errors.New("intents required")
	}
	if fallbackIntentName == "" {
		fallbackIntentName = "ask-ai"
	}
	if strings.TrimSpace(fallbackIntentName) == "" {
		return nil, errors.New("fallbackIntentName must not be whitespace")
	}
	cp := make([]VoiceIntent, len(intents))
	copy(cp, intents)
	return &KeywordVoiceIntentRouter{intents: cp, fallbackIntentID: fallbackIntentName}, nil
}

// BackendID returns "keyword".
func (r *KeywordVoiceIntentRouter) BackendID() string { return "keyword" }

// Route matches transcript against the configured intents in order.
func (r *KeywordVoiceIntentRouter) Route(ctx context.Context, transcript string) (VoiceIntentMatch, error) {
	if err := ctx.Err(); err != nil {
		return VoiceIntentMatch{}, err
	}
	text := strings.TrimSpace(transcript)
	if len(text) == 0 {
		return VoiceIntentMatch{IntentName: r.fallbackIntentID, Transcript: "", Captures: map[string]string{}}, nil
	}

	for _, intent := range r.intents {
		m := intent.Pattern.FindStringSubmatch(text)
		if m == nil {
			continue
		}

		captures := map[string]string{}
		for gi, name := range intent.Pattern.SubexpNames() {
			// Skip the implicit whole-match group (index 0, name "") and any
			// unnamed groups (name "") — mirrors the C# int.TryParse("0") guard.
			if name == "" {
				continue
			}
			val := m[gi]
			if val != "" {
				captures[name] = strings.TrimSpace(val)
			}
		}

		return VoiceIntentMatch{IntentName: intent.Name, Transcript: text, Captures: captures}, nil
	}

	return VoiceIntentMatch{IntentName: r.fallbackIntentID, Transcript: text, Captures: map[string]string{}}, nil
}

// NullVoiceIntentRouter always returns the fallback intent ("ask-ai"). Ports
// NullVoiceIntentRouter.
type NullVoiceIntentRouter struct{}

// NullVoiceIntentRouterInstance mirrors NullVoiceIntentRouter.Instance.
var NullVoiceIntentRouterInstance = NullVoiceIntentRouter{}

// BackendID returns "null".
func (NullVoiceIntentRouter) BackendID() string { return "null" }

// Route always returns the "ask-ai" fallback with empty captures.
func (NullVoiceIntentRouter) Route(_ context.Context, transcript string) (VoiceIntentMatch, error) {
	return VoiceIntentMatch{IntentName: "ask-ai", Transcript: transcript, Captures: map[string]string{}}, nil
}

// Interface guards.
var (
	_ IVoiceIntentRouter = (*KeywordVoiceIntentRouter)(nil)
	_ IVoiceIntentRouter = NullVoiceIntentRouter{}
)
