package com.bhengubv.circleai.codeagent

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** Parsing whatever the model said. */
class CodeAgentParserTest {

    @Test fun `parses read_file`() {
        val a = AgentActionParser.parse("""{"action":"read_file","path":"src/a.kt"}""")
        assertEquals(AgentActionKind.READ_FILE, a.kind)
        assertEquals("src/a.kt", a.path)
    }

    @Test fun `parses edit_file with a range`() {
        val a = AgentActionParser.parse(
            """{"action":"edit_file","path":"a.txt","range_start":4,"range_end":9,"replacement":"hi"}"""
        )
        assertEquals(AgentActionKind.EDIT_FILE, a.kind)
        assertEquals(4, a.rangeStart)
        assertEquals(9, a.rangeEnd)
        assertEquals("hi", a.replacement)
    }

    @Test fun `parses run_command with args`() {
        val a = AgentActionParser.parse(
            """{"action":"run_command","executable":"gradle","args":["build","--info"],"cwd":"."}"""
        )
        assertEquals(AgentActionKind.RUN_COMMAND, a.kind)
        assertEquals("gradle", a.executable)
        assertEquals(listOf("build", "--info"), a.args)
        assertEquals(".", a.path)
    }

    @Test fun `top_k defaults to ten when absent`() {
        val a = AgentActionParser.parse("""{"action":"search_code","query":"parser"}""")
        assertEquals(AgentActionKind.SEARCH_CODE, a.kind)
        assertEquals(10, a.topK)
    }

    @Test fun `finish carries its summary`() {
        val a = AgentActionParser.parse("""{"action":"finish","summary":"renamed the thing"}""")
        assertEquals(AgentActionKind.FINISH, a.kind)
        assertEquals("renamed the thing", a.summary)
    }

    // A model that wraps its JSON in prose and a code fence is the NORMAL case.
    @Test fun `extracts json from surrounding prose`() {
        val reply = """
            Sure! Here is what I will do:

            ```json
            {"action":"read_file","path":"README.md"}
            ```

            Let me know if that works.
        """.trimIndent()
        val a = AgentActionParser.parse(reply)
        assertEquals(AgentActionKind.READ_FILE, a.kind)
        assertEquals("README.md", a.path)
    }

    // A brace inside a quoted replacement must not close the object early.
    @Test fun `braces inside strings do not end the object`() {
        val a = AgentActionParser.parse(
            """{"action":"edit_file","path":"a.c","replacement":"if (x) { y(); }"}"""
        )
        assertEquals(AgentActionKind.EDIT_FILE, a.kind)
        assertEquals("if (x) { y(); }", a.replacement)
    }

    @Test fun `an escaped quote is not a string terminator`() {
        val a = AgentActionParser.parse(
            """{"action":"finish","summary":"said \"done\" and {stopped}"}"""
        )
        assertEquals(AgentActionKind.FINISH, a.kind)
        assertEquals("said \"done\" and {stopped}", a.summary)
    }

    @Test fun `an unknown action keeps the json as raw`() {
        val a = AgentActionParser.parse("""{"action":"launch_missiles"}""")
        assertEquals(AgentActionKind.UNKNOWN, a.kind)
        assertEquals("""{"action":"launch_missiles"}""", a.raw)
    }

    @Test fun `prose with no json is unknown and keeps the text`() {
        val a = AgentActionParser.parse("I think we should refactor this.")
        assertEquals(AgentActionKind.UNKNOWN, a.kind)
        assertEquals("I think we should refactor this.", a.raw)
    }

    @Test fun `truncated json is unknown rather than a crash`() {
        assertEquals(AgentActionKind.UNKNOWN, AgentActionParser.parse("""{"action":"read_file","path":"a.k""").kind)
    }

    @Test fun `an empty reply is unknown`() {
        assertEquals(AgentActionKind.UNKNOWN, AgentActionParser.parse("").kind)
        assertEquals(AgentActionKind.UNKNOWN, AgentActionParser.parse(null).kind)
        assertEquals(AgentActionKind.UNKNOWN, AgentActionParser.parse("   ").kind)
    }

    // A number where a string belongs, and a string where a number belongs,
    // must both fall back rather than coerce.
    @Test fun `wrong types fall back instead of coercing`() {
        val a = AgentActionParser.parse("""{"action":"edit_file","path":42,"range_start":"nine"}""")
        assertEquals(AgentActionKind.EDIT_FILE, a.kind)
        assertNull(a.path)
        assertEquals(0, a.rangeStart)
    }

    @Test fun `a boolean is not a number`() {
        val a = AgentActionParser.parse("""{"action":"search_code","query":"x","top_k":true}""")
        assertEquals(10, a.topK)
    }

    @Test fun `non-string array entries are dropped`() {
        val a = AgentActionParser.parse("""{"action":"run_command","executable":"ls","args":["-l",7,"-a"]}""")
        assertEquals(listOf("-l", "-a"), a.args)
    }

    @Test fun `the action name is case and space insensitive`() {
        assertEquals(AgentActionKind.READ_FILE,
            AgentActionParser.parse("""{"action":"  Read_File ","path":"x"}""").kind)
    }
}
