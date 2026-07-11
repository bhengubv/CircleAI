// Runtime.kt
//
// Kotlin port of CircleAI.Runtime — the C# reference is the EXACT spec
// (Capabilities/HostProfile.cs, Capabilities/ICapabilityProbe.cs,
// Capabilities/CapabilityProbe.cs, Backends/BackendKind.cs,
// Backends/CapabilityTier.cs, Backends/IBackendSelector.cs,
// Backends/BackendSelector.cs, NativeRuntimes/NativeRuntimeBundle.cs,
// NativeRuntimes/INativeRuntimeFetcher.cs, NativeRuntimes/NativeRuntimeFetcher.cs,
// NativeRuntimes/NativeRuntimeRegistry.cs).
//
// Runtime capability discovery + MNN backend selection + native-runtime
// bundle resolution. BackendSelector is a deterministic, table-style,
// no-I/O mapping of (HostProfile, requested tier) -> (backend, actual tier,
// rationale). The native-runtime download/extract path in C# is real HTTP +
// ZIP/TAR file I/O; here the network materialisation is injected behind
// [RuntimeArchiveProvider] and the fetcher resolves against an in-memory
// [NativeRuntimeRegistry], keeping the module deterministic and in-memory.
//
// This is the canonical home of these Runtime types (C# namespace
// CircleAI.Runtime.*). A self-contained copy of the capability enums +
// HostProfile also lives in the `server` package because that work unit needs
// them locally; the two are in different Kotlin packages and do not collide.
//
// C# -> Kotlin conventions:
//   OperatingSystemKind / ArchitectureKind / ...  -> enum class (same members)
//   HostProfile (record)   -> data class
//   Task / async           -> suspend fun
//   IProgress<double>      -> (Double) -> Unit
//   Uri                    -> java.net.URI

package com.bhengubv.circleai.runtime

import java.net.URI
import java.time.Instant

// ===========================================================================
// Capabilities — enums + HostProfile  (HostProfile.cs)
// ===========================================================================

/** OS family the probe recognised. */
enum class OperatingSystemKind { Unknown, Windows, Linux, MacOS, Android, IOS, HarmonyOS }

/** CPU architecture family. */
enum class ArchitectureKind { Unknown, X86, X64, Arm, Arm64, Loong64 }

/** GPU vendor identifier. */
enum class GpuVendor { None, Nvidia, Amd, Intel, Apple, Qualcomm, Huawei, Arm, Other }

/** NPU / neural accelerator vendor identifier. */
enum class NpuVendor { None, AppleNeuralEngine, QualcommHexagon, HuaweiAscend, IntelVpu, CambriconMlu, Other }

/** Discovered GPU details. */
data class GpuInfo(
    val vendor: GpuVendor,
    val model: String,
    val vramBytes: Long,
    val driverVersion: String?,
)

/** Discovered NPU details. */
data class NpuInfo(val vendor: NpuVendor, val model: String)

/** Full host capability snapshot — the result of an [ICapabilityProbe.probe] call. */
data class HostProfile(
    val os: OperatingSystemKind,
    val osVersion: String,
    val arch: ArchitectureKind,
    val cpuModel: String,
    val logicalCoreCount: Int,
    val physicalCoreCount: Int,
    val totalPhysicalMemoryBytes: Long,
    val gpu: GpuInfo?,
    val npu: NpuInfo?,
    val probedAt: Instant,
) {
    /** True when [gpu] is present and has at least [minimumVramBytes] of dedicated VRAM. */
    fun hasUsableGpu(minimumVramBytes: Long = 2L * 1024 * 1024 * 1024): Boolean =
        gpu != null && gpu.vramBytes >= minimumVramBytes

    /** True when the host runs on a 64-bit architecture (X64, Arm64, Loong64). */
    val is64Bit: Boolean
        get() = arch == ArchitectureKind.X64 || arch == ArchitectureKind.Arm64 || arch == ArchitectureKind.Loong64
}

// ===========================================================================
// Capabilities — ICapabilityProbe  (ICapabilityProbe.cs)
// ===========================================================================

/** Discovers the host's hardware capabilities and returns a normalised [HostProfile]. */
interface ICapabilityProbe {
    /**
     * Runs the probe. Implementations MUST NOT throw on probe failure — fields
     * that cannot be resolved come back Unknown / null / 0.
     */
    suspend fun probe(): HostProfile
}

