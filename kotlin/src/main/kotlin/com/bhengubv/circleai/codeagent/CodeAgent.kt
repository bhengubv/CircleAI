// CodeAgent.kt
//
// Kotlin port of CircleAI.CodeAgent — the C# reference is the EXACT spec.
//
// An on-device coding agent: one JSON action per turn, executed against real
// seams (an editor, a command runner, a code search), with a device gate in
// front of the loop so a phone that cannot do this never pretends it can.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `JsonDocument` parsing -> a hand-rolled reader over
//     `kotlinx.serialization.json`, keeping the SAME type strictness: a
//     number where a string belongs falls back rather than coercing.
//   * The brace scanner is character-for-character the C# one, including its
//     string and escape handling.
//   * `ProcessCommandRunner` is NOT ported: the JVM has ProcessBuilder, but
//     the allow-list refusal is the part with the value and it lives in the
//     runner contract. A host wiring a real runner keeps the same shape.

package com.bhengubv.circleai.codeagent

import kotlinx.serialization.json.*

/** The one thing the model asked for this turn. */
enum class AgentActionKind { UNKNOWN, READ_FILE, EDIT_FILE, RUN_COMMAND, SEARCH_CODE, FINISH }

/**
 * A parsed action. Every field is optional because the model supplies them and
 * the model is not to be trusted; [AgentActionParser] never throws.
 */
data class AgentAction(
    val kind: AgentActionKind,
    val path: String? = null,
    val rangeStart: Int = 0,
    val rangeEnd: Int = 0,
    val replacement: String? = null,
    val executable: String? = null,
    val args: List<String>? = null,
    val query: String? = null,
    val topK: Int = 10,
    val summary: String? = null,
    /** What the model actually said, kept so a parse failure can be shown back. */
    val raw: String? = null,
)

object AgentActionParser {

    /**
     * Never throws. A reply that is prose, truncated JSON or an action nobody
     * has heard of all come back as [AgentActionKind.UNKNOWN] carrying the raw
     * text, so the loop can show the model what it did wrong instead of dying.
     */
    fun parse(modelText: String?): AgentAction {
        if (modelText.isNullOrBlank()) return AgentAction(AgentActionKind.UNKNOWN, raw = modelText)

        val json = extractFirstJsonObject(modelText)
            ?: return AgentAction(AgentActionKind.UNKNOWN, raw = modelText)

        val root = try {
            Json.parseToJsonElement(json) as? JsonObject
        } catch (_: Exception) {
            null
        } ?: return AgentAction(AgentActionKind.UNKNOWN, raw = modelText)

        return when (string(root, "action")?.trim()?.lowercase()) {
            "read_file" -> AgentAction(AgentActionKind.READ_FILE, path = string(root, "path"), raw = json)
            "edit_file" -> AgentAction(
                AgentActionKind.EDIT_FILE,
                path = string(root, "path"),
                rangeStart = int(root, "range_start"),
                rangeEnd = int(root, "range_end"),
                replacement = string(root, "replacement") ?: "",
                raw = json,
            )
            "run_command" -> AgentAction(
                AgentActionKind.RUN_COMMAND,
                path = string(root, "cwd"),
                executable = string(root, "executable"),
                args = stringArray(root, "args"),
                raw = json,
            )
            "search_code" -> AgentAction(
                AgentActionKind.SEARCH_CODE,
                query = string(root, "query"),
                topK = int(root, "top_k", 10),
                raw = json,
            )
            "finish" -> AgentAction(AgentActionKind.FINISH, summary = string(root, "summary") ?: "", raw = json)
            else -> AgentAction(AgentActionKind.UNKNOWN, raw = json)
        }
    }

