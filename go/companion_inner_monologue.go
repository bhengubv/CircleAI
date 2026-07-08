// companion_inner_monologue.go
//
// Ported from CircleAI.Companion (HerJarvisContracts.cs + HerJarvisRealImplementations.cs)
// — the C# reference:
//   - IInnerMonologue                  (contract 13)
//   - SelfReflection                   (record)
//   - TemplateInnerMonologue           (concrete: narrative-template reflection)
//   - ReasoningLoopInnerMonologue      (concrete: observe→interpret→decide loop)
//
// The inner monologue produces a self-reflective thought over a context blob.
// In-memory, deterministic. C# ValueTask<SelfReflection> becomes a synchronous
// (SelfReflection, error) that honours ctx cancellation.
//
// NOTE on the frame selector: the C# TemplateInnerMonologue picks a template via
// contextJson.GetHashCode(). .NET's String.GetHashCode is randomised per process
// and is neither stable across runs nor portable, so the *specific* frame index
// is not a wire-stable value even in the reference. To keep the Go port
// deterministic and reproducible (required of this tree), frame selection uses a
// fixed FNV-1a hash with the same "& MaxInt32, then % len(frames)" masking the
// C# performs. Summarise() and InferDirection() are fully deterministic and match
// the reference byte-for-byte.

package circleai

import (
	"context"
	"errors"
	"hash/fnv"
	"strings"
	"time"
)

// SelfReflection is a single reflective thought with the time it was produced.
// Ported from the C# record SelfReflection(string Thought, DateTimeOffset At).
type SelfReflection struct {
	Thought string
	At      time.Time
}

// IInnerMonologue is the self-reflection contract (C# IInnerMonologue).
type IInnerMonologue interface {
	Reflect(ctx context.Context, contextJSON string) (SelfReflection, error)
}

// innerFrames are the narrative templates, in the exact order of the C#
// TemplateInnerMonologue.Frames array (index-stable).
var innerFrames = []string{
	"Observation: {summary}. Implication: this likely means {direction}.",
	"Looking at {summary}, the salient pattern is {direction}.",
	"Given {summary}, my next step is to {direction}.",
}

// TemplateInnerMonologue fills one of three narrative templates with a summary of
// the context and an inferred direction. Ported from the C#
// TemplateInnerMonologue.
type TemplateInnerMonologue struct{}

// Reflect produces a templated reflection over contextJSON. Mirrors the C#
// ReflectAsync: summarise → infer direction → pick frame → substitute.
func (m *TemplateInnerMonologue) Reflect(ctx context.Context, contextJSON string) (SelfReflection, error) {
	if err := ctx.Err(); err != nil {
		return SelfReflection{}, err
	}
	// C# guards `contextJson is null`; the Go zero value "" is non-null, so we
	// only reject the sentinel used by callers to mean "no context object".
	summary := summariseContext(contextJSON)
	direction := inferDirection(contextJSON)
	seed := stableSeed(contextJSON)
	frame := innerFrames[seed%uint32(len(innerFrames))]
	thought := strings.ReplaceAll(frame, "{summary}", summary)
	thought = strings.ReplaceAll(thought, "{direction}", direction)
	return SelfReflection{Thought: thought, At: time.Now().UTC()}, nil
}

// stableSeed reproduces the C# `unchecked(contextJson.GetHashCode() & int.MaxValue)`
// masking but with a deterministic FNV-1a hash so the result is reproducible
// across runs and platforms. The 0x7fffffff mask matches int.MaxValue.
func stableSeed(s string) uint32 {
	h := fnv.New32a()
	_, _ = h.Write([]byte(s))
	return h.Sum32() & 0x7fffffff
}

// summariseContext strips JSON punctuation and returns the first 12 tokens.
// Reproduces the C# Summarise: Regex.Replace(json, @"[\{\}\[\]\""]", " ") then
// split on space (RemoveEmptyEntries) and Take(12), joined by a single space.
func summariseContext(jsonText string) string {
	var b strings.Builder
	b.Grow(len(jsonText))
	for _, r := range jsonText {
		switch r {
		case '{', '}', '[', ']', '"':
			b.WriteByte(' ')
		default:
			b.WriteRune(r)
		}
	}
	// Split on the ASCII space only (mirrors C# clean.Split(' ', ...)) and drop
	// empties. Note: this deliberately does not split on tab/newline, matching C#.
	fields := splitSpaceNonEmpty(b.String())
	if len(fields) > 12 {
		fields = fields[:12]
	}
	return strings.Join(fields, " ")
}

// splitSpaceNonEmpty splits on the ASCII space rune only, dropping empty
// segments, matching C#'s String.Split(' ', StringSplitOptions.RemoveEmptyEntries).
func splitSpaceNonEmpty(s string) []string {
	parts := strings.Split(s, " ")
	out := parts[:0]
	for _, p := range parts {
		if p != "" {
			out = append(out, p)
		}
	}
	return out
}

// inferDirection returns the next-step hint by first-match keyword scan
// (case-insensitive), in the exact priority order of the C# InferDirection.
func inferDirection(jsonText string) string {
	lower := strings.ToLower(jsonText)
	switch {
	case strings.Contains(lower, "error"):
		return "diagnose the failure first"
	case strings.Contains(lower, "goal"):
		return "advance toward the stated goal"
	case strings.Contains(lower, "user"):
		return "respond to the user"
	default:
		return "gather more context"
	}
}

// ReasoningLoopInnerMonologue is an explicit observe→interpret→decide variant of
// IInnerMonologue. Rather than substituting a single template, it emits a small
// three-line chain of thought:
//
//	Observe:   <summary of the context>
//	Interpret: <the salient signal detected>
//	Decide:    <the chosen next step>
//
// The interpretation and decision are derived deterministically from the same
// keyword signals the template engine uses, so the two implementations agree on
// *what* they conclude while differing in *how* the thought is rendered. This is
// the "show your working" counterpart to the terse templated reflection.
type ReasoningLoopInnerMonologue struct{}

// Reflect produces a three-step reasoning-loop reflection over contextJSON.
func (m *ReasoningLoopInnerMonologue) Reflect(ctx context.Context, contextJSON string) (SelfReflection, error) {
	if err := ctx.Err(); err != nil {
		return SelfReflection{}, err
	}
	summary := summariseContext(contextJSON)
	signal := interpretSignal(contextJSON)
	decision := inferDirection(contextJSON)
	thought := "Observe: " + summary +
		"\nInterpret: " + signal +
		"\nDecide: " + decision + "."
	return SelfReflection{Thought: thought, At: time.Now().UTC()}, nil
}

// interpretSignal names the salient signal behind the direction the loop will
// take, using the same first-match keyword priority as inferDirection so the two
// stay consistent.
func interpretSignal(jsonText string) string {
	lower := strings.ToLower(jsonText)
	switch {
	case strings.Contains(lower, "error"):
		return "a failure signal is present"
	case strings.Contains(lower, "goal"):
		return "an active goal is in focus"
	case strings.Contains(lower, "user"):
		return "the user is awaiting a response"
	default:
		return "the situation is underspecified"
	}
}

// errNilContext is returned when a caller passes the sentinel meaning "no
// context object at all"; retained so behaviour parity with the C# null-guard is
// explicit if a host wants to opt into it.
var errNilContext = errors.New("contextJson required")

// Compile-time assertions.
var (
	_ IInnerMonologue = (*TemplateInnerMonologue)(nil)
	_ IInnerMonologue = (*ReasoningLoopInnerMonologue)(nil)
)
