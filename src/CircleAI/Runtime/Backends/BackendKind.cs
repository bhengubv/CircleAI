// BackendKind.cs
//
// Enumerates the MNN execution backends CircleAI.Runtime can route to.
// Values match the runtime-package layout shipped by Alibaba MNN — each
// kind has a distinct pre-built native bundle.

namespace CircleAI.Runtime.Backends;

/// <summary>
/// MNN execution backend. Picked by <see cref="IBackendSelector"/> based on
/// the host's <see cref="Capabilities.HostProfile"/>.
/// </summary>
public enum BackendKind
{
    /// <summary>Pure-CPU SIMD backend. Always available.</summary>
    Cpu = 0,
    /// <summary>NVIDIA CUDA. Requires CUDA toolkit + NVIDIA driver.</summary>
    Cuda = 1,
    /// <summary>Vulkan compute. Cross-vendor (AMD, Intel, Apple via MoltenVK).</summary>
    Vulkan = 2,
    /// <summary>OpenCL. Mostly used on older AMD/Intel Linux deployments.</summary>
    OpenCL = 3,
    /// <summary>Apple Metal. Apple Silicon and Intel mac discrete GPUs.</summary>
    Metal = 4,
    /// <summary>Huawei Ascend (CANN). Atlas + Ascend 310/910 + Kirin NPU.</summary>
    Ascend = 5,
    /// <summary>Cambricon MLU.</summary>
    Cambricon = 6,
    /// <summary>Apple Core ML — used for ANE acceleration on Apple Silicon.</summary>
    CoreML = 7,
}
