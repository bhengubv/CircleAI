// AtomLog.kt
//
// The durable half: an append-only line per remembered thing.
//
// THIS FILE FORMAT OUTLIVES THIS CODE. It is what actually crosses between a
// Linux box, a Windows box and a Mac, what a person can open and read, and what
// any other tool would have to understand. The database is a CACHE of it. So it
// is plain JSON, one object per line, and every field is named for what it
// means rather than for how it is stored.
//
// APPEND-ONLY CHANGES THE MODEL, and this is where it bites. A row in a table
// can be UPDATEd to say it was superseded; a line already written to a log
// cannot. So a correction is a NEW line naming what it SUPERSEDES, and the
// forward pointer is derived when the log is replayed. Nothing is ever edited
// and nothing is ever removed - which is also what makes two machines logs
// mergeable by simple concatenation.
//
// ORDER IS BY TIME, NOT BY FILE. Replay sorts every machine lines together, so
// a correction made on the Mac supersedes a decision made on Windows the same
// way it would have locally.

package com.bhengubv.circleai.memory

import java.io.File
import java.time.Instant
import java.time.format.DateTimeParseException
import java.util.UUID
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

@Serializable
data class AtomRecord(
    @SerialName("id") val id: String = "",
    @SerialName("kind") val kind: String = "Decision",
    @SerialName("text") val text: String = "",
    @SerialName("subject") val subject: String? = null,
    @SerialName("challenge") val challenge: String? = null,
    @SerialName("outcome") val outcome: String? = null,
    @SerialName("recorded") val recorded: String = "",
    @SerialName("machine") val machine: String = "",
    @SerialName("source") val sourceEpisode: String? = null,
    @SerialName("supersedes") val supersedes: String? = null,
    @SerialName("verify") val verify: String? = null,
)

class AtomLog(private val folder: MemoryFolder) {

    fun append(atom: MemoryAtom, supersedes: UUID? = null): AtomRecord {
        val record = AtomRecord(
            id = compact(atom.id),
            kind = pascal(atom.kind.name),
            text = atom.text,
            subject = atom.subject,
            challenge = atom.challenge,
            outcome = atom.outcome?.let { pascal(it.name) },
            recorded = atom.recordedAtUtc.toString(),
            machine = folder.machine,
            sourceEpisode = atom.sourceEpisode?.let { compact(it) },
            supersedes = supersedes?.let { compact(it) },
            verify = atom.verify,
        )

        // ONE LINE, ONE WRITE, and a newline first only if the file does not
        // already end in one - a half-written line from an interrupted session
        // would otherwise swallow the next record into itself.
        val file = File(folder.ownLog)
        val needsNewline = file.exists() && file.length() > 0 && !endsWithNewline(file)
        file.appendText((if (needsNewline) "\n" else "") + JSON.encodeToString(AtomRecord.serializer(), record) + "\n")
        return record
    }

    /**
     * Every record from every machine, in ONE order.
     *
     * The machine name and then the id break ties, so replay is identical on
     * all three boxes: two records with the same timestamp must not order
     * differently depending on which machine read them.
     */
    fun readAll(): List<AtomRecord> {
        val records = mutableListOf<AtomRecord>()
        for (path in folder.allLogs) {
            for (line in File(path).readLines()) {
                if (line.isBlank()) continue
                try {
                    val record = JSON.decodeFromString(AtomRecord.serializer(), line)
                    if (record.id.isNotBlank()) records.add(record)
                } catch (e: Exception) {
                    // An unreadable line. Keep the rest: one truncated write must
                    // not cost every memory in the file behind it.
                }
            }
        }
        return records.sortedWith(
            compareBy<AtomRecord> { time(it.recorded) }
                .thenBy { it.machine }
                .thenBy { it.id },
        )
    }

    companion object {
        /**
         * Lenient on the way in, exact on the way out.
         *
         * A field this does not know is IGNORED rather than fatal, because a
         * newer machine writing an extra key must not make its lines unreadable
         * to an older one - the log is what crosses between machines and it has
         * to survive them being on different versions.
         */
        internal val JSON = Json {
            ignoreUnknownKeys = true
            encodeDefaults = false
            explicitNulls = false
        }

        fun rehydrate(record: AtomRecord): MemoryAtom = MemoryAtom(
            id = parseCompact(record.id) ?: UUID.randomUUID(),
            kind = AtomKind.entries.firstOrNull { it.name.equals(underscore(record.kind), true) }
                ?: AtomKind.DECISION,
            text = record.text,
            subject = record.subject,
            challenge = record.challenge,
            outcome = record.outcome?.let { o ->
                DecisionOutcome.entries.firstOrNull { it.name.equals(underscore(o), true) }
            },
            sourceEpisode = record.sourceEpisode?.let { parseCompact(it) },
            recordedAtUtc = time(record.recorded),
            machine = record.machine,
            verify = record.verify,
        )

        /**
         * An unparseable timestamp sorts FIRST rather than throwing: a line with
         * a broken date is still a memory, and putting it at the start of the
         * replay is the least surprising place for it.
         */
        internal fun time(raw: String): Instant = try {
            Instant.parse(raw)
        } catch (e: DateTimeParseException) {
            try {
                java.time.OffsetDateTime.parse(raw).toInstant()
            } catch (e2: Exception) {
                Instant.MIN
            }
        } catch (e: Exception) {
            Instant.MIN
        }

        /** 32 hex characters, no hyphens - the form the C# writes. */
        internal fun compact(id: UUID): String = id.toString().replace("-", "")

        internal fun parseCompact(s: String): UUID? {
            if (s.length != 32) return null
            return try {
                UUID.fromString(
                    s.substring(0, 8) + "-" + s.substring(8, 12) + "-" + s.substring(12, 16) +
                        "-" + s.substring(16, 20) + "-" + s.substring(20),
                )
            } catch (e: Exception) {
                null
            }
        }

        /**
         * COVER_LETTER becomes CoverLetter on the way out and back on the way
         * in. The log is shared with the C#, which writes the enum name, and a
         * Kotlin SCREAMING_CASE name in the file would be unreadable to it.
         */
        internal fun pascal(name: String): String =
            name.split('_').joinToString("") { part ->
                if (part.isEmpty()) "" else part[0].uppercase() + part.substring(1).lowercase()
            }

        internal fun underscore(name: String): String {
            val out = StringBuilder()
            for ((i, c) in name.withIndex()) {
                if (i > 0 && c.isUpperCase()) out.append('_')
                out.append(c.uppercaseChar())
            }
            return out.toString()
        }

        private fun endsWithNewline(file: File): Boolean = try {
            java.io.RandomAccessFile(file, "r").use { raf ->
                raf.seek(raf.length() - 1)
                raf.read() == 10
            }
        } catch (e: Exception) {
            true
        }
    }
}
