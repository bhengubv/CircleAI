// CodeUnderstanding.kt
//
// Kotlin port of CircleAI.CodeUnderstanding (Contracts.cs +
// InMemoryCodeUnderstanding.cs + NullImplementations.cs) — the C# reference is
// the EXACT spec. A filesystem code indexer (regex declaration scan across
// .cs/.ts/.js/.py/.go), an index-backed searcher, and an in-memory symbol graph.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `ValueTask` async members -> `suspend fun`.
//   * `Directory.EnumerateFiles(..., AllDirectories)` -> File.walkTopDown().
//   * The C# regexes use lookbehind + capture groups; the Kotlin ports use the
//     same patterns via matched groups (declaration keyword group + name group).
//   * SemanticSearchAsync delegates to SearchAsync (substring fallback, no
//     embedding), matching C#.

package com.bhengubv.circleai.codeunderstanding

import java.io.File

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/** A declared symbol at a source location. Mirrors C# `CodeSymbol`. */
data class CodeSymbol(val path: String, val line: Int, val name: String, val kind: String)

/** A search match. Mirrors C# `CodeMatch`. */
data class CodeMatch(val path: String, val line: Int, val snippet: String, val score: Float)

/** An edge between two symbols. Mirrors C# `SymbolEdge`. */
data class SymbolEdge(val from: CodeSymbol, val to: CodeSymbol, val kind: String)

/** Code indexer. Mirrors C# `ICodeIndexer`. */
interface ICodeIndexer {
    val backendId: String
    suspend fun indexAsync(repoPath: String)
    suspend fun countSymbolsAsync(repoPath: String): Int
}

/** Code searcher. Mirrors C# `ICodeSearch`. */
interface ICodeSearch {
    val backendId: String
    suspend fun searchAsync(query: String, topK: Int = 10): List<CodeMatch>
    suspend fun semanticSearchAsync(query: String, topK: Int = 10): List<CodeMatch>
}

/** Symbol call graph. Mirrors C# `ISymbolGraph`. */
interface ISymbolGraph {
    val backendId: String
    suspend fun callersOfAsync(s: CodeSymbol): List<SymbolEdge>
    suspend fun calleesOfAsync(s: CodeSymbol): List<SymbolEdge>
}

// =====================================================================
// In-memory implementations (InMemoryCodeUnderstanding.cs)
// =====================================================================

/** Filesystem regex-scan indexer. Mirrors C# `FilesystemCodeIndexer`. */
class FilesystemCodeIndexer : ICodeIndexer {
    private data class LangRule(val ext: String, val declRx: Regex, val kind: String)

    private val languages = listOf(
        LangRule(".cs", Regex("""\b(?:class|interface|record|enum|struct)\s+(\w+)"""), "csharp"),
        LangRule(".cs", Regex("""\b(?:public|private|internal|protected|static)\s+\w+\s+(\w+)\s*\("""), "csharp-method"),
        LangRule(".ts", Regex("""\b(?:class|interface|type|enum)\s+(\w+)"""), "ts"),
        LangRule(".js", Regex("""\b(?:class|function)\s+(\w+)"""), "js"),
        LangRule(".py", Regex("""^\s*(?:def|class)\s+(\w+)"""), "python"),
        LangRule(".go", Regex("""^\s*func\s+(?:\(\w+\s+\*?\w+\)\s+)?(\w+)"""), "go"),
    )

    internal val index = java.util.concurrent.ConcurrentHashMap<String, List<CodeSymbol>>()

    override val backendId: String get() = "filesystem"

    override suspend fun indexAsync(repoPath: String) {
        require(repoPath.isNotBlank()) { "repoPath required" }
        val root = File(repoPath)
        if (!root.isDirectory) throw java.io.FileNotFoundException(repoPath)

        val symbols = ArrayList<CodeSymbol>()
        for (file in enumerateSourceFiles(root)) {
            val lines = file.readLines()
            val ext = "." + file.extension.lowercase()
            for (i in lines.indices) {
                for (rule in languages) {
                    if (rule.ext != ext) continue
                    for (m in rule.declRx.findAll(lines[i])) {
                        val name = m.groupValues.getOrNull(1)
                        if (!name.isNullOrEmpty()) {
                            symbols.add(CodeSymbol(file.path, i + 1, name, rule.kind))
                        }
                    }
                }
            }
        }
        index[repoPath] = symbols
    }

