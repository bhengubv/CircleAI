// AdoAtomStore.kt
//
// IAtomStore on any JDBC engine: PostgreSQL, SQL Server, MySQL, Oracle, SQLite.
//
// THE SHARED CASE, NOT THE DEFAULT ONE. A phone runs the embedded store and
// always will - no server, ships in the app, works with the aeroplane mode on.
// This is for the other situation: a team, or a machine somebody already runs,
// where the memory should live where the rest of their data lives.
//
// THE CALLER BRINGS THE CONNECTION. This file references no driver, so it pulls
// the Oracle client into nothing, and an engine nobody here has heard of works
// by writing a SqlDialect rather than by shipping a package for it.
//
// UPSERT IS DELETE-THEN-INSERT IN A TRANSACTION, not MERGE. Five engines spell
// MERGE five ways and two of them have footguns in it; delete-then-insert is
// the same everywhere, is exactly the idempotence a replay needs, and costs one
// extra statement on a table that takes a handful of writes a day.
//
// SUPERSEDED ATOMS ARE NEVER DELETED, same as everywhere else. They stop being
// answers and stay readable, because the history is what gives a current atom
// its weight.

package com.bhengubv.circleai.memory.sql

import com.bhengubv.circleai.memory.AtomKind
import com.bhengubv.circleai.memory.CueExtractor
import com.bhengubv.circleai.memory.DecisionOutcome
import com.bhengubv.circleai.memory.IAtomStore
import com.bhengubv.circleai.memory.MemoryAtom
import com.bhengubv.circleai.memory.Situation
import java.sql.Connection
import java.sql.PreparedStatement
import java.sql.SQLException
import java.time.Instant
import java.util.UUID
import kotlin.math.max

