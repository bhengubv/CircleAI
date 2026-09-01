// SqlDialect.kt
//
// The five engines, as the handful of things they actually disagree about.
//
// THE DIFFERENCES ARE SMALLER THAN THEY LOOK. An atom store needs a table, an
// upsert, a keyword search and a row limit. Everything else - pooling, drivers,
// authentication - belongs to whoever owns the server. So a dialect is four or
// five overrides, and a sixth engine is a class rather than a project.
//
// NO DRIVER DEPENDENCIES LIVE HERE. The caller hands in an open JDBC
// Connection, so this file depends on nothing but java.sql. That keeps the
// Oracle driver and its licence out of a phone build, keeps the Postgres driver
// out of a SQL Server deployment, and means an engine nobody anticipated needs
// no change here at all.
//
// TIMESTAMPS ARE ISO-8601 STRINGS, not native date types. Five engines have
// five opinions about time zones, precision and what a driver hands back, and
// every one of those opinions is a way for a memory to come back an hour wrong.
// A sortable string is the same everywhere and reads correctly in a dump.

package com.bhengubv.circleai.memory.sql

/** One named parameter and its value, in the order the SQL expects them. */
data class SqlParameter(val name: String, val value: Any)

/** A WHERE fragment and the parameters it needs. */
data class SqlSearch(val where: String, val parameters: List<SqlParameter>)

abstract class SqlDialect {

    abstract val name: String

    open fun parameter(name: String): String = "@" + name

    open fun parameterName(name: String): String = name

    open fun quote(identifier: String): String = Char(34) + identifier + Char(34)

    protected open val textType: String get() = "TEXT"

    protected open fun tagType(length: Int): String = "VARCHAR(" + length + ")"

    protected open val intType: String get() = "INTEGER"

    open fun limit(rows: Int): String = "LIMIT " + rows

    open val fullText: Boolean get() = false

    open fun search(terms: List<String>): SqlSearch {
        val parameters = mutableListOf<SqlParameter>()
        val clauses = mutableListOf<String>()

        for (i in terms.indices) {
            val p = parameter("t" + i)
            clauses.add(
                quote("text") + " LIKE " + p + " OR " + quote("subject") + " LIKE " + p +
                    " OR " + quote("challenge") + " LIKE " + p,
            )
            parameters.add(SqlParameter("t" + i, "%" + terms[i] + "%"))
        }

        return SqlSearch("(" + clauses.joinToString(") OR (") + ")", parameters)
    }

    open fun indexes(table: String): List<String> = listOf(
        "CREATE INDEX ix_atoms_subject ON " + quote(table) +
            " (" + quote("subject") + ", " + quote("superseded_by") + ")",
        "CREATE INDEX ix_atoms_kind ON " + quote(table) +
            " (" + quote("kind") + ", " + quote("superseded_by") + ")",
        "CREATE INDEX ix_atoms_text_key ON " + quote(table) +
            " (" + quote("text_key") + ", " + quote("superseded_by") + ")",
    )

    open fun createTable(table: String): String = buildString {
        append("CREATE TABLE ").append(quote(table)).append(" (\n")
        append("    ").append(quote("id")).append(" ").append(tagType(32)).append(" NOT NULL PRIMARY KEY,\n")
        append("    ").append(quote("kind")).append(" ").append(tagType(32)).append(" NOT NULL,\n")
        append("    ").append(quote("text")).append(" ").append(textType).append(" NOT NULL,\n")
        append("    ").append(quote("subject")).append(" ").append(tagType(200)).append(",\n")
        append("    ").append(quote("source_episode")).append(" ").append(tagType(32)).append(",\n")
        append("    ").append(quote("recorded_at_utc")).append(" ").append(tagType(40)).append(" NOT NULL,\n")
        append("    ").append(quote("corrections")).append(" ").append(intType).append(" NOT NULL,\n")
        append("    ").append(quote("last_corrected_utc")).append(" ").append(tagType(40)).append(",\n")
        append("    ").append(quote("superseded_by")).append(" ").append(tagType(32)).append(",\n")
        append("    ").append(quote("challenge")).append(" ").append(textType).append(",\n")
        append("    ").append(quote("outcome")).append(" ").append(tagType(16)).append(",\n")
        append("    ").append(quote("verify")).append(" ").append(textType).append(",\n")
        append("    ").append(quote("verified_at_utc")).append(" ").append(tagType(40)).append(",\n")
        append("    ").append(quote("verified_ok")).append(" ").append(intType).append(",\n")
        append("    ").append(quote("machine")).append(" ").append(tagType(120)).append(",\n")
        append("    ").append(quote("text_key")).append(" ").append(tagType(400)).append("\n")
        append(")")
    }

    abstract fun tableExists(table: String): String

    companion object {
        val postgreSql: SqlDialect = PostgreSqlDialect()
        val sqlServer: SqlDialect = SqlServerDialect()
        val mySql: SqlDialect = MySqlDialect()
        val oracle: SqlDialect = OracleDialect()
        val sqlite: SqlDialect = SqliteDialect()

        val all: List<SqlDialect> get() = listOf(postgreSql, sqlServer, mySql, oracle, sqlite)
    }
}

private class PostgreSqlDialect : SqlDialect() {
    override val name: String get() = "PostgreSQL"

    override fun tableExists(table: String): String =
        "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = '" + table + "'"

    /**
     * Postgres full text needs NOTHING installed and no external catalogue: a
     * generated tsvector column and a GIN index, both in the schema. That is
     * why it gets the real thing where SQL Server and Oracle do not.
     */
    override val fullText: Boolean get() = true

