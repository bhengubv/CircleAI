// MemorySync.kt
//
// Turning three machines logs into ONE local index.
//
// THE INDEX IS DISPOSABLE AND THE LOGS ARE NOT. Replay rebuilds the database
// from the text, which means a corrupt index, a schema change, or a machine
// that has never seen the folder before all cost the same thing: a rebuild.
// Nothing about a memory depends on a file git cannot merge.
//
// SUPERSEDING IS RESOLVED HERE, not in the log. A log line can only point
// BACKWARDS at what it replaces; the forward pointer the index wants is worked
// out by walking the records in time order. Doing it during replay is also what
// makes a correction on the Mac apply to a decision made on Windows - they are
// just two lines in one ordered stream.

package com.bhengubv.circleai.memory

import java.time.Instant
import java.util.UUID

data class SyncReport(val records: Int, val atoms: Int, val current: Int, val machines: Int)

class MemorySync(private val folder: MemoryFolder) {

    val log = AtomLog(folder)

    suspend fun record(store: IAtomStore, atom: MemoryAtom, supersedes: UUID? = null) {
        // INDEX WHAT THE LOG SAYS, not what the caller passed. The line is
        // stamped with this machine and normalised on the way out, and reading
        // it back is what makes "the index now" and "the index after a rebuild"
        // the same thing without two pieces of code having to agree.
        val stored = AtomLog.rehydrate(log.append(atom, supersedes))
        if (supersedes != null) store.supersede(supersedes, stored) else store.add(stored)
    }

    suspend fun rebuild(store: IAtomStore): SyncReport {
        val replay = replay()
        if (replay.records == 0) return SyncReport(0, 0, 0, 0)

        var stored = 0
        for (atom in replay.atoms) {
            store.add(atom)
            stored++
        }

        return SyncReport(
            records = replay.records,
            atoms = stored,
            current = replay.atoms.count { it.isCurrent },
            machines = replay.machines,
        )
    }

    fun current(): List<MemoryAtom> = replay().atoms.filter { it.isCurrent }

    internal data class Replayed(val records: Int, val machines: Int, val atoms: List<MemoryAtom>)

    internal fun replay(): Replayed {
        val records = log.readAll()
        if (records.isEmpty()) return Replayed(0, 0, emptyList())

        val atoms = LinkedHashMap<String, MemoryAtom>()
        val supersededBy = HashMap<String, String>()
        val corrections = HashMap<String, Int>()
        val correctedAt = HashMap<String, Instant>()

        for (record in records) {
            val old = record.supersedes
            if (!old.isNullOrEmpty()) {
                supersededBy[old] = record.id
                // THE COUNT CARRIES DOWN THE CHAIN, so an atom corrected on
                // three different machines reads as corrected three times rather
                // than once each - which is what makes a much-argued rule
                // outrank a fresh one.
                corrections[record.id] = (corrections[old] ?: 0) + 1
                correctedAt[record.id] = AtomLog.time(record.recorded)
            }
            atoms[record.id] = AtomLog.rehydrate(record)
        }

        val finished = atoms.map { (key, value) ->
            value.copy(
                corrections = corrections[key] ?: 0,
                lastCorrectedUtc = correctedAt[key],
                supersededBy = supersededBy[key]?.let { AtomLog.parseCompact(it) },
            )
        }

        return Replayed(
            records = records.size,
            machines = records.map { it.machine.lowercase() }.distinct().size,
            atoms = finished,
        )
    }
}