/**
 * Default cross-platform probe. Reads what the JVM can portably observe (OS
 * name/version, architecture, logical core count, total physical memory via
 * the OS MXBean when available) and returns a best-effort [HostProfile]. GPU /
 * NPU discovery requires native platform probes (WMI / sysctl / /proc) that
 * hosts supply by passing a fixed profile via [CapabilityProbe.fixed]; the
 * default probe never reports a GPU/NPU and never throws.
 */
class CapabilityProbe private constructor(
    private val inner: ICapabilityProbe,
) : ICapabilityProbe {

    constructor() : this(JvmCapabilityProbe)

    /** Construct with an explicit inner probe (tests, port-specific probes). */
    constructor(inner: ICapabilityProbe, unused: Boolean = false) : this(inner)

    override suspend fun probe(): HostProfile = inner.probe()

    companion object {
        /** A probe that always returns [profile] — for hosts that already know the hardware. */
        fun fixed(profile: HostProfile): CapabilityProbe = CapabilityProbe(FixedProbe(profile))
    }

    private class FixedProbe(private val profile: HostProfile) : ICapabilityProbe {
        override suspend fun probe(): HostProfile = profile
    }
}

/** Best-effort JVM-derived probe (no native GPU/NPU discovery). */
internal object JvmCapabilityProbe : ICapabilityProbe {
    override suspend fun probe(): HostProfile {
        val os = detectOs()
        val arch = detectArch()
        val cores = Runtime.getRuntime().availableProcessors()
        val totalMem = readTotalPhysicalMemoryBytes()
        return HostProfile(
            os = os,
            osVersion = System.getProperty("os.version") ?: "0.0",
            arch = arch,
            cpuModel = System.getProperty("os.arch") ?: "Unknown CPU",
            logicalCoreCount = cores,
            physicalCoreCount = cores,
            totalPhysicalMemoryBytes = totalMem,
            gpu = null,
            npu = null,
            probedAt = Instant.now(),
        )
    }

    private fun detectOs(): OperatingSystemKind {
        val name = (System.getProperty("os.name") ?: "").lowercase()
        return when {
            name.contains("win") -> OperatingSystemKind.Windows
            name.contains("mac") || name.contains("darwin") -> OperatingSystemKind.MacOS
            name.contains("android") -> OperatingSystemKind.Android
            name.contains("nux") || name.contains("nix") -> OperatingSystemKind.Linux
            else -> OperatingSystemKind.Unknown
        }
    }

    private fun detectArch(): ArchitectureKind {
        val a = (System.getProperty("os.arch") ?: "").lowercase()
        return when {
            a == "amd64" || a == "x86_64" -> ArchitectureKind.X64
            a == "x86" || a == "i386" || a == "i686" -> ArchitectureKind.X86
            a == "aarch64" || a == "arm64" -> ArchitectureKind.Arm64
            a.startsWith("arm") -> ArchitectureKind.Arm
            a.startsWith("loong") -> ArchitectureKind.Loong64
            else -> ArchitectureKind.Unknown
        }
    }

    private fun readTotalPhysicalMemoryBytes(): Long {
        // Reflectively read com.sun.management.OperatingSystemMXBean when present.
        return try {
            val bean = java.lang.management.ManagementFactory.getOperatingSystemMXBean()
            val m = bean.javaClass.methods.firstOrNull {
                it.name == "getTotalMemorySize" || it.name == "getTotalPhysicalMemorySize"
            }
            m?.apply { isAccessible = true }?.invoke(bean) as? Long ?: 0L
        } catch (ex: Throwable) {
            0L
        }
    }
}

// ===========================================================================
// Backends — BackendKind / CapabilityTier  (BackendKind.cs, CapabilityTier.cs)
// ===========================================================================

/** MNN execution backend. */
enum class BackendKind { Cpu, Cuda, Vulkan, OpenCL, Metal, Ascend, Cambricon, CoreML }

/** Capability tier that maps to a Qwen / DeepSeek / GLM / Kimi model size band. */
enum class CapabilityTier { Tier0_Tiny, Tier1_Small, Tier2_Medium, Tier3_Large, Tier4_Frontier }

// ===========================================================================
// Backends — IBackendSelector + BackendSelector
//            (IBackendSelector.cs, BackendSelector.cs)
// ===========================================================================

/** Result of an [IBackendSelector.select] call. */
data class BackendSelection(
    val backend: BackendKind,
    val actualTier: CapabilityTier,
    val rationale: String,
)

/**
 * Picks the MNN backend and model tier for a given host. Implementations must
 * NEVER throw and must NEVER return null — every host can run the CPU backend
 * at Tier 0 as a last resort.
 */
