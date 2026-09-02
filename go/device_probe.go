// device_probe.go
//
// DeviceProbe + tier classification + DeviceTierDefaults +
// IDeviceContext + DefaultDeviceContext + NullDeviceContext.
// Port of CircleAI.Core.DeviceProbe.

package circleai

import (
	"runtime"
	"time"
)

// GpuKind is what kind of GPU acceleration the device exposes.
type GpuKind int

const (
	GpuNone       GpuKind = 0
	GpuIntegrated GpuKind = 1
	GpuDiscrete   GpuKind = 2
	GpuNPU        GpuKind = 3
	GpuMetal      GpuKind = 4
	GpuVulkan     GpuKind = 5
	GpuOpenCL     GpuKind = 6
)

// ThermalClass classifies sustained-load thermal capacity.
type ThermalClass int

const (
	ThermalActive      ThermalClass = 0
	ThermalPassive     ThermalClass = 1
	ThermalConstrained ThermalClass = 2
	ThermalSealed      ThermalClass = 3
)

// Connectivity classifies reachability of the model registry.
type Connectivity int

const (
	ConnectivityUnknown   Connectivity = 0
	ConnectivityOffline   Connectivity = 1
	ConnectivityMeshOnly  Connectivity = 2
	ConnectivityMetered   Connectivity = 3
	ConnectivityUnlimited Connectivity = 4
)

// DeviceProbe is a point-in-time snapshot of what the device can do.
type DeviceProbe struct {
	RAMAvailableBytes int64
	StorageFreeBytes  int64
	CPUCores          int
	GpuKind           GpuKind
	ThermalClass      ThermalClass
	Connectivity      Connectivity
}

// SnapshotOptions controls DeviceProbe.Snapshot.
type SnapshotOptions struct {
	ModelCacheDirectory string
	GpuOverride         *GpuKind
	ThermalOverride     *ThermalClass
}

// Snapshot captures the current device state.
func Snapshot(opts SnapshotOptions) DeviceProbe {
	gpu := GpuNone
	if opts.GpuOverride != nil {
		gpu = *opts.GpuOverride
	}
	thermal := ThermalActive
	if opts.ThermalOverride != nil {
		thermal = *opts.ThermalOverride
	}
	return DeviceProbe{
		RAMAvailableBytes: probeRAMAvailable(),
		StorageFreeBytes:  probeStorageFree(opts.ModelCacheDirectory),
		CPUCores:          runtime.NumCPU(),
		GpuKind:           gpu,
		ThermalClass:      thermal,
		Connectivity:      ConnectivityUnknown,
	}
}

// Classify classifies the probe into one of the five tiers.
func (p DeviceProbe) Classify() DeviceTier {
	gb := float64(p.RAMAvailableBytes) / (1024 * 1024 * 1024)
	if p.ThermalClass == ThermalSealed {
		return DeviceTierWearable
	}
	if gb < 2 || p.ThermalClass == ThermalConstrained {
		return DeviceTierPhone
	}
	if gb < 8 || p.ThermalClass == ThermalPassive {
		return DeviceTierTablet
	}
	if gb < 32 {
		return DeviceTierDesktop
	}
	return DeviceTierWorkstation
}

// DeviceTierDefaults sizes defaults by tier.
type DeviceTierDefaults struct{}

// ContextWindow returns the default token context window per tier.
func (DeviceTierDefaults) ContextWindow(tier DeviceTier) int {
	switch tier {
	case DeviceTierWearable:
		return 2048
	case DeviceTierPhone:
		return 4096
	case DeviceTierTablet:
		return 8192
	case DeviceTierDesktop:
		return 32768
	case DeviceTierWorkstation:
		return 131072
	}
	return 4096
}

// MaxConcurrency returns the default concurrent-task cap per tier.
func (DeviceTierDefaults) MaxConcurrency(tier DeviceTier, cpuCores int) int {
	switch tier {
	case DeviceTierWearable:
		return 1
	case DeviceTierPhone:
		return 2
	case DeviceTierTablet:
		return 4
	case DeviceTierDesktop:
		return 8
	case DeviceTierWorkstation:
		v := cpuCores - 2
		if v < 1 {
			v = 1
		}
		if v > 16 {
			v = 16
		}
		return v
	}
	return 2
}

