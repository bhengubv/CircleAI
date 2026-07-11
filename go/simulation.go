// simulation.go
//
// Ports CircleAI.Simulation:
//   GraphNode  -> SimGraphNode   (GraphNode.cs)
//   GraphEdge  -> SimGraphEdge   (GraphEdge.cs)
//   KnowledgeGraph -> SimKnowledgeGraph (KnowledgeGraph.cs)
//   IGraphBuilder (IGraphBuilder.cs), ISimulationEngine (ISimulationEngine.cs)
//   SimulationScenario / ScenarioKind (SimulationScenario.cs)
//   SimulationResult / SimulationOutcome (SimulationResult.cs)
//   EpisodicGraphExtractor (EpisodicGraphExtractor.cs)
//   MiroFishAdapter + LocalSimulationEngine (MiroFishAdapter.cs / NetworkHealthSimulator.cs)
//   NetworkHealthSimulator (NetworkHealthSimulator.cs)
//   ThreatPropagationScenario (ThreatPropagationScenario.cs)
//
// NOTE: the graph value types are prefixed Sim* because the flat package
// already has a DIFFERENT KnowledgeGraph/GraphNode (memory_graph.go — the
// HippoRAG personal-knowledge graph). These are the Simulation module's
// entity-relationship graph (nodes + weighted directed edges). The diffusion
// math, thresholds, and step-count table are ported verbatim.

package circleai

import (
	"context"
	"fmt"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/google/uuid"
)

// SimGraphNode is a node in the simulation knowledge graph. Ports GraphNode.
type SimGraphNode struct {
	ID          uuid.UUID
	Label       string
	Kind        string // "person" | "topic" | "app" | "event" | "system"
	Properties  map[string]string
	ExtractedAt time.Time
}

// NewSimGraphNode creates a node with a fresh id + now timestamp. Ports
// GraphNode.Create.
func NewSimGraphNode(label, kind string, properties map[string]string) SimGraphNode {
	if properties == nil {
		properties = map[string]string{}
	}
	return SimGraphNode{
		ID:          uuid.New(),
		Label:       label,
		Kind:        kind,
		Properties:  properties,
		ExtractedAt: time.Now().UTC(),
	}
}

// SimGraphEdge is a directed, weighted edge. Ports GraphEdge.
type SimGraphEdge struct {
	ID        uuid.UUID
	SourceID  uuid.UUID
	TargetID  uuid.UUID
	Relation  string
	Weight    float32 // 0.0–1.0
	CreatedAt time.Time
}

// NewSimGraphEdge creates an edge with a fresh id + now timestamp; weight is
// clamped to [0,1]. Ports GraphEdge.Create.
func NewSimGraphEdge(sourceID, targetID uuid.UUID, relation string, weight float32) SimGraphEdge {
	return SimGraphEdge{
		ID:        uuid.New(),
		SourceID:  sourceID,
		TargetID:  targetID,
		Relation:  relation,
		Weight:    clamp32(weight, 0, 1),
		CreatedAt: time.Now().UTC(),
	}
}

// SimKnowledgeGraph is an in-memory entity-relationship graph. Ports
// KnowledgeGraph. Concurrency-safe.
type SimKnowledgeGraph struct {
	mu    sync.Mutex
	nodes map[uuid.UUID]SimGraphNode
	edges map[uuid.UUID]SimGraphEdge
}

// NewSimKnowledgeGraph constructs an empty graph.
func NewSimKnowledgeGraph() *SimKnowledgeGraph {
	return &SimKnowledgeGraph{
		nodes: make(map[uuid.UUID]SimGraphNode),
		edges: make(map[uuid.UUID]SimGraphEdge),
	}
}

// Nodes returns a snapshot of nodes keyed by id. Ports Nodes.
func (g *SimKnowledgeGraph) Nodes() map[uuid.UUID]SimGraphNode {
	g.mu.Lock()
	defer g.mu.Unlock()
	out := make(map[uuid.UUID]SimGraphNode, len(g.nodes))
	for k, v := range g.nodes {
		out[k] = v
	}
	return out
}