interface IBackendSelector {
    /** Pick the best backend + tier for [profile]; [requestedTier] is the upper bound. */
    fun select(profile: HostProfile, requestedTier: CapabilityTier): BackendSelection
}

/**
 * Default [IBackendSelector]. Deterministic; no I/O; safe on hot paths. The
 * selection logic is intentionally explicit so operators can predict routing
 * without running the code.
 */
class BackendSelector : IBackendSelector {

    override fun select(profile: HostProfile, requestedTier: CapabilityTier): BackendSelection {
        val gib = profile.totalPhysicalMemoryBytes / GIB

        // 1. Apple Silicon — Metal + ANE coexist via unified memory.
        if (profile.os == OperatingSystemKind.MacOS &&
            profile.arch == ArchitectureKind.Arm64 &&
            profile.gpu?.vendor == GpuVendor.Apple
        ) {
            val tier = clampTier(requestedTier, tierForUnifiedMemory(profile.totalPhysicalMemoryBytes))
            return BackendSelection(
                BackendKind.Metal, tier,
                "Apple Silicon (${profile.cpuModel}); Metal over unified-memory GPU; tier capped to $tier by $gib GiB unified RAM.",
            )
        }

        // 2. NVIDIA + CUDA — best on Linux + Windows.
        val gpu = profile.gpu
        if (gpu?.vendor == GpuVendor.Nvidia && gpu.vramBytes >= 4 * GIB) {
            val tier = clampTier(requestedTier, tierForVram(gpu.vramBytes))
            return BackendSelection(
                BackendKind.Cuda, tier,
                "NVIDIA ${gpu.model} with ${gpu.vramBytes / GIB} GiB VRAM; CUDA backend; tier capped to $tier by VRAM.",
            )
        }

        // 3. Huawei Ascend NPU — Chinese data-centre + Kirin laptops.
        if (profile.npu?.vendor == NpuVendor.HuaweiAscend) {
            val tier = clampTier(requestedTier, CapabilityTier.Tier3_Large)
            return BackendSelection(
                BackendKind.Ascend, tier,
                "Huawei Ascend NPU detected (${profile.npu.model}); Ascend (CANN) backend; tier capped to $tier.",
            )
        }

        // 4. Cambricon MLU — Chinese accelerator.
        if (profile.npu?.vendor == NpuVendor.CambriconMlu) {
            val tier = clampTier(requestedTier, CapabilityTier.Tier3_Large)
            return BackendSelection(
                BackendKind.Cambricon, tier,
                "Cambricon MLU detected; Cambricon backend; tier capped to $tier.",
            )
        }

        // 5. AMD / Intel discrete GPU — Vulkan.
        if (gpu != null &&
            (gpu.vendor == GpuVendor.Amd || gpu.vendor == GpuVendor.Intel) &&
            gpu.vramBytes >= 4 * GIB
        ) {
            val tier = clampTier(requestedTier, tierForVram(gpu.vramBytes))
            return BackendSelection(
                BackendKind.Vulkan, tier,
                "${gpu.vendor} ${gpu.model} with ${gpu.vramBytes / GIB} GiB VRAM; Vulkan backend; tier capped to $tier by VRAM.",
            )
        }

        // 6. Qualcomm Hexagon NPU on Android / Snapdragon X — OpenCL.
        if (profile.npu?.vendor == NpuVendor.QualcommHexagon || profile.gpu?.vendor == GpuVendor.Qualcomm) {
            val tier = clampTier(requestedTier, CapabilityTier.Tier1_Small)
            return BackendSelection(
                BackendKind.OpenCL, tier,
                "Qualcomm Snapdragon platform; OpenCL backend (Adreno/Hexagon shared compute); tier capped to $tier.",
            )
        }

        // 7. ARM Mali via Vulkan (MediaTek, Exynos, Tensor).
        if (profile.gpu?.vendor == GpuVendor.Arm || profile.gpu?.vendor == GpuVendor.Huawei) {
            val tier = clampTier(requestedTier, CapabilityTier.Tier1_Small)
            return BackendSelection(
                BackendKind.Vulkan, tier,
                "ARM/Mali class GPU (${profile.gpu.model}); Vulkan backend; tier capped to $tier.",
            )
        }

        // 8. CPU fallback — always selectable.
        val cpuTier = clampTier(requestedTier, tierForCpuRam(profile.totalPhysicalMemoryBytes))
        return BackendSelection(
            BackendKind.Cpu, cpuTier,
            "No usable accelerator detected; CPU SIMD backend on ${profile.cpuModel} " +
                "(${profile.logicalCoreCount} logical cores, $gib GiB RAM); tier capped to $cpuTier by available RAM.",
        )
    }

