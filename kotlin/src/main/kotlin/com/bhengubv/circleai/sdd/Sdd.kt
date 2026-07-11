// Sdd.kt
//
// Kotlin port of CircleAI.SDD (Contracts.cs + InMemorySDD.cs +
// NullImplementations.cs) — the C# reference is the EXACT spec. Spec-Driven
// Development: a specification store, a JSON-shape validator, and a
// hello-world scaffolder (C# / TypeScript / Python).
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `ReadOnlyMemory<byte>` -> `ByteArray`.
//   * C# `ValueTask` async members -> `suspend fun`.
//   * `System.Text.Json` -> `kotlinx.serialization.json`.
//   * The validator requires Title + Body, and (when a schema is supplied) that
//     it parses as a JSON object declaring a top-level "type".
//   * The scaffolder throws for unsupported languages (matches C#
//     NotSupportedException semantics via UnsupportedOperationException).

package com.bhengubv.circleai.sdd

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/** A specification document. Mirrors C# `Specification`. */
data class Specification(
    val specId: String,
    val title: String,
    val body: String,
    val schema: String?,
    val metadata: Map<String, String>? = null,
)

/** The outcome of validating a spec. Mirrors C# `SpecValidationResult`. */
data class SpecValidationResult(val isValid: Boolean, val errors: List<String>)

/** A scaffolded project as path -> bytes. Mirrors C# `ScaffoldedProject`. */
data class ScaffoldedProject(val projectId: String, val files: Map<String, ByteArray>)

/** Persistent specification store. Mirrors C# `ISpecificationStore`. */
interface ISpecificationStore {
    val backendId: String
    suspend fun upsertAsync(spec: Specification)
    suspend fun getAsync(specId: String): Specification?
    suspend fun listAsync(): List<Specification>
}

/** Specification validator. Mirrors C# `ISpecificationValidator`. */
interface ISpecificationValidator {
    val backendId: String
    suspend fun validateAsync(spec: Specification): SpecValidationResult
}

/** Spec-to-scaffold codegen. Mirrors C# `ISpecToScaffold`. */
interface ISpecToScaffold {
    val backendId: String
    suspend fun scaffoldAsync(spec: Specification, targetLanguage: String): ScaffoldedProject
}

// =====================================================================
// In-memory implementations (InMemorySDD.cs)
// =====================================================================

private val SddJson = Json { ignoreUnknownKeys = true }

/** In-memory [ISpecificationStore]. Mirrors C# `InMemorySpecificationStore`. */
class InMemorySpecificationStore : ISpecificationStore {
    private val items = java.util.concurrent.ConcurrentHashMap<String, Specification>()

    override val backendId: String get() = "in-memory"

    override suspend fun upsertAsync(spec: Specification) {
        require(spec.specId.isNotBlank()) { "SpecId required" }
        items[spec.specId] = spec
    }

    override suspend fun getAsync(specId: String): Specification? {
        require(specId.isNotBlank()) { "specId required" }
        return items[specId]
    }

    override suspend fun listAsync(): List<Specification> = items.values.toList()
}

/** JSON-shape validator. Mirrors C# `JsonShapeSpecificationValidator`. */
class JsonShapeSpecificationValidator : ISpecificationValidator {
    override val backendId: String get() = "json-shape"

    override suspend fun validateAsync(spec: Specification): SpecValidationResult {
        val errors = ArrayList<String>()
        if (spec.title.isBlank()) errors.add("Title is required.")
        if (spec.body.isBlank()) errors.add("Body is required.")
        if (!spec.schema.isNullOrBlank()) {
            try {
                val root = SddJson.parseToJsonElement(spec.schema)
                if (root !is JsonObject) {
                    errors.add("Schema must be a JSON object.")
                } else if (!root.containsKey("type")) {
                    errors.add("Schema must declare a top-level 'type'.")
                }
            } catch (ex: Exception) {
                errors.add("Schema is not valid JSON: ${ex.message}")
            }
        }
        return SpecValidationResult(errors.isEmpty(), errors)
    }
}

/** Hello-world scaffolder (C# / TypeScript / Python). Mirrors C# `HelloWorldSpecToScaffold`. */
class HelloWorldSpecToScaffold : ISpecToScaffold {
    override val backendId: String get() = "hello-world"

