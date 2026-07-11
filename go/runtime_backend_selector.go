// runtime_backend_selector.go
//
// Ports CircleAI.Runtime.Backends.IBackendSelector + BackendSelector
// (IBackendSelector.cs + BackendSelector.cs). The BackendKind and CapabilityTier
// enums are already ported in server_backend.go; this file adds the selection
// record + interface + the deterministic table-style selector.
//
//	BackendSelection (record)  -> value struct
//	IBackendSelector           -> BackendSelector interface (I-prefix dropped)
//	BackendSelector (class)    -> DefaultBackendSelector
//
// The selector NEVER errors and NEVER returns an empty selection — every host
// can run the CPU backend at Tier0 as a last resort. Rationale strings mirror
// the C# interpolated messages (GiB values, model names) so operator dashboards
// read identically.

package circleai

import "fmt"

const backendSelectorGiB = int64(1024) * 1024 * 1024

// BackendSelection is the result of a Select call. Ports the BackendSelection
// record. ActualTier is <= the requested tier (the selector downgrades when
// compute is short).
type BackendSelection struct {
	Backend    BackendKind
	ActualTier CapabilityTier
	Rationale  string
}

// BackendSelectorContract picks the MNN backend + tier for a host. Ports
// IBackendSelector. (Named with a Contract suffix because the C# concrete class
// is also named BackendSelector; the Go concrete type is DefaultBackendSelector.)
type BackendSelectorContract interface {
	// Select picks the best backend + tier for profile, capped at requestedTier.
	Select(profile HostProfile, requestedTier CapabilityTier) BackendSelection
}

// DefaultBackendSelector is the deterministic default BackendSelector. Ports the
// BackendSelector class. No I/O; safe on hot paths. The zero value is usable.
type DefaultBackendSelector struct{}

// Select picks the backend + tier for profile. Ports Select — the branch order
// and per-branch ceilings match the C# exactly.
func (DefaultBackendSelector) Select(profile HostProfile, requestedTier CapabilityTier) BackendSelection {
	giB := backendSelectorGiB

	// 1. Apple Silicon — Metal + ANE over unified memory.
	if profile.Os == OSMacOS && profile.Arch == ArchArm64 &&
		profile.Gpu != nil && profile.Gpu.Vendor == GpuVendorApple {
		tier := clampTier(requestedTier, tierForUnifiedMemory(profile.TotalPhysicalMemoryBytes))
		return BackendSelection{
			Backend:    BackendMetal,
			ActualTier: tier,
			Rationale: fmt.Sprintf("Apple Silicon (%s); Metal over unified-memory GPU; tier capped to %s by %d GiB unified RAM.",
				profile.CpuModel, tier, profile.TotalPhysicalMemoryBytes/giB),
		}
	}

	// 2. NVIDIA + CUDA.
	if profile.Gpu != nil && profile.Gpu.Vendor == GpuVendorNvidia && profile.Gpu.VramBytes >= 4*giB {
		tier := clampTier(requestedTier, tierForVram(profile.Gpu.VramBytes))
		return BackendSelection{
			Backend:    BackendCuda,
			ActualTier: tier,
			Rationale: fmt.Sprintf("NVIDIA %s with %d GiB VRAM; CUDA backend; tier capped to %s by VRAM.",
				profile.Gpu.Model, profile.Gpu.VramBytes/giB, tier),
		}
	}

	// 3. Huawei Ascend NPU.
	if profile.Npu != nil && profile.Npu.Vendor == NpuVendorHuaweiAscend {
		tier := clampTier(requestedTier, CapabilityTier3Large)
		return BackendSelection{
			Backend:    BackendAscend,
			ActualTier: tier,
			Rationale: fmt.Sprintf("Huawei Ascend NPU detected (%s); Ascend (CANN) backend; tier capped to %s.",
				profile.Npu.Model, tier),
		}
	}

	// 4. Cambricon MLU.
	if profile.Npu != nil && profile.Npu.Vendor == NpuVendorCambriconMlu {
		tier := clampTier(requestedTier, CapabilityTier3Large)
		return BackendSelection{
			Backend:    BackendCambricon,
			ActualTier: tier,
			Rationale:  fmt.Sprintf("Cambricon MLU detected; Cambricon backend; tier capped to %s.", tier),
		}
	}

	// 5. AMD / Intel discrete GPU — Vulkan.
	if profile.Gpu != nil &&
		(profile.Gpu.Vendor == GpuVendorAmd || profile.Gpu.Vendor == GpuVendorIntel) &&
		profile.Gpu.VramBytes >= 4*giB {
		tier := clampTier(requestedTier, tierForVram(profile.Gpu.VramBytes))
		return BackendSelection{
			Backend:    BackendVulkan,
			ActualTier: tier,
			Rationale: fmt.Sprintf("%s %s with %d GiB VRAM; Vulkan backend; tier capped to %s by VRAM.",
				gpuVendorName(profile.Gpu.Vendor), profile.Gpu.Model, profile.Gpu.VramBytes/giB, tier),
		}
	}

	// 6. Qualcomm Snapdragon — OpenCL.
	if (profile.Npu != nil && profile.Npu.Vendor == NpuVendorQualcommHexagon) ||
		(profile.Gpu != nil && profile.Gpu.Vendor == GpuVendorQualcomm) {
		tier := clampTier(requestedTier, CapabilityTier1Small)
		return BackendSelection{
			Backend:    BackendOpenCL,
			ActualTier: tier,
			Rationale:  fmt.Sprintf("Qualcomm Snapdragon platform; OpenCL backend (Adreno/Hexagon shared compute); tier capped to %s.", tier),
		}
	}

	// 7. ARM Mali / Huawei GPU — Vulkan.
	if profile.Gpu != nil && (profile.Gpu.Vendor == GpuVendorArm || profile.Gpu.Vendor == GpuVendorHuawei) {
		tier := clampTier(requestedTier, CapabilityTier1Small)
		return BackendSelection{
			Backend:    BackendVulkan,
			ActualTier: tier,
			Rationale: fmt.Sprintf("ARM/Mali class GPU (%s); Vulkan backend; tier capped to %s.",
				profile.Gpu.Model, tier),
		}
	}

	// 8. CPU fallback — always selectable.
	cpuTier := clampTier(requestedTier, tierForCpuRam(profile.TotalPhysicalMemoryBytes))
	return BackendSelection{
		Backend:    BackendCpu,
		ActualTier: cpuTier,
		Rationale: fmt.Sprintf("No usable accelerator detected; CPU SIMD backend on %s (%d logical cores, %d GiB RAM); tier capped to %s by available RAM.",
			profile.CpuModel, profile.LogicalCoreCount, profile.TotalPhysicalMemoryBytes/giB, cpuTier),
	}
}

