// DevTools.kt
//
// Kotlin port of CircleAI.DevTools (Contracts.cs + InMemoryDevTools.cs +
// NullImplementations.cs) — the C# reference is the EXACT spec. The dev-tools
// replacement surface: filesystem code editor, token-context inline suggester,
// in-memory agent shell, pattern-match patch planner, and regex refactor tool.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `ValueTask` async members -> `suspend fun`.
//   * Edits are applied per-file, ranges sorted descending so earlier offsets
//     stay valid (matches C# StringBuilder Remove+Insert loop).
//   * The agent-shell executor seam is a `(prompt) -> AgentTurn` function; the
//     default is the deterministic built-in echo responder.
//   * File I/O uses java.io.File; regex uses Kotlin Regex with the same patterns.

package com.bhengubv.circleai.devtools

import java.io.File
import java.util.concurrent.atomic.AtomicLong
import kotlin.math.min

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/** A single-file edit: replace [rangeStart, rangeEnd) with [replacement]. Mirrors C# `FileEdit`. */
data class FileEdit(val path: String, val rangeStart: Int, val rangeEnd: Int, val replacement: String)

/** A ghost-text completion. Mirrors C# `InlineSuggestion`. */
data class InlineSuggestion(val text: String, val confidence: Float)

/** One agent-shell turn. Mirrors C# `AgentTurn`. */
data class AgentTurn(val turnId: String, val userPrompt: String, val response: String, val edits: List<FileEdit>)

/** A proposed multi-file patch plan. Mirrors C# `PatchPlan`. */
data class PatchPlan(val goal: String, val steps: List<String>, val proposedEdits: List<FileEdit>)

/** A cross-file refactor request. Mirrors C# `RefactorRequest`. */
data class RefactorRequest(val description: String, val targetPaths: List<String>)

/** Read / write text buffers. Mirrors C# `ICodeEditor`. */
interface ICodeEditor {
    val backendId: String
    suspend fun readAsync(path: String): String
    suspend fun applyAsync(edits: List<FileEdit>)
    suspend fun saveAsync(path: String)
}

/** Tab-completion / ghost-text suggester. Mirrors C# `IInlineSuggester`. */
interface IInlineSuggester {
    val backendId: String
    suspend fun suggestAsync(path: String, line: Int, column: Int, contextBefore: String): InlineSuggestion?
}

/** Agent-shell loop. Mirrors C# `IAgentShell`. */
interface IAgentShell {
    val backendId: String
    suspend fun runTurnAsync(userPrompt: String): AgentTurn
    suspend fun historyAsync(limit: Int = 50): List<AgentTurn>
}

/** Multi-file patch planner. Mirrors C# `IPatchPlanner`. */
interface IPatchPlanner {
    val backendId: String
    suspend fun planAsync(goal: String): PatchPlan
    suspend fun applyAsync(plan: PatchPlan)
}

/** Cross-file refactor primitives. Mirrors C# `IRefactorTool`. */
interface IRefactorTool {
    val backendId: String
    suspend fun proposeAsync(request: RefactorRequest): List<FileEdit>
}

// =====================================================================
// In-memory implementations (InMemoryDevTools.cs)
// =====================================================================

/** Filesystem-backed [ICodeEditor]. Mirrors C# `FilesystemCodeEditor`. */
class FilesystemCodeEditor : ICodeEditor {
    override val backendId: String get() = "filesystem"

    override suspend fun readAsync(path: String): String {
        require(path.isNotBlank()) { "path required" }
        return File(path).readText()
    }

    override suspend fun applyAsync(edits: List<FileEdit>) {
        for (byFile in edits.groupBy { it.path }) {
            val file = File(byFile.key)
            val sb = StringBuilder(file.readText())
            // Apply from the end so earlier offsets stay valid.
            for (e in byFile.value.sortedByDescending { it.rangeStart }) {
                if (e.rangeStart < 0 || e.rangeEnd > sb.length || e.rangeEnd < e.rangeStart) {
                    throw IndexOutOfBoundsException("Invalid edit range ${e.rangeStart}..${e.rangeEnd} for ${e.path}")
                }
                sb.replace(e.rangeStart, e.rangeEnd, e.replacement)
            }
            file.writeText(sb.toString())
        }
    }