    override suspend fun scaffoldAsync(spec: Specification, targetLanguage: String): ScaffoldedProject {
        require(targetLanguage.isNotBlank()) { "targetLanguage required" }

        val files = LinkedHashMap<String, ByteArray>()
        val lang = targetLanguage.lowercase()
        val name = sanitizeName(spec.specId)

        when (lang) {
            "csharp", "c#" -> {
                files["Program.cs"] = bytes("Console.WriteLine(\"$name: ${escapeText(spec.title)}\");\n")
                files["$name.csproj"] = bytes(
                    "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><OutputType>Exe</OutputType>" +
                        "<TargetFramework>net9.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>\n</Project>\n",
                )
                files["README.md"] = bytes("# ${escapeText(spec.title)}\n\n${escapeText(spec.body)}\n")
            }
            "typescript", "ts" -> {
                files["index.ts"] = bytes("console.log(\"$name: ${escapeText(spec.title)}\");\n")
                files["package.json"] = bytes(
                    "{\"name\":\"$name\",\"version\":\"0.1.0\",\"main\":\"index.ts\"," +
                        "\"scripts\":{\"start\":\"ts-node index.ts\"}}\n",
                )
                files["tsconfig.json"] = bytes("{\"compilerOptions\":{\"strict\":true,\"target\":\"ES2022\",\"module\":\"commonjs\"}}\n")
                files["README.md"] = bytes("# ${escapeText(spec.title)}\n\n${escapeText(spec.body)}\n")
            }
            "python", "py" -> {
                files["main.py"] = bytes(
                    "def main():\n    print(\"$name: ${escapeText(spec.title)}\")\n\n" +
                        "if __name__ == \"__main__\":\n    main()\n",
                )
                files["pyproject.toml"] = bytes("[project]\nname = \"$name\"\nversion = \"0.1.0\"\nrequires-python = \">=3.10\"\n")
                files["README.md"] = bytes("# ${escapeText(spec.title)}\n\n${escapeText(spec.body)}\n")
            }
            else -> throw UnsupportedOperationException(
                "Language '$targetLanguage' is not supported by HelloWorldSpecToScaffold (csharp / typescript / python).",
            )
        }

        return ScaffoldedProject("$name-$lang", files)
    }

    private companion object {
        fun sanitizeName(id: String): String {
            if (id.isBlank()) return "project"
            val sb = StringBuilder()
            for (ch in id) {
                if (ch.isLetterOrDigit() || ch == '_' || ch == '-') sb.append(ch)
            }
            return if (sb.isEmpty()) "project" else sb.toString()
        }

        fun escapeText(s: String): String =
            s.replace("\\", "\\\\").replace("\"", "\\\"").replace("\n", "\\n")

        fun bytes(s: String): ByteArray = s.toByteArray(Charsets.UTF_8)
    }
}

// =====================================================================
// Null implementations (NullImplementations.cs)
// =====================================================================

private const val SDD_EMPTY_GUID = "00000000-0000-0000-0000-000000000000"

/** No-op [ISpecificationStore]. Mirrors C# `NullSpecificationStore`. */
class NullSpecificationStore private constructor() : ISpecificationStore {
    override val backendId: String get() = "null"
    override suspend fun upsertAsync(spec: Specification) {}
    override suspend fun getAsync(specId: String): Specification? = null
    override suspend fun listAsync(): List<Specification> = emptyList()

    companion object {
        val Instance = NullSpecificationStore()
    }
}

/** Always-invalid [ISpecificationValidator]. Mirrors C# `NullSpecificationValidator`. */
class NullSpecificationValidator private constructor() : ISpecificationValidator {
    override val backendId: String get() = "null"
    override suspend fun validateAsync(spec: Specification): SpecValidationResult =
        SpecValidationResult(isValid = false, errors = listOf("No real validator wired."))

    companion object {
        val Instance = NullSpecificationValidator()
    }
}

/** Empty-scaffold [ISpecToScaffold]. Mirrors C# `NullSpecToScaffold`. */
class NullSpecToScaffold private constructor() : ISpecToScaffold {
    override val backendId: String get() = "null"
    override suspend fun scaffoldAsync(spec: Specification, targetLanguage: String): ScaffoldedProject =
        ScaffoldedProject(SDD_EMPTY_GUID, emptyMap())

    companion object {
        val Instance = NullSpecToScaffold()
    }
}