    /**
     * The first balanced `{...}` in the text, brace-counting with string and
     * escape awareness so a `}` inside a quoted replacement does not end it.
     * Models wrap JSON in prose and code fences; this is what survives that.
     */
    internal fun extractFirstJsonObject(text: String): String? {
        val start = text.indexOf(char = Char(123))
        if (start < 0) return null

        var depth = 0
        var inString = false
        var escape = false

        for (i in start until text.length) {
            val c = text[i]
            if (escape) { escape = false; continue }
            if (inString) {
                when (c) {
                    Char(92) -> escape = true
                    Char(34) -> inString = false
                }
                continue
            }
            when (c) {
                Char(34) -> inString = true
                Char(123) -> depth++
                Char(125) -> {
                    depth--
                    if (depth == 0) return text.substring(start, i + 1)
                }
            }
        }
        return null
    }

    /** A number where a string belongs falls back rather than coercing. */
    private fun string(o: JsonObject, name: String): String? {
        val p = o[name] as? JsonPrimitive ?: return null
        return if (p.isString) p.content else null
    }

    /** Booleans are not numbers, and a non-integral value is not an int. */
    private fun int(o: JsonObject, name: String, fallback: Int = 0): Int {
        val p = o[name] as? JsonPrimitive ?: return fallback
        if (p.isString) return fallback
        if (p.content == "true" || p.content == "false") return fallback
        return p.content.toIntOrNull() ?: fallback
    }

    private fun stringArray(o: JsonObject, name: String): List<String> {
        val arr = o[name] as? JsonArray ?: return emptyList()
        return arr.mapNotNull { e ->
            val p = e as? JsonPrimitive ?: return@mapNotNull null
            if (p.isString) p.content else null
        }
    }
}

// ── Requirements and the catalogue ──────────────────────────────────────────

/** The floor a device and a model must both clear before coding is offered. */
data class CodingModelRequirements(
    val minParametersBillion: Int,
    val minRamGb: Double,
    val minFreeStorageGb: Double,
    val minDeviceTier: Int,
    val requiredCapabilities: Set<String>,
) {
    companion object {
        /**
         * Deliberately high. A 1B model that cannot hold a file in context
         * writes code that compiles and does the wrong thing, which is worse
         * than nothing.
         */
        val DEFAULT = CodingModelRequirements(
            minParametersBillion = 3,
            minRamGb = 8.0,
            minFreeStorageGb = 6.0,
            minDeviceTier = 2,                       // tablet
            requiredCapabilities = setOf("tools", "reasoning", "longContext"),
        )
    }
}

/** One catalogued coding model. */
data class CodingModelDescriptor(
    val modelId: String,
    val parametersBillion: Int,
    val minRamGb: Double,
    val minFreeStorageGb: Double,
    val totalBytes: Long,
    /** Non-empty, always: an unverifiable bundle is refused at registration. */
    val sha256: String,
    val capabilities: Set<String>,
)

/** Where the coding models are declared. Empty is the honest default. */
interface CodingModelCatalog {
    val backendId: String
    val available: List<CodingModelDescriptor>
}

/** No coding model is installed - which is the truth on most builds. */
object EmptyCodingModelCatalog : CodingModelCatalog {
    override val backendId = "empty"
    override val available: List<CodingModelDescriptor> = emptyList()
}

class UnverifiableModelException(modelId: String) : IllegalArgumentException(
    "A coding model MUST carry a SHA-256 verification hash. Refusing to register " +
        "an unverifiable bundle ($modelId) - that would fake on-device availability."
)

/** A catalogue the host fills in at startup. Idempotent by model id. */
class InMemoryCodingModelCatalog(seed: List<CodingModelDescriptor>? = null) : CodingModelCatalog {
    private val lock = Any()
    private val models = mutableListOf<CodingModelDescriptor>()

    init { seed?.forEach { add(it) } }

    override val backendId = "in-memory"

    override val available: List<CodingModelDescriptor>
        get() = synchronized(lock) { models.toList() }

    /** Adding the same id twice is a no-op, not an error. */
    fun add(descriptor: CodingModelDescriptor): InMemoryCodingModelCatalog {
        if (descriptor.sha256.isBlank()) throw UnverifiableModelException(descriptor.modelId)
        synchronized(lock) {
            if (models.none { it.modelId.equals(descriptor.modelId, ignoreCase = true) }) {
                models.add(descriptor)
            }
        }
        return this
    }
}

