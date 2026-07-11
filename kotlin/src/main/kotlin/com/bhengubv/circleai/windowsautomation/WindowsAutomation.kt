// WindowsAutomation.kt
//
// Kotlin port of CircleAI.WindowsAutomation — the C# reference is the EXACT
// spec. Portable UI-automation contract plus a real-but-virtual in-memory
// driver and a null driver. The platform-specific Win32-UIA backend is NOT
// portable and is intentionally excluded; hosts inject a native
// [IUiAutomationDriver] on Windows. This module ports the contract + logic only.
//
// Covers (C# file -> Kotlin type):
//   Contracts.cs                  -> UiElement, IUiAutomationDriver
//   InMemoryWindowsAutomation.cs  -> UiAutomationEvent, InMemoryUiAutomationDriver
//   NullImplementations.cs        -> NullUiAutomationDriver
//   WindowsAutomationHelpers.cs   -> UiElementHelpers (containsPoint/hitTest/dump)
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `ValueTask` / `ValueTask<T>` -> `suspend fun` / `suspend fun (...) : T`.
//   * C# `Action<UiAutomationEvent>` observer -> `(UiAutomationEvent) -> Unit`.
//   * C# `ConcurrentDictionary` (Ordinal) -> `ConcurrentHashMap`; the observer
//     list lives behind a lock, mirroring the C# `List` + `lock`.
//   * C# extension methods (`this UiElement`) -> Kotlin extension functions.
//   * Argument validation mirrors the C# throws:
//       - blank elementId/keyName -> IllegalArgumentException
//       - unknown element on click -> IllegalStateException

package com.bhengubv.circleai.windowsautomation

import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/** A single element in a UI-automation snapshot. */
data class UiElement(
    val elementId: String,
    val name: String,
    val kind: String,
    val x: Int,
    val y: Int,
    val width: Int,
    val height: Int,
)

/**
 * A UI-automation backend. Hosts snap a real Win32-UIA implementation in for
 * production; portable code drives it through this contract.
 */
interface IUiAutomationDriver {
    val backendId: String
    suspend fun snapshot(): List<UiElement>
    suspend fun click(elementId: String)
    suspend fun type(text: String)
    suspend fun key(keyName: String)
}

// =====================================================================
// InMemoryWindowsAutomation (InMemoryWindowsAutomation.cs)
// =====================================================================

/** An event raised by [InMemoryUiAutomationDriver] on click / type / key. */
data class UiAutomationEvent(val kind: String, val elementId: String?, val payload: String?)

/**
 * Real-but-virtual UIA driver. Lets tests drive a virtual UI without touching
 * the desktop. Click + Type + Key raise events the host can observe.
 */
class InMemoryUiAutomationDriver : IUiAutomationDriver {
    private val elements = ConcurrentHashMap<String, UiElement>()
    private val observers = ArrayList<(UiAutomationEvent) -> Unit>()
    private val lock = Any()

    override val backendId: String get() = "in-memory"

    fun register(el: UiElement) {
        elements[el.elementId] = el
    }

    fun observe(obs: (UiAutomationEvent) -> Unit) {
        synchronized(lock) { observers.add(obs) }
    }

    override suspend fun snapshot(): List<UiElement> = elements.values.toList()

    override suspend fun click(elementId: String) {
        require(elementId.isNotBlank()) { "elementId required" }
        check(elements.containsKey(elementId)) { "Unknown element '$elementId'." }
        notify(UiAutomationEvent("click", elementId, null))
    }

    override suspend fun type(text: String) {
        notify(UiAutomationEvent("type", null, text))
    }

    override suspend fun key(keyName: String) {
        require(keyName.isNotBlank()) { "keyName required" }
        notify(UiAutomationEvent("key", null, keyName))
    }

    private fun notify(ev: UiAutomationEvent) {
        val snap: List<(UiAutomationEvent) -> Unit> = synchronized(lock) { observers.toList() }
        for (o in snap) {
            try {
                o(ev)
            } catch (ex: Exception) {
                // Mirrors the C# Debug.WriteLine: an observer must not break the driver.
                System.err.println("[CircleAI.WindowsAutomation] UI observer threw: ${ex.message}")
            }
        }
    }
}

// =====================================================================
// NullImplementations (NullImplementations.cs)
// =====================================================================

/** No-op [IUiAutomationDriver] — snapshots empty, all actions succeed silently. */
class NullUiAutomationDriver private constructor() : IUiAutomationDriver {
    override val backendId: String get() = "null"
    override suspend fun snapshot(): List<UiElement> = emptyList()
    override suspend fun click(elementId: String) {}
    override suspend fun type(text: String) {}
    override suspend fun key(keyName: String) {}

    companion object {
        val Instance = NullUiAutomationDriver()
    }
}

// =====================================================================
// WindowsAutomationHelpers (WindowsAutomationHelpers.cs)
// =====================================================================

/** Returns true when the point ([x], [y]) falls inside this element's bounds. */
fun UiElement.containsPoint(x: Int, y: Int): Boolean =
    x >= this.x && y >= this.y && x < this.x + width && y < this.y + height

/** Returns every element in [elements] whose bounds contain the point ([x], [y]). */
fun hitTest(elements: Iterable<UiElement>, x: Int, y: Int): List<UiElement> =
    elements.filter { it.containsPoint(x, y) }

/** Formats [elements] as a newline-delimited debug dump. */
fun dump(elements: Iterable<UiElement>): String {
    val sb = StringBuilder()
    for (e in elements) {
        sb.append(e.elementId).append(" \"").append(e.name).append("\" ")
            .append(e.kind).append(" @ (").append(e.x).append(",").append(e.y)
            .append(") ").append(e.width).append('x').append(e.height).append('\n')
    }
    return sb.toString()
}
