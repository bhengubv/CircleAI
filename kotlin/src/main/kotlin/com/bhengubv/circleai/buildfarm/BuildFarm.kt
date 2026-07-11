// BuildFarm.kt
//
// Kotlin port of CircleAI.BuildFarm — the C# reference is the EXACT spec
// (Contracts.cs, InMemoryBuildFarm.cs, NullImplementations.cs).
//
// Build-farm primitives: an agent pool, a job runner (state machine:
// Pending -> Running -> Succeeded/Failed), and an artifact store. Hosts that
// integrate real CI swap in a real impl.
//
// C# -> Kotlin conventions: ReadOnlyMemory<byte> -> ByteArray,
// DateTimeOffset -> java.time.Instant, ValueTask -> suspend,
// Interlocked.Increment -> AtomicLong, Guid.Empty -> all-zero UUID.

package com.bhengubv.circleai.buildfarm

import java.time.Instant
import java.util.UUID
import java.util.concurrent.atomic.AtomicLong

// ===========================================================================
// Contracts  (Contracts.cs)
// ===========================================================================

enum class BuildAgentKind { Linux, Mac, Windows, Android, Ios }
enum class BuildJobPhase { Pending, Running, Succeeded, Failed }

data class BuildAgent(val agentId: String, val kind: BuildAgentKind, val os: String, val hardware: String?)

data class BuildJob(
    val jobId: String,
    val agentId: String,
    val repo: String,
    val branch: String,
    val phase: BuildJobPhase,
    val startUtc: Instant,
)

data class BuildArtifact(val artifactId: String, val jobId: String, val name: String, val payload: ByteArray) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is BuildArtifact) return false
        return artifactId == other.artifactId &&
            jobId == other.jobId &&
            name == other.name &&
            payload.contentEquals(other.payload)
    }

    override fun hashCode(): Int {
        var result = artifactId.hashCode()
        result = 31 * result + jobId.hashCode()
        result = 31 * result + name.hashCode()
        result = 31 * result + payload.contentHashCode()
        return result
    }
}

interface IBuildAgentPool {
    val backendId: String
    suspend fun acquire(kind: BuildAgentKind): BuildAgent?
    suspend fun release(agentId: String)
    suspend fun list(): List<BuildAgent>
}

interface IBuildJobRunner {
    val backendId: String
    suspend fun start(agentId: String, repo: String, branch: String): BuildJob
    suspend fun get(jobId: String): BuildJob?
}

interface IBuildArtifactStore {
    val backendId: String
    suspend fun save(artifact: BuildArtifact)
    suspend fun get(artifactId: String): BuildArtifact?
}

// ===========================================================================
// In-memory implementations  (InMemoryBuildFarm.cs)
// ===========================================================================

class InMemoryBuildAgentPool : IBuildAgentPool {
    private val all = LinkedHashMap<String, BuildAgent>()
    private val busy = HashSet<String>()
    private val lock = Any()

    override val backendId: String get() = "in-memory"

    fun register(a: BuildAgent) {
        synchronized(lock) { all[a.agentId] = a }
    }

    override suspend fun acquire(kind: BuildAgentKind): BuildAgent? = synchronized(lock) {
        for (a in all.values.filter { it.kind == kind }) {
            if (busy.add(a.agentId)) return a
        }
        null
    }

    override suspend fun release(agentId: String) {
        require(agentId.isNotBlank()) { "agentId required" }
        synchronized(lock) { busy.remove(agentId) }
    }

    override suspend fun list(): List<BuildAgent> = synchronized(lock) { all.values.toList() }
}

class InMemoryBuildJobRunner : IBuildJobRunner {
    private val jobs = HashMap<String, BuildJob>()
    private val lock = Any()
    private val seq = AtomicLong(0)

    override val backendId: String get() = "in-memory"

    override suspend fun start(agentId: String, repo: String, branch: String): BuildJob {
        require(agentId.isNotBlank()) { "agentId required" }
        require(repo.isNotBlank()) { "repo required" }
        require(branch.isNotBlank()) { "branch required" }
        val jobId = "job-${seq.incrementAndGet()}"
        val job = BuildJob(jobId, agentId, repo, branch, BuildJobPhase.Running, Instant.now())
        synchronized(lock) { jobs[jobId] = job }
        return job
    }

    override suspend fun get(jobId: String): BuildJob? {
        require(jobId.isNotBlank()) { "jobId required" }
        return synchronized(lock) { jobs[jobId] }
    }

    fun complete(jobId: String, success: Boolean) {
        synchronized(lock) {
            val j = jobs[jobId] ?: throw IllegalStateException("Unknown job $jobId")
            jobs[jobId] = j.copy(phase = if (success) BuildJobPhase.Succeeded else BuildJobPhase.Failed)
        }
    }
}

class InMemoryBuildArtifactStore : IBuildArtifactStore {
    private val items = HashMap<String, BuildArtifact>()
    private val lock = Any()
    override val backendId: String get() = "in-memory"

    override suspend fun save(artifact: BuildArtifact) {
        require(artifact.artifactId.isNotBlank()) { "ArtifactId required" }
        synchronized(lock) { items[artifact.artifactId] = artifact }
    }

    override suspend fun get(artifactId: String): BuildArtifact? {
        require(artifactId.isNotBlank()) { "artifactId required" }
        return synchronized(lock) { items[artifactId] }
    }
}

// ===========================================================================
// Null implementations  (NullImplementations.cs)
// ===========================================================================

private val EMPTY_GUID: String = UUID(0, 0).toString()

class NullBuildAgentPool private constructor() : IBuildAgentPool {
    override val backendId: String get() = "null"
    override suspend fun acquire(kind: BuildAgentKind): BuildAgent? = null
    override suspend fun release(agentId: String) {}
    override suspend fun list(): List<BuildAgent> = emptyList()

    companion object {
        val Instance = NullBuildAgentPool()
    }
}

class NullBuildJobRunner private constructor() : IBuildJobRunner {
    override val backendId: String get() = "null"
    override suspend fun start(agentId: String, repo: String, branch: String): BuildJob =
        BuildJob(EMPTY_GUID, agentId, repo, branch, BuildJobPhase.Failed, Instant.MIN)

    override suspend fun get(jobId: String): BuildJob? = null

    companion object {
        val Instance = NullBuildJobRunner()
    }
}

class NullBuildArtifactStore private constructor() : IBuildArtifactStore {
    override val backendId: String get() = "null"
    override suspend fun save(artifact: BuildArtifact) {}
    override suspend fun get(artifactId: String): BuildArtifact? = null

    companion object {
        val Instance = NullBuildArtifactStore()
    }
}
