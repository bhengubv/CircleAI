// server_backend.go
//
// Ports CircleAI.Runtime.Backends.BackendKind (BackendKind.cs) and
// CircleAI.Runtime.Backends.CapabilityTier (CapabilityTier.cs).
//
// These are the routing keys the inference-server lifecycle layer uses: a
// backend selector returns a BackendKind + CapabilityTier, and the model
// registry / model selector looks up which model fits. Kept together so the
// server-lifecycle port has its enum dependencies in-package.

package circleai

import "strings"

// BackendKind identifies the compute backend a load runs on. Ports
// CircleAI.Runtime.Backends.BackendKind (stable ordinals).
type BackendKind int

const (
	// BackendCpu — pure-CPU SIMD backend. Always available.
	BackendCpu BackendKind = 0
	// BackendCuda — NVIDIA CUDA.
	BackendCuda BackendKind = 1
	// BackendVulkan — Vulkan compute (cross-vendor).
	BackendVulkan BackendKind = 2
	// BackendOpenCL — OpenCL (older AMD/Intel Linux).
	BackendOpenCL BackendKind = 3
	// BackendMetal — Apple Metal.
	BackendMetal BackendKind = 4
	// BackendAscend — Huawei Ascend (CANN).
	BackendAscend BackendKind = 5
	// BackendCambricon — Cambricon MLU.
	BackendCambricon BackendKind = 6
	// BackendCoreML — Apple Core ML (ANE).
	BackendCoreML BackendKind = 7
)

// backendNames maps ordinals to the C# enum member names (used by ToString /
// parse for the admin endpoint).
var backendNames = map[BackendKind]string{
	BackendCpu: "Cpu", BackendCuda: "Cuda", BackendVulkan: "Vulkan", BackendOpenCL: "OpenCL",
	BackendMetal: "Metal", BackendAscend: "Ascend", BackendCambricon: "Cambricon", BackendCoreML: "CoreML",
}

// String returns the C# enum member name for the backend.
func (b BackendKind) String() string {
	if n, ok := backendNames[b]; ok {
		return n
	}
	return "Cpu"
}

// ParseBackendKind parses a case-insensitive backend name. Ports Enum.TryParse.
func ParseBackendKind(s string) (BackendKind, bool) {
	for k, n := range backendNames {
		if strings.EqualFold(n, strings.TrimSpace(s)) {
			return k, true
		}
	}
	return BackendCpu, false
}

// IsGPUBackend reports whether the backend is GPU-class (VRAM-admitted). Mirrors
// the ModelLifecycleManager VRAM gate condition.
func (b BackendKind) IsGPUBackend() bool {
	switch b {
	case BackendCuda, BackendVulkan, BackendMetal, BackendOpenCL:
		return true
	default:
		return false
	}
}

// CapabilityTier maps to a Qwen / DeepSeek / GLM / Kimi model size band. Ports
// CircleAI.Runtime.Backends.CapabilityTier (stable ordinals).
type CapabilityTier int

const (
	// CapabilityTier0Tiny — Qwen3-0.6B class. ≈600 MB. Always available.
	CapabilityTier0Tiny CapabilityTier = 0
	// CapabilityTier1Small — 1.7B–4B class. ≈2 GB.
	CapabilityTier1Small CapabilityTier = 1
	// CapabilityTier2Medium — 7B–9B class Q4. ≈6 GB.
	CapabilityTier2Medium CapabilityTier = 2
	// CapabilityTier3Large — 14B–32B class Q4. ≈12 GB.
	CapabilityTier3Large CapabilityTier = 3
	// CapabilityTier4Frontier — 70B+ class Q4. ≈24 GB+.
	CapabilityTier4Frontier CapabilityTier = 4
)

var tierNames = map[CapabilityTier]string{
	CapabilityTier0Tiny: "Tier0_Tiny", CapabilityTier1Small: "Tier1_Small",
	CapabilityTier2Medium: "Tier2_Medium", CapabilityTier3Large: "Tier3_Large",
	CapabilityTier4Frontier: "Tier4_Frontier",
}

// String returns the C# enum member name for the tier.
func (t CapabilityTier) String() string {
	if n, ok := tierNames[t]; ok {
		return n
	}
	return "Tier1_Small"
}

// ParseCapabilityTier parses a case-insensitive tier name. Ports Enum.TryParse.
func ParseCapabilityTier(s string) (CapabilityTier, bool) {
	for k, n := range tierNames {
		if strings.EqualFold(n, strings.TrimSpace(s)) {
			return k, true
		}
	}
	return CapabilityTier1Small, false
}
