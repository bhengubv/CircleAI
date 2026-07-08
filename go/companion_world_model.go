// companion_world_model.go
//
// Ported from CircleAI.Companion (HerJarvisContracts.cs + HerJarvisRealImplementations.cs)
// — the C# reference:
//   - IWorldModel                     (contract 5)
//   - CausalPrediction                (record)
//   - FrequencyWorldModel             (concrete: frequency P(outcome|observation))
//   - BayesianWorldModel              (concrete: Laplace-smoothed Bayesian variant)
//
// A world model learns P(outcome | observation) from registered evidence and,
// given a scenario, predicts the most likely causal outcome. In-memory,
// deterministic. The C# ValueTask<CausalPrediction> is rendered as a synchronous
// (CausalPrediction, error) return that honours ctx cancellation.

package circleai

import (
	"context"
	"encoding/json"
	"errors"
	"math"
	"sort"
	"strings"
	"sync"
)

// CausalPrediction is a predicted outcome with its probability and the
// observations that supported it. Ported from the C# record
// CausalPrediction(string Outcome, double Probability, IReadOnlyList<string> SupportingFactors).
type CausalPrediction struct {
	Outcome           string
	Probability       float64
	SupportingFactors []string
}

// IWorldModel is the world-model + causal-reasoning contract (C# IWorldModel).
// PredictAsync takes a scenario as a flat JSON object and returns the most
// likely outcome given the evidence observed so far.
type IWorldModel interface {
	Predict(ctx context.Context, scenarioJSON string) (CausalPrediction, error)
}

// extractObservations parses a flat JSON object into "name=value" observation
// tokens, exactly as the C# FrequencyWorldModel.ExtractObservations does. A
// non-object or unparseable payload yields no observations (never an error).
func extractObservations(scenarioJSON string) []string {
	var root map[string]json.RawMessage
	dec := json.NewDecoder(strings.NewReader(scenarioJSON))
	dec.UseNumber()
	if err := dec.Decode(&root); err != nil {
		return []string{}
	}
	// Preserve source-object order the way System.Text.Json enumerates it.
	names := orderedJSONKeys(scenarioJSON)
	hits := make([]string, 0, len(root))
	for _, name := range names {
		raw, ok := root[name]
		if !ok {
			continue
		}
		hits = append(hits, name+"="+jsonElementToString(raw))
	}
	return hits
}

// orderedJSONKeys returns the top-level keys of a JSON object in document order.
// System.Text.Json's EnumerateObject yields properties in source order, so the
// SupportingFactors list is order-stable against the C# reference.
func orderedJSONKeys(objJSON string) []string {
	dec := json.NewDecoder(strings.NewReader(objJSON))
	tok, err := dec.Token()
	if err != nil {
		return nil
	}
	if d, ok := tok.(json.Delim); !ok || d != '{' {
		return nil
	}
	var keys []string
	for dec.More() {
		keyTok, err := dec.Token()
		if err != nil {
			return keys
		}
		key, ok := keyTok.(string)
		if !ok {
			return keys
		}
		keys = append(keys, key)
		// Skip the value (which may itself be an object/array).
		if err := skipJSONValue(dec); err != nil {
			return keys
		}
	}
	return keys
}

// skipJSONValue consumes exactly one JSON value from dec, descending into nested
// objects/arrays so the decoder is positioned at the next key.
func skipJSONValue(dec *json.Decoder) error {
	tok, err := dec.Token()
	if err != nil {
		return err
	}
	if d, ok := tok.(json.Delim); ok && (d == '{' || d == '[') {
		depth := 1
		for depth > 0 {
			t, err := dec.Token()
			if err != nil {
				return err
			}
			if dd, ok := t.(json.Delim); ok {
				if dd == '{' || dd == '[' {
					depth++
				} else {
					depth--
				}
			}
		}
	}
	return nil
}

// jsonElementToString mirrors System.Text.Json's JsonElement.ToString() for the
// scalar/compound kinds used as observation values:
//   - string  -> the raw string (no quotes)
//   - number  -> the source token
//   - bool    -> "True"/"False" (JsonElement.ToString capitalises)
//   - null    -> "" (JsonValueKind.Null renders as empty)
//   - object/array -> the minified JSON text
func jsonElementToString(raw json.RawMessage) string {
	trimmed := strings.TrimSpace(string(raw))
	if trimmed == "" {
		return ""
	}
	switch trimmed[0] {
	case '"':
		var s string
		if err := json.Unmarshal(raw, &s); err == nil {
			return s
		}
		return trimmed
	case '{', '[':
		return trimmed
	}
	switch trimmed {
	case "true":
		return "True"
	case "false":
		return "False"
	case "null":
		return ""
	}
	return trimmed // number
}

