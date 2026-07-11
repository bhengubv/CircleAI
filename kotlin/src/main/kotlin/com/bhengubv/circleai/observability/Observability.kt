// Observability.kt
//
// Kotlin port of CircleAI.Observability (Contracts.cs + InMemoryObservability.cs
// + NullImplementations.cs) — the C# reference is the EXACT spec. Metric sink,
// trace sink, and dashboard publisher, each with a real in-memory backing and a
// drop-all null default.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `DateTimeOffset` -> `java.time.Instant`; C# `TimeSpan` -> `java.time.Duration`.
//   * C# `ValueTask` async members -> `suspend fun`.
//   * Metric sink aggregates samples per-name; trace sink stores spans per traceId
//     ordered by StartUtc; dashboard publisher round-trips specs by id.

package com.bhengubv.circleai.observability

import java.time.Duration
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/** One metric observation. Mirrors C# `MetricSample`. */
data class MetricSample(val name: String, val value: Double, val tags: Map<String, String>? = null)

/** One trace span. Mirrors C# `TraceSpan`. */
data class TraceSpan(
    val traceId: String,
    val spanId: String,
    val parentSpanId: String?,
    val name: String,
    val startUtc: Instant,
    val duration: Duration,
    val attributes: Map<String, String>? = null,
)

/** A dashboard specification blob. Mirrors C# `DashboardSpec`. */
data class DashboardSpec(val dashboardId: String, val title: String, val jsonBlob: String)

/** Metric sink — Prometheus / OTel. Mirrors C# `IMetricSink`. */
interface IMetricSink {
    val backendId: String
    suspend fun emitAsync(sample: MetricSample)
}

/** Trace sink — OTel. Mirrors C# `ITraceSink`. */
interface ITraceSink {
    val backendId: String
    suspend fun emitAsync(span: TraceSpan)
}

/** Dashboard publisher — Grafana / claude-team-dashboard. Mirrors C# `IDashboardPublisher`. */
interface IDashboardPublisher {
    val backendId: String
    suspend fun publishAsync(spec: DashboardSpec)
}

// =====================================================================
// In-memory implementations (InMemoryObservability.cs)
// =====================================================================

/** In-memory [IMetricSink] aggregating samples per name. Mirrors C# `InMemoryMetricSink`. */
class InMemoryMetricSink : IMetricSink {
    private val byName = ConcurrentHashMap<String, MutableList<MetricSample>>()
    private val lock = Any()

    override val backendId: String get() = "in-memory"

    override suspend fun emitAsync(sample: MetricSample) {
        require(sample.name.isNotBlank()) { "Name required" }
        synchronized(lock) {
            byName.getOrPut(sample.name) { mutableListOf() }.add(sample)
        }
    }

    fun read(name: String): List<MetricSample> {
        synchronized(lock) {
            return byName[name]?.toList() ?: emptyList()
        }
    }

    val metricNames: List<String> get() = byName.keys.sorted()
}

/** In-memory [ITraceSink] storing spans per traceId. Mirrors C# `InMemoryTraceSink`. */
class InMemoryTraceSink : ITraceSink {
    private val byTrace = ConcurrentHashMap<String, MutableList<TraceSpan>>()
    private val lock = Any()

    override val backendId: String get() = "in-memory"

    override suspend fun emitAsync(span: TraceSpan) {
        require(span.traceId.isNotBlank()) { "TraceId required" }
        synchronized(lock) {
            byTrace.getOrPut(span.traceId) { mutableListOf() }.add(span)
        }
    }

    fun read(traceId: String): List<TraceSpan> {
        synchronized(lock) {
            return byTrace[traceId]?.sortedBy { it.startUtc } ?: emptyList()
        }
    }
}

/** In-memory [IDashboardPublisher] round-tripping specs. Mirrors C# `InMemoryDashboardPublisher`. */
class InMemoryDashboardPublisher : IDashboardPublisher {
    private val specs = ConcurrentHashMap<String, DashboardSpec>()

    override val backendId: String get() = "in-memory"

    override suspend fun publishAsync(spec: DashboardSpec) {
        require(spec.dashboardId.isNotBlank()) { "DashboardId required" }
        specs[spec.dashboardId] = spec
    }

    fun get(dashboardId: String): DashboardSpec? = specs[dashboardId]
    val all: List<DashboardSpec> get() = specs.values.sortedBy { it.dashboardId }
}

// =====================================================================
// Null implementations (NullImplementations.cs)
// =====================================================================

/** Drop-all [IMetricSink]. Mirrors C# `NullMetricSink`. */
class NullMetricSink private constructor() : IMetricSink {
    override val backendId: String get() = "null"
    override suspend fun emitAsync(sample: MetricSample) {}

    companion object {
        val Instance = NullMetricSink()
    }
}

/** Drop-all [ITraceSink]. Mirrors C# `NullTraceSink`. */
class NullTraceSink private constructor() : ITraceSink {
    override val backendId: String get() = "null"
    override suspend fun emitAsync(span: TraceSpan) {}

    companion object {
        val Instance = NullTraceSink()
    }
}

/** Drop-all [IDashboardPublisher]. Mirrors C# `NullDashboardPublisher`. */
class NullDashboardPublisher private constructor() : IDashboardPublisher {
    override val backendId: String get() = "null"
    override suspend fun publishAsync(spec: DashboardSpec) {}

    companion object {
        val Instance = NullDashboardPublisher()
    }
}
