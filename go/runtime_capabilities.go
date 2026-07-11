// runtime_capabilities.go
//
// Ports CircleAI.Runtime.Capabilities (HostProfile.cs + ICapabilityProbe.cs +
// CapabilityProbe.cs):
//
//	OperatingSystemKind / ArchitectureKind / GpuVendor / NpuVendor (enums)
//	    -> int consts (stable ordinals)
//	GpuInfo / NpuInfo / HostProfile (records)  -> value structs (+ helper methods)
//	ICapabilityProbe                            -> CapabilityProbe interface
//	CapabilityProbe / UnknownCapabilityProbe    -> StaticCapabilityProbe + Unknown
//
// The C# OS-specific probes (Windows WMI / Linux /proc / macOS sysctl / Android
// Build.*) are platform I/O and out of the portable in-memory contract — exactly
// the "any external/native is injected" case. The port keeps the ICapabilityProbe
// SEAM and supplies deterministic probes: StaticCapabilityProbe returns an
// injected HostProfile, and UnknownCapabilityProbe returns the all-Unknown
// fallback the C# CapabilityProbe emits on unrecognised platforms.
//
// NOTE: the enum name HostProfile / GpuVendor / NpuVendor do NOT collide — the
// existing device_probe.go uses GpuKind (a device-tier enum), a different type.

package circleai

import (
	"context"
	"time"
)

// OperatingSystemKind is the OS family a probe recognised. Ports
// OperatingSystemKind (stable ordinals).
type OperatingSystemKind int

const (
	// OSUnknown — probe could not identify the OS.
	OSUnknown OperatingSystemKind = 0
	// OSWindows — Microsoft Windows.
	OSWindows OperatingSystemKind = 1
	// OSLinux — any Linux distribution.
	OSLinux OperatingSystemKind = 2
	// OSMacOS — Apple macOS.
	OSMacOS OperatingSystemKind = 3
	// OSAndroid — Google Android.
	OSAndroid OperatingSystemKind = 4
	// OSIOS — Apple iOS / iPadOS / tvOS / watchOS.
	OSIOS OperatingSystemKind = 5
	// OSHarmonyOS — Huawei HarmonyOS / OpenHarmony.
	OSHarmonyOS OperatingSystemKind = 6
)

// ArchitectureKind is the CPU architecture family. Ports ArchitectureKind
// (stable ordinals).
type ArchitectureKind int

const (
	// ArchUnknown — probe could not identify the architecture.
	ArchUnknown ArchitectureKind = 0
	// ArchX86 — 32-bit Intel/AMD.
	ArchX86 ArchitectureKind = 1
	// ArchX64 — 64-bit Intel/AMD.
	ArchX64 ArchitectureKind = 2
	// ArchArm — 32-bit ARM.
	ArchArm ArchitectureKind = 3
	// ArchArm64 — 64-bit ARM / Apple Silicon.
	ArchArm64 ArchitectureKind = 4
	// ArchLoong64 — Loongson LoongArch64.
	ArchLoong64 ArchitectureKind = 5
)

// GpuVendor is a GPU vendor identifier. Ports GpuVendor (stable ordinals; note
// Other = 99).
type GpuVendor int

const (
	// GpuVendorNone — no GPU detected, or vendor unknown.
	GpuVendorNone GpuVendor = 0
	// GpuVendorNvidia — NVIDIA.
	GpuVendorNvidia GpuVendor = 1
	// GpuVendorAmd — AMD.
	GpuVendorAmd GpuVendor = 2
	// GpuVendorIntel — Intel.
	GpuVendorIntel GpuVendor = 3
	// GpuVendorApple — Apple Silicon GPU.
	GpuVendorApple GpuVendor = 4
	// GpuVendorQualcomm — Qualcomm Adreno.
	GpuVendorQualcomm GpuVendor = 5
	// GpuVendorHuawei — Huawei Maleoon / Mali-on-Kirin.
	GpuVendorHuawei GpuVendor = 6
	// GpuVendorArm — ARM Mali (third-party SoCs).
	GpuVendorArm GpuVendor = 7
	// GpuVendorOther — identified but not enumerated.
	GpuVendorOther GpuVendor = 99
)

// NpuVendor is an NPU / neural-accelerator vendor identifier. Ports NpuVendor
// (stable ordinals; note Other = 99).
type NpuVendor int