// AgenticMaxIterations returns the default agentic-loop cap per tier.
func (DeviceTierDefaults) AgenticMaxIterations(tier DeviceTier) int {
	switch tier {
	case DeviceTierWearable:
		return 2
	case DeviceTierPhone:
		return 3
	case DeviceTierTablet:
		return 5
	case DeviceTierDesktop, DeviceTierWorkstation:
		return 10
	}
	return 5
}

// IDeviceContext is the sensorium contract.
// All fields return zero-equivalent values when the host has no answer.
type IDeviceContext interface {
	ActiveAppID() string
	Locale() string
	TimeZoneID() string
	LocalTime() time.Time
	Latitude() *float64
	Longitude() *float64
	LocationHint() string
	BatteryLevel() *float32
	IsCharging() *bool
	NetworkType() string
	CPUUsagePercent() *float32
	AvailableMemoryBytes() int64
	ThermalState() string
	StorageFreeBytes() int64
	LastActiveUTC() time.Time
}

// NullDeviceContext is a no-op IDeviceContext for tests.
type NullDeviceContext struct{}

func (NullDeviceContext) ActiveAppID() string         { return "" }
func (NullDeviceContext) Locale() string              { return "" }
func (NullDeviceContext) TimeZoneID() string          { return "" }
func (NullDeviceContext) LocalTime() time.Time        { return time.Time{} }
func (NullDeviceContext) Latitude() *float64          { return nil }
func (NullDeviceContext) Longitude() *float64         { return nil }
func (NullDeviceContext) LocationHint() string        { return "" }
func (NullDeviceContext) BatteryLevel() *float32      { return nil }
func (NullDeviceContext) IsCharging() *bool           { return nil }
func (NullDeviceContext) NetworkType() string         { return "" }
func (NullDeviceContext) CPUUsagePercent() *float32   { return nil }
func (NullDeviceContext) AvailableMemoryBytes() int64 { return 0 }
func (NullDeviceContext) ThermalState() string        { return "" }
func (NullDeviceContext) StorageFreeBytes() int64     { return 0 }
func (NullDeviceContext) LastActiveUTC() time.Time    { return time.Time{} }

// DefaultDeviceContext probes the runtime via stdlib.
// Platform-specific sensors (GPS, battery, active app) stay zero.
type DefaultDeviceContext struct {
	ModelCacheDir string
	ThermalHint   ThermalClass
}

func (d *DefaultDeviceContext) ActiveAppID() string { return "" }

func (d *DefaultDeviceContext) Locale() string {
	// time.Local has no locale; stdlib has no language code lookup. Leave blank.
	return ""
}

func (d *DefaultDeviceContext) TimeZoneID() string {
	return time.Local.String()
}

func (d *DefaultDeviceContext) LocalTime() time.Time        { return time.Now() }
func (d *DefaultDeviceContext) Latitude() *float64          { return nil }
func (d *DefaultDeviceContext) Longitude() *float64         { return nil }
func (d *DefaultDeviceContext) LocationHint() string        { return "" }
func (d *DefaultDeviceContext) BatteryLevel() *float32      { return nil }
func (d *DefaultDeviceContext) IsCharging() *bool           { return nil }
func (d *DefaultDeviceContext) NetworkType() string         { return "" }
func (d *DefaultDeviceContext) CPUUsagePercent() *float32   { return nil }
func (d *DefaultDeviceContext) AvailableMemoryBytes() int64 { return probeRAMAvailable() }
func (d *DefaultDeviceContext) ThermalState() string        { return "normal" }
func (d *DefaultDeviceContext) StorageFreeBytes() int64     { return probeStorageFree(d.ModelCacheDir) }
func (d *DefaultDeviceContext) LastActiveUTC() time.Time    { return time.Time{} }

// BuildProbe constructs a DeviceProbe using this context's settings.
func (d *DefaultDeviceContext) BuildProbe(gpuOverride *GpuKind) DeviceProbe {
	return Snapshot(SnapshotOptions{
		ModelCacheDirectory: d.ModelCacheDir,
		GpuOverride:         gpuOverride,
		ThermalOverride:     &d.ThermalHint,
	})
}

// ── platform probes ─────────────────────────────────────────────────────

func probeRAMAvailable() int64 {
	// runtime.MemStats reports the Go process — not the system. Better
	// approach: syscall.Sysinfo on Linux. Other platforms fall back to
	// 0 (callers can supply a real implementation as IDeviceContext).
	if v := sysinfoFreeRAM(); v > 0 {
		return v
	}
	return 0
}

// probeStorageFree is implemented per-platform — see sysinfo_*.go.