// ── Commands ────────────────────────────────────────────────────────────────

data class CommandRequest(
    val executable: String,
    val arguments: List<String>,
    val workingDirectory: String,
    val timeoutMs: Int = 60_000,
)

/** `executed == false` means it never ran, and [denied] says why. */
data class CommandResult(
    val executed: Boolean,
    val exitCode: Int,
    val stdout: String,
    val stderr: String,
    val timedOut: Boolean,
    val denied: String? = null,
) {
    val success: Boolean get() = executed && !timedOut && exitCode == 0

    companion object {
        fun notRun(reason: String) = CommandResult(
            executed = false, exitCode = -1, stdout = "", stderr = "",
            timedOut = false, denied = reason,
        )
    }
}

interface CommandRunner {
    val backendId: String
    suspend fun run(request: CommandRequest): CommandResult
}

/**
 * The default. Running arbitrary commands is opt-in, and this is what
 * "not opted in" looks like: a refusal with a reason, not a silent failure.
 */
object DisabledCommandRunner : CommandRunner {
    override val backendId = "disabled"
    override suspend fun run(request: CommandRequest) = CommandResult.notRun(
        "command execution is disabled. The host must opt in with a ProcessCommandRunner allow-list."
    )
}

/**
 * Runs commands from an allow-list. There is no unrestricted mode: a runner
 * built with an empty allow-list throws rather than becoming a shell.
 */
class AllowListCommandRunner(
    allowedExecutables: List<String>,
    private val maxOutputChars: Int = 64 * 1024,
    private val execute: (suspend (CommandRequest) -> CommandResult)? = null,
) : CommandRunner {

    private val allowed: Set<String> = allowedExecutables.map { it.lowercase() }.toSet()

    init {
        require(allowed.isNotEmpty()) {
            "An allow-list with at least one executable is required. Refusing to run an unrestricted shell."
        }
    }

    override val backendId = "allow-list"

    override suspend fun run(request: CommandRequest): CommandResult {
        val name = request.executable.substringAfterLast(Char(47)).lowercase()
        if (name !in allowed && request.executable.lowercase() !in allowed) {
            return CommandResult.notRun("${request.executable} is not on the allow-list.")
        }
        val runner = execute ?: return CommandResult.notRun(
            "no process executor is wired; supply one to actually run commands."
        )
        val r = runner(request)
        return r.copy(stdout = clamp(r.stdout), stderr = clamp(r.stderr))
    }

    private fun clamp(s: String) =
        if (s.length <= maxOutputChars) s else s.take(maxOutputChars) + "\n...[truncated]"
}

// ── The device gate ─────────────────────────────────────────────────────────

/** Quality of a selection. Mirrors CircleAI.Inference.SelectionQuality. */
enum class CodingSelectionQuality { GOOD, BELOW_FLOOR, NOTHING_FITS, HEURISTIC_FALLBACK, UNAVAILABLE }

/** The outcome of planning: a quality, an optional model id, and a readable reason. */
data class CodingPlan(
    val quality: CodingSelectionQuality,
    val modelId: String?,
    val reason: String,
) {
    val isAvailable: Boolean get() = quality != CodingSelectionQuality.UNAVAILABLE
}

/** What the planner knows about the device it is running on. */
data class CodingDeviceProbe(
    val ramAvailableBytes: Long,
    val storageFreeBytes: Long,
    val tier: Int,
) {
    companion object {
        /** Fraction of free RAM a model may claim; the rest keeps the phone a phone. */
        const val RAM_FIT_HEADROOM = 0.85
        /** Decimal gigabyte - the unit a downloaded model size is quoted in. */
        const val BYTES_PER_GB = 1_000_000_000.0
    }

    val usableRamGb: Double get() = ramAvailableBytes * RAM_FIT_HEADROOM / BYTES_PER_GB
    val storageFreeGb: Double get() = storageFreeBytes / BYTES_PER_GB
}

/**
 * The honest planner: a weak phone, or a build with no installed coding model,
 * is told so in words rather than being let into a loop it cannot finish.
 */