// Edges returns a snapshot of edges keyed by id. Ports Edges.
func (g *SimKnowledgeGraph) Edges() map[uuid.UUID]SimGraphEdge {
	g.mu.Lock()
	defer g.mu.Unlock()
	out := make(map[uuid.UUID]SimGraphEdge, len(g.edges))
	for k, v := range g.edges {
		out[k] = v
	}
	return out
}

// AddNode adds or replaces a node (last-write wins). Ports AddNode.
func (g *SimKnowledgeGraph) AddNode(node SimGraphNode) {
	g.mu.Lock()
	g.nodes[node.ID] = node
	g.mu.Unlock()
}

// AddEdge adds or replaces an edge (last-write wins). Ports AddEdge.
func (g *SimKnowledgeGraph) AddEdge(edge SimGraphEdge) {
	g.mu.Lock()
	g.edges[edge.ID] = edge
	g.mu.Unlock()
}

// EdgesFor returns all edges incident to nodeID. Ports EdgesFor.
func (g *SimKnowledgeGraph) EdgesFor(nodeID uuid.UUID) []SimGraphEdge {
	g.mu.Lock()
	defer g.mu.Unlock()
	out := make([]SimGraphEdge, 0)
	for _, e := range g.edges {
		if e.SourceID == nodeID || e.TargetID == nodeID {
			out = append(out, e)
		}
	}
	return out
}

// ReachableFrom returns nodes reachable from startID by BFS. Ports
// ReachableFrom.
func (g *SimKnowledgeGraph) ReachableFrom(startID uuid.UUID) []SimGraphNode {
	visited := make(map[uuid.UUID]struct{})
	queue := []uuid.UUID{startID}
	result := make([]SimGraphNode, 0)
	for len(queue) > 0 {
		current := queue[0]
		queue = queue[1:]
		if _, seen := visited[current]; seen {
			continue
		}
		visited[current] = struct{}{}
		g.mu.Lock()
		node, ok := g.nodes[current]
		g.mu.Unlock()
		if ok {
			result = append(result, node)
		}
		for _, edge := range g.EdgesFor(current) {
			next := edge.TargetID
			if edge.SourceID != current {
				next = edge.SourceID
			}
			if _, seen := visited[next]; !seen {
				queue = append(queue, next)
			}
		}
	}
	return result
}

// Merge folds another graph in (last-write wins). Ports Merge.
func (g *SimKnowledgeGraph) Merge(other *SimKnowledgeGraph) {
	if other == nil {
		panic("other must not be nil")
	}
	other.mu.Lock()
	nodes := make([]SimGraphNode, 0, len(other.nodes))
	edges := make([]SimGraphEdge, 0, len(other.edges))
	for _, n := range other.nodes {
		nodes = append(nodes, n)
	}
	for _, e := range other.edges {
		edges = append(edges, e)
	}
	other.mu.Unlock()
	g.mu.Lock()
	for _, n := range nodes {
		g.nodes[n.ID] = n
	}
	for _, e := range edges {
		g.edges[e.ID] = e
	}
	g.mu.Unlock()
}

// IGraphBuilder builds a SimKnowledgeGraph from episodic entries. Ports
// IGraphBuilder.
type IGraphBuilder interface {
	Build(entries []EpisodicMemoryEntry) *SimKnowledgeGraph
}

// ISimulationEngine runs a scenario against a graph. Ports ISimulationEngine.
type ISimulationEngine interface {
	Run(ctx context.Context, scenario SimulationScenario, graph *SimKnowledgeGraph) (SimulationResult, error)
}

// ---------------------------------------------------------------------------
// SimulationScenario / ScenarioKind
// ---------------------------------------------------------------------------

// ScenarioKind enumerates simulation scenario kinds. Ports ScenarioKind
// (stable ordinals in declaration order).
type ScenarioKind int

const (
	// ScenarioConfigurationShift models a configuration key change.
	ScenarioConfigurationShift ScenarioKind = 0
	// ScenarioDataPipelineChange models a new data-sharing pipeline.
	ScenarioDataPipelineChange ScenarioKind = 1
	// ScenarioSoftwareDeployment models a code deployment propagating.
	ScenarioSoftwareDeployment ScenarioKind = 2
	// ScenarioSecurityPatch models a security patch propagating.
	ScenarioSecurityPatch ScenarioKind = 3
	// ScenarioThreatPropagation models an unmitigated runtime threat spreading.
	ScenarioThreatPropagation ScenarioKind = 4
)

