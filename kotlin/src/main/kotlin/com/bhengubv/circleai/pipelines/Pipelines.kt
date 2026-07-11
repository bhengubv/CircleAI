// Pipelines.kt
//
// Kotlin port of CircleAI.Pipelines — the C# reference is the EXACT spec
// (Contracts.cs, InMemoryPipelines.cs, NullImplementations.cs).
//
// Data-pipeline source / sink / executor, plus a tiny in-memory
// database-query tool operating on a dictionary of in-memory tables. The
// executor wires registered pipelines (a function that reads from a source and
// writes to a sink) and tracks runs in a dictionary.
//
// C# -> Kotlin conventions:
//   IAsyncEnumerable<T>  -> kotlinx.coroutines.flow.Flow<T>
//   Channel (unbounded)  -> Channel(UNLIMITED) per stream
//   object?              -> Any?
//   ValueTask / async    -> suspend fun
//   DateTimeOffset       -> java.time.Instant

package com.bhengubv.circleai.pipelines

import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import java.time.Instant
import java.util.concurrent.atomic.AtomicLong

// ===========================================================================
// Contracts  (Contracts.cs)
// ===========================================================================

data class PipelineRecord(val stream: String, val values: Map<String, Any?>)

data class PipelineRun(
    val runId: String,
    val pipelineId: String,
    val startUtc: Instant,
    val endUtc: Instant?,
    val rowsProcessed: Long,
    val failureReason: String?,
)

interface IPipelineSource {
    val backendId: String
    fun read(stream: String): Flow<PipelineRecord>
}

interface IPipelineSink {
    val backendId: String
    suspend fun write(record: PipelineRecord)
    suspend fun flush()
}

interface IPipelineExecutor {
    val backendId: String
    suspend fun run(pipelineId: String): PipelineRun
    suspend fun getRun(runId: String): PipelineRun?
}

data class DatabaseQueryResult(val rows: List<Map<String, Any?>>, val rowCount: Int)

interface IDatabaseQueryTool {
    val backendId: String
    suspend fun query(sql: String, parameters: Map<String, Any?>? = null): DatabaseQueryResult
}

// ===========================================================================
// In-memory implementations  (InMemoryPipelines.cs)
// ===========================================================================

class InMemoryPipelineSource : IPipelineSource {
    private val streams = HashMap<String, Channel<PipelineRecord>>()
    private val lock = Any()

    override val backendId: String get() = "in-memory"

    private fun channelFor(stream: String): Channel<PipelineRecord> =
        synchronized(lock) { streams.getOrPut(stream) { Channel(Channel.UNLIMITED) } }

    fun push(stream: String, record: PipelineRecord) {
        require(stream.isNotBlank()) { "stream required" }
        channelFor(stream).trySend(record)
    }

    fun complete(stream: String) {
        synchronized(lock) { streams[stream] }?.close()
    }

    override fun read(stream: String): Flow<PipelineRecord> {
        require(stream.isNotBlank()) { "stream required" }
        val ch = channelFor(stream)
        return flow {
            for (record in ch) {
                emit(record)
            }
        }
    }
}

class InMemoryPipelineSink : IPipelineSink {
    private val records = ArrayList<PipelineRecord>()
    private val lock = Any()

    override val backendId: String get() = "in-memory"

    override suspend fun write(record: PipelineRecord) {
        synchronized(lock) { records.add(record) }
    }

    override suspend fun flush() {}

    val recordsSnapshot: List<PipelineRecord>
        get() = synchronized(lock) { records.toList() }
}

class InMemoryPipelineExecutor : IPipelineExecutor {
    private val pipelines = HashMap<String, suspend () -> Long>()
    private val runs = HashMap<String, PipelineRun>()
    private val lock = Any()
    private val runSeq = AtomicLong(0)

    override val backendId: String get() = "in-memory"

    fun register(pipelineId: String, runner: suspend () -> Long) {
        require(pipelineId.isNotBlank()) { "pipelineId required" }
        synchronized(lock) { pipelines[pipelineId] = runner }
    }

