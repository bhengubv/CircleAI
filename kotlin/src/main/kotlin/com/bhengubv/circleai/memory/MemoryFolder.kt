// MemoryFolder.kt
//
// Where memory lives, and which machine is writing to it.
//
// THREE MACHINES, ONE MEMORY. Linux, Windows and a Mac all have to see the same
// store, and the arrangement answers how: the memory directory is a symlink
// into a git repository, so it travels by pull and push like everything else.
//
// THAT DECIDES THE FILE LAYOUT, not taste. A SQLite database is a binary blob
// and git cannot merge one - two machines writing the same day produce a
// conflict whose only resolutions are "keep mine" and "keep theirs", and both
// destroy memory. So the durable thing is an append-only text log, and there is
// ONE PER MACHINE: a file with a single writer can never conflict, which is a
// stronger guarantee than any merge strategy.
//
// The database is a local index built from the logs. It is disposable, never
// committed, and losing it costs a rebuild rather than a memory.

package com.bhengubv.circleai.memory

import java.io.File
import java.io.IOException
import java.util.UUID

class MemoryFolder(path: String, machine: String? = null) {

    val path: String
    val machine: String

    init {
        if (path.isBlank()) throw IllegalArgumentException("A memory folder path is required.")
        this.path = File(path).absoluteFile.normalize().path
        File(this.path).mkdirs()

        var name = sanitise(machine ?: defaultMachineName())

        // A HOST NAME THAT IDENTIFIES NOTHING IS WORSE THAN NO HOST NAME. Every
        // Android device reports "localhost" for its machine name, so two phones
        // would both call themselves android-localhost and append to ONE log -
        // which is the merge problem this whole layout exists to avoid, arriving
        // through the front door. Found by running it on a P30.
        //
        // The condition is the NAME, not where it came from: a caller passing
        // "android-unnamed" is saying the same thing the environment said, and
        // deserves the same answer.
        if (name.endsWith(ANONYMOUS)) {
            name = name.substring(0, name.length - ANONYMOUS.length) + "-" + installed()
        }
        this.machine = name
    }

    val ownLog: String get() = File(path, "atoms." + machine + ".jsonl").path

    /** Every machine log, in a stable order so a rebuild is reproducible. */
    val allLogs: List<String>
        get() {
            val dir = File(path)
            if (!dir.isDirectory) return emptyList()
            return (dir.listFiles() ?: emptyArray())
                .filter { it.name.startsWith("atoms.") && it.name.endsWith(".jsonl") }
                .map { it.path }
                .sorted()
        }

    val indexPath: String get() = File(path, "index." + machine + ".db").path

    val indexConnectionString: String get() = "jdbc:sqlite:" + indexPath

    fun ensureGitIgnore() {
        val file = File(path, ".gitignore")
        if (!file.exists()) file.writeText(GIT_IGNORE)
    }

    /**
     * A machine id that survives restarts, minted once into the folder.
     *
     * A read-only folder still has to work: the fallback is not stable across
     * runs, which is worse than a file and far better than a collision with
     * every other device.
     */
    private fun installed(): String {
        val file = File(path, ".machine-id")
        return try {
            if (file.exists()) {
                val existing = file.readText().trim()
                if (existing.isNotEmpty()) return sanitise(existing)
            }
            val minted = UUID.randomUUID().toString().replace("-", "").substring(0, 8)
            file.writeText(minted)
            minted
        } catch (e: IOException) {
            UUID.randomUUID().toString().replace("-", "").substring(0, 8)
        }
    }

    companion object {
        internal const val ANONYMOUS = "-unnamed"

        val GIT_IGNORE: String = """
            # Derived, not memory. Rebuilt from the logs on demand.
            index.*.db
            index.*.db-wal
            index.*.db-shm

            # This machine's name for itself. Per-machine by definition - sharing it
            # would put two machines back in one log.
            .machine-id

            # How worn the paths are HERE. What was decided is shared; how often
            # somebody reached for it on this machine is not, and syncing it would
            # put one machine's habits in charge of what another finds easy to
            # bring to mind.
            wear.*.json
            wear.*.json.tmp
        """.trimIndent()

        fun defaultMachineName(host: String? = null, os: String? = null): String {
            val osName = (os ?: System.getProperty("os.name") ?: "").lowercase()
            val platform = when {
                osName.contains("win") -> "windows"
                osName.contains("mac") || osName.contains("darwin") -> "mac"
                // Android reports Linux too; the caller names it when it knows.
                osName.contains("linux") -> "linux"
                osName.contains("android") -> "android"
                else -> "other"
            }

            val name = host ?: try {
                java.net.InetAddress.getLocalHost().hostName
            } catch (e: Exception) {
                ""
            }

            // "localhost" is what every Android device answers, and an empty or
            // unknown name is no better. Say so plainly and let the caller settle it.
            if (name.isBlank() ||
                name.equals("localhost", ignoreCase = true) ||
                name.equals("unknown", ignoreCase = true)
            ) {
                return platform + ANONYMOUS
            }

            return platform + "-" + name
        }

        /** A file name, not a host name: anything else becomes a hyphen. */
        internal fun sanitise(name: String): String {
            val cleaned = name.trim().lowercase()
                .map { if (it.isLetterOrDigit() || it == '-' || it == '_') it else '-' }
                .joinToString("")
                .trim('-')
            return cleaned.ifEmpty { "unknown" }
        }
    }
}
