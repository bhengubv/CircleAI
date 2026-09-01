// CodeAgentLoop.kt
//
// The read-edit-run loop, and the process runner underneath it.
//
// EVERY COMMAND IS ALLOW-LISTED. A coding agent that can run arbitrary shell is
// a remote-code-execution hole with a friendly interface, and the allow-list is
// the difference. It is checked on the RESOLVED executable name, not the string
// the model produced, so "git" and "/usr/bin/git" are the same decision and
// "git; rm -rf /" is not a command at all.
//
// Ported from src/CircleAI.CodeAgent/{CodeAgentLoop, CommandRunner}.cs.

package com.bhengubv.circleai.codeagent

import java.io.File
import java.util.concurrent.TimeUnit

data class CommandResult(
    val exitCode: Int,
    val stdout: String,
    val stderr: String,
    val timedOut: Boolean = false
) {
    val succeeded: Boolean get() = exitCode == 0 && !timedOut
}

/**
 * Runs a command in a workspace.
 *
 * The TIMEOUT is not optional. A build that hangs holds the agent forever, and
 * an agent that never returns looks identical to one that is thinking hard.
 */
class ProcessCommandRunner(
    private val workspaceRoot: String,
    /** What may be run. Empty means NOTHING — fail closed, so a host that
     *  forgets to configure this gets a refusal rather than a shell. */
    private val allowed: Set<String> = emptySet(),
    private val timeoutSeconds: Long = 120,
    /** Output is truncated, because a 200 MB build log fed back into a model is
     *  both useless and expensive. */
    private val maxOutputChars: Int = 64_000
) {
    fun run(command: List<String>, cwd: String? = null): CommandResult {
        if (command.isEmpty()) {
            return CommandResult(-1, "", "No command was given.")
        }

        // Checked on the RESOLVED name so "/usr/bin/git" and "git" are the same
        // decision, and a path cannot smuggle something past a name check.
        val executable = File(command.first()).name
        if (executable !in allowed) {
            return CommandResult(
                -1, "",
                "'$executable' is not on the allow-list. Add it deliberately if it belongs there."
            )
        }

        // The working directory must stay INSIDE the workspace. A relative "../"
        // is how an agent edits the host's own source tree.
        val dir = File(cwd ?: workspaceRoot).canonicalFile
        val root = File(workspaceRoot).canonicalFile
        if (!dir.path.startsWith(root.path)) {
            return CommandResult(-1, "", "That directory is outside the workspace.")
        }

        return try {
            val process = ProcessBuilder(command).directory(dir).start()
            val finished = process.waitFor(timeoutSeconds, TimeUnit.SECONDS)
            if (!finished) {
                process.destroyForcibly()
                return CommandResult(-1, "", "Timed out after ${timeoutSeconds}s.", timedOut = true)
            }
            CommandResult(
                process.exitValue(),
                process.inputStream.bufferedReader().readText().take(maxOutputChars),
                process.errorStream.bufferedReader().readText().take(maxOutputChars)
            )
        } catch (t: Throwable) {
            CommandResult(-1, "", "Could not run '$executable': ${t.message}")
        }
    }
}

data class CodeAgentStep(
    val thought: String,
    val command: List<String>?,
    val result: CommandResult?
)

data class CodeAgentRunResult(
    val steps: List<CodeAgentStep>,
    val finished: Boolean,
    /** Why it stopped. "reached the step limit" and "the task is done" are
     *  completely different outcomes and a caller must be able to tell. */
    val reason: String
)

interface ICodeAgent {
    suspend fun run(task: String, workspaceRoot: String): CodeAgentRunResult
}

/**
 * Think, run one command, look at the result, repeat.
 *
 * BOUNDED BY STEPS, always. An agent that loops until it decides it is done will
 * sometimes never decide, and on a metered connection or a phone battery that is
 * not a theoretical cost.
 */
class CodeAgentLoop(
    private val think: suspend (task: String, history: List<CodeAgentStep>) -> Pair<String, List<String>?>,
    private val runner: ProcessCommandRunner,
    private val maxSteps: Int = 12
) : ICodeAgent {

    override suspend fun run(task: String, workspaceRoot: String): CodeAgentRunResult {
        val steps = ArrayList<CodeAgentStep>()

        repeat(maxSteps) {
            val (thought, command) = think(task, steps)

            // No command means the agent is finished. Recorded as a step anyway,
            // so the transcript shows WHY it stopped rather than just ending.
            if (command == null) {
                steps.add(CodeAgentStep(thought, null, null))
                return CodeAgentRunResult(steps, finished = true, reason = "the agent stopped")
            }

            val result = runner.run(command, workspaceRoot)
            steps.add(CodeAgentStep(thought, command, result))
        }

        return CodeAgentRunResult(
            steps, finished = false,
            reason = "reached the step limit ($maxSteps) without finishing"
        )
    }
}