class CodingCapabilityPlanner(
    private val catalog: CodingModelCatalog = EmptyCodingModelCatalog,
    private val req: CodingModelRequirements = CodingModelRequirements.DEFAULT,
) {
    fun planForCoding(probe: CodingDeviceProbe): CodingPlan {
        // The FLOOR uses raw free bytes; the FIT uses the headroom-scaled figure.
        val floorRamGb = probe.ramAvailableBytes / (1024.0 * 1024 * 1024)
        val floorStorage = probe.storageFreeBytes / (1024.0 * 1024 * 1024)
        val fitRamGb = probe.usableRamGb
        val fitStorageGb = probe.storageFreeGb

        if (probe.tier < req.minDeviceTier || floorRamGb + 0.0001 < req.minRamGb) {
            return CodingPlan(
                CodingSelectionQuality.UNAVAILABLE, null,
                "on-device coding needs ~${fmt(req.minRamGb)} GB free RAM and tier >= ${req.minDeviceTier}; " +
                    "this device has ${fmt(floorRamGb)} GB free and is tier ${probe.tier}. Unavailable by design.",
            )
        }

        if (floorStorage > 0 && floorStorage + 0.0001 < req.minFreeStorageGb) {
            return CodingPlan(
                CodingSelectionQuality.UNAVAILABLE, null,
                "a ${req.minParametersBillion}B+ coding model needs ~${fmt(req.minFreeStorageGb)} GB free storage; " +
                    "only ${fmt(floorStorage)} GB available.",
            )
        }

        if (catalog.available.isEmpty()) {
            return CodingPlan(
                CodingSelectionQuality.UNAVAILABLE, null,
                "device is capable, but no on-device coding model is installed. A real 3-7B coding " +
                    "model requires a downloaded, SHA-256-verified bundle this build does not carry. " +
                    "Register one via CodingModelCatalog to enable.",
            )
        }

        val winner = catalog.available
            .filter { it.capabilities.containsAll(req.requiredCapabilities) }
            .filter { it.parametersBillion >= req.minParametersBillion }
            .filter {
                it.minRamGb <= fitRamGb + 0.0001 &&
                    (fitStorageGb <= 0 || it.minFreeStorageGb <= fitStorageGb + 0.0001)
            }
            .maxByOrNull { it.parametersBillion }
            ?: return CodingPlan(
                CodingSelectionQuality.NOTHING_FITS, null,
                "coding models are catalogued but none clears this device RAM / storage / capability floor.",
            )

        return CodingPlan(
            CodingSelectionQuality.GOOD, winner.modelId,
            "${winner.modelId} (${winner.parametersBillion}B) fits this device.",
        )
    }

    /** The C# "0.#" format: at most one decimal, and no trailing ".0". */
    private fun fmt(v: Double): String {
        val r = Math.round(v * 10) / 10.0
        return if (r == Math.floor(r)) r.toInt().toString() else String.format("%.1f", r)
    }
}

// ── The loop ────────────────────────────────────────────────────────────────

/** Knobs. The defaults are the safe ones: no commands, bounded observations. */
data class CodeAgentOptions(
    val maxIterations: Int? = null,
    val allowCommands: Boolean = false,
    val requirements: CodingModelRequirements = CodingModelRequirements.DEFAULT,
    val maxObservationChars: Int = 8 * 1024,
)

/** One turn, kept so a person can read back what the agent did. */
data class CodeAgentStep(
    val index: Int,
    val action: AgentActionKind,
    val detail: String,
    val observation: String,
    /** The command this step ran, when it ran one. Null for a step that only
     *  thought. Without it a transcript cannot show what was executed. */
    val command: List<String>? = null,
    /** What that command returned. Null when nothing ran. */
    val result: CommandResult? = null,
)

/** An edit the agent applied. */
data class CodeFileEdit(
    val path: String,
    val rangeStart: Int,
    val rangeEnd: Int,
    val replacement: String,
)

/**
 * The whole run. `available == false` means the device gate refused, and
 * [reason] says why in words.
 */
