// FacexTools.kt
//
// The tool definitions for the face-exchange surface.
//
// DECLARED, NOT IMPLEMENTED HERE. A tool definition is a name, a description and
// a schema — the thing the model reads to decide whether to call it. The
// handlers live where the capability does; keeping the definitions together is
// what stops two callers describing the same tool differently to the model.
//
// Ported from src/CircleAI.Tools/FacexTools.cs.

package com.bhengubv.circleai.tools

object FacexTools {

    /**
     * Every tool this surface offers.
     *
     * The DESCRIPTIONS are written at the model, not at a developer: they say
     * when to use the tool, because a model choosing between six tools reads
     * these and nothing else.
     */
    val definitions: List<ToolDefinition> = listOf(
        ToolDefinition(
            name = "facex_enrol",
            description = "Register a face for a named person, from a photo they have " +
                "consented to. Use only when the person is present and has agreed.",
            parameters = mapOf(
                "person_id" to ToolParameter("string", "Who this face belongs to."),
                "image_path" to ToolParameter("string", "Path to the photo.")
            ),
            requiredParameters = listOf("person_id", "image_path")
        ),
        ToolDefinition(
            name = "facex_identify",
            description = "Say who is in a photo, from faces already enrolled. Returns " +
                "nothing when there is no confident match — do not guess a name.",
            parameters = mapOf(
                "image_path" to ToolParameter("string", "Path to the photo.")
            ),
            requiredParameters = listOf("image_path")
        ),
        ToolDefinition(
            name = "facex_forget",
            description = "Remove an enrolled face. Use whenever somebody asks to be " +
                "forgotten; this is theirs to decide, not the user's.",
            parameters = mapOf(
                "person_id" to ToolParameter("string", "Whose face to remove.")
            ),
            requiredParameters = listOf("person_id")
        )
    )
}
