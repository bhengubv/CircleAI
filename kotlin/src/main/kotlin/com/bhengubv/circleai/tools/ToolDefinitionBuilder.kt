// ToolDefinitionBuilder.kt
//
// Kotlin port of CircleAI.Tools/ToolDefinitionBuilder.cs.
//
// Fluent builder for constructing ToolDefinition instances. Accumulates
// parameters in a list and builds an immutable map on build().

package com.bhengubv.circleai.tools

/**
 * Fluent builder for constructing [ToolDefinition] instances.
 *
 * ```
 * val tool = ToolDefinitionBuilder.create("get_weather")
 *     .description("Get current weather for a location")
 *     .parameter("city", "string", "The city name", required = true)
 *     .parameter("units", "string", "Temperature units", required = false,
 *         enumValues = arrayOf("celsius", "fahrenheit"))
 *     .build()
 * ```
 */
class ToolDefinitionBuilder private constructor(private val name: String) {

    private var description: String? = null
    private val parameters = ArrayList<Triple<String, ToolParameter, Boolean>>()

    /**
     * Sets the human-readable description for the tool.
     *
     * @throws IllegalArgumentException if [description] is blank.
     */
    fun description(description: String): ToolDefinitionBuilder {
        require(description.isNotEmpty()) { "description must not be null or empty" }
        this.description = description
        return this
    }

    /**
     * Adds a parameter to the tool definition.
     *
     * @param name The parameter name. Must be non-empty.
     * @param type JSON Schema type: "string", "number", "boolean", "object", "array".
     * @param description Human-readable description of the parameter.
     * @param required When `true`, the parameter is added to the required list.
     * @param enumValues Optional set of allowed values (for string parameters).
     * @throws IllegalArgumentException if [name], [type], or [description] is empty.
     */
    fun parameter(
        name: String,
        type: String,
        description: String,
        required: Boolean = false,
        enumValues: Array<String>? = null,
    ): ToolDefinitionBuilder {
        require(name.isNotEmpty()) { "name must not be null or empty" }
        require(type.isNotEmpty()) { "type must not be null or empty" }
        require(description.isNotEmpty()) { "description must not be null or empty" }

        parameters.add(Triple(name, ToolParameter(type = type, description = description, enum = enumValues), required))
        return this
    }

    /**
     * Builds the final [ToolDefinition] from the accumulated state.
     *
     * @throws IllegalStateException if [description] was not called before [build].
     */
    fun build(): ToolDefinition {
        val desc = description
        checkNotNull(desc?.takeIf { it.isNotEmpty() }) {
            "ToolDefinition '$name' requires a description. Call description() before build()."
        }

        val params = LinkedHashMap<String, ToolParameter>(parameters.size)
        val required = ArrayList<String>()
        for ((pName, param, isRequired) in parameters) {
            params[pName] = param
            if (isRequired) required.add(pName)
        }

        return ToolDefinition(
            name = name,
            description = desc,
            parameters = params,
            requiredParameters = required,
        )
    }

    companion object {
        /**
         * Creates a new builder for a tool with the given [name].
         *
         * @throws IllegalArgumentException if [name] is empty.
         */
        fun create(name: String): ToolDefinitionBuilder {
            require(name.isNotEmpty()) { "name must not be null or empty" }
            return ToolDefinitionBuilder(name)
        }
    }
}