    private fun clampTier(requested: CapabilityTier, ceiling: CapabilityTier): CapabilityTier =
        if (requested.ordinal <= ceiling.ordinal) requested else ceiling

    private fun tierForVram(vramBytes: Long): CapabilityTier = when {
        vramBytes >= 24L * GIB -> CapabilityTier.Tier4_Frontier
        vramBytes >= 12L * GIB -> CapabilityTier.Tier3_Large
        vramBytes >= 8L * GIB -> CapabilityTier.Tier2_Medium
        vramBytes >= 4L * GIB -> CapabilityTier.Tier1_Small
        else -> CapabilityTier.Tier0_Tiny
    }

    private fun tierForUnifiedMemory(ramBytes: Long): CapabilityTier = when {
        ramBytes >= 64L * GIB -> CapabilityTier.Tier4_Frontier
        ramBytes >= 32L * GIB -> CapabilityTier.Tier3_Large
        ramBytes >= 16L * GIB -> CapabilityTier.Tier2_Medium
        ramBytes >= 8L * GIB -> CapabilityTier.Tier1_Small
        else -> CapabilityTier.Tier0_Tiny
    }

    private fun tierForCpuRam(ramBytes: Long): CapabilityTier = when {
        ramBytes >= 64L * GIB -> CapabilityTier.Tier3_Large
        ramBytes >= 32L * GIB -> CapabilityTier.Tier2_Medium
        ramBytes >= 16L * GIB -> CapabilityTier.Tier1_Small
        ramBytes >= 8L * GIB -> CapabilityTier.Tier1_Small
        else -> CapabilityTier.Tier0_Tiny
    }

    private companion object {
        const val GIB = 1024L * 1024 * 1024
    }
}

// ===========================================================================
// NativeRuntimes — bundle + install records  (NativeRuntimeBundle.cs)
// ===========================================================================

/** A single fetchable MNN runtime bundle for one (OS, arch, backend) tuple. */
data class NativeRuntimeBundle(
    val mnnVersion: String,
    val os: OperatingSystemKind,
    val arch: ArchitectureKind,
    val backend: BackendKind,
    val primaryUri: URI,
    val fallbackUri: URI?,
    val archiveSha256Hex: String?,
    val mnnCoreLibraryName: String,
)

/** Result of a successful [INativeRuntimeFetcher.ensureRuntime] call. */
data class NativeRuntimeInstall(
    val bundle: NativeRuntimeBundle,
    val extractedRoot: String,
    val mnnCorePath: String,
)

// ===========================================================================
// NativeRuntimes — registry  (NativeRuntimeRegistry.cs)
// ===========================================================================

/** In-process registry of pre-built MNN runtime bundles, with lookup by tuple. */
class NativeRuntimeRegistry(bundles: List<NativeRuntimeBundle>) {

    private val bundles: List<NativeRuntimeBundle> = bundles.toList()

    /** All loaded bundles. */
    val all: List<NativeRuntimeBundle> get() = bundles

    /**
     * Look up the newest bundle matching (os, arch, backend). When several MNN
     * versions are registered for the same tuple, the highest version string
     * wins (ordinal string sort).
     */
    fun find(os: OperatingSystemKind, arch: ArchitectureKind, backend: BackendKind): NativeRuntimeBundle? =
        bundles
            .filter { it.os == os && it.arch == arch && it.backend == backend }
            .maxWithOrNull(compareBy { it.mnnVersion })

    companion object {
        /** Default MNN core library file name for an OS family. */
        fun defaultCoreLibName(os: OperatingSystemKind): String = when (os) {
            OperatingSystemKind.Windows -> "MNN.dll"
            OperatingSystemKind.MacOS, OperatingSystemKind.IOS -> "MNN"
            else -> "libMNN.so"
        }

        /** An empty registry — hosts register bundles explicitly. */
        fun empty(): NativeRuntimeRegistry = NativeRuntimeRegistry(emptyList())
    }
}

// ===========================================================================
// NativeRuntimes — fetcher  (INativeRuntimeFetcher.cs, NativeRuntimeFetcher.cs)
// ===========================================================================

/**
 * Materialises a runtime archive's bytes for a bundle. In C# the concrete
 * fetcher downloads from ModelScope / GitHub over HTTP; that network concern is
 * injected here so the fetcher stays deterministic and in-memory.
 */
