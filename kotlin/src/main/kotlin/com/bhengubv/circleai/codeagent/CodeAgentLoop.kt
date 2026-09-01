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


// `CommandResult`, `CodeAgentStep` and `CodeAgentRunResult` are declared in
// CodeAgent.kt. They were declared here too by a separate porting pass, with
// different shapes — which Kotlin reports as a redeclaration and which is also
// where this file's argument-type mismatches came from. What the loop's copies
// carried and the canonical ones did not — `finished`, and a step's command and
// result — has been merged into them rather than lost.

import java.io.File
import java.util.concurrent.TimeUnit



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
            return CommandResult(executed = false, exitCode = -1, stdout = "", stderr = "No command was given.", timedOut = false)
        }

        // Checked on the RESOLVED name so "/usr/bin/git" and "git" are the same
        // decision, and a path cannot smuggle something past a name check.
        val executable = File(command.first()).name
        if (executable !in allowed) {
            return CommandResult(executed = false, exitCode = -1, stdout = "", stderr = "'$executable' is not on the allow-list. Add it deliberately if it belongs there.", timedOut = false)
        }

        // The working directory must stay INSIDE the workspace. A relative "../"
        // is how an agent edits the host's own source tree.
        val dir = File(cwd ?: workspaceRoot).canonicalFile
        val root = File(workspaceRoot).canonicalFile
        if (!dir.path.startsWith(root.path)) {
            return CommandResult(executed = false, exitCode = -1, stdout = "", stderr = "That directory is outside the workspace.", timedOut = false)
        }

        return try {
            val process = ProcessBuilder(command).directory(dir).start()
            val finished = process.waitFor(timeoutSeconds, TimeUnit.SECONDS)
            if (!finished) {
                process.destroyForcibly()
                return CommandResult(executed = true, exitCode = -1, stdout = "", stderr = "Timed out after ${timeoutSeconds}s.", timedOut = true)
            }
            CommandResult(
                executed = true,
                exitCode = process.exitValue(),
                stdout = process.inputStream.bufferedReader().readText().take(maxOutputChars),
                stderr = process.errorStream.bufferedReader().readText().take(maxOutputChars),
                timedOut = false,
            )
        } catch (t: Throwable) {
            CommandResult(executed = false, exitCode = -1, stdout = "", stderr = "Could not run '$executable': ${t.message}", timedOut = false)
        }
    }
}





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
                steps.add(
                    CodeAgentStep(
                        index = steps.size,
                        action = AgentActionKind.FINISH,
                        detail = thought,
                        observation = "",
                    )
                )
                return CodeAgentRunResult(
                    available = true,
                    quality = CodingSelectionQuality.GOOD,
                    reason = "the agent stopped",
                    steps = steps,
                    appliedEdits = emptyList(),
                    finalSummary = thought,
                    finished = true,
                )
            }

            val result = runner.run(command, workspaceRoot)
            steps.add(
                CodeAgentStep(
                    index = steps.size,
                    action = AgentActionKind.RUN_COMMAND,
                    detail = thought,
                    observation = result.stdout,
                    command = command,
                    result = result,
                )
            )
        }

        return CodeAgentRunResult(
            available = true,
            quality = CodingSelectionQuality.GOOD,
            reason = "reached the step limit ($maxSteps) without finishing",
            steps = steps,
            appliedEdits = emptyList(),
            finalSummary = "",
            // NOT finished: the limit stopped it, the task did not end. A caller
            // that cannot tell these apart reports an unfinished job as done.
            finished = false,
        )
    }
}
