// Device.kt
//
// DeviceProbe + DeviceTier + DeviceTierDefaults + IDeviceContext.
// Port of CircleAI.Core.DeviceProbe.

package com.bhengubv.circleai.device

import java.io.File
import java.lang.management.ManagementFactory
import java.time.Instant
import java.util.Locale
import java.util.TimeZone

enum class GpuKind { NONE, INTEGRATED, DISCRETE, NPU, METAL, VULKAN, OPEN_CL }
enum class ThermalClass { ACTIVE, PASSIVE, CONSTRAINED, SEALED }
enum class Connectivity { UNKNOWN, OFFLINE, MESH_ONLY, METERED, UNLIMITED }
enum class DeviceTier { WEARABLE, PHONE, TABLET, DESKTOP, WORKSTATION }

data class DeviceProbe(
    val ramAvailableBytes: Long,
    val storageFreeBytes: Long,
    val cpuCores: Int,
    val gpuKind: GpuKind = GpuKind.NONE,
    val thermalClass: ThermalClass = ThermalClass.ACTIVE,
    val connectivity: Connectivity = Connectivity.UNKNOWN,
) {
    fun classify(): DeviceTier {
        val gb = ramAvailableBytes.toDouble() / (1024.0 * 1024 * 1024)
        return when {
            thermalClass == ThermalClass.SEALED -> DeviceTier.WEARABLE
            gb < 2 || thermalClass == ThermalClass.CONSTRAINED -> DeviceTier.PHONE
            gb < 8 || thermalClass == ThermalClass.PASSIVE -> DeviceTier.TABLET
            gb < 32 -> DeviceTier.DESKTOP
            else -> DeviceTier.WORKSTATION
        }
    }

    companion object {
        fun snapshot(
            modelCacheDirectory: String? = null,
            gpuOverride: GpuKind? = null,
            thermalOverride: ThermalClass? = null,
        ): DeviceProbe {
            return DeviceProbe(
                ramAvailableBytes = probeRamAvailable(),
                storageFreeBytes = probeStorageFree(modelCacheDirectory),
                cpuCores = Runtime.getRuntime().availableProcessors(),
                gpuKind = gpuOverride ?: GpuKind.NONE,
                thermalClass = thermalOverride ?: ThermalClass.ACTIVE,
                connectivity = Connectivity.UNKNOWN,
            )
        }

        private fun probeRamAvailable(): Long {
            // JVM hint: max heap available. For real OS-level free RAM use
            // OperatingSystemMXBean.getFreeMemorySize when the host runtime
            // supports it (com.sun.management.OperatingSystemMXBean).
            val osBean = ManagementFactory.getOperatingSystemMXBean()
            return try {
                // Reflection-based read avoids a hard dep on com.sun.management.
                val method = osBean.javaClass.getMethod("getFreeMemorySize")
                method.isAccessible = true
                (method.invoke(osBean) as? Long) ?: 0L
            } catch (_: Throwable) {
                Runtime.getRuntime().freeMemory()
            }
        }

        private fun probeStorageFree(path: String?): Long {
            if (path.isNullOrBlank()) return 0L
            return try {
                File(path).usableSpace
            } catch (_: Throwable) {
                0L
            }
        }
    }
}

object DeviceTierDefaults {
    fun contextWindow(tier: DeviceTier): Int = when (tier) {
        DeviceTier.WEARABLE -> 2048
        DeviceTier.PHONE -> 4096
        DeviceTier.TABLET -> 8192
        DeviceTier.DESKTOP -> 32_768
        DeviceTier.WORKSTATION -> 131_072
    }

    fun maxConcurrency(tier: DeviceTier, cpuCores: Int): Int = when (tier) {
        DeviceTier.WEARABLE -> 1
        DeviceTier.PHONE -> 2
        DeviceTier.TABLET -> 4
        DeviceTier.DESKTOP -> 8
        DeviceTier.WORKSTATION -> minOf(16, maxOf(1, cpuCores - 2))
    }

    fun agenticMaxIterations(tier: DeviceTier): Int = when (tier) {
        DeviceTier.WEARABLE -> 2
        DeviceTier.PHONE -> 3
        DeviceTier.TABLET -> 5
        DeviceTier.DESKTOP, DeviceTier.WORKSTATION -> 10
    }
}

/** Sensorium contract. All nullable; the SDK degrades gracefully. */
interface IDeviceContext {
    val activeAppId: String? get() = null
    val locale: String? get() = null
    val timeZoneId: String? get() = null
    val localTime: Instant? get() = null
    val latitude: Double? get() = null
    val longitude: Double? get() = null
    val locationHint: String? get() = null
    val batteryLevel: Float? get() = null
    val isCharging: Boolean? get() = null
    val networkType: String? get() = null
    val cpuUsagePercent: Float? get() = null
    val availableMemoryBytes: Long? get() = null
    val thermalState: String? get() = null
    val storageFreeBytes: Long? get() = null
    val lastActiveUtc: Instant? get() = null
}

object NullDeviceContext : IDeviceContext

class DefaultDeviceContext(
    private val modelCacheDir: String = "",
    private val thermalHint: ThermalClass = ThermalClass.ACTIVE,
) : IDeviceContext {
    override val locale: String? = Locale.getDefault().toLanguageTag()
    override val timeZoneId: String? = TimeZone.getDefault().id
    override val localTime: Instant? get() = Instant.now()
    override val networkType: String? get() = null
    override val availableMemoryBytes: Long?
        get() {
            val r = Runtime.getRuntime()
            return r.maxMemory() - (r.totalMemory() - r.freeMemory())
        }
    override val thermalState: String? = "normal"
    override val storageFreeBytes: Long?
        get() = if (modelCacheDir.isBlank()) null else File(modelCacheDir).usableSpace

    fun buildProbe(gpuOverride: GpuKind? = null): DeviceProbe =
        DeviceProbe.snapshot(modelCacheDir.takeIf { it.isNotBlank() }, gpuOverride, thermalHint)
}