fun interface RuntimeArchiveProvider {
    /** Return the archive bytes for [bundle], or null when the archive cannot be provided. */
    suspend fun fetch(bundle: NativeRuntimeBundle): ByteArray?
}

/** Pre-built MNN native runtime fetcher. Single source of truth for the on-disk runtime tree. */
interface INativeRuntimeFetcher {
    /**
     * Ensure the runtime archive matching (os, arch, backend) is present and
     * "extracted". Returns the install pointing at the resolved native library.
     */
    suspend fun ensureRuntime(
        os: OperatingSystemKind,
        arch: ArchitectureKind,
        backend: BackendKind,
        progress: ((Double) -> Unit)? = null,
    ): NativeRuntimeInstall

    /** True when the runtime for the requested tuple is already cached. No network I/O. */
    suspend fun isRuntimeCached(os: OperatingSystemKind, arch: ArchitectureKind, backend: BackendKind): Boolean

    /** Lists the runtime bundles known to the registry for diagnostics. */
    fun listAvailableBundles(): List<NativeRuntimeBundle>
}

/**
 * Deterministic in-memory [INativeRuntimeFetcher]. Resolves the bundle via the
 * [NativeRuntimeRegistry], obtains archive bytes via the injected
 * [RuntimeArchiveProvider] (SHA-256-verified when the bundle is pinned), and
 * records an install pointing at a synthetic extract root. Hosts that need real
 * disk extraction swap in a native fetcher behind the same contract.
 */
class InMemoryNativeRuntimeFetcher(
    private val registry: NativeRuntimeRegistry,
    private val archiveProvider: RuntimeArchiveProvider,
    private val cacheRoot: String = "runtime-cache",
) : INativeRuntimeFetcher {

    private val installs = HashMap<String, NativeRuntimeInstall>()
    private val lock = Any()

    override fun listAvailableBundles(): List<NativeRuntimeBundle> = registry.all

    override suspend fun isRuntimeCached(
        os: OperatingSystemKind,
        arch: ArchitectureKind,
        backend: BackendKind,
    ): Boolean {
        val bundle = registry.find(os, arch, backend) ?: return false
        return synchronized(lock) { installs.containsKey(extractDir(bundle)) }
    }

    override suspend fun ensureRuntime(
        os: OperatingSystemKind,
        arch: ArchitectureKind,
        backend: BackendKind,
        progress: ((Double) -> Unit)?,
    ): NativeRuntimeInstall {
        val bundle = registry.find(os, arch, backend)
            ?: throw IllegalStateException(
                "No native runtime bundle registered for ($os, $arch, $backend). " +
                    "Available bundles: " +
                    registry.all.joinToString(", ") { "(${it.os},${it.arch},${it.backend})" },
            )

        val extractDir = extractDir(bundle)

        // Fast path: already materialised.
        synchronized(lock) { installs[extractDir] }?.let {
            progress?.invoke(1.0)
            return it
        }

        // Slow path: fetch archive bytes, verify SHA (when pinned), record install.
        val bytes = archiveProvider.fetch(bundle)
            ?: throw IllegalStateException(
                "Archive provider returned no bytes for bundle (${bundle.os}, ${bundle.arch}, ${bundle.backend}, MNN ${bundle.mnnVersion}).",
            )

        val expected = bundle.archiveSha256Hex
        if (expected != null && !verifySha256(bytes, expected)) {
            throw IllegalStateException(
                "SHA-256 mismatch for runtime bundle (${bundle.os}, ${bundle.arch}, ${bundle.backend}, MNN ${bundle.mnnVersion}).",
            )
        }

        val corePath = "$extractDir/${bundle.mnnCoreLibraryName}"
        val install = NativeRuntimeInstall(bundle, extractDir, corePath)
        synchronized(lock) { installs[extractDir] = install }
        progress?.invoke(1.0)
        return install
    }

    private fun extractDir(b: NativeRuntimeBundle): String =
        "$cacheRoot/${b.mnnVersion}-${b.os.name.lowercase()}-${b.arch.name.lowercase()}-${b.backend.name.lowercase()}"

    private fun verifySha256(bytes: ByteArray, expectedHex: String): Boolean {
        val actual = java.security.MessageDigest.getInstance("SHA-256").digest(bytes)
        val actualHex = actual.joinToString("") { "%02X".format(it) }
        return actualHex.equals(expectedHex, ignoreCase = true)
    }
}
