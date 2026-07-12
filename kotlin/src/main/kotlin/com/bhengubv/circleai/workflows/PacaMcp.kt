// PacaMcp.kt
//
// Kotlin port of CircleAI.Workflows/PacaMcp.cs.
//
// (3.3.0) MCP server for paca workflows. Tools surface = create_task,
// list_tasks, edit_task, add_comment, create_doc, link_doc_to_task, and any
// plugin-registered MCP tools. Three transports: stdio, SSE, HTTP. Per-agent
// MCP server config so each agent has its own toolset.
//
// C# delegate `ValueTask<string> PacaMcpHandler(string, CancellationToken)`
// -> Kotlin `suspend (String) -> String` (PacaMcpHandler fun interface).

package com.bhengubv.circleai.workflows

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.add
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import kotlinx.serialization.json.putJsonObject
import java.util.concurrent.ConcurrentHashMap

/** (3.3.0) MCP transport types. */
enum class McpTransportKind { Stdio, ServerSentEvents, Http }

/** (3.3.0) Per-agent MCP server config. */
data class AgentMcpConfig(
    val agentMemberId: String,
    val transports: List<McpTransportKind>,
    val enabledTools: List<String>,
    val toolSettings: Map<String, String>,
)

/** (3.3.0) MCP tool descriptor. */
data class PacaMcpTool(val name: String, val description: String, val inputSchema: String)

/** (3.3.0) MCP tool handler signature. */
fun interface PacaMcpHandler {
    suspend fun handle(argumentsJson: String): String
}

/**
 * (3.3.0) Paca's MCP server: registers built-in workflow tools + plugin tools.
 */
class PacaMcpServer {

    private val tools = ConcurrentHashMap<String, Pair<PacaMcpTool, PacaMcpHandler>>()
    private val agentConfigs = ConcurrentHashMap<String, AgentMcpConfig>()

    val toolList: List<PacaMcpTool> get() = tools.values.map { it.first }

    fun registerTool(tool: PacaMcpTool, handler: PacaMcpHandler) {
        // Case-insensitive keying like the C# OrdinalIgnoreCase dictionary.
        tools[tool.name.lowercase()] = tool to handler
    }

    /** (3.3.0) Configure a per-agent toolset. */
    fun configureAgent(config: AgentMcpConfig) {
        agentConfigs[config.agentMemberId] = config
    }

    fun getAgentConfig(agentMemberId: String): AgentMcpConfig? = agentConfigs[agentMemberId]

    /**
     * (3.3.0) Invoke a tool for a specific agent — enforces the agent's
     * enabled-tool list.
     */
    suspend fun invoke(agentMemberId: String, toolName: String, argumentsJson: String): String {
        val entry = tools[toolName.lowercase()] ?: return wrapError("Unknown tool '$toolName'.")
        val cfg = agentConfigs[agentMemberId]
        if (cfg != null && cfg.enabledTools.isNotEmpty() &&
            cfg.enabledTools.none { it.equals(toolName, ignoreCase = true) }
        ) {
            return wrapError("Tool '$toolName' is not enabled for agent '$agentMemberId'.")
        }
        return try {
            entry.second.handle(argumentsJson)
        } catch (ex: Exception) {
            wrapError(ex.message ?: ex.javaClass.simpleName)
        }
    }

    /** (3.3.0) JSON-RPC tools/list response payload. */
    fun toolsListJson(): String {
        val arr = buildJsonArray {
            for ((tool, _) in tools.values) {
                add(
                    buildJsonObject {
                        put("name", tool.name)
                        put("description", tool.description)
                        put("inputSchema", JSON.parseToJsonElement(tool.inputSchema))
                    },
                )
            }
        }
        val root = buildJsonObject { put("tools", arr) }
        return JSON.encodeToString(JsonObject.serializer(), root)
    }

    companion object {
        private val JSON = Json { ignoreUnknownKeys = true }

        private fun wrapError(message: String): String {
            val root = buildJsonObject { putJsonObject("error") { put("message", message) } }
            return JSON.encodeToString(JsonObject.serializer(), root)
        }
    }
}

/** (3.3.0) Built-in workflow tools. */
object PacaCoreMcpTools {

    val createTask: PacaMcpTool = PacaMcpTool(
        name = "create_task",
        description = "Create a new task in a project.",
        inputSchema = """{"type":"object","properties":{"project_id":{"type":"string"},"title":{"type":"string"},"description":{"type":"string"}},"required":["project_id","title"]}""",
    )

    val listTasks: PacaMcpTool = PacaMcpTool(
        name = "list_tasks",
        description = "List live tasks in a project.",
        inputSchema = """{"type":"object","properties":{"project_id":{"type":"string"}},"required":["project_id"]}""",
    )

    val editTask: PacaMcpTool = PacaMcpTool(
        name = "edit_task",
        description = "Edit a task (title, description, status).",
        inputSchema = """{"type":"object","properties":{"project_id":{"type":"string"},"number":{"type":"integer"},"title":{"type":"string"},"description":{"type":"string"},"status":{"type":"string"}},"required":["project_id","number"]}""",
    )

    val createDoc: PacaMcpTool = PacaMcpTool(
        name = "create_doc",
        description = "Create a doc in the project's doc tree.",
        inputSchema = """{"type":"object","properties":{"project_id":{"type":"string"},"title":{"type":"string"},"parent_id":{"type":"string","nullable":true},"content_json":{"type":"string"}},"required":["project_id","title","content_json"]}""",
    )

    val linkDocToTask: PacaMcpTool = PacaMcpTool(
        name = "link_doc_to_task",
        description = "Link a doc section to a task.",
        inputSchema = """{"type":"object","properties":{"doc_id":{"type":"string"},"section_anchor":{"type":"string"},"project_id":{"type":"string"},"task_number":{"type":"integer"}},"required":["doc_id","section_anchor","project_id","task_number"]}""",
    )
}