data class CodeAgentRunResult(
    val available: Boolean,
    val quality: CodingSelectionQuality,
    val reason: String,
    val steps: List<CodeAgentStep>,
    val appliedEdits: List<CodeFileEdit>,
    val finalSummary: String,
    /**
     * Whether the agent stopped because it was DONE.
     *
     * "reached the step limit" and "the task is done" are completely different
     * outcomes and a caller must be able to tell them apart. `reason` is prose
     * for a person; this is the flag code branches on.
     */
    val finished: Boolean = false,
)

/** The workspace seams the loop drives. */
interface CodeEditor {
    suspend fun read(path: String): String
    suspend fun apply(edits: List<CodeFileEdit>)
    suspend fun save(path: String)
}

data class CodeMatch(val path: String, val line: Int, val snippet: String)

interface CodeSearch {
    suspend fun search(query: String, topK: Int): List<CodeMatch>
}

/** On-device coding is not wired on this build. Says so; does nothing. */
object NullCodeAgent {
    suspend fun run(task: String, workspaceRoot: String) = CodeAgentRunResult(
        available = false,
        quality = CodingSelectionQuality.UNAVAILABLE,
        reason = "null code agent: on-device coding is not wired on this build.",
        steps = emptyList(),
        appliedEdits = emptyList(),
        finalSummary = "",
    )
}

/** Helpers the loop uses, exposed so they can be tested without a brain. */
object CodeAgentPaths {

    /**
     * Resolve a model-supplied path against the workspace and refuse anything
     * that escapes it. Path traversal out of the workspace is the obvious way
     * an on-device agent goes from "edit my repo" to "edit /etc" - closed here.
     */
    fun resolve(workspaceRoot: String, candidate: String?): String? {
        if (candidate.isNullOrBlank()) return null

        val root = java.io.File(workspaceRoot).normalize().path
        val full = if (candidate.startsWith(Char(47))) {
            java.io.File(candidate).normalize().path
        } else {
            java.io.File(workspaceRoot, candidate).normalize().path
        }

        if (full.equals(root, ignoreCase = true)) return full
        val rootWithSep = if (root.endsWith(Char(47))) root else root + Char(47)
        return if (full.lowercase().startsWith(rootWithSep.lowercase())) full else null
    }
}

object CodeAgentPrompt {

    /**
     * The contract handed to the model. Actions it cannot perform are not
     * listed, so it never asks for one and never gets refused for asking.
     */
    fun build(task: String, workspaceRoot: String, allowCommands: Boolean, hasSearch: Boolean): String {
        val q = Char(34)
        val lines = mutableListOf(
            "You are an on-device coding agent working inside the workspace: $workspaceRoot.",
            "Work ONE step at a time. Reply with a SINGLE JSON object and nothing else.",
            "Supported actions:",
            "  {${q}action${q}:${q}read_file${q},${q}path${q}:${q}relative/path${q}}",
        )
        if (hasSearch) lines += "  {${q}action${q}:${q}search_code${q},${q}query${q}:${q}text${q},${q}top_k${q}:10}"
        lines += "  {${q}action${q}:${q}edit_file${q},${q}path${q}:${q}relative/path${q}," +
            "${q}range_start${q}:0,${q}range_end${q}:0,${q}replacement${q}:${q}text${q}}"
        if (allowCommands) {
            lines += "  {${q}action${q}:${q}run_command${q},${q}executable${q}:${q}dotnet${q}," +
                "${q}args${q}:[${q}build${q}],${q}cwd${q}:${q}.${q}}"
        }
        lines += "  {${q}action${q}:${q}finish${q},${q}summary${q}:${q}what you did${q}}"
        lines += "range_start/range_end are absolute character offsets into the file CURRENT text; read before you edit."
        lines += "Paths must stay inside the workspace. After each action you receive an observation. Finish when done."
        lines += "Task: $task"
        return lines.joinToString("\n")
    }

    fun truncate(s: String, max: Int): String {
        if (s.isEmpty()) return s
        val m = if (max < 1) 1 else max
        return if (s.length <= m) s else s.take(m) + "\n...[truncated ${s.length - m} chars]"
    }
}
