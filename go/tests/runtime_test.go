// runtime_test.go
//
// Verifies the CircleAI.Runtime port: HostProfile helpers + StaticCapabilityProbe
// (runtime_capabilities.go), the deterministic BackendSelector branches +
// tier clamping (runtime_backend_selector.go), and the native-runtime registry +
// in-memory fetcher (runtime_native.go).

package circleai_test

import (
	"context"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestRuntime_HostProfileHelpersAndProbe(t *testing.T) {
	prof := circleai.HostProfile{
		Os:                       circleai.OSLinux,
		Arch:                     circleai.ArchX64,
		TotalPhysicalMemoryBytes: 32 << 30,
		Gpu:                      &circleai.GpuInfo{Vendor: circleai.GpuVendorNvidia, VramBytes: 8 << 30},
	}
	if !prof.Is64Bit() {
		t.Fatalf("x64 should be 64-bit")
	}
	if !prof.HasUsableGpu(2 << 30) {
		t.Fatalf("8 GiB GPU should be usable at 2 GiB threshold")
	}
	probe := circleai.NewStaticCapabilityProbe(prof)
	got, err := probe.Probe(context.Background())
	if err != nil || got.Arch != circleai.ArchX64 {
		t.Fatalf("static probe = %+v err=%v", got, err)
	}
	unk, _ := circleai.UnknownCapabilityProbe{}.Probe(context.Background())
	if unk.Os != circleai.OSUnknown || unk.CpuModel != "Unknown CPU" {
		t.Fatalf("unknown probe = %+v", unk)
	}
}

func TestRuntime_BackendSelectorBranches(t *testing.T) {
	sel := circleai.DefaultBackendSelector{}

	// Apple Silicon -> Metal.
	apple := circleai.HostProfile{Os: circleai.OSMacOS, Arch: circleai.ArchArm64, CpuModel: "M2",
		TotalPhysicalMemoryBytes: 16 << 30, Gpu: &circleai.GpuInfo{Vendor: circleai.GpuVendorApple}}
	if got := sel.Select(apple, circleai.CapabilityTier4Frontier); got.Backend != circleai.BackendMetal || got.ActualTier != circleai.CapabilityTier2Medium {
		t.Fatalf("apple selection = %+v (want Metal / Tier2 by 16GiB)", got)
	}

	// NVIDIA 24 GiB -> CUDA Tier4 (but clamped to requested Tier2).
	nv := circleai.HostProfile{Os: circleai.OSLinux, Arch: circleai.ArchX64,
		Gpu: &circleai.GpuInfo{Vendor: circleai.GpuVendorNvidia, Model: "RTX", VramBytes: 24 << 30}}
	if got := sel.Select(nv, circleai.CapabilityTier2Medium); got.Backend != circleai.BackendCuda || got.ActualTier != circleai.CapabilityTier2Medium {
		t.Fatalf("nvidia selection = %+v (want CUDA / clamped Tier2)", got)
	}

	// No accelerator -> CPU fallback, always selectable.
	cpu := circleai.HostProfile{Os: circleai.OSWindows, Arch: circleai.ArchX64, CpuModel: "Ryzen", TotalPhysicalMemoryBytes: 4 << 30}
	if got := sel.Select(cpu, circleai.CapabilityTier4Frontier); got.Backend != circleai.BackendCpu || got.ActualTier != circleai.CapabilityTier0Tiny {
		t.Fatalf("cpu fallback = %+v (want Cpu / Tier0 by 4GiB)", got)
	}

	// Ascend NPU -> Ascend.
	ascend := circleai.HostProfile{Os: circleai.OSLinux, Arch: circleai.ArchArm64,
		Npu: &circleai.NpuInfo{Vendor: circleai.NpuVendorHuaweiAscend, Model: "910"}}
	if got := sel.Select(ascend, circleai.CapabilityTier4Frontier); got.Backend != circleai.BackendAscend || got.ActualTier != circleai.CapabilityTier3Large {
		t.Fatalf("ascend selection = %+v", got)
	}
}

func TestRuntime_NativeRegistryAndFetcher(t *testing.T) {
	bundles := []circleai.NativeRuntimeBundle{
		{MnnVersion: "3.4.0", Os: circleai.OSLinux, Arch: circleai.ArchX64, Backend: circleai.BackendCpu, PrimaryURI: "u1", MnnCoreLibraryName: "libMNN.so"},
		{MnnVersion: "3.5.0", Os: circleai.OSLinux, Arch: circleai.ArchX64, Backend: circleai.BackendCpu, PrimaryURI: "u2", MnnCoreLibraryName: "libMNN.so"},
	}
	reg := circleai.NewNativeRuntimeRegistry(bundles)
	// Newest version wins.
	found, ok := reg.Find(circleai.OSLinux, circleai.ArchX64, circleai.BackendCpu)
	if !ok || found.MnnVersion != "3.5.0" {
		t.Fatalf("find newest = %+v ok=%v", found, ok)
	}
	if _, ok := reg.Find(circleai.OSWindows, circleai.ArchX64, circleai.BackendCpu); ok {
		t.Fatalf("unknown tuple must not be found")
	}

	store := circleai.NewMapRuntimeContentStore()
	store.Add(found, "/cache/linux-x64-cpu", "/cache/linux-x64-cpu/libMNN.so")
	fetcher := circleai.NewInMemoryNativeRuntimeFetcher(reg, store)

	if cached, _ := fetcher.IsRuntimeCached(context.Background(), circleai.OSLinux, circleai.ArchX64, circleai.BackendCpu); !cached {
		t.Fatalf("bundle should be cached in the store")
	}
	var lastProgress float64
	inst, err := fetcher.EnsureRuntime(context.Background(), circleai.OSLinux, circleai.ArchX64, circleai.BackendCpu, func(p float64) { lastProgress = p })
	if err != nil || inst.MnnCorePath != "/cache/linux-x64-cpu/libMNN.so" {
		t.Fatalf("ensure = %+v err=%v", inst, err)
	}
	if lastProgress != 1.0 {
		t.Fatalf("progress should end at 1.0, got %v", lastProgress)
	}
	// Unknown tuple -> error.
	if _, err := fetcher.EnsureRuntime(context.Background(), circleai.OSMacOS, circleai.ArchArm64, circleai.BackendMetal, nil); err == nil {
		t.Fatalf("unknown tuple must error")
	}
}

func TestRuntime_NativeRegistryJSON(t *testing.T) {
	json := []byte(`{"mnn_versions":[{"version":"3.5.0","bundles":[
		{"os":"Windows","arch":"X64","backend":"Cuda","url":"https://x/win.zip","sha256":"AB"},
		{"os":"BadOs","arch":"X64","backend":"Cpu","url":"https://x/bad.zip"}
	]}]}`)
	reg, err := circleai.LoadNativeRuntimeRegistryJSON(json)
	if err != nil {
		t.Fatalf("load json: %v", err)
	}
	// The BadOs entry is skipped; only the Windows/Cuda bundle survives.
	if len(reg.All()) != 1 {
		t.Fatalf("loaded %d bundles, want 1 (bad entry skipped)", len(reg.All()))
	}
	b, ok := reg.Find(circleai.OSWindows, circleai.ArchX64, circleai.BackendCuda)
	if !ok || b.MnnCoreLibraryName != "MNN.dll" {
		t.Fatalf("windows bundle = %+v ok=%v (default lib name)", b, ok)
	}
}
