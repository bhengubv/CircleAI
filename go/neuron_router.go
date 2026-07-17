// neuron_router.go
//
// Concierge router — port of CircleAI.Hosting.Neuron router + gate. Per turn,
// decide whether the always-warm generalist answers or a capability-matched
// specialist should. Cheap heuristics, no model inference.

package circleai

import "strings"

// Organ names which organ answers a turn.
type Organ int

const (
	// OrganGeneralist is the always-warm generalist (the floor — never evicted).
	OrganGeneralist Organ = iota
	// OrganSpecialist is a capability-matched specialist in the second slot.
	OrganSpecialist
)

// RouteContext is the input the concierge classifies for a single turn.
type RouteContext struct {
	Query    string
	HasImage bool
}

// RouteDecision is the concierge's per-turn decision.
type RouteDecision struct {
	Organ      Organ
	Capability ChatCapability
	Reason     string
}

// GeneralistDecision routes to the always-warm generalist.
func GeneralistDecision(reason string) RouteDecision {
	return RouteDecision{Organ: OrganGeneralist, Capability: CapDefault, Reason: reason}
}

// SpecialistDecision routes to a capability-matched specialist.
func SpecialistDecision(capability ChatCapability, reason string) RouteDecision {
	return RouteDecision{Organ: OrganSpecialist, Capability: capability, Reason: reason}
}

// INeuronRouter is the concierge's decision layer. Mirrors INeuronRouter.
type INeuronRouter interface {
	Route(ctx RouteContext) RouteDecision
}

// NeuronGate is the guardrail checkpoint. A nil predicate applies no veto — the
// honest default. Mirrors NeuronGate.
type NeuronGate struct {
	allowSpecialist func(query string) bool
}

// NewNeuronGate builds a gate. allowSpecialist may be nil (no veto).
func NewNeuronGate(allowSpecialist func(query string) bool) *NeuronGate {
	return &NeuronGate{allowSpecialist: allowSpecialist}
}

// Apply returns the effective decision after applying the guardrail.
func (g *NeuronGate) Apply(d RouteDecision, ctx RouteContext) RouteDecision {
	if d.Organ == OrganSpecialist && g.allowSpecialist != nil && !g.allowSpecialist(ctx.Query) {
		return GeneralistDecision("gate: specialist vetoed -> generalist")
	}
	return d
}

// neuronReasoningCues are lowercase substrings signalling a reasoning turn.
var neuronReasoningCues = []string{
	"prove", "solve", "calculate", "derive", "algorithm", "complexity",
	"debug", "stack trace", "refactor", "regex", "step by step",
	"step-by-step", "theorem", "equation", "big-o", "big o",
}

// HeuristicNeuronRouter is the default router: modality (image -> vision), length
// (long -> long-context), and reasoning cues (-> reasoning); else the generalist.
// Mirrors HeuristicNeuronRouter.
type HeuristicNeuronRouter struct {
	gate             *NeuronGate
	longContextChars int
}

// NewHeuristicNeuronRouter builds the default router. A nil gate uses a no-veto
// gate; longContextChars <= 0 uses 4000.
func NewHeuristicNeuronRouter(gate *NeuronGate, longContextChars int) *HeuristicNeuronRouter {
	if gate == nil {
		gate = NewNeuronGate(nil)
	}
	if longContextChars <= 0 {
		longContextChars = 4000
	}
	return &HeuristicNeuronRouter{gate: gate, longContextChars: longContextChars}
}

// Route classifies a turn and applies the gate.
func (r *HeuristicNeuronRouter) Route(ctx RouteContext) RouteDecision {
	return r.gate.Apply(r.classify(ctx), ctx)
}

func (r *HeuristicNeuronRouter) classify(ctx RouteContext) RouteDecision {
	// 1. An image attachment needs a vision model.
	if ctx.HasImage {
		return SpecialistDecision(CapVision, "image attached -> vision specialist")
	}

	// 2. A very long prompt needs a long-context model.
	if len(ctx.Query) >= r.longContextChars {
		return SpecialistDecision(CapLongContext, "long prompt -> long-context specialist")
	}

	// 3. Reasoning / coding cues want an explicit reasoning model.
	lower := strings.ToLower(ctx.Query)
	for _, cue := range neuronReasoningCues {
		if strings.Contains(lower, cue) {
			return SpecialistDecision(CapReasoning, "reasoning cue '"+cue+"' -> reasoning specialist")
		}
	}

	// 4. Everything else: the always-warm generalist.
	return GeneralistDecision("no specialist cue -> generalist")
}
