// layer_streaming.go
//
// Ports CircleAI.Inference.LayerStreamingInference (LayerStreamingInference.cs):
//   LayerWeightShard, LayerStreamingPlan, LayerActivations,
//   ILayerStreamingRunner, NullLayerStreamingRunner, LayerStreamingOrchestrator,
//   LayerShardDiscovery.
//
// (3.3.0) Layer-by-layer streaming inference — the AirLLM idea: load one
// transformer layer's weights at a time, run forward, save activations, evict,
// load the next. Lets a 70B model fit on a 4 GB device at the cost of disk
// bandwidth per token. The MNN/CUDA glue is host-supplied via
// ILayerStreamingRunner; this file is the contract + null default + orchestrator
// + on-disk shard discovery.

package circleai

import (
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
)

// LayerWeightShard is one transformer layer's weights packed for streaming.
// Ports LayerWeightShard.
type LayerWeightShard struct {
	// LayerIndex is the 0-based transformer layer index.
	LayerIndex int
	// WeightShardPath is the on-disk path to this layer's tensor shard.
	WeightShardPath string
	// ApproxBytes is the shard size, for memory accounting.
	ApproxBytes int64
}

// LayerStreamingPlan is a layer-streaming model plan. Ports LayerStreamingPlan.
type LayerStreamingPlan struct {
	ModelID              string
	TotalLayers          int
	Shards               []LayerWeightShard
	ApproxParameterBytes int64
}

// LayerActivations is one layer's hidden-state output after forward. Ports
// LayerActivations.
type LayerActivations struct {
	LayerIndex int
	Hidden     []float32
}

// ILayerStreamingRunner is the host-supplied per-layer runner (load + forward +
// evict). Ports ILayerStreamingRunner.
type ILayerStreamingRunner interface {
	BackendID() string
	IsAvailable() bool

	// RunLayer runs a forward pass for one layer and returns its hidden states.
	RunLayer(ctx context.Context, shard LayerWeightShard, inputHidden []float32) (LayerActivations, error)

	// Evict drops the layer from RAM after forward.
	Evict(ctx context.Context, layerIndex int) error
}

// ErrNoLayerStreamingRunner is returned by NullLayerStreamingRunner.RunLayer.
var ErrNoLayerStreamingRunner = errors.New(
	"no ILayerStreamingRunner is wired. Register one (CircleAI.Inference.Native.AirLlm) to enable layer-streaming")

// NullLayerStreamingRunner is the drop-in default that fails on use. Ports
// NullLayerStreamingRunner.
type NullLayerStreamingRunner struct{}

// NullLayerStreamingRunnerInstance mirrors NullLayerStreamingRunner.Instance.
var NullLayerStreamingRunnerInstance = NullLayerStreamingRunner{}

// BackendID returns "null".
func (NullLayerStreamingRunner) BackendID() string { return "null" }

// IsAvailable is always false.
func (NullLayerStreamingRunner) IsAvailable() bool { return false }

// RunLayer always fails.
func (NullLayerStreamingRunner) RunLayer(context.Context, LayerWeightShard, []float32) (LayerActivations, error) {
	return LayerActivations{}, ErrNoLayerStreamingRunner
}

// Evict is a no-op.
func (NullLayerStreamingRunner) Evict(context.Context, int) error { return nil }

// LayerStreamingOrchestrator drives a full forward pass layer by layer. Ports
// LayerStreamingOrchestrator.
type LayerStreamingOrchestrator struct {
	runner ILayerStreamingRunner
}

// NewLayerStreamingOrchestrator builds an orchestrator over a runner.
func NewLayerStreamingOrchestrator(runner ILayerStreamingRunner) (*LayerStreamingOrchestrator, error) {
	if runner == nil {
		return nil, errors.New("runner is required")
	}
	return &LayerStreamingOrchestrator{runner: runner}, nil
}

// Forward streams every layer in plan, evicting after each, and returns the
// final hidden state. onLayerComplete (nil allowed) fires after each layer so
// callers can update progress. Ports ForwardAsync (context cancellation between
// layers preserved). Requires at least one shard.
func (o *LayerStreamingOrchestrator) Forward(
	ctx context.Context,
	plan LayerStreamingPlan,
	initialHidden []float32,
	onLayerComplete func(LayerActivations),
) (LayerActivations, error) {
	if len(plan.Shards) == 0 {
		return LayerActivations{}, errors.New("plan has no layer shards")
	}

	hidden := initialHidden
	var last LayerActivations
	for _, shard := range plan.Shards {
		if err := ctx.Err(); err != nil {
			return LayerActivations{}, err
		}
		act, err := o.runner.RunLayer(ctx, shard, hidden)
		if err != nil {
			return LayerActivations{}, err
		}
		last = act
		hidden = act.Hidden
		if onLayerComplete != nil {
			onLayerComplete(last)
		}
		if err := o.runner.Evict(ctx, shard.LayerIndex); err != nil {
			return LayerActivations{}, err
		}
	}
	return last, nil
}

// DiscoverLayerShards scans modelDirectory for files named "layer_NNN.*" and
// builds a LayerStreamingPlan sorted by layer index. Ports LayerShardDiscovery.Discover.
func DiscoverLayerShards(modelID, modelDirectory string) (LayerStreamingPlan, error) {
	if strings.TrimSpace(modelID) == "" {
		return LayerStreamingPlan{}, errors.New("modelId required")
	}
	info, err := os.Stat(modelDirectory)
	if err != nil || !info.IsDir() {
		return LayerStreamingPlan{}, fmt.Errorf("model directory not found: %s", modelDirectory)
	}

	entries, err := os.ReadDir(modelDirectory)
	if err != nil {
		return LayerStreamingPlan{}, err
	}

	shards := make([]LayerWeightShard, 0)
	var total int64
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		name := e.Name()
		if !strings.HasPrefix(name, "layer_") {
			continue
		}
		stem := name
		if dot := strings.LastIndexByte(stem, '.'); dot >= 0 {
			stem = stem[:dot]
		}
		underscoreIdx := strings.IndexByte(stem, '_')
		if underscoreIdx < 0 {
			continue
		}
		index, perr := strconv.Atoi(stem[underscoreIdx+1:])
		if perr != nil {
			continue
		}
		fi, ierr := e.Info()
		if ierr != nil {
			continue
		}
		size := fi.Size()
		shards = append(shards, LayerWeightShard{
			LayerIndex:      index,
			WeightShardPath: filepath.Join(modelDirectory, name),
			ApproxBytes:     size,
		})
		total += size
	}

	sort.Slice(shards, func(i, j int) bool { return shards[i].LayerIndex < shards[j].LayerIndex })
	return LayerStreamingPlan{
		ModelID:              modelID,
		TotalLayers:          len(shards),
		Shards:               shards,
		ApproxParameterBytes: total,
	}, nil
}

var _ ILayerStreamingRunner = NullLayerStreamingRunner{}
