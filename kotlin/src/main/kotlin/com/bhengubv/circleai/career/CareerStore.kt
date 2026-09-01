// CareerStore.kt
//
// The profile on disk, tailoring a profile to a job spec, and rendering it.
//
// WHY A SCHEMA AND NOT A JSON BLOB. The point of the profile is that it is
// queryable and reusable: the same facts answer "draft me a CV for this security
// job" today and "which of my jobs match this one" next month. A blob can be
// rendered and cannot be reasoned about — and a blob is exactly what people
// already have, a CV.doc they edit and re-save until nobody knows which one they
// sent.
//
// ON-DEVICE, AND THERE IS NO SYNC. Employment history and contact details are
// the personal information most able to do harm if it travelled. Nothing here
// opens a socket.
//
// Ported from src/CircleAI.Career/{SqliteCareerStore, ProfileTailoring,
// ProfileToCv}.cs.

package com.bhengubv.circleai.career

import java.sql.Connection
import java.sql.DriverManager
import java.time.Instant

data class JobSpec(
    val title: String,
    val employer: String? = null,
    val text: String,
    val source: String = "typed",
    val added: Instant? = null,
    val id: Long = 0
)

/**
 * The document AND what went into it.
 *
 * [selectedFacts] is why a second application can start from the first instead
 * of from scratch — a blob alone cannot be re-tailored. It also makes the record
 * honest: for any application there is a row saying which facts were claimed, to
 * whom, and when.
 */
data class ApprovedDocument(
    val specId: Long?,
    val pdf: ByteArray,
    val selectedFacts: List<Long>,
    val approved: Instant,
    val id: Long = 0
) {
    override fun equals(other: Any?): Boolean =
        this === other || (other is ApprovedDocument && id == other.id &&
            specId == other.specId && pdf.contentEquals(other.pdf) &&
            selectedFacts == other.selectedFacts && approved == other.approved)

    override fun hashCode(): Int =
        (((id.hashCode() * 31 + (specId?.hashCode() ?: 0)) * 31 +
            pdf.contentHashCode()) * 31 + selectedFacts.hashCode()) * 31 + approved.hashCode()
}

class SqliteCareerStore(private val db: Connection) : AutoCloseable {

    constructor(databasePath: String) :
        this(DriverManager.getConnection("jdbc:sqlite:$databasePath"))

    init {
        db.createStatement().use { st ->
            // ONE ROW, ENFORCED. A person has one career profile on their own
            // phone; a table that permits two invites the bug where half the app
            // reads the other one.
            st.executeUpdate(
                """
                CREATE TABLE IF NOT EXISTS profile (
                    id        INTEGER PRIMARY KEY CHECK (id = 1),
                    full_name TEXT NOT NULL DEFAULT '',
                    headline  TEXT NOT NULL DEFAULT '',
                    phone     TEXT, email TEXT, location TEXT, summary TEXT
                )
                """.trimIndent()
            )
            st.executeUpdate("INSERT OR IGNORE INTO profile (id) VALUES (1)")

            // Organisation is nullable and formal is a FLAG: piece work, a
            // family business and a season on a farm are all work history, and a
            // schema that only accepts salaried employment quietly tells most of
            // the country it has never worked.
            st.executeUpdate(
                """
                CREATE TABLE IF NOT EXISTS history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    role TEXT NOT NULL, organisation TEXT,
                    formal INTEGER NOT NULL DEFAULT 1,
                    start_text TEXT, end_text TEXT,
                    achievements TEXT NOT NULL DEFAULT '',
                    ordinal INTEGER NOT NULL DEFAULT 0
                )
                """.trimIndent()
            )
            // evidence_history_id ties a skill to WHERE IT WAS USED, so a CV can
            // cite it instead of asserting a level nobody can check.
            st.executeUpdate(
                """
                CREATE TABLE IF NOT EXISTS skill (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL, years REAL,
                    evidence_history_id INTEGER REFERENCES history(id) ON DELETE SET NULL
                )
                """.trimIndent()
            )
            // Specs are KEPT, not consumed. Applying to a similar job later
            // should start from one that already worked.
            st.executeUpdate(
                """
                CREATE TABLE IF NOT EXISTS job_spec (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    title TEXT NOT NULL, employer TEXT, body TEXT NOT NULL,
                    source TEXT NOT NULL DEFAULT 'typed', added_utc TEXT NOT NULL
                )
                """.trimIndent()
            )
            st.executeUpdate(
                """
                CREATE TABLE IF NOT EXISTS approved_document (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    spec_id INTEGER REFERENCES job_spec(id) ON DELETE SET NULL,
                    pdf BLOB NOT NULL, selected_facts TEXT NOT NULL DEFAULT '',
                    approved_utc TEXT NOT NULL
                )
                """.trimIndent()
            )
        }
    }

    @Synchronized
    fun addSpec(spec: JobSpec, now: Instant = Instant.now()): Long =
        db.prepareStatement(
            "INSERT INTO job_spec (title, employer, body, source, added_utc) VALUES (?, ?, ?, ?, ?)",
            java.sql.Statement.RETURN_GENERATED_KEYS
        ).use { ps ->
            ps.setString(1, spec.title)
            ps.setString(2, spec.employer)
            ps.setString(3, spec.text)
            ps.setString(4, spec.source)
            ps.setString(5, (spec.added ?: now).toString())
            ps.executeUpdate()
            ps.generatedKeys.use { if (it.next()) it.getLong(1) else 0L }
        }