class AdoAtomStore(
    private val conn: Connection,
    private val sql: SqlDialect,
) : IAtomStore {

    val engine: String get() = sql.name

    var fullTextAvailable: Boolean = false
        private set

    init {
        ensureSchema()
    }

    // ------------------------------------------------------------- Schema

    private fun ensureSchema() {
        if (scalar(sql.tableExists(TABLE)) > 0) {
            // Already built. Whether the full-text index came up is not knowable
            // portably, so trust what the dialect claims it does.
            fullTextAvailable = sql.fullText
            return
        }

        execute(sql.createTable(TABLE))

        // EACH INDEX ON ITS OWN, AND A FAILURE IS NOT FATAL. A server that
        // refuses to build a full-text index still has to serve a memory, and it
        // does - through the LIKE floor. Throwing here would turn a missing
        // optimisation into a store that will not start.
        var built = true
        for (statement in sql.indexes(TABLE)) {
            try {
                execute(statement)
            } catch (e: SQLException) {
                built = false
            }
        }

        fullTextAvailable = sql.fullText && built
    }

    // ------------------------------------------------------------ Writing

    override suspend fun add(atom: MemoryAtom) {
        inTransaction { upsert(atom) }
    }

    override suspend fun supersede(oldAtomId: UUID, replacement: MemoryAtom): MemoryAtom {
        val previous = read(oldAtomId)

        // THE COUNT CARRIES FORWARD. Losing the tally would throw away the
        // signal that makes a repeatedly-corrected atom outrank a fresh one, and
        // a memory that behaves differently on two engines is worse than one
        // that only runs on one.
        val carried = MemoryAtom(
            id = replacement.id,
            text = replacement.text,
            sourceEpisode = replacement.sourceEpisode,
            recordedAtUtc = replacement.recordedAtUtc,
            machine = replacement.machine ?: previous?.machine,
            verify = replacement.verify ?: previous?.verify,
            verifiedAtUtc = replacement.verifiedAtUtc,
            verifiedOk = replacement.verifiedOk,

            // The KIND is the old one: a correction refines what was said, it
            // does not reclassify it. A ruling corrected into a decision would
            // quietly lose its floor and start fading.
            kind = previous?.kind ?: replacement.kind,
            subject = replacement.subject ?: previous?.subject,
            challenge = replacement.challenge ?: previous?.challenge,
            outcome = replacement.outcome ?: previous?.outcome,

            corrections = max(replacement.corrections, (previous?.corrections ?: 0) + 1),
            lastCorrectedUtc = replacement.lastCorrectedUtc ?: Instant.now(),
        )

        inTransaction {
            upsert(carried)
            prepare(
                "UPDATE " + sql.quote(TABLE) + " SET " + sql.quote("superseded_by") + " = " +
                    sql.parameter("next") + " WHERE " + sql.quote("id") + " = " + sql.parameter("old"),
                listOf(compact(carried.id), compact(oldAtomId)),
            ).use { it.executeUpdate() }
        }

        return carried
    }

    override suspend fun markVerified(id: UUID, ok: Boolean, whenUtc: Instant) {
        prepare(
            "UPDATE " + sql.quote(TABLE) + " SET " +
                sql.quote("verified_ok") + " = " + sql.parameter("ok") + ", " +
                sql.quote("verified_at_utc") + " = " + sql.parameter("at") +
                " WHERE " + sql.quote("id") + " = " + sql.parameter("id"),
            listOf(if (ok) 1 else 0, whenUtc.toString(), compact(id)),
        ).use { it.executeUpdate() }
    }

    // ------------------------------------------------------------ Reading

    override suspend fun match(situation: Situation, limit: Int): List<MemoryAtom> {
        val results = mutableListOf<MemoryAtom>()
        val seen = HashSet<UUID>()

        // SUBJECT FIRST, MOST SPECIFIC FIRST. Matching what the action is about
        // against what the atom is about is a lookup; searching prose for
        // relevance is a guess. Keyword search fills in behind it.
        for (key in situation.keys) {
            if (results.size >= limit) break
            prepare(
                "SELECT " + columns() + " FROM " + sql.quote(TABLE) +
                    " WHERE " + sql.quote("superseded_by") + " IS NULL" +
                    " AND " + sql.quote("subject") + " = " + sql.parameter("key") +
                    " ORDER BY " + sql.quote("recorded_at_utc") + " DESC " + sql.limit(limit - results.size),
                listOf(key),
            ).use { take(it, results, seen) }
        }

        if (results.size < limit) {
            val terms = terms(situation.query)
            if (terms.isNotEmpty()) {
                val search = sql.search(terms)
                try {
                    prepare(
                        "SELECT " + columns() + " FROM " + sql.quote(TABLE) +
                            " WHERE " + sql.quote("superseded_by") + " IS NULL AND (" + search.where + ")" +
                            " ORDER BY " + sql.quote("recorded_at_utc") + " DESC " + sql.limit(limit),
                        search.parameters.map { it.value },
                    ).use { take(it, results, seen) }
                } catch (e: SQLException) {
                    // A MALFORMED FULL-TEXT QUERY IS A THIN RESULT, NOT AN
                    // OUTAGE. The subject matches above already stand, and a
                    // memory that throws because somebody typed a bracket is
                    // worse than one that finds less.
                }
            }
        }

        return results.take(limit)
    }

    override suspend fun byKind(kind: AtomKind, limit: Int): List<MemoryAtom> =
        prepare(
            "SELECT " + columns() + " FROM " + sql.quote(TABLE) +
                " WHERE " + sql.quote("superseded_by") + " IS NULL" +
                " AND " + sql.quote("kind") + " = " + sql.parameter("kind") +
                " ORDER BY " + sql.quote("recorded_at_utc") + " DESC " + sql.limit(limit),
            listOf(pascal(kind.name)),
        ).use { readAtoms(it) }

    override suspend fun all(includeSuperseded: Boolean, limit: Int): List<MemoryAtom> {
        val filter = if (includeSuperseded) "" else " WHERE " + sql.quote("superseded_by") + " IS NULL"
        return prepare(
            "SELECT " + columns() + " FROM " + sql.quote(TABLE) + filter +
                " ORDER BY " + sql.quote("recorded_at_utc") + " DESC " + sql.limit(limit),
            emptyList(),
        ).use { readAtoms(it) }
    }

    override suspend fun knows(text: String): Boolean {
        if (text.isBlank()) return false
        // INDEXED, because learning asks this of every sentence it spots and
        // learning runs on every turn of a conversation.
        return prepare(
            "SELECT " + sql.quote("id") + " FROM " + sql.quote(TABLE) +
                " WHERE " + sql.quote("text_key") + " = " + sql.parameter("key") +
                " AND " + sql.quote("superseded_by") + " IS NULL " + sql.limit(1),
            listOf(CueExtractor.normalise(text)),
        ).use { it.executeQuery().use { rs -> rs.next() } }
    }

    override suspend fun get(id: UUID): MemoryAtom? = read(id)

    override suspend fun count(): Int = scalar(
        "SELECT COUNT(*) FROM " + sql.quote(TABLE) + " WHERE " + sql.quote("superseded_by") + " IS NULL",
    )

    // ---------------------------------------------------------------- Rows

    private fun columns(): String = COLUMN_NAMES.joinToString(", ") { sql.quote(it) }

    private fun upsert(atom: MemoryAtom) {
        prepare(
            "DELETE FROM " + sql.quote(TABLE) + " WHERE " + sql.quote("id") + " = " + sql.parameter("id"),
            listOf(compact(atom.id)),
        ).use { it.executeUpdate() }

        val names = COLUMN_NAMES + "text_key"
        prepare(
            "INSERT INTO " + sql.quote(TABLE) + " (" + names.joinToString(", ") { sql.quote(it) } + ") " +
                "VALUES (" + names.joinToString(", ") { sql.parameter(it) } + ")",
            listOf(
                compact(atom.id),
                pascal(atom.kind.name),
                atom.text,
                atom.subject,
                atom.sourceEpisode?.let { compact(it) },
                atom.recordedAtUtc.toString(),
                atom.corrections,
                atom.lastCorrectedUtc?.toString(),
                atom.supersededBy?.let { compact(it) },
                atom.challenge,
                atom.outcome?.let { pascal(it.name) },
                atom.verify,
                atom.verifiedAtUtc?.toString(),
                atom.verifiedOk?.let { if (it) 1 else 0 },
                atom.machine,
                CueExtractor.normalise(atom.text),
            ),
        ).use { it.executeUpdate() }
    }

    private fun read(id: UUID): MemoryAtom? = prepare(
        "SELECT " + columns() + " FROM " + sql.quote(TABLE) +
            " WHERE " + sql.quote("id") + " = " + sql.parameter("id"),
        listOf(compact(id)),
    ).use { readAtoms(it).firstOrNull() }

    private fun readAtoms(stmt: PreparedStatement): List<MemoryAtom> {
        val results = mutableListOf<MemoryAtom>()
        stmt.executeQuery().use { rs ->
            while (rs.next()) {
                results.add(
                    MemoryAtom(
                        id = parseCompact(rs.getString(1)) ?: UUID.randomUUID(),
                        kind = enumOf(rs.getString(2), AtomKind.entries) ?: AtomKind.FACT,
                        text = rs.getString(3) ?: "",
                        subject = rs.getString(4),
                        sourceEpisode = rs.getString(5)?.let { parseCompact(it) },
                        recordedAtUtc = time(rs.getString(6)) ?: Instant.MIN,
                        // Engines disagree about the type of an integer column -
                        // int, long and decimal all turn up - so it is read as a
                        // NUMBER rather than cast to one.
                        corrections = rs.getInt(7),
                        lastCorrectedUtc = rs.getString(8)?.let { time(it) },
                        supersededBy = rs.getString(9)?.let { parseCompact(it) },
                        challenge = rs.getString(10),
                        outcome = rs.getString(11)?.let { enumOf(it, DecisionOutcome.entries) },
                        verify = rs.getString(12),
                        verifiedAtUtc = rs.getString(13)?.let { time(it) },
                        verifiedOk = rs.getObject(14)?.let { rs.getInt(14) == 1 },
                        machine = rs.getString(15),
                    ),
                )
            }
        }
        return results
    }

    private fun take(stmt: PreparedStatement, into: MutableList<MemoryAtom>, seen: MutableSet<UUID>) {
        for (atom in readAtoms(stmt)) if (seen.add(atom.id)) into.add(atom)
    }

    // ------------------------------------------------------------ Commands

    /**
     * The dialects speak NAMED parameters and JDBC speaks positional ones, so
     * every marker is rewritten to a question mark and the values are bound in
     * the order the markers appear.
     *
     * That order is the contract: the caller passes values in the order the SQL
     * mentions them, and a marker used twice consumes two values. The alternative
     * - a map keyed by name - would look tidier and silently reorder a WHERE
     * clause the day two parameters share a prefix.
     */
    internal fun prepare(named: String, values: List<Any?>): PreparedStatement {
        val positional = NAMED_MARKER.replace(named, "?")
        val stmt = conn.prepareStatement(positional)
        for ((i, v) in values.withIndex()) {
            when (v) {
                null -> stmt.setObject(i + 1, null)
                is Int -> stmt.setInt(i + 1, v)
                is Long -> stmt.setLong(i + 1, v)
                is Boolean -> stmt.setInt(i + 1, if (v) 1 else 0)
                else -> stmt.setString(i + 1, v.toString())
            }
        }
        return stmt
    }

    private fun execute(statement: String) {
        conn.createStatement().use { it.execute(statement) }
    }

    private fun scalar(statement: String): Int =
        conn.createStatement().use { st ->
            st.executeQuery(statement).use { rs -> if (rs.next()) rs.getInt(1) else 0 }
        }

    /**
     * The transaction is restored to whatever the CALLER had it set to. This
     * connection belongs to them, and leaving auto-commit off would silently
     * change how the rest of their application behaves.
     */
    private inline fun inTransaction(body: () -> Unit) {
        val previous = conn.autoCommit
        conn.autoCommit = false
        try {
            body()
            conn.commit()
        } catch (e: Throwable) {
            try { conn.rollback() } catch (ignored: SQLException) { }
            throw e
        } finally {
            conn.autoCommit = previous
        }
    }

    companion object {
        const val TABLE = "atoms"

        internal val COLUMN_NAMES = listOf(
            "id", "kind", "text", "subject", "source_episode", "recorded_at_utc",
            "corrections", "last_corrected_utc", "superseded_by", "challenge",
            "outcome", "verify", "verified_at_utc", "verified_ok", "machine",
        )

        private val NAMED_MARKER = Regex("[@:$][A-Za-z_][A-Za-z0-9_]*")

        /**
         * Words shorter than three characters match everything, and past eight
         * terms a keyword search stops narrowing and starts costing.
         */
        internal fun terms(query: String): List<String> =
            query.split(' ', '\t', '\n', ',', ';')
                .filter { it.length > 2 }
                .distinctBy { it.lowercase() }
                .take(8)

        internal fun compact(id: UUID): String = id.toString().replace("-", "")

        internal fun parseCompact(s: String?): UUID? {
            if (s == null) return null
            val hex = s.replace("-", "")
            if (hex.length != 32) return null
            return try {
                UUID.fromString(
                    hex.substring(0, 8) + "-" + hex.substring(8, 12) + "-" + hex.substring(12, 16) +
                        "-" + hex.substring(16, 20) + "-" + hex.substring(20),
                )
            } catch (e: Exception) {
                null
            }
        }

        internal fun time(raw: String?): Instant? {
            if (raw == null) return null
            return try {
                Instant.parse(raw)
            } catch (e: Exception) {
                try { java.time.OffsetDateTime.parse(raw).toInstant() } catch (e2: Exception) { null }
            }
        }

        internal fun pascal(name: String): String =
            name.split('_').joinToString("") { p ->
                if (p.isEmpty()) "" else p[0].uppercase() + p.substring(1).lowercase()
            }

        private inline fun <reified T : Enum<T>> enumOf(raw: String?, values: List<T>): T? {
            if (raw == null) return null
            val flat = raw.replace("_", "")
            return values.firstOrNull { it.name.replace("_", "").equals(flat, ignoreCase = true) }
        }
    }
}

/**
 * The embedded engine, which is the one that matters first.
 *
 * On the JVM there is no separate implementation to write: SQLite through JDBC
 * IS the shared path with the SQLite dialect, and saying so here is more honest
 * than a second class that wraps the same statements.
 */
class SqliteAtomStore(connection: Connection) : IAtomStore by AdoAtomStore(connection, SqlDialect.sqlite)