// FrequencyWorldModel learns P(outcome|observation) from registered evidence via
// raw co-occurrence counts. Ported from the C# FrequencyWorldModel. Observation
// keys are matched case-insensitively (StringComparer.OrdinalIgnoreCase).
type FrequencyWorldModel struct {
	mu       sync.Mutex
	counts   map[string]map[string]int64 // lowered(observation) -> lowered(outcome) -> count; display keys preserved separately
	outNames map[string]string           // lowered(outcome) -> display outcome
}

// NewFrequencyWorldModel returns an empty FrequencyWorldModel.
func NewFrequencyWorldModel() *FrequencyWorldModel {
	return &FrequencyWorldModel{
		counts:   make(map[string]map[string]int64),
		outNames: make(map[string]string),
	}
}

// Observe records that, when these observations occurred, the given outcome was
// seen. Mirrors the C# Observe(IEnumerable<string> observations, string outcome).
func (m *FrequencyWorldModel) Observe(observations []string, outcome string) error {
	if strings.TrimSpace(outcome) == "" {
		return errors.New("outcome required")
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	outKey := strings.ToLower(outcome)
	if _, ok := m.outNames[outKey]; !ok {
		m.outNames[outKey] = outcome
	}
	for _, obs := range observations {
		key := strings.ToLower(obs)
		inner, ok := m.counts[key]
		if !ok {
			inner = make(map[string]int64)
			m.counts[key] = inner
		}
		inner[outKey]++
	}
	return nil
}

// Predict returns the most likely outcome given the scenario's observations.
// With no matching evidence it returns ("unknown", 0.5, supporters) exactly as
// the C# reference. Ties on count are broken by highest count then by the
// insertion-stable ordering of the tally.
func (m *FrequencyWorldModel) Predict(ctx context.Context, scenarioJSON string) (CausalPrediction, error) {
	if err := ctx.Err(); err != nil {
		return CausalPrediction{}, err
	}
	observations := extractObservations(scenarioJSON)

	m.mu.Lock()
	defer m.mu.Unlock()

	tally := make(map[string]int64)
	tallyOrder := make([]string, 0)
	supporters := make([]string, 0)
	for _, obs := range observations {
		inner, ok := m.counts[strings.ToLower(obs)]
		if !ok {
			continue
		}
		supporters = append(supporters, obs)
		for outKey, v := range inner {
			if _, seen := tally[outKey]; !seen {
				tallyOrder = append(tallyOrder, outKey)
			}
			tally[outKey] += v
		}
	}
	if len(tally) == 0 {
		return CausalPrediction{Outcome: "unknown", Probability: 0.5, SupportingFactors: supporters}, nil
	}
	var total int64
	for _, v := range tally {
		total += v
	}
	// OrderByDescending(kv => kv.Value).First(): highest count wins; deterministic
	// tie-break by the stable discovery order so results are reproducible.
	bestKey := tallyOrder[0]
	bestVal := tally[bestKey]
	for _, k := range tallyOrder {
		if tally[k] > bestVal {
			bestVal = tally[k]
			bestKey = k
		}
	}
	return CausalPrediction{
		Outcome:           m.outNames[bestKey],
		Probability:       float64(bestVal) / float64(total),
		SupportingFactors: supporters,
	}, nil
}

// BayesianWorldModel is a Laplace-smoothed Bayesian variant of IWorldModel. It
// maintains per-observation likelihoods and a global outcome prior, then scores
// each candidate outcome by the (log) posterior
//
//	log P(outcome) + Σ_obs log P(obs | outcome)
//
// with add-one (Laplace) smoothing so unseen (obs, outcome) pairs never zero out
// a candidate. The reported Probability is the softmax-normalised posterior over
// candidate outcomes. Deterministic and in-memory. This is the "informed prior"
// counterpart to FrequencyWorldModel's raw-count estimate.
type BayesianWorldModel struct {
	mu           sync.Mutex
	outcomeCount map[string]int64            // lowered(outcome) -> times seen (prior)
	obsGivenOut  map[string]map[string]int64 // lowered(outcome) -> lowered(obs) -> count
	outNames     map[string]string           // lowered(outcome) -> display
	vocab        map[string]struct{}         // distinct observation tokens seen
	totalObs     int64
	alpha        float64 // Laplace smoothing constant
}

// NewBayesianWorldModel returns an empty BayesianWorldModel with add-one
// smoothing (alpha = 1.0).
func NewBayesianWorldModel() *BayesianWorldModel {
	return &BayesianWorldModel{
		outcomeCount: make(map[string]int64),
		obsGivenOut:  make(map[string]map[string]int64),
		outNames:     make(map[string]string),
		vocab:        make(map[string]struct{}),
		alpha:        1.0,
	}
}

// Observe records one training example: this outcome was seen together with
// these observations. Increments the outcome prior once and each observation's
// per-outcome likelihood count.
func (m *BayesianWorldModel) Observe(observations []string, outcome string) error {
	if strings.TrimSpace(outcome) == "" {
		return errors.New("outcome required")
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	outKey := strings.ToLower(outcome)
	if _, ok := m.outNames[outKey]; !ok {
		m.outNames[outKey] = outcome
	}
	m.outcomeCount[outKey]++
	m.totalObs++
	inner, ok := m.obsGivenOut[outKey]
	if !ok {
		inner = make(map[string]int64)
		m.obsGivenOut[outKey] = inner
	}
	for _, obs := range observations {
		key := strings.ToLower(obs)
		inner[key]++
		m.vocab[key] = struct{}{}
	}
	return nil
}

// Predict scores every known outcome by its smoothed log-posterior over the
// scenario's observations and returns the argmax with a softmax-normalised
// probability. With no training data it returns ("unknown", 0.5, supporters),
// matching the FrequencyWorldModel fallback.
func (m *BayesianWorldModel) Predict(ctx context.Context, scenarioJSON string) (CausalPrediction, error) {
	if err := ctx.Err(); err != nil {
		return CausalPrediction{}, err
	}
	observations := extractObservations(scenarioJSON)

	m.mu.Lock()
	defer m.mu.Unlock()

	if len(m.outcomeCount) == 0 {
		// Supporters: any observation is a "supporter" only if it exists in the
		// vocabulary; with no training data none do.
		return CausalPrediction{Outcome: "unknown", Probability: 0.5, SupportingFactors: []string{}}, nil
	}

	// Supporters = observations we have any evidence for.
	supporters := make([]string, 0)
	for _, obs := range observations {
		if _, ok := m.vocab[strings.ToLower(obs)]; ok {
			supporters = append(supporters, obs)
		}
	}

	vocabSize := float64(len(m.vocab))
	// Deterministic candidate order: sort outcome keys.
	outKeys := make([]string, 0, len(m.outcomeCount))
	for k := range m.outcomeCount {
		outKeys = append(outKeys, k)
	}
	sort.Strings(outKeys)

	logPost := make([]float64, len(outKeys))
	for i, outKey := range outKeys {
		// Prior: P(outcome) with Laplace smoothing over outcomes.
		prior := (float64(m.outcomeCount[outKey]) + m.alpha) /
			(float64(m.totalObs) + m.alpha*float64(len(m.outcomeCount)))
		lp := math.Log(prior)
		inner := m.obsGivenOut[outKey]
		// Denominator for likelihood: total obs tokens seen for this outcome + smoothing.
		var denom float64
		for _, c := range inner {
			denom += float64(c)
		}
		denom += m.alpha * vocabSize
		for _, obs := range observations {
			key := strings.ToLower(obs)
			c := float64(inner[key])
			lp += math.Log((c + m.alpha) / denom)
		}
		logPost[i] = lp
	}

	// Argmax + softmax normalisation (numerically stabilised by max-subtraction).
	best := 0
	for i := 1; i < len(logPost); i++ {
		if logPost[i] > logPost[best] {
			best = i
		}
	}
	maxLP := logPost[best]
	var sumExp float64
	for _, lp := range logPost {
		sumExp += math.Exp(lp - maxLP)
	}
	prob := 1.0 / sumExp // exp(max-max)=1 in the numerator for the argmax

	return CausalPrediction{
		Outcome:           m.outNames[outKeys[best]],
		Probability:       prob,
		SupportingFactors: supporters,
	}, nil
}

// Compile-time assertions.
var (
	_ IWorldModel = (*FrequencyWorldModel)(nil)
	_ IWorldModel = (*BayesianWorldModel)(nil)
)
