// PacaSkills.kt
//
// Kotlin port of CircleAI.Workflows/PacaSkills.cs.
//
// (3.3.0) Eleven built-in Claude Code skills ported from paca: paca, paca-epic,
// paca-breakdown, paca-clarify, paca-sprint, paca-estimate, paca-prioritize,
// paca-do, paca-test, paca-doc, paca-setup. Plus a skill installer that strips
// frontmatter and drops the markdown into ~/.claude/commands, and templates for
// the nine creator skills.

package com.bhengubv.circleai.workflows

import java.io.File

/** (3.3.0) A skill definition: frontmatter metadata + body. */
data class PacaSkill(val name: String, val description: String, val body: String) {
    /** (3.3.0) Render as a Claude-Code-compatible markdown file with frontmatter. */
    fun toMarkdown(): String = "---\nname: $name\ndescription: $description\n---\n\n$body"

    /** (3.3.0) Render as the bare body (frontmatter stripped) for the installer. */
    fun toBodyOnly(): String = body
}

/** (3.3.0) The nine creator-skill templates (markdown body). */
object SkillTemplates {
    const val EPIC = "You are running paca-epic. Use only the paca MCP tools. Output structure: title, problem statement, success criteria, scope, out-of-scope, risks."
    const val BREAKDOWN = "You are running paca-breakdown. Use only the paca MCP tools. Take the supplied epic and produce a numbered list of tasks with title + acceptance criteria."
    const val CLARIFY = "You are running paca-clarify. Pose the smallest set of clarifying questions needed to estimate the supplied task."
    const val SPRINT = "You are running paca-sprint. Use the create_sprint / start_sprint / complete_sprint MCP tools."
    const val ESTIMATE = "You are running paca-estimate. For each task, propose story points (1-13). Cite assumptions."
    const val PRIORITIZE = "You are running paca-prioritize. Reorder the backlog by importance (0-5). Cite reasoning."
    const val DO = "You are running paca-do. Pick the next-best ready task, mark in_progress, execute, then mark done."
    const val TEST = "You are running paca-test. Write and run unit + integration tests for the current change."
    const val DOC = "You are running paca-doc. Update the living document with the smallest accurate diff."
}

/** (3.3.0) The eleven built-in paca skills. */
object PacaSkillLibrary {
    /** (3.3.0) The full set. */
    val all: List<PacaSkill> = listOf(
        PacaSkill("paca", "Run the paca workflow on the current ask.", "Use the paca MCP tools to plan and execute the user's request."),
        PacaSkill("paca-epic", "Capture a large initiative as a paca epic.", SkillTemplates.EPIC),
        PacaSkill("paca-breakdown", "Break a paca epic into actionable tasks.", SkillTemplates.BREAKDOWN),
        PacaSkill("paca-clarify", "Ask the right clarifying questions before estimating.", SkillTemplates.CLARIFY),
        PacaSkill("paca-sprint", "Form / close a sprint with the paca sprint surface.", SkillTemplates.SPRINT),
        PacaSkill("paca-estimate", "Estimate story points for a set of tasks.", SkillTemplates.ESTIMATE),
        PacaSkill("paca-prioritize", "Reorder the backlog by importance.", SkillTemplates.PRIORITIZE),
        PacaSkill("paca-do", "Pick the next-best task and start it.", SkillTemplates.DO),
        PacaSkill("paca-test", "Generate and run tests for the current change.", SkillTemplates.TEST),
        PacaSkill("paca-doc", "Update the project's living doc to reflect the latest change.", SkillTemplates.DOC),
        PacaSkill("paca-setup", "First-run setup: pick project, configure agents, install plugins.", "Walk the user through paca first-run setup."),
    )

    fun find(name: String): PacaSkill? = all.firstOrNull { it.name.equals(name, ignoreCase = true) }
}

/** (3.3.0) Installer that drops bare skill bodies into ~/.claude/commands/. */
class PacaSkillInstaller(commandsDir: String) {

    private val commandsDir: String

    init {
        require(commandsDir.isNotBlank()) { "commandsDir required" }
        this.commandsDir = commandsDir
    }

    /** (3.3.0) Install all built-in skills. */
    fun installAll(): List<String> = installEach(PacaSkillLibrary.all)

    /** (3.3.0) Install a custom set of skills. */
    fun installEach(skills: Iterable<PacaSkill>): List<String> {
        File(commandsDir).mkdirs()
        val installed = ArrayList<String>()
        for (skill in skills) {
            val file = File(commandsDir, "${skill.name}.md")
            val body = stripFrontmatter(skill.toMarkdown())
            file.writeText(body, Charsets.UTF_8)
            installed.add(file.path)
        }
        return installed
    }

    /** (3.3.0) Uninstall a set of skills by name. */
    fun uninstallByName(names: Iterable<String>): Int {
        var count = 0
        for (name in names) {
            val file = File(commandsDir, "$name.md")
            if (file.exists()) {
                file.delete()
                count++
            }
        }
        return count
    }

    companion object {
        // Matches a leading YAML frontmatter block (--- ... ---) at file start.
        private val FRONTMATTER_PATTERN = Regex("^\\s*---.*?---\\s*\\n", setOf(RegexOption.DOT_MATCHES_ALL))

        /** (3.3.0) Strip the frontmatter block from a markdown skill file. */
        fun stripFrontmatter(markdown: String): String {
            if (markdown.isEmpty()) return ""
            val match = FRONTMATTER_PATTERN.find(markdown)
            if (match == null || match.range.first != 0) return markdown.trimStart()
            return markdown.substring(match.range.last + 1).trimStart()
        }
    }
}