    override fun indexes(table: String): List<String> = listOf(
        "ALTER TABLE " + quote(table) + " ADD COLUMN " + quote("search") + " tsvector " +
            "GENERATED ALWAYS AS (to_tsvector('english', " +
            "coalesce(" + quote("text") + ", '') || ' ' || coalesce(" + quote("subject") + ", '') || ' ' || " +
            "coalesce(" + quote("challenge") + ", ''))) STORED",
        "CREATE INDEX ix_atoms_search ON " + quote(table) + " USING GIN (" + quote("search") + ")",
        "CREATE INDEX ix_atoms_subject ON " + quote(table) +
            " (" + quote("subject") + ", " + quote("superseded_by") + ")",
        "CREATE INDEX ix_atoms_kind ON " + quote(table) +
            " (" + quote("kind") + ", " + quote("superseded_by") + ")",
        "CREATE INDEX ix_atoms_text_key ON " + quote(table) +
            " (" + quote("text_key") + ", " + quote("superseded_by") + ")",
    )

    override fun search(terms: List<String>): SqlSearch {
        // websearch_to_tsquery takes what a person would TYPE and does not throw
        // on punctuation - which matters when half the memory is about flags
        // like -t:InstallKeepingData.
        val query = terms.joinToString(" or ")
        return SqlSearch(
            quote("search") + " @@ websearch_to_tsquery('english', " + parameter("q") + ")",
            listOf(SqlParameter("q", query)),
        )
    }
}

private class SqlServerDialect : SqlDialect() {
    override val name: String get() = "SQL Server"
    override fun quote(identifier: String): String = "[" + identifier + "]"
    override val textType: String get() = "NVARCHAR(MAX)"
    override fun tagType(length: Int): String = "NVARCHAR(" + length + ")"
    override val intType: String get() = "INT"

    /**
     * TOP would be simpler; OFFSET and FETCH is what COMPOSES with an ORDER BY
     * the caller wrote rather than one pasted in here.
     */
    override fun limit(rows: Int): String = "OFFSET 0 ROWS FETCH NEXT " + rows + " ROWS ONLY"

    override fun tableExists(table: String): String =
        "SELECT COUNT(*) FROM sys.tables WHERE name = '" + table + "'"

    // NOT full text ON PURPOSE: CONTAINS needs the Full-Text Search feature
    // installed on the instance and a catalogue created, and neither is a safe
    // assumption about somebody else server. LIKE finds things; a store that
    // throws at startup because a feature is absent does not.
}

private class MySqlDialect : SqlDialect() {
    override val name: String get() = "MySQL"
    override fun quote(identifier: String): String = "`" + identifier + "`"
    override val textType: String get() = "TEXT"
    override val intType: String get() = "INT"

    override fun tableExists(table: String): String =
        "SELECT COUNT(*) FROM information_schema.tables " +
            "WHERE table_schema = DATABASE() AND table_name = '" + table + "'"

    /** A FULLTEXT index on InnoDB needs nothing installed, so MySQL gets the real thing too. */
    override val fullText: Boolean get() = true

    override fun indexes(table: String): List<String> = listOf(
        "CREATE FULLTEXT INDEX ix_atoms_search ON " + quote(table) +
            " (" + quote("text") + ", " + quote("subject") + ", " + quote("challenge") + ")",
        "CREATE INDEX ix_atoms_subject ON " + quote(table) +
            " (" + quote("subject") + ", " + quote("superseded_by") + ")",
        "CREATE INDEX ix_atoms_kind ON " + quote(table) +
            " (" + quote("kind") + ", " + quote("superseded_by") + ")",
        "CREATE INDEX ix_atoms_text_key ON " + quote(table) +
            " (" + quote("text_key") + ", " + quote("superseded_by") + ")",
    )

    override fun search(terms: List<String>): SqlSearch {
        // Boolean mode, and every term QUOTED: an unquoted term starting with a
        // hyphen means NOT in this syntax, so "-t:Install" would ask MySQL to
        // exclude the very thing being looked for.
        val q = Char(34)
        val query = terms.joinToString(" ") { q + it.replace(q.toString(), " ") + q }
        return SqlSearch(
            "MATCH (" + quote("text") + ", " + quote("subject") + ", " + quote("challenge") + ") " +
                "AGAINST (" + parameter("q") + " IN BOOLEAN MODE)",
            listOf(SqlParameter("q", query)),
        )
    }
}

private class OracleDialect : SqlDialect() {
    override val name: String get() = "Oracle"
    override fun parameter(name: String): String = ":" + name
    override fun parameterName(name: String): String = name
    override val textType: String get() = "CLOB"
    override fun tagType(length: Int): String = "VARCHAR2(" + length + ")"
    override val intType: String get() = "NUMBER(10)"

    override fun limit(rows: Int): String = "FETCH FIRST " + rows + " ROWS ONLY"

    override fun tableExists(table: String): String =
        "SELECT COUNT(*) FROM user_tables WHERE table_name = '" + table.uppercase() + "'"

    // NOT full text ON PURPOSE: Oracle Text is a separate index type owned by
    // CTXSYS, and creating one needs a privilege a memory has no business
    // asking for. LIKE on a CLOB is slower and it works.
}

private class SqliteDialect : SqlDialect() {
    override val name: String get() = "SQLite"
    override fun parameter(name: String): String = "$" + name
    override fun parameterName(name: String): String = "$" + name

    override fun tableExists(table: String): String =
        "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '" + table + "'"

    // No FTS5 here on purpose. The dedicated SQLite store uses FTS5; this
    // dialect exists so the shared JDBC path can be exercised against a file
    // nobody has to install a server for.
}
