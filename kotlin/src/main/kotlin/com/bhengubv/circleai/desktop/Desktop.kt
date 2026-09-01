// Desktop.kt
//
// Kotlin port of CircleAI.Desktop — the C# reference is the EXACT spec.
//
// The desktop board (windows, shortcuts, sessions) and the context a companion
// adapter folds into every message.
//
// Fidelity notes:
//   * C# `record` -> `data class`.
//   * `ConcurrentDictionary` with OrdinalIgnoreCase for shortcuts -> a map
//     keyed on the LOWERCASED chord; process lookup folds case the same way.
//   * The clipboard excerpt limit is a NAMED constant here rather than an
//     inline 200, because it is a privacy boundary and deserves a name.

package com.bhengubv.circleai.desktop

import java.time.Instant

data class WindowDescriptor(
    val windowId: String,
    val title: String,
    val processName: String,
    val x: Int,
    val y: Int,
    val width: Int,
    val height: Int,
    val isForeground: Boolean,
)

data class DesktopShortcut(val shortcutId: String, val keyChord: String, val action: String)

data class DesktopSession(
    val sessionId: String,
    val userName: String,
    val startedUtc: Instant,
    val activeWorkspaces: List<String>,
)

interface DesktopBoard {
    fun track(window: WindowDescriptor)
    fun window(id: String): WindowDescriptor?
    fun windowsOfProcess(processName: String): List<WindowDescriptor>
    fun registerShortcut(shortcut: DesktopShortcut)
    fun actionForKeyChord(keyChord: String): String?
    fun openSession(session: DesktopSession)
    fun session(id: String): DesktopSession?
}

class InMemoryDesktopBoard : DesktopBoard {
    private val lock = Any()
    private val windows = mutableMapOf<String, WindowDescriptor>()
    /** Keyed on the LOWERCASED chord: nobody types Ctrl and ctrl meaning two different shortcuts. */
    private val shortcuts = mutableMapOf<String, DesktopShortcut>()
    private val sessions = mutableMapOf<String, DesktopSession>()

    override fun track(window: WindowDescriptor) {
        synchronized(lock) { windows[window.windowId] = window }
    }

    override fun window(id: String): WindowDescriptor? = synchronized(lock) { windows[id] }

    /**
     * Process names are matched case-insensitively - the same program is
     * reported as Code, code and CODE by different shells.
     */
    override fun windowsOfProcess(processName: String): List<WindowDescriptor> {
        val needle = processName.lowercase()
        return synchronized(lock) { windows.values.filter { it.processName.lowercase() == needle } }
    }

    override fun registerShortcut(shortcut: DesktopShortcut) {
        synchronized(lock) { shortcuts[shortcut.keyChord.lowercase()] = shortcut }
    }

    override fun actionForKeyChord(keyChord: String): String? {
        require(keyChord.isNotBlank()) { "keyChord required" }
        return synchronized(lock) { shortcuts[keyChord.lowercase()]?.action }
    }

    override fun openSession(session: DesktopSession) {
        synchronized(lock) { sessions[session.sessionId] = session }
    }

    override fun session(id: String): DesktopSession? = synchronized(lock) { sessions[id] }
}

/**
 * Folds desktop context into every message a companion is sent.
 *
 * The clipboard is CLAMPED. Somebody who just copied a password, a private key
 * or half a document should not have all of it posted into a prompt because
 * they then asked an unrelated question.
 */
class DesktopContextEnricher {
    companion object {
        /** The longest clipboard excerpt that will ever be attached. */
        const val CLIPBOARD_EXCERPT_LIMIT = 200
    }

    @Volatile var activeApplication: String? = null
    @Volatile var clipboardContent: String? = null

    /** Appends whatever context is set, and nothing when none is. */
    fun enrich(message: String): String {
        var out = message
        activeApplication?.takeIf { it.isNotBlank() }?.let {
            out += "\n[Desktop context] Active app: " + it
        }
        clipboardContent?.takeIf { it.isNotBlank() }?.let {
            out += "\n[Clipboard] " + it.take(CLIPBOARD_EXCERPT_LIMIT)
        }
        return out.trimEnd()
    }

    // ── Desktop helpers ─────────────────────────────────────────────────────
    //
    // These carry their OWN full instruction and go straight to the agent
    // unenriched; desktop context would only dilute them.

    fun diagnoseSlowdownPrompt(symptoms: String, systemSpecs: String) =
        "Diagnose desktop slowdown: $symptoms on $systemSpecs. " +
            "Top 5 suspect causes + how to verify each in 60 seconds."

    fun shortcutCheatsheetPrompt(appName: String, proficiencyLevel: String) =
        "Write a one-page keyboard shortcut cheatsheet for $appName, " +
            "$proficiencyLevel user. Group by action category."

    fun automateTaskPrompt(taskDescription: String, preferredTool: String) =
        "Suggest automation for: $taskDescription using $preferredTool. Step-by-step + edge cases."

    fun workspaceLayoutPrompt(monitorCount: String, primaryWorkflow: String) =
        "Design a $monitorCount-monitor workspace layout for: $primaryWorkflow. " +
            "Apps per screen, hotkey conventions, eye-line ergonomics."
}
