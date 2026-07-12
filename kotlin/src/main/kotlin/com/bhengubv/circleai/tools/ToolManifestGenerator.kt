// ToolManifestGenerator.kt
//
// Kotlin port of CircleAI.Tools/ToolManifestGenerator.cs.
//
// Renders ToolDefinition collections into formats consumable by LLMs:
//   - JSON in OpenAI/Qwen function-calling format (for tool_choice / tools).
//   - Markdown for inclusion in a system prompt as documentation.
//
// C# System.Text.Json -> kotlinx.serialization.json (buildJsonObject / Json).

package com.bhengubv.circleai.tools

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.add
import kotlinx.serialization.json.addJsonObject
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import kotlinx.serialization.json.putJsonArray
import kotlinx.serialization.json.putJsonObject

/**
 * Renders [ToolDefinition] collections into JSON (OpenAI/Qwen function-calling)
 * or Markdown (system-prompt documentation).
 */
object ToolManifestGenerator {

    private val json = Json { prettyPrint = true }

    /**
     * Renders the given tools as a JSON array in OpenAI/Qwen function-calling
     * format. Each element is
     * `{ "type": "function", "function": { "name", "description", "parameters" } }`.
     */
    fun generateJsonManifest(tools: List<ToolDefinition>): String {
        val array: JsonArray = buildJsonArray {
            for (tool in tools) {
                addJsonObject {
                    put("type", "function")
                    putJsonObject("function") {
                        put("name", tool.name)
                        put("description", tool.description)
                        putJsonObject("parameters") {
                            put("type", "object")
                            putJsonObject("properties") {
                                for ((key, value) in tool.parameters) {
                                    putJsonObject(key) {
                                        put("type", value.type)
                                        put("description", value.description)
                                        val e = value.enum
                                        if (e != null && e.isNotEmpty()) {
                                            putJsonArray("enum") { e.forEach { add(it) } }
                                        }
                                    }
                                }
                            }
                            putJsonArray("required") { tool.requiredParameters.forEach { add(it) } }
                        }
                    }
                }
            }
        }
        return json.encodeToString(JsonArray.serializer(), array)
    }

    /**
     * Renders the given tools as a human-readable Markdown summary, suitable for
     * inclusion in a system prompt. Tools are grouped by API (the first segment
     * after the "tgn." prefix).
     */
    fun generateMarkdownManifest(tools: List<ToolDefinition>): String {
        val sb = StringBuilder()
        sb.appendLine("# Available Tools")
        sb.appendLine()
        sb.appendLine("Total: ${tools.size} tools.")
        sb.appendLine()

        val groups = sortedMapOf<String, MutableList<ToolDefinition>>()
        for (tool in tools) {
            groups.getOrPut(extractApiSlug(tool.name)) { ArrayList() }.add(tool)
        }

        for ((groupKey, groupTools) in groups) {
            sb.appendLine("## $groupKey")
            sb.appendLine()
            for (tool in groupTools) {
                sb.appendLine("### `${tool.name}`")
                sb.appendLine()
                sb.appendLine(tool.description)
                sb.appendLine()

                if (tool.parameters.isEmpty()) {
                    sb.appendLine("_No parameters._")
                    sb.appendLine()
                    continue
                }

                sb.appendLine("Parameters:")
                sb.appendLine()
                sb.appendLine("| Name | Type | Required | Description |")
                sb.appendLine("|------|------|----------|-------------|")

                val requiredSet = tool.requiredParameters.toHashSet()
                for ((key, value) in tool.parameters) {
                    val required = if (key in requiredSet) "yes" else "no"
                    var desc = escapePipe(value.description)
                    val e = value.enum
                    if (e != null && e.isNotEmpty()) {
                        desc += " Allowed values: " + e.joinToString(", ") + "."
                    }
                    sb.appendLine("| `$key` | ${value.type} | $required | $desc |")
                }
                sb.appendLine()
            }
        }

        return sb.toString()
    }

    private fun extractApiSlug(toolName: String): String {
        // Tool names are "tgn.<api>.<verb>". Group by "tgn.<api>".
        val prefix = "tgn."
        if (!toolName.startsWith(prefix)) return toolName
        val rest = toolName.substring(prefix.length)
        val dot = rest.indexOf('.')
        return if (dot < 0) prefix + rest else prefix + rest.substring(0, dot)
    }

    private fun escapePipe(s: String): String = s.replace("|", "\\|")
}