// SimulationScenario describes one scenario. Ports SimulationScenario.
type SimulationScenario struct {
	ID          uuid.UUID
	Kind        ScenarioKind
	Description string
	Parameters  map[string]string
	StepCount   int
	CreatedAt   time.Time
}

// NewSimulationScenario creates a scenario with a fresh id + now timestamp.
// Ports SimulationScenario.Create (steps default 10).
func NewSimulationScenario(kind ScenarioKind, description string, parameters map[string]string, steps int) SimulationScenario {
	if parameters == nil {
		parameters = map[string]string{}
	}
	return SimulationScenario{
		ID:          uuid.New(),
		Kind:        kind,
		Description: description,
		Parameters:  parameters,
		StepCount:   steps,
		CreatedAt:   time.Now().UTC(),
	}
}

// ---------------------------------------------------------------------------
// SimulationResult / SimulationOutcome
// ---------------------------------------------------------------------------

// SimulationOutcome is the overall health outcome. Ports SimulationOutcome
// (stable ordinals: Healthy=0, Degraded=1, Critical=2, Unknown=3).
type SimulationOutcome int

const (
	// SimulationOutcomeHealthy = health >= 0.8.
	SimulationOutcomeHealthy SimulationOutcome = 0
	// SimulationOutcomeDegraded = 0.5 <= health < 0.8.
	SimulationOutcomeDegraded SimulationOutcome = 1
	// SimulationOutcomeCritical = 0.2 <= health < 0.5.
	SimulationOutcomeCritical SimulationOutcome = 2
	// SimulationOutcomeUnknown = health < 0.2.
	SimulationOutcomeUnknown SimulationOutcome = 3
)

// SimulationResult captures one run's outcome. Ports SimulationResult.
type SimulationResult struct {
	ScenarioID      uuid.UUID
	Outcome         SimulationOutcome
	HealthScore     float32
	Findings        []string
	Recommendations []string
	StepsRun        int
	CompletedAt     time.Time
}

// ---------------------------------------------------------------------------
// EpisodicGraphExtractor
// ---------------------------------------------------------------------------

// EpisodicGraphExtractor builds a graph from episodic entries via keyword + tag
// heuristics. Ports EpisodicGraphExtractor.
type EpisodicGraphExtractor struct{}

// Build extracts a SimKnowledgeGraph from entries. Ports Build.
func (EpisodicGraphExtractor) Build(entries []EpisodicMemoryEntry) *SimKnowledgeGraph {
	graph := NewSimKnowledgeGraph()
	appNodes := make(map[string]SimGraphNode)
	topicNodes := make(map[string]SimGraphNode)
	var prev *SimGraphNode
	var prevTime time.Time
	prevSet := false

	// Order by RecordedAtUtc ascending (stable).
	ordered := append([]EpisodicMemoryEntry(nil), entries...)
	sort.SliceStable(ordered, func(i, j int) bool { return ordered[i].RecordedAtUTC.Before(ordered[j].RecordedAtUTC) })

	for _, entry := range ordered {
		label := entry.UserText
		if runes := []rune(entry.UserText); len(runes) > 60 {
			label = string(runes[:60])
		}
		evNode := NewSimGraphNode(label, "event", map[string]string{"episode_id": entry.ID.String()})
		graph.AddNode(evNode)

		// App context → node + edge.
		if entry.AppContext != nil && strings.TrimSpace(*entry.AppContext) != "" {
			key := strings.ToLower(*entry.AppContext)
			appNode, ok := appNodes[key]
			if !ok {
				appNode = NewSimGraphNode(*entry.AppContext, "app", nil)
				appNodes[key] = appNode
				graph.AddNode(appNode)
			}
			graph.AddEdge(NewSimGraphEdge(evNode.ID, appNode.ID, "occurred_in", 1.0))
		}

		// Tags → topic nodes + edges. Deterministic order.
		if entry.Tags != nil {
			tagKeys := make([]string, 0, len(entry.Tags))
			for k := range entry.Tags {
				tagKeys = append(tagKeys, k)
			}
			sort.Strings(tagKeys)
			for _, tag := range tagKeys {
				key := strings.ToLower(tag)
				topicNode, ok := topicNodes[key]
				if !ok {
					topicNode = NewSimGraphNode(tag, "topic", nil)
					topicNodes[key] = topicNode
					graph.AddNode(topicNode)
				}
				graph.AddEdge(NewSimGraphEdge(evNode.ID, topicNode.ID, "tagged_with", 1.0))
			}
		}

		// Temporal sequence — connect to previous event within 1 hour.
		if prevSet && entry.RecordedAtUTC.Sub(prevTime).Hours() <= 1.0 {
			graph.AddEdge(NewSimGraphEdge(prev.ID, evNode.ID, "followed_by", 0.5))
		}

		ev := evNode
		prev = &ev
		prevTime = entry.RecordedAtUTC
		prevSet = true
	}
	return graph
}