    override suspend fun run(pipelineId: String): PipelineRun {
        require(pipelineId.isNotBlank()) { "pipelineId required" }
        val runner = synchronized(lock) { pipelines[pipelineId] }
            ?: throw IllegalStateException("Unknown pipeline '$pipelineId'.")

        val runId = "run-${runSeq.incrementAndGet()}"
        val start = Instant.now()
        var rows = 0L
        var err: String? = null
        try {
            rows = runner()
        } catch (ex: Exception) {
            err = ex.message
        }
        val run = PipelineRun(runId, pipelineId, start, Instant.now(), rows, err)
        synchronized(lock) { runs[runId] = run }
        return run
    }

    override suspend fun getRun(runId: String): PipelineRun? {
        require(runId.isNotBlank()) { "runId required" }
        return synchronized(lock) { runs[runId] }
    }
}

/** Tiny in-memory database — supports simple SELECTs against registered tables. */
class InMemoryDatabaseQueryTool : IDatabaseQueryTool {
    // case-insensitive table names
    private val tables = HashMap<String, MutableList<Map<String, Any?>>>()
    private val lock = Any()

    override val backendId: String get() = "in-memory"

    fun insert(tableName: String, row: Map<String, Any?>) {
        require(tableName.isNotBlank()) { "tableName required" }
        synchronized(lock) {
            tables.getOrPut(tableName.lowercase()) { ArrayList() }.add(LinkedHashMap(row))
        }
    }

    override suspend fun query(sql: String, parameters: Map<String, Any?>?): DatabaseQueryResult {
        require(sql.isNotBlank()) { "sql required" }
        val trimmed = sql.trim()
        if (!trimmed.startsWith("SELECT ", ignoreCase = true)) {
            throw UnsupportedOperationException("Only SELECT queries are supported by InMemoryDatabaseQueryTool.")
        }

        // "SELECT * FROM <table>" (extremely simple parser; sufficient for in-memory use).
        val fromIdx = trimmed.indexOf("FROM ", ignoreCase = true)
        if (fromIdx < 0) throw IllegalStateException("SELECT requires a FROM clause.")
        val rest = trimmed.substring(fromIdx + 5).trim()
        val spaceIdx = rest.indexOfFirst { it == ' ' || it == ';' }
        val tableName = if (spaceIdx > 0) rest.substring(0, spaceIdx) else rest

        val rows: List<Map<String, Any?>> = synchronized(lock) {
            tables[tableName.lowercase()]?.toList() ?: return DatabaseQueryResult(emptyList(), 0)
        }
        return DatabaseQueryResult(rows, rows.size)
    }
}

// ===========================================================================
// Null implementations  (NullImplementations.cs)
// ===========================================================================

private val NULL_GUID: String = java.util.UUID(0, 0).toString()

class NullPipelineSource private constructor() : IPipelineSource {
    override val backendId: String get() = "null"
    override fun read(stream: String): Flow<PipelineRecord> = flow { }

    companion object {
        val Instance = NullPipelineSource()
    }
}

class NullPipelineSink private constructor() : IPipelineSink {
    override val backendId: String get() = "null"
    override suspend fun write(record: PipelineRecord) {}
    override suspend fun flush() {}

    companion object {
        val Instance = NullPipelineSink()
    }
}

class NullPipelineExecutor private constructor() : IPipelineExecutor {
    override val backendId: String get() = "null"
    override suspend fun run(pipelineId: String): PipelineRun =
        PipelineRun(NULL_GUID, pipelineId, Instant.MIN, Instant.MIN, 0, "NullPipelineExecutor")

    override suspend fun getRun(runId: String): PipelineRun? = null

    companion object {
        val Instance = NullPipelineExecutor()
    }
}

class NullDatabaseQueryTool private constructor() : IDatabaseQueryTool {
    override val backendId: String get() = "null"
    override suspend fun query(sql: String, parameters: Map<String, Any?>?): DatabaseQueryResult =
        DatabaseQueryResult(emptyList(), 0)

    companion object {
        val Instance = NullDatabaseQueryTool()
    }
}
