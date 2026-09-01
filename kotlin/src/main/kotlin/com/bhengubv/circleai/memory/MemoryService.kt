// MemoryService.kt
//
// The memory an application actually holds.
//
// EVERYTHING UNTIL NOW HAS BEEN PIECES. A store, a log, a curve, a command -
// each one correct and none of them a memory an app has. This is the thing that
// gets held.
//
// IT IS BUILT FOR BEING KILLED, because that is the ordinary case rather than
// the exception. An app on a phone does not get to finish what it was doing:
// the system takes it for memory, the person swipes it away, the battery goes.
// So nothing here is held back for later. Atoms go to the log the moment they
// are recorded, and the wear that decides what has faded is written on the way
// OUT of every recall - not on a timer, and not on a lifecycle callback, both
// of which a force-stop walks straight past.
//
// ONE STORE, GUARDED. A SQLite connection is not thread-safe and an app will
// reach for its memory from the UI thread and a background one in the same
// second. Memory operations are not a parallel workload, so they are serialised
// rather than made clever.
//
// NO PLATFORM IN HERE. It takes a folder path and a store, so the same service
// is what Android holds, what a server holds, and what a test holds.

package com.bhengubv.circleai.memory

import java.util.UUID
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

interface IMemoryService {
    suspend fun recall(situation: Situation, budget: RecallBudget? = null): RecallResult
    suspend fun remember(atom: MemoryAtom, supersedes: UUID? = null)
    suspend fun learn(wasSaid: String, subject: String? = null): LearnReport
    suspend fun all(limit: Int = 200): List<MemoryAtom>
    suspend fun count(): Int
}

/**
 * @param store the index. The caller opens it, because opening a database is
 *   the one thing in this file that is platform-shaped - JDBC on a server, the
 *   platform SQLite on a phone - and this class must not know which.
 * @param wearStore where retrieval wear is persisted; null keeps it in memory
 *   only, which is what a test wants and what a read-only folder gets.
 */
class MemoryService(
    folderPath: String,
    private val store: IAtomStore,
    machine: String? = null,
    private val wearStore: ((Map<UUID, MemoryTrace>) -> Unit)? = null,
) : IMemoryService {

    private val folder = MemoryFolder(folderPath, machine)
    private val sync: MemorySync
    private val wear = MemoryWear()
    private val recall: Recall
    private val learner = AtomLearner()

    // ONE AT A TIME. Not for throughput - a memory is asked a few times a
    // minute by a person - but because the alternative is a torn read on a
    // connection two threads are using, which fails rarely and unreproducibly.
    private val one = Mutex()

    init {
        folder.ensureGitIgnore()
        sync = MemorySync(folder)
        recall = Recall(store, wear)
    }

    val path: String get() = folder.path
    val machineName: String get() = folder.machine
    val log: AtomLog get() = sync.log

    /** Replays every machine log into the index. */
    suspend fun rebuild(): SyncReport = one.withLock { sync.rebuild(store) }

    override suspend fun recall(situation: Situation, budget: RecallBudget?): RecallResult =
        one.withLock {
            val result = recall.forSituation(situation, budget)
            // WRITTEN NOW, NOT LATER. Recall is the only thing that changes
            // wear, and holding it back would mean a force-stop - which never
            // calls a lifecycle callback, and is how a phone usually kills an
            // app - taking the session familiarity with it.
            flushWear()
            result
        }

    override suspend fun remember(atom: MemoryAtom, supersedes: UUID?) {
        one.withLock {
            // Straight through to the log, which is the durable half. Nothing
            // is queued, so nothing is lost when the app goes away.
            sync.record(store, atom, supersedes)
        }
    }

    override suspend fun learn(wasSaid: String, subject: String?): LearnReport {
        if (wasSaid.isBlank()) return LearnReport(0, emptyList(), emptyList(), emptyList())
        return one.withLock {
            val episode = EpisodicMemoryEntry(
                id = UUID.randomUUID().toString(),
                userId = "",
                content = wasSaid,
                embedding = FloatArray(0),
                userText = wasSaid,
                appContext = subject,
                recordedAtUtc = java.time.Instant.now(),
            )
            // Asked with an INDEX rather than handed the whole memory: this runs
            // on every turn of a conversation.
            learner.learn(
                listOf(episode),
                { candidate -> sync.record(store, candidate) },
                { text -> store.knows(text) },
                subject,
            )
        }
    }

    override suspend fun all(limit: Int): List<MemoryAtom> = one.withLock { store.all(limit = limit) }

    override suspend fun count(): Int = one.withLock { store.count() }

    private fun flushWear() {
        if (!wear.isDirty) return
        wearStore?.invoke(wear.snapshot())
        wear.markClean()
    }
}