    override suspend fun countSymbolsAsync(repoPath: String): Int = index[repoPath]?.size ?: 0

    private fun enumerateSourceFiles(root: File): Sequence<File> {
        val sep = File.separator
        return root.walkTopDown().filter { file ->
            if (!file.isFile) return@filter false
            val ext = "." + file.extension.lowercase()
            if (ext !in setOf(".cs", ".ts", ".js", ".py", ".go")) return@filter false
            val p = file.path
            !p.contains("${sep}obj$sep") && !p.contains("${sep}bin$sep") && !p.contains("${sep}node_modules$sep")
        }
    }
}

/** Index-backed searcher. Mirrors C# `IndexBackedCodeSearch`. */
class IndexBackedCodeSearch(private val indexer: FilesystemCodeIndexer) : ICodeSearch {
    override val backendId: String get() = "index-backed"

    override suspend fun searchAsync(query: String, topK: Int): List<CodeMatch> {
        if (topK <= 0) throw IndexOutOfBoundsException("topK")
        return indexer.index.values.asSequence().flatten()
            .filter { it.name.contains(query, ignoreCase = true) }
            .map { CodeMatch(it.path, it.line, "${it.kind} ${it.name}", 1.0f) }
            .take(topK)
            .toList()
    }

    override suspend fun semanticSearchAsync(query: String, topK: Int): List<CodeMatch> =
        searchAsync(query, topK) // No real embedding; substring fallback.
}

/** In-memory adjacency-list symbol graph. Mirrors C# `InMemorySymbolGraph`. */
class InMemorySymbolGraph : ISymbolGraph {
    private val edges = ArrayList<SymbolEdge>()
    private val lock = Any()

    override val backendId: String get() = "in-memory"

    fun link(from: CodeSymbol, to: CodeSymbol, kind: String = "calls") {
        synchronized(lock) { edges.add(SymbolEdge(from, to, kind)) }
    }

    override suspend fun callersOfAsync(s: CodeSymbol): List<SymbolEdge> {
        synchronized(lock) { return edges.filter { it.to.name == s.name } }
    }

    override suspend fun calleesOfAsync(s: CodeSymbol): List<SymbolEdge> {
        synchronized(lock) { return edges.filter { it.from.name == s.name } }
    }
}

// =====================================================================
// Null implementations (NullImplementations.cs)
// =====================================================================

/** No-op [ICodeIndexer]. Mirrors C# `NullCodeIndexer`. */
class NullCodeIndexer private constructor() : ICodeIndexer {
    override val backendId: String get() = "null"
    override suspend fun indexAsync(repoPath: String) {}
    override suspend fun countSymbolsAsync(repoPath: String): Int = 0

    companion object {
        val Instance = NullCodeIndexer()
    }
}

/** No-op [ICodeSearch]. Mirrors C# `NullCodeSearch`. */
class NullCodeSearch private constructor() : ICodeSearch {
    override val backendId: String get() = "null"
    override suspend fun searchAsync(query: String, topK: Int): List<CodeMatch> = emptyList()
    override suspend fun semanticSearchAsync(query: String, topK: Int): List<CodeMatch> = emptyList()

    companion object {
        val Instance = NullCodeSearch()
    }
}

/** No-op [ISymbolGraph]. Mirrors C# `NullSymbolGraph`. */
class NullSymbolGraph private constructor() : ISymbolGraph {
    override val backendId: String get() = "null"
    override suspend fun callersOfAsync(s: CodeSymbol): List<SymbolEdge> = emptyList()
    override suspend fun calleesOfAsync(s: CodeSymbol): List<SymbolEdge> = emptyList()

    companion object {
        val Instance = NullSymbolGraph()
    }
}