    override suspend fun saveAsync(path: String) {}
}

/** Token-context inline suggester. Mirrors C# `TokenContextInlineSuggester`. */
class TokenContextInlineSuggester : IInlineSuggester {
    override val backendId: String get() = "token-context"

    override suspend fun suggestAsync(path: String, line: Int, column: Int, contextBefore: String): InlineSuggestion? {
        require(path.isNotBlank()) { "path required" }

        val partial = extractPartialAtCursor(contextBefore)
        if (partial.length < 2) return null

        val f = File(path)
        val fileText = if (f.exists()) f.readText() else contextBefore
        val freq = HashMap<String, Int>()
        for (m in IDENTIFIER_RX.findAll(fileText)) {
            val v = m.value
            if (v.startsWith(partial) && v.length > partial.length) {
                freq[v] = (freq[v] ?: 0) + 1
            }
        }
        if (freq.isEmpty()) return null
        val best = freq.entries.sortedWith(
            compareByDescending<Map.Entry<String, Int>> { it.value }.thenBy { it.key.length },
        ).first()
        val completion = best.key.substring(partial.length)
        val confidence = min(1.0, best.value / 10.0)
        return InlineSuggestion(completion, confidence.toFloat())
    }

    private fun extractPartialAtCursor(contextBefore: String): String {
        var i = contextBefore.length
        while (i > 0 && (contextBefore[i - 1].isLetterOrDigit() || contextBefore[i - 1] == '_')) i--
        return contextBefore.substring(i)
    }

    private companion object {
        val IDENTIFIER_RX = Regex("""[A-Za-z_][A-Za-z0-9_]*""")
    }
}

/** In-memory agent shell with a built-in echo executor. Mirrors C# `InMemoryAgentShell`. */
class InMemoryAgentShell(
    executor: (suspend (String) -> AgentTurn)? = null,
) : IAgentShell {
    private val executor: suspend (String) -> AgentTurn = executor ?: { builtInExecutor(it) }
    private val history = ArrayList<AgentTurn>()
    private val lock = Any()
    private val seq = AtomicLong(0)

    override val backendId: String get() = "in-memory"

    override suspend fun runTurnAsync(userPrompt: String): AgentTurn {
        val t = executor(userPrompt)
        val turn = if (t.turnId.isEmpty()) t.copy(turnId = "turn-${seq.incrementAndGet()}") else t
        synchronized(lock) { history.add(turn) }
        return turn
    }

    override suspend fun historyAsync(limit: Int): List<AgentTurn> {
        if (limit <= 0) throw IndexOutOfBoundsException("limit")
        synchronized(lock) {
            // Most-recent `limit`, restored to chronological order (matches C# Reverse/Take/Reverse).
            return history.asReversed().take(limit).asReversed().toList()
        }
    }

    private companion object {
        fun builtInExecutor(prompt: String): AgentTurn {
            val trimmed = prompt.trim()
            val response = when {
                trimmed.startsWith("read ", ignoreCase = true) -> "Reading ${trimmed.substring(5)} ..."
                trimmed.startsWith("write ", ignoreCase = true) -> "Writing ${trimmed.substring(6)} ..."
                trimmed.contains('?') -> "Acknowledged the question; need more context to give a useful answer."
                else -> "Acknowledged: $trimmed."
            }
            return AgentTurn(turnId = "", userPrompt = prompt, response = response, edits = emptyList())
        }
    }
}

/** Pattern-match patch planner. Mirrors C# `PatternMatchPatchPlanner`. */
class PatternMatchPatchPlanner(private val editor: ICodeEditor) : IPatchPlanner {
    override val backendId: String get() = "pattern-match"

