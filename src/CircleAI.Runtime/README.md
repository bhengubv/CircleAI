# CircleAI.Runtime

Runtime capability detection (OS / arch / CPU / GPU / RAM / NPU),
backend selection (CPU / CUDA / Vulkan / OpenCL / Metal / Ascend /
Cambricon / CoreML), and on-demand fetch of pre-built Alibaba MNN
native runtime bundles from ModelScope / GitHub releases.

```bash
dotnet add package CircleAI.Runtime
```

```csharp
using Microsoft.Extensions.DependencyInjection;
using CircleAI.Runtime;
using CircleAI.Runtime.Capabilities;
using CircleAI.Runtime.Backends;

services.AddCircleAIRuntime(runtimeCacheRoot: "./runtime-cache");

// Manual usage:
var probe = new CapabilityProbe();
var profile = await probe.ProbeAsync(ct);
var selection = new BackendSelector().Select(profile, CapabilityTier.Tier2_Medium);
// selection.Backend  -> Cuda / Metal / OpenCL / Vulkan / Cpu / ...
// selection.Rationale -> "Apple Silicon (M2 Pro); Metal over unified-memory GPU; ..."
```

`NativeRuntimeFetcher.EnsureRuntimeAsync(os, arch, backend, …)` mirrors
the proven `CircleAI.Inference.ModelDownloadService.EnsureModelAsync`
pattern: cache-hit fast path, atomic download + SHA-256 verify +
archive-magic-byte sniff, fallback URI, cleanup on failure.

The embedded registry pins real Alibaba MNN 3.5.0 URLs + SHA-256s for
Windows x64, Linux x64, macOS (x64 + Arm64), Android (Arm + Arm64), iOS
Arm64. Tuples Alibaba doesn't ship pre-built (CUDA, Ascend, Cambricon,
Linux arm64, Loong64, HarmonyOS) are deliberately absent — see the
registry's `_notes` block.

See [docs/ARCHITECTURE.md](https://github.com/bhengubv/CircleAI/blob/master/docs/ARCHITECTURE.md) § 6–7.