const (
	// NpuVendorNone — no NPU detected.
	NpuVendorNone NpuVendor = 0
	// NpuVendorAppleNeuralEngine — Apple ANE.
	NpuVendorAppleNeuralEngine NpuVendor = 1
	// NpuVendorQualcommHexagon — Qualcomm Hexagon.
	NpuVendorQualcommHexagon NpuVendor = 2
	// NpuVendorHuaweiAscend — Huawei Ascend.
	NpuVendorHuaweiAscend NpuVendor = 3
	// NpuVendorIntelVpu — Intel VPU.
	NpuVendorIntelVpu NpuVendor = 4
	// NpuVendorCambriconMlu — Cambricon MLU.
	NpuVendorCambriconMlu NpuVendor = 5
	// NpuVendorOther — identified but not enumerated.
	NpuVendorOther NpuVendor = 99
)

// GpuInfo describes a discovered GPU. Ports the GpuInfo record. DriverVersion is
// empty when unknown (C# nullable string).
type GpuInfo struct {
	Vendor        GpuVendor
	Model         string
	VramBytes     int64
	DriverVersion string
}

// NpuInfo describes a discovered NPU. Ports the NpuInfo record.
type NpuInfo struct {
	Vendor NpuVendor
	Model  string
}

// HostProfile is a full host capability snapshot. Ports the HostProfile record.
// Gpu / Npu are nil when none was detected (C# nullable records).
type HostProfile struct {
	Os                       OperatingSystemKind
	OsVersion                string
	Arch                     ArchitectureKind
	CpuModel                 string
	LogicalCoreCount         int
	PhysicalCoreCount        int
	TotalPhysicalMemoryBytes int64
	Gpu                      *GpuInfo
	Npu                      *NpuInfo
	ProbedAt                 time.Time
}

// HasUsableGpu reports whether Gpu is present with at least minimumVramBytes of
// VRAM. Ports HasUsableGpu (default 2 GiB — pass 2*1024*1024*1024).
func (p HostProfile) HasUsableGpu(minimumVramBytes int64) bool {
	return p.Gpu != nil && p.Gpu.VramBytes >= minimumVramBytes
}

// Is64Bit reports whether the host runs a 64-bit architecture. Ports Is64Bit.
func (p HostProfile) Is64Bit() bool {
	return p.Arch == ArchX64 || p.Arch == ArchArm64 || p.Arch == ArchLoong64
}

// CapabilityProbe discovers the host's hardware capabilities. Ports
// ICapabilityProbe. Implementations must not error on probe failure — they
// return best-effort Unknown/0/nil fields.
type CapabilityProbe interface {
	Probe(ctx context.Context) (HostProfile, error)
}

// StaticCapabilityProbe returns a fixed HostProfile. It is the deterministic
// in-memory probe the port supplies in place of the platform-specific C# probes
// (which read WMI/proc/sysctl). Inject the profile the host would have detected.
type StaticCapabilityProbe struct {
	Profile HostProfile
}

// NewStaticCapabilityProbe constructs a probe returning profile.
func NewStaticCapabilityProbe(profile HostProfile) StaticCapabilityProbe {
	return StaticCapabilityProbe{Profile: profile}
}

// Probe returns the fixed profile. Ports ProbeAsync (honours ctx cancellation).
func (p StaticCapabilityProbe) Probe(ctx context.Context) (HostProfile, error) {
	if err := ctx.Err(); err != nil {
		return HostProfile{}, err
	}
	return p.Profile, nil
}

// UnknownCapabilityProbe returns the all-Unknown fallback profile the C#
// CapabilityProbe emits on unrecognised platforms (Unknown OS, zero memory, no
// GPU/NPU). Ports UnknownCapabilityProbe.
type UnknownCapabilityProbe struct {
	// OsVersion / CpuModel / cores default to the fallback values below when zero.
	OsVersion string
	CpuModel  string
	Arch      ArchitectureKind
	CoreCount int
}

// Probe returns the Unknown-OS fallback profile. Ports ProbeAsync.
func (p UnknownCapabilityProbe) Probe(ctx context.Context) (HostProfile, error) {
	if err := ctx.Err(); err != nil {
		return HostProfile{}, err
	}
	cpu := p.CpuModel
	if cpu == "" {
		cpu = "Unknown CPU"
	}
	return HostProfile{
		Os:                       OSUnknown,
		OsVersion:                p.OsVersion,
		Arch:                     p.Arch,
		CpuModel:                 cpu,
		LogicalCoreCount:         p.CoreCount,
		PhysicalCoreCount:        p.CoreCount,
		TotalPhysicalMemoryBytes: 0,
		Gpu:                      nil,
		Npu:                      nil,
		ProbedAt:                 time.Now().UTC(),
	}, nil
}

// Interface guards.
var (
	_ CapabilityProbe = StaticCapabilityProbe{}
	_ CapabilityProbe = UnknownCapabilityProbe{}
)