    override suspend fun planAsync(goal: String): PatchPlan {
        require(goal.isNotBlank()) { "goal required" }

        RENAME_RX.find(goal)?.let { rename ->
            val oldName = rename.groupValues[1]
            val newName = rename.groupValues[2]
            val scope = rename.groupValues[3].ifEmpty { System.getProperty("user.dir") }
            val edits = computeRenameEdits(scope, oldName, newName)
            return PatchPlan(goal, listOf("Rename '$oldName' -> '$newName' across ${edits.size} location(s)"), edits)
        }
        REMOVE_RX.find(goal)?.let { remove ->
            val lineNo = remove.groupValues[1].toInt()
            val path = remove.groupValues[2].trim()
            val edits = computeRemoveLineEdits(path, lineNo)
            return PatchPlan(goal, listOf("Remove line $lineNo from $path"), edits)
        }
        APPEND_RX.find(goal)?.let { append ->
            val text = append.groupValues[1].trim().trim('"')
            val path = append.groupValues[2].trim()
            val f = File(path)
            val len = if (f.exists()) f.readText().length else 0
            val edits = listOf(FileEdit(path, len, len, text))
            return PatchPlan(goal, listOf("Append to $path"), edits)
        }
        return PatchPlan(goal, listOf("no recognised intent"), emptyList())
    }

    override suspend fun applyAsync(plan: PatchPlan) {
        editor.applyAsync(plan.proposedEdits)
    }

    private companion object {
        val RENAME_RX = Regex("""^rename\s+(\S+)\s+to\s+(\S+)(?:\s+in\s+(.+))?$""", RegexOption.IGNORE_CASE)
        val REMOVE_RX = Regex("""^remove\s+line\s+(\d+)\s+from\s+(.+)$""", RegexOption.IGNORE_CASE)
        val APPEND_RX = Regex("""^append\s+(.+?)\s+to\s+(.+)$""", RegexOption.IGNORE_CASE)

        fun computeRenameEdits(scope: String, oldName: String, newName: String): List<FileEdit> {
            val scopeFile = File(scope)
            if (!scopeFile.isDirectory && !scopeFile.isFile) throw java.io.FileNotFoundException(scope)
            val sep = File.separator
            val files = if (scopeFile.isFile) {
                listOf(scopeFile)
            } else {
                scopeFile.walkTopDown().filter { it.isFile && it.extension.equals("cs", ignoreCase = true) }.toList()
            }
            val edits = ArrayList<FileEdit>()
            for (f in files) {
                if (f.path.contains("${sep}obj$sep") || f.path.contains("${sep}bin$sep")) continue
                val text = f.readText()
                val rx = Regex("""\b${Regex.escape(oldName)}\b""")
                for (m in rx.findAll(text)) {
                    edits.add(FileEdit(f.path, m.range.first, m.range.last + 1, newName))
                }
            }
            return edits
        }

        fun computeRemoveLineEdits(path: String, lineNo: Int): List<FileEdit> {
            val f = File(path)
            if (!f.exists()) throw java.io.FileNotFoundException(path)
            val text = f.readText()
            var current = 1
            for (i in text.indices) {
                if (current == lineNo) {
                    val end = text.indexOf('\n', i)
                    val rangeEnd = if (end < 0) text.length else end + 1
                    return listOf(FileEdit(path, i, rangeEnd, ""))
                }
                if (text[i] == '\n') current++
            }
            return emptyList()
        }
    }
}

/** Regex refactor tool (Rename + ExtractConstant). Mirrors C# `RegexRefactorTool`. */
class RegexRefactorTool : IRefactorTool {
    override val backendId: String get() = "regex"

    override suspend fun proposeAsync(request: RefactorRequest): List<FileEdit> {
        val description = request.description.trim()
        if (description.startsWith("rename ", ignoreCase = true)) {
            val m = Regex("""^rename\s+(\S+)\s+to\s+(\S+)""", RegexOption.IGNORE_CASE).find(description) ?: return emptyList()
            return renameInFiles(request.targetPaths, m.groupValues[1], m.groupValues[2])
        }
        if (description.startsWith("extract ", ignoreCase = true)) {
            val m = Regex("""^extract\s+constant\s+from\s+"([^"]+)"\s+as\s+(\S+)""", RegexOption.IGNORE_CASE).find(description)
                ?: return emptyList()
            return extractConstant(request.targetPaths, m.groupValues[1], m.groupValues[2])
        }
        return emptyList()
    }