var _ IGraphBuilder = EpisodicGraphExtractor{}

// ---------------------------------------------------------------------------
// LocalSimulationEngine + MiroFishAdapter
// ---------------------------------------------------------------------------

const (
	simDecayPerStep        = float32(0.01)
	simHighImpactThreshold = float32(0.7)
)

// localSimulationEngine is the deterministic graph-diffusion fallback engine.
// Ports the internal LocalSimulationEngine.
type localSimulationEngine struct{}

// Run executes the diffusion model. Ports RunAsync.
func (localSimulationEngine) Run(ctx context.Context, scenario SimulationScenario, graph *SimKnowledgeGraph) (SimulationResult, error) {
	if err := ctx.Err(); err != nil {
		return SimulationResult{}, err
	}
	health := float32(1.0)
	highImpact := make(map[string]struct{})
	nodes := graph.Nodes()
	edges := graph.Edges()

	// Deterministic edge iteration order for stable findings.
	edgeList := make([]SimGraphEdge, 0, len(edges))
	for _, e := range edges {
		edgeList = append(edgeList, e)
	}
	sort.SliceStable(edgeList, func(i, j int) bool { return edgeList[i].ID.String() < edgeList[j].ID.String() })

	for step := 0; step < scenario.StepCount && health > 0; step++ {
		for _, edge := range edgeList {
			health -= (1 - edge.Weight) * simDecayPerStep
			if edge.Weight >= simHighImpactThreshold {
				if src, ok := nodes[edge.SourceID]; ok {
					highImpact[src.Label] = struct{}{}
				}
			}
		}
		if err := ctx.Err(); err != nil {
			return SimulationResult{}, err
		}
	}

	health = clamp32(health, 0, 1)

	var outcome SimulationOutcome
	switch {
	case health >= 0.8:
		outcome = SimulationOutcomeHealthy
	case health >= 0.5:
		outcome = SimulationOutcomeDegraded
	case health >= 0.2:
		outcome = SimulationOutcomeCritical
	default:
		outcome = SimulationOutcomeUnknown
	}

	var findings []string
	if len(highImpact) > 0 {
		labels := make([]string, 0, len(highImpact))
		for l := range highImpact {
			labels = append(labels, l)
		}
		sort.Strings(labels)
		findings = make([]string, 0, len(labels))
		for _, l := range labels {
			findings = append(findings, "High-impact node detected: "+l)
		}
	} else {
		findings = []string{"No high-impact nodes detected."}
	}

	var recs []string
	if outcome == SimulationOutcomeDegraded || outcome == SimulationOutcomeCritical {
		recs = []string{"Review high-weight edges before deployment.", "Consider incremental rollout."}
	} else {
		recs = []string{"Network health nominal — proceed with deployment."}
	}

	return SimulationResult{
		ScenarioID:      scenario.ID,
		Outcome:         outcome,
		HealthScore:     health,
		Findings:        findings,
		Recommendations: recs,
		StepsRun:        scenario.StepCount,
		CompletedAt:     time.Now().UTC(),
	}, nil
}

// MiroFishAdapter prefers an external engine, falling back to the local
// diffusion engine. Ports MiroFishAdapter.
type MiroFishAdapter struct {
	inner ISimulationEngine
}

// NewMiroFishAdapter constructs the adapter; a nil externalEngine uses the
// built-in local engine. Ports the ctor.
func NewMiroFishAdapter(externalEngine ISimulationEngine) *MiroFishAdapter {
	if externalEngine == nil {
		externalEngine = localSimulationEngine{}
	}
	return &MiroFishAdapter{inner: externalEngine}
}