// -------------------------------------------------------- Module memory

/**
 * What a module is allowed to KEEP.
 *
 * RULES_ONLY is not "no memory". A live interpreter must never retain what
 * passes through it, because those are two other people words; a safety gate
 * must never remember that something was allowed, because being talked past
 * once would then buy you past it forever. But "never keep this" is ITSELF a
 * thing that has to be remembered, and a module with no continuity cannot
 * remember its own prohibition.
 *
 * So the line is not which modules have memory. It is WHAT THEY HOLD: the
 * interpreter remembers "never keep what passes through me", never the words.
 */
enum class MemoryRetention { EVERYTHING, RULES_ONLY }

interface IModuleMemory {
    val module: String
    val retention: MemoryRetention
    suspend fun recall(situation: Situation, budget: RecallBudget? = null): RecallResult
    suspend fun remember(atom: MemoryAtom, supersedes: UUID? = null): Boolean
    suspend fun heard(said: String, subject: String? = null): LearnReport
}

/**
 * A module own view of the memory the device holds.
 *
 * MEMORY IS A SERVICE EVERY MODULE CONSUMES, not a feature one app has. There
 * is one memory on a device and a hundred and fifty things that might want it,
 * and each needs the same two answers: what do we already know, and how do I
 * record something without pretending it came from somewhere else.
 *
 * THE GUARANTEE IS IN THE REGISTRATION, NOT IN THE MEMORY. The retention a
 * module was built with is declared where it is constructed, so it holds even
 * on a device whose memory was wiped, edited, or has not been written to yet.
 * A rule that could be forgotten is not a rule, and a prohibition that fails
 * open is worse than none at all.
 */
class ModuleMemory(
    private val memory: IMemoryService,
    module: String,
    override val retention: MemoryRetention = MemoryRetention.EVERYTHING,
) : IModuleMemory {

    override val module: String

    init {
        if (module.isBlank()) throw IllegalArgumentException("A module has to say what it is.")
        this.module = module.trim().lowercase()
    }

    override suspend fun recall(situation: Situation, budget: RecallBudget?): RecallResult =
        memory.recall(situation, budget)

    override suspend fun remember(atom: MemoryAtom, supersedes: UUID?): Boolean {
        if (!mayKeep(atom.kind)) return false
        memory.remember(owned(atom), supersedes)
        return true
    }

    override suspend fun heard(said: String, subject: String?): LearnReport {
        // A module that must not retain what passes through it does not extract
        // from it either. The words never reach the learner.
        if (retention == MemoryRetention.RULES_ONLY) return NOTHING
        return memory.learn(said, subject ?: module)
    }

    private fun mayKeep(kind: AtomKind): Boolean =
        retention == MemoryRetention.EVERYTHING ||
            kind == AtomKind.RULING || kind == AtomKind.PREFERENCE || kind == AtomKind.RELATIONSHIP

    /**
     * PREFIXED rather than replaced, so "interpret:languages" still rolls up to
     * "interpret" and a module whole memory can be read at once.
     */
    private fun owned(atom: MemoryAtom): MemoryAtom {
        val subject = atom.subject
        val owned = when {
            subject.isNullOrEmpty() -> module
            subject.startsWith(module + ":") -> subject
            else -> module + ":" + subject
        }
        return atom.copy(subject = owned)
    }

    companion object {
        private val NOTHING = LearnReport(0, emptyList(), emptyList(), emptyList())
    }
}