    private fun renameInFiles(paths: List<String>, oldName: String, newName: String): List<FileEdit> {
        val edits = ArrayList<FileEdit>()
        for (p in paths) {
            val f = File(p)
            if (!f.exists()) continue
            val text = f.readText()
            val rx = Regex("""\b${Regex.escape(oldName)}\b""")
            for (m in rx.findAll(text)) {
                edits.add(FileEdit(p, m.range.first, m.range.last + 1, newName))
            }
        }
        return edits
    }

    private fun extractConstant(paths: List<String>, literal: String, constantName: String): List<FileEdit> {
        val edits = ArrayList<FileEdit>()
        val quoted = "\"" + literal + "\""
        for (p in paths) {
            val f = File(p)
            if (!f.exists()) continue
            val text = f.readText()
            val first = text.indexOf(quoted)
            if (first < 0) continue
            val classIdx = text.indexOf("class ")
            if (classIdx < 0) continue
            val brace = text.indexOf('{', classIdx)
            if (brace < 0) continue
            val insertion = "\n    private const string $constantName = $quoted;\n"
            edits.add(FileEdit(p, brace + 1, brace + 1, insertion))
            var idx = first
            while (idx >= 0) {
                edits.add(FileEdit(p, idx, idx + quoted.length, constantName))
                idx = text.indexOf(quoted, idx + 1)
            }
        }
        return edits
    }
}

// =====================================================================
// Null implementations (NullImplementations.cs)
// =====================================================================

private const val DEVTOOLS_EMPTY_GUID = "00000000-0000-0000-0000-000000000000"

/** No-op [ICodeEditor]. Mirrors C# `NullCodeEditor`. */
class NullCodeEditor private constructor() : ICodeEditor {
    override val backendId: String get() = "null"
    override suspend fun readAsync(path: String): String = ""
    override suspend fun applyAsync(edits: List<FileEdit>) {}
    override suspend fun saveAsync(path: String) {}

    companion object {
        val Instance = NullCodeEditor()
    }
}

/** No-op [IInlineSuggester]. Mirrors C# `NullInlineSuggester`. */
class NullInlineSuggester private constructor() : IInlineSuggester {
    override val backendId: String get() = "null"
    override suspend fun suggestAsync(path: String, line: Int, column: Int, contextBefore: String): InlineSuggestion? = null

    companion object {
        val Instance = NullInlineSuggester()
    }
}

/** No-op [IAgentShell]. Mirrors C# `NullAgentShell`. */
class NullAgentShell private constructor() : IAgentShell {
    override val backendId: String get() = "null"
    override suspend fun runTurnAsync(userPrompt: String): AgentTurn =
        AgentTurn(DEVTOOLS_EMPTY_GUID, userPrompt, "", emptyList())
    override suspend fun historyAsync(limit: Int): List<AgentTurn> = emptyList()

    companion object {
        val Instance = NullAgentShell()
    }
}

/** No-op [IPatchPlanner]. Mirrors C# `NullPatchPlanner`. */
class NullPatchPlanner private constructor() : IPatchPlanner {
    override val backendId: String get() = "null"
    override suspend fun planAsync(goal: String): PatchPlan = PatchPlan(goal, emptyList(), emptyList())
    override suspend fun applyAsync(plan: PatchPlan) {}

    companion object {
        val Instance = NullPatchPlanner()
    }
}

/** No-op [IRefactorTool]. Mirrors C# `NullRefactorTool`. */
class NullRefactorTool private constructor() : IRefactorTool {
    override val backendId: String get() = "null"
    override suspend fun proposeAsync(request: RefactorRequest): List<FileEdit> = emptyList()

    companion object {
        val Instance = NullRefactorTool()
    }
}
