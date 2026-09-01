package com.bhengubv.circleai.desktop

import java.time.Instant
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** The desktop board and the context the enricher attaches. */
class DesktopTest {

    private fun win(id: String, process: String = "code", foreground: Boolean = false) =
        WindowDescriptor(id, "t", process, 0, 0, 800, 600, foreground)

    @Test fun `a tracked window comes back by id`() {
        val b = InMemoryDesktopBoard()
        b.track(win("w1"))
        assertEquals("w1", b.window("w1")!!.windowId)
        assertNull(b.window("nope"))
    }

    @Test fun `tracking the same window twice replaces it`() {
        val b = InMemoryDesktopBoard()
        b.track(win("w1", foreground = false))
        b.track(win("w1", foreground = true))
        assertEquals(1, b.windowsOfProcess("code").size)
        assertTrue(b.window("w1")!!.isForeground)
    }

    // The same program is reported as Code, code and CODE by different shells.
    @Test fun `process lookup ignores case`() {
        val b = InMemoryDesktopBoard()
        b.track(win("w1", "Code"))
        b.track(win("w2", "code"))
        b.track(win("w3", "firefox"))
        assertEquals(2, b.windowsOfProcess("CODE").size)
        assertEquals(1, b.windowsOfProcess("firefox").size)
        assertTrue(b.windowsOfProcess("safari").isEmpty())
    }

    @Test fun `a shortcut resolves to its action`() {
        val b = InMemoryDesktopBoard()
        b.registerShortcut(DesktopShortcut("s1", "Ctrl+Shift+P", "command-palette"))
        assertEquals("command-palette", b.actionForKeyChord("Ctrl+Shift+P"))
    }

    // Nobody types Ctrl and ctrl meaning two different shortcuts.
    @Test fun `chord lookup ignores case`() {
        val b = InMemoryDesktopBoard()
        b.registerShortcut(DesktopShortcut("s1", "Ctrl+K", "clear"))
        assertEquals("clear", b.actionForKeyChord("ctrl+k"))
        assertEquals("clear", b.actionForKeyChord("CTRL+K"))
    }

    @Test fun `an unknown chord has no action`() {
        assertNull(InMemoryDesktopBoard().actionForKeyChord("Ctrl+Q"))
    }

    @Test fun `a blank chord is refused rather than matching nothing`() {
        assertFailsWith<IllegalArgumentException> { InMemoryDesktopBoard().actionForKeyChord("  ") }
    }

    @Test fun `registering the same chord twice replaces the action`() {
        val b = InMemoryDesktopBoard()
        b.registerShortcut(DesktopShortcut("s1", "Ctrl+K", "old"))
        b.registerShortcut(DesktopShortcut("s2", "ctrl+k", "new"))
        assertEquals("new", b.actionForKeyChord("Ctrl+K"))
    }

    @Test fun `a session comes back by id`() {
        val b = InMemoryDesktopBoard()
        b.openSession(DesktopSession("s1", "nandi", Instant.EPOCH, listOf("main", "side")))
        assertEquals("nandi", b.session("s1")!!.userName)
        assertEquals(listOf("main", "side"), b.session("s1")!!.activeWorkspaces)
        assertNull(b.session("s2"))
    }

    @Test fun `with no context the message is unchanged`() {
        assertEquals("what is this error", DesktopContextEnricher().enrich("what is this error"))
    }

    @Test fun `the active application is attached`() {
        val e = DesktopContextEnricher()
        e.activeApplication = "Visual Studio Code"
        assertEquals(
            "what is this error\n[Desktop context] Active app: Visual Studio Code",
            e.enrich("what is this error"),
        )
    }

    // Somebody who just copied a password should not have all of it posted
    // into a prompt because they then asked an unrelated question.
    @Test fun `a long clipboard is clamped`() {
        val e = DesktopContextEnricher()
        e.clipboardContent = "s".repeat(5000)
        val seen = e.enrich("hello")
        assertTrue(seen.contains("[Clipboard] "))
        assertEquals(
            DesktopContextEnricher.CLIPBOARD_EXCERPT_LIMIT,
            seen.substringAfter("[Clipboard] ").length,
        )
    }

    @Test fun `a short clipboard is attached whole`() {
        val e = DesktopContextEnricher()
        e.clipboardContent = "SELECT 1"
        assertTrue(e.enrich("explain").endsWith("[Clipboard] SELECT 1"))
    }

    @Test fun `blank context is not attached`() {
        val e = DesktopContextEnricher()
        e.activeApplication = "   "
        e.clipboardContent = ""
        assertEquals("hello", e.enrich("hello"))
    }

    @Test fun `both pieces of context are attached in order`() {
        val e = DesktopContextEnricher()
        e.activeApplication = "Terminal"
        e.clipboardContent = "rm -rf"
        assertEquals(
            "is this safe\n[Desktop context] Active app: Terminal\n[Clipboard] rm -rf",
            e.enrich("is this safe"),
        )
    }

    @Test fun `the helper prompts carry their own full instruction`() {
        val e = DesktopContextEnricher()
        assertTrue(e.diagnoseSlowdownPrompt("fans loud", "16GB M1").contains("fans loud on 16GB M1"))
        assertTrue(e.shortcutCheatsheetPrompt("Blender", "beginner").contains("Blender, beginner user"))
        assertTrue(e.workspaceLayoutPrompt("3", "editing").contains("3-monitor workspace layout"))
        assertTrue(e.automateTaskPrompt("renaming files", "Python").contains("using Python"))
    }
}