// Run delegates to the inner engine. Ports RunAsync.
func (a *MiroFishAdapter) Run(ctx context.Context, scenario SimulationScenario, graph *SimKnowledgeGraph) (SimulationResult, error) {
	return a.inner.Run(ctx, scenario, graph)
}

var (
	_ ISimulationEngine = localSimulationEngine{}
	_ ISimulationEngine = (*MiroFishAdapter)(nil)
)

// ---------------------------------------------------------------------------
// NetworkHealthSimulator
// ---------------------------------------------------------------------------

// NetworkHealthSimulator extracts a graph from episodic memory and forecasts a
// scenario's health impact. Ports NetworkHealthSimulator.
type NetworkHealthSimulator struct {
	extractor IGraphBuilder
	engine    ISimulationEngine
}

// NewNetworkHealthSimulator constructs the simulator; nil extractor/engine
// default to EpisodicGraphExtractor and MiroFishAdapter. Ports the ctor.
func NewNetworkHealthSimulator(extractor IGraphBuilder, engine ISimulationEngine) *NetworkHealthSimulator {
	if extractor == nil {
		extractor = EpisodicGraphExtractor{}
	}
	if engine == nil {
		engine = NewMiroFishAdapter(nil)
	}
	return &NetworkHealthSimulator{extractor: extractor, engine: engine}
}

// Forecast builds a graph from history and runs scenario through the engine.
// Ports ForecastAsync.
func (s *NetworkHealthSimulator) Forecast(ctx context.Context, history []EpisodicMemoryEntry, scenario SimulationScenario) (SimulationResult, error) {
	graph := s.extractor.Build(history)
	return s.engine.Run(ctx, scenario, graph)
}

// ---------------------------------------------------------------------------
// ThreatPropagationScenario
// ---------------------------------------------------------------------------

// threatStepCountFor returns the diffusion depth for a threat vector. Ports
// ThreatPropagationScenario.StepCountFor.
func threatStepCountFor(vector ThreatVector) int {
	switch vector {
	case ThreatVectorNetworkPivot:
		return 30
	case ThreatVectorControlFlowDrift:
		return 25
	case ThreatVectorPrivilegeEscalation:
		return 25
	case ThreatVectorStateCorruption:
		return 20
	case ThreatVectorMemoryAnomaly:
		return 15
	case ThreatVectorAgentPatchRejected:
		return 15
	case ThreatVectorBiometricSpoofAttempt:
		return 12
	default:
		return 10
	}
}

// ThreatPropagationScenarioFromAnomalySignal builds a ThreatPropagation
// SimulationScenario from an anomaly signal. Ports
// ThreatPropagationScenario.FromAnomalySignal. A nil stepOverride derives the
// step count from the vector.
func ThreatPropagationScenarioFromAnomalySignal(signal *AnomalySignal, stepOverride *int) SimulationScenario {
	if signal == nil {
		panic("signal must not be nil")
	}
	parameters := make(map[string]string, len(signal.Evidence)+5)
	for k, v := range signal.Evidence {
		parameters[k] = v
	}
	parameters["signal_id"] = signal.ID
	parameters["vector"] = threatVectorName(signal.Vector)
	parameters["confidence"] = strconv.FormatFloat(float64(signal.Confidence), 'f', 3, 64)
	parameters["affected_module"] = signal.AffectedModule
	parameters["detected_at"] = formatRoundtrip(signal.DetectedAt)

	steps := threatStepCountFor(signal.Vector)
	if stepOverride != nil {
		steps = *stepOverride
	}

	description := fmt.Sprintf("threat-propagation: %s in %s (confidence %s)",
		threatVectorName(signal.Vector), signal.AffectedModule, formatPercent0(float64(signal.Confidence)))

	return SimulationScenario{
		ID:          uuid.New(),
		Kind:        ScenarioThreatPropagation,
		Description: description,
		Parameters:  parameters,
		StepCount:   steps,
		CreatedAt:   time.Now().UTC(),
	}
}

// (formatPercent0 is shared with security_watchdog.go — reused here.)
