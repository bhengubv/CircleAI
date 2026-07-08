// server_bridge_factory.go
//
// Ports CircleAI.Inference.Server.Endpoints.IBridgeFactory +
// UnconfiguredBridgeFactory (AdminEndpoints.cs) and the composition role of
// CircleAI.Inference.Server.Lifecycle.MnnInferenceBridgeFactory
// (MnnInferenceBridgeFactory.cs) as a working, native-free factory.
//
// IBridgeFactory materialises an IInferenceBridge for a (modelId, backend,
// tier). The default UnconfiguredBridgeFactory refuses every load with a clear
// error (matching C#). LocalInferenceBridgeFactory is the production-shaped
// factory: resolve the model entry from the registry, ensure it is on disk via
// the injected download service, construct a LocalChatGenerator, and wrap it as
// a LocalProcessInferenceBridge — the exact pipeline of MnnInferenceBridgeFactory
// minus the native MNN runtime fetch (injected behind the download seam).

package circleai

import (
	"context"
	"errors"
	"fmt"
	"path/filepath"
	"strings"
)

// IBridgeFactory materialises an IInferenceBridge for a given model + backend +
// tier. Ports CircleAI.Inference.Server.Endpoints.IBridgeFactory.
type IBridgeFactory interface {
	Create(ctx context.Context, modelID string, backend BackendKind, tier CapabilityTier) (IInferenceBridge, error)
}

// ErrUnconfiguredBridgeFactory mirrors the UnconfiguredBridgeFactory exception.
var ErrUnconfiguredBridgeFactory = errors.New(
	"no IBridgeFactory is configured. Register one (e.g. LocalInferenceBridgeFactory) before requesting a model load")

// UnconfiguredBridgeFactory refuses every load. Ports UnconfiguredBridgeFactory.
type UnconfiguredBridgeFactory struct{}

// Create always fails with ErrUnconfiguredBridgeFactory.
func (UnconfiguredBridgeFactory) Create(context.Context, string, BackendKind, CapabilityTier) (IInferenceBridge, error) {
	return nil, ErrUnconfiguredBridgeFactory
}

// LocalInferenceBridgeFactory composes the registry + download service +
// deterministic generator into a working IInferenceBridge. Ports the
// MnnInferenceBridgeFactory pipeline with the native runtime fetch injected
// behind the download seam.
type LocalInferenceBridgeFactory struct {
	registry     *ModelRegistryService
	download     IModelDownloadService
	deviceCaps   DeviceCapabilities
	nativeStatus INativeRuntimeStatus
	responder    LocalResponder // optional; nil → defaultLocalResponder
}

// NewLocalInferenceBridgeFactory builds the factory. registry + download are
// required; deviceCaps is the host view the produced bridges report; nativeStatus
// (nil allowed) receives a prep record after each successful materialisation;
// responder (nil allowed) scripts generator output.
func NewLocalInferenceBridgeFactory(
	registry *ModelRegistryService,
	download IModelDownloadService,
	deviceCaps DeviceCapabilities,
	nativeStatus INativeRuntimeStatus,
	responder LocalResponder,
) (*LocalInferenceBridgeFactory, error) {
	if registry == nil {
		return nil, errors.New("registry is required")
	}
	if download == nil {
		return nil, errors.New("download service is required")
	}
	return &LocalInferenceBridgeFactory{
		registry:     registry,
		download:     download,
		deviceCaps:   deviceCaps,
		nativeStatus: nativeStatus,
		responder:    responder,
	}, nil
}

// Create resolves + ensures the model then wraps a generator as a bridge. Ports
// MnnInferenceBridgeFactory.CreateAsync (native runtime prep collapsed into the
// injected download seam; the deterministic generator replaces the MNN load).
func (f *LocalInferenceBridgeFactory) Create(ctx context.Context, modelID string, backend BackendKind, tier CapabilityTier) (IInferenceBridge, error) {
	if strings.TrimSpace(modelID) == "" {
		return nil, errors.New("modelId is required")
	}

	// 1. Resolve the model entry FIRST (cheap, no network) — fail fast on unknown.
	entry, ok := f.registry.GetLatestModel(modelID)
	if !ok {
		return nil, fmt.Errorf("model '%s' is not in the registry", modelID)
	}

	// 2. Ensure the model is on disk.
	var modelPath string
	if entry.IsBundle() {
		if strings.TrimSpace(entry.Repo) == "" {
			return nil, fmt.Errorf("registry entry for '%s' has BundleFiles but no Repo path — bundle URLs cannot be built", modelID)
		}
		specs := make([]BundleFileSpec, 0, len(entry.BundleFiles))
		for _, bf := range entry.BundleFiles {
			specs = append(specs, BundleFileSpec{Name: bf.Name, Sha256: bf.Sha256, SizeBytes: bf.SizeBytes})
		}
		modelDir, err := f.download.EnsureBundle(ctx, modelID, entry.Repo, specs, nil)
		if err != nil {
			return nil, err
		}
		if svc, isConcrete := f.download.(*ModelDownloadService); isConcrete {
			svc.WriteInstalledManifest(modelDir, modelID, entry.Version, entry.Repo, specs)
		}
		modelPath = filepath.Join(modelDir, "config.json")
	} else {
		if strings.TrimSpace(entry.URL) == "" {
			return nil, fmt.Errorf("registry entry for '%s' has neither BundleFiles nor a Url", modelID)
		}
		p, err := f.download.EnsureModel(ctx, modelID, entry.URL, entry.Checksum, nil)
		if err != nil {
			return nil, err
		}
		modelPath = p
	}

	// 3. Record native prep (best-effort) so diagnostics can surface it.
	if f.nativeStatus != nil {
		f.nativeStatus.Update(NativeRuntimePaths{
			MnnCorePath:  modelPath,
			ResolvedRoot: filepath.Dir(modelPath),
			SelfCheckOK:  true,
		})
	}

	// 4. Construct the generator (4096-token Qwen-family default context).
	var genOpts []LocalChatGeneratorOption
	if f.responder != nil {
		genOpts = append(genOpts, WithResponder(f.responder))
	}
	generator, err := NewLocalChatGenerator(modelPath, 4096, genOpts...)
	if err != nil {
		return nil, err
	}

	// 5. Build a descriptor + wrap as a bridge.
	descriptor := ModelDescriptor{
		ModelID:                modelID,
		Version:                entry.Version,
		Format:                 ModelFormatGguf,
		ContextWindowTokens:    4096,
		VocabSize:              151936, // Qwen 3 family default
		ParameterCount:         0,
		QuantisationLabel:      entry.Quantization,
		ApproximateMemoryBytes: approxMemoryFromTier(tier),
	}
	return NewLocalProcessInferenceBridge(generator, descriptor, f.deviceCaps)
}

// approxMemoryFromTier ports MnnInferenceBridgeFactory.ApproxMemoryFromTier.
func approxMemoryFromTier(tier CapabilityTier) int64 {
	const gib = int64(1024) * 1024 * 1024
	switch tier {
	case CapabilityTier0Tiny:
		return 1 * gib
	case CapabilityTier1Small:
		return 2 * gib
	case CapabilityTier2Medium:
		return 6 * gib
	case CapabilityTier3Large:
		return 12 * gib
	case CapabilityTier4Frontier:
		return 24 * gib
	default:
		return 1 * gib
	}
}

var (
	_ IBridgeFactory = UnconfiguredBridgeFactory{}
	_ IBridgeFactory = (*LocalInferenceBridgeFactory)(nil)
)