    /** Newest first: the spec somebody is working on is the one they just added. */
    @Synchronized
    fun specs(): List<JobSpec> =
        db.prepareStatement(
            "SELECT id, title, employer, body, source, added_utc FROM job_spec " +
                "ORDER BY added_utc DESC, id DESC"
        ).use { ps ->
            ps.executeQuery().use { rs ->
                val out = ArrayList<JobSpec>()
                while (rs.next()) {
                    out.add(
                        JobSpec(
                            rs.getString(2), rs.getString(3), rs.getString(4), rs.getString(5),
                            runCatching { Instant.parse(rs.getString(6)) }.getOrNull(),
                            rs.getLong(1)
                        )
                    )
                }
                out
            }
        }

    @Synchronized
    fun approve(doc: ApprovedDocument): Long =
        db.prepareStatement(
            "INSERT INTO approved_document (spec_id, pdf, selected_facts, approved_utc) " +
                "VALUES (?, ?, ?, ?)",
            java.sql.Statement.RETURN_GENERATED_KEYS
        ).use { ps ->
            if (doc.specId != null) ps.setLong(1, doc.specId) else ps.setNull(1, java.sql.Types.INTEGER)
            ps.setBytes(2, doc.pdf)
            ps.setString(3, doc.selectedFacts.joinToString(","))
            ps.setString(4, doc.approved.toString())
            ps.executeUpdate()
            ps.generatedKeys.use { if (it.next()) it.getLong(1) else 0L }
        }

    /** Every approval, newest first. Nothing is ever deleted — the record of
     *  what was claimed, to whom, and when is the point. */
    @Synchronized
    fun approvals(specId: Long? = null): List<ApprovedDocument> {
        val sql = if (specId == null)
            "SELECT id, spec_id, pdf, selected_facts, approved_utc FROM approved_document " +
                "ORDER BY approved_utc DESC, id DESC"
        else
            "SELECT id, spec_id, pdf, selected_facts, approved_utc FROM approved_document " +
                "WHERE spec_id = ? ORDER BY approved_utc DESC, id DESC"

        return db.prepareStatement(sql).use { ps ->
            if (specId != null) ps.setLong(1, specId)
            ps.executeQuery().use { rs ->
                val out = ArrayList<ApprovedDocument>()
                while (rs.next()) {
                    val raw = rs.getLong(2)
                    out.add(
                        ApprovedDocument(
                            if (rs.wasNull()) null else raw,
                            rs.getBytes(3) ?: ByteArray(0),
                            rs.getString(4).orEmpty().split(",").mapNotNull { it.toLongOrNull() },
                            runCatching { Instant.parse(rs.getString(5)) }.getOrDefault(Instant.EPOCH),
                            rs.getLong(1)
                        )
                    )
                }
                out
            }
        }
    }

    override fun close() = db.close()
}

/** One decision about one fact: include it, and why. */
data class TailoringChoice(
    val factId: Long,
    val text: String,
    val include: Boolean,
    /** Said to a PERSON. "Matched: forklift, warehouse" is a reason somebody can
     *  argue with; a score is not. */
    val reason: String,
    val score: Double
)

/**
 * Chooses which facts go into an application.
 *
 * BY WORD OVERLAP WITH THE SPEC, deliberately, and it SHOWS ITS WORKING. The
 * person is the one signing the application: they have to be able to see why a
 * job was left out and put it back. A ranked list with no reasons is something
 * they can only accept or reject wholesale.
 */
object ProfileTailoring {

    fun choose(
        facts: List<Pair<Long, String>>,
        spec: JobSpec,
        maxFacts: Int = 8
    ): List<TailoringChoice> {
        val wanted = terms(spec.title + " " + spec.text)

        val scored = facts.map { (id, text) ->
            val hits = terms(text).filter { it in wanted }.distinct()
            val score = hits.size.toDouble()
            TailoringChoice(
                factId = id,
                text = text,
                include = false,
                reason = if (hits.isEmpty()) "nothing in the advert matches this"
                else "matched: " + hits.sorted().joinToString(", "),
                score = score
            )
        }.sortedWith(compareByDescending<TailoringChoice> { it.score }.thenBy { it.factId })

        // Everything that matched at all, up to the cap. A fact with NO overlap
        // is excluded rather than padded in: an application that lists
        // everything is the CV the person already had.
        return scored.mapIndexed { i, c ->
            c.copy(include = c.score > 0 && i < maxFacts)
        }
    }

    private fun terms(text: String): List<String> =
        text.lowercase().split(Regex("[^a-z0-9]+")).filter { it.length > 2 }
}

/**
 * Renders a chosen profile as plain text.
 *
 * TEXT, not PDF: the PDF engine is a host concern (see PARITY-EXCLUSIONS.md),
 * and the layout decisions — what goes first, what is omitted when empty — are
 * the part worth carrying across.
 */
object ProfileToCv {

    fun render(
        name: String,
        headline: String,
        contact: List<String>,
        chosen: List<TailoringChoice>
    ): String = buildString {
        appendLine(name)
        if (headline.isNotBlank()) appendLine(headline)
        // Contact goes at the TOP. An employer who cannot find a phone number in
        // three seconds does not scroll for it.
        val reachable = contact.filter { it.isNotBlank() }
        if (reachable.isNotEmpty()) appendLine(reachable.joinToString(" · "))

        val included = chosen.filter { it.include }
        if (included.isNotEmpty()) {
            appendLine()
            appendLine("Experience")
            included.forEach { appendLine(" - ${it.text}") }
        }
    }.trimEnd()
}
