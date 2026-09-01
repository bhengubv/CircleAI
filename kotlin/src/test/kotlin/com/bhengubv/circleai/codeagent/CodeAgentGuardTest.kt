package com.bhengubv.circleai.codeagent

import kotlinx.coroutines.test.runTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** The path guard, the command refusal and the prompt. */
class CodeAgentGuardTest {

    @Test fun `a relative path resolves inside the workspace`() {
        assertEquals("/work/repo/src/a.kt", CodeAgentPaths.resolve("/work/repo", "src/a.kt"))
    }

    // The one that matters: "edit my repo" must not become "edit /etc".
    @Test fun `dot dot escaping the workspace is refused`() {
        assertNull(CodeAgentPaths.resolve("/work/repo", "../../etc/passwd"))
    }

    @Test fun `an absolute path outside the workspace is refused`() {
        assertNull(CodeAgentPaths.resolve("/work/repo", "/etc/passwd"))
    }

    @Test fun `an absolute path inside the workspace is allowed`() {
        assertEquals("/work/repo/a.kt", CodeAgentPaths.resolve("/work/repo", "/work/repo/a.kt"))
    }

    // A sibling that merely starts with the same characters is not inside it.
    @Test fun `a sibling with a shared prefix is refused`() {
        assertNull(CodeAgentPaths.resolve("/work/repo", "/work/repo-secrets/keys"))
    }

    @Test fun `the workspace root itself is allowed`() {
        assertEquals("/work/repo", CodeAgentPaths.resolve("/work/repo", "/work/repo"))
    }

    @Test fun `a missing path is refused`() {
        assertNull(CodeAgentPaths.resolve("/work/repo", null))
        assertNull(CodeAgentPaths.resolve("/work/repo", "   "))
    }

    // Inner ".." that stays inside is fine - only escaping is refused.
    @Test fun `inner dot dot that stays inside is allowed`() {
        assertEquals("/work/repo/lib/b.kt", CodeAgentPaths.resolve("/work/repo", "src/../lib/b.kt"))
    }

    @Test fun `the disabled runner refuses and says why`() = runTest {
        val r = DisabledCommandRunner.run(CommandRequest("rm", listOf("-rf", "/"), "/"))
        assertFalse(r.executed)
        assertFalse(r.success)
        assertTrue(r.denied!!.contains("disabled"))
    }

    @Test fun `a runner with an empty allow-list refuses to exist`() {
        assertFailsWith<IllegalArgumentException> { AllowListCommandRunner(emptyList()) }
    }

    @Test fun `an executable off the allow-list is not run`() = runTest {
        val runner = AllowListCommandRunner(listOf("echo"), execute = { _ ->
            CommandResult(true, 0, "should not happen", "", false)
        })
        val r = runner.run(CommandRequest("/bin/rm", listOf("-rf", "/tmp/nope"), "/tmp"))
        assertFalse(r.executed)
        assertTrue(r.denied!!.contains("allow-list"))
    }

    @Test fun `an allowed executable runs`() = runTest {
        val runner = AllowListCommandRunner(listOf("echo"), execute = { _ ->
            CommandResult(true, 0, "hello", "", false)
        })
        val r = runner.run(CommandRequest("/bin/echo", listOf("hello"), "/tmp"))
        assertTrue(r.success)
        assertEquals("hello", r.stdout)
    }

    @Test fun `not run carries the reason and is not success`() {
        val r = CommandResult.notRun("because")
        assertFalse(r.executed)
        assertFalse(r.success)
        assertEquals("because", r.denied)
    }

    @Test fun `the prompt hides search when no backend is wired`() {
        val p = CodeAgentPrompt.build("t", "/w", allowCommands = false, hasSearch = false)
        assertFalse(p.contains("search_code"))
        assertFalse(p.contains("run_command"))
        assertTrue(p.contains("read_file"))
        assertTrue(p.contains("Task: t"))
    }

    @Test fun `the prompt offers commands only when allowed`() {
        val p = CodeAgentPrompt.build("t", "/w", allowCommands = true, hasSearch = true)
        assertTrue(p.contains("run_command"))
        assertTrue(p.contains("search_code"))
    }

    @Test fun `truncation says how much it removed`() {
        val t = CodeAgentPrompt.truncate("x".repeat(100), 10)
        assertTrue(t.startsWith("x".repeat(10)))
        assertTrue(t.contains("truncated 90 chars"))
    }

    @Test fun `short text is left alone`() {
        assertEquals("short", CodeAgentPrompt.truncate("short", 100))
        assertEquals("", CodeAgentPrompt.truncate("", 100))
    }

    @Test fun `the null agent declines honestly`() = runTest {
        val r = NullCodeAgent.run("anything", "/w")
        assertFalse(r.available)
        assertEquals(CodingSelectionQuality.UNAVAILABLE, r.quality)
        assertTrue(r.steps.isEmpty())
        assertTrue(r.appliedEdits.isEmpty())
    }
}