// clampTier returns the lower of requested and ceiling. Ports ClampTier.
func clampTier(requested, ceiling CapabilityTier) CapabilityTier {
	if requested <= ceiling {
		return requested
	}
	return ceiling
}

// tierForVram maps VRAM bytes to a tier ceiling. Ports TierForVram.
func tierForVram(vramBytes int64) CapabilityTier {
	giB := backendSelectorGiB
	switch {
	case vramBytes >= 24*giB:
		return CapabilityTier4Frontier
	case vramBytes >= 12*giB:
		return CapabilityTier3Large
	case vramBytes >= 8*giB:
		return CapabilityTier2Medium
	case vramBytes >= 4*giB:
		return CapabilityTier1Small
	default:
		return CapabilityTier0Tiny
	}
}

// tierForUnifiedMemory maps unified RAM bytes to a tier ceiling (conservative,
// shared-pool). Ports TierForUnifiedMemory.
func tierForUnifiedMemory(ramBytes int64) CapabilityTier {
	giB := backendSelectorGiB
	switch {
	case ramBytes >= 64*giB:
		return CapabilityTier4Frontier
	case ramBytes >= 32*giB:
		return CapabilityTier3Large
	case ramBytes >= 16*giB:
		return CapabilityTier2Medium
	case ramBytes >= 8*giB:
		return CapabilityTier1Small
	default:
		return CapabilityTier0Tiny
	}
}

// tierForCpuRam maps system RAM bytes to a CPU-backend tier ceiling. Ports
// TierForCpuRam (note: the >= 16 and >= 8 branches both yield Tier1_Small,
// matching the C# table exactly).
func tierForCpuRam(ramBytes int64) CapabilityTier {
	giB := backendSelectorGiB
	switch {
	case ramBytes >= 64*giB:
		return CapabilityTier3Large
	case ramBytes >= 32*giB:
		return CapabilityTier2Medium
	case ramBytes >= 16*giB:
		return CapabilityTier1Small
	case ramBytes >= 8*giB:
		return CapabilityTier1Small
	default:
		return CapabilityTier0Tiny
	}
}

// gpuVendorName returns the C# enum member name for a GPU vendor (used in the
// AMD/Intel Vulkan rationale, which interpolates the vendor enum).
func gpuVendorName(v GpuVendor) string {
	switch v {
	case GpuVendorNone:
		return "None"
	case GpuVendorNvidia:
		return "Nvidia"
	case GpuVendorAmd:
		return "Amd"
	case GpuVendorIntel:
		return "Intel"
	case GpuVendorApple:
		return "Apple"
	case GpuVendorQualcomm:
		return "Qualcomm"
	case GpuVendorHuawei:
		return "Huawei"
	case GpuVendorArm:
		return "Arm"
	default:
		return "Other"
	}
}

// Interface guard.
var _ BackendSelectorContract = DefaultBackendSelector{}
