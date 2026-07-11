// Games.kt
//
// Kotlin port of CircleAI.Games (Contracts.cs + InMemoryGames.cs +
// NullImplementations.cs) — the C# reference is the EXACT spec. Game-runtime
// contracts (loop, input map, scene graph) plus a real timer-driven loop, an
// in-memory input map and scene graph, and null implementations.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `TimeSpan` -> `java.time.Duration`.
//   * C# `IAsyncDisposable` -> `AutoCloseable` + suspend `disposeAsync()` with a
//     `close()` bridge (the codebase convention, cf. realtime/Realtime.kt).
//   * C# `Func<GameTick, ValueTask>` handlers -> `fun interface`s with a suspend
//     `invoke`. Subscribers are snapshotted under the lock and invoked OUTSIDE it
//     (fan-out), matching the C# `snap = _subs.ToArray()` pattern — a handler that
//     unsubscribes from within its own callback cannot deadlock.
//   * `TimerGameLoop` uses a `java.util.Timer` at `max(1, 1000/targetFps)` ms,
//     increments the frame counter atomically, and fans ticks out to a launched
//     coroutine per subscriber (mirrors C#'s fire-and-forget `_ = s(tick)`).
//     Subscriber exceptions are swallowed (logged to stderr), never crashing the
//     loop. `StartAsync` twice throws; `targetFps <= 0` throws.
//   * `Subscribe` returns an [AutoCloseable] token that removes the handler.

package com.bhengubv.circleai.games

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import java.time.Duration
import java.time.Instant
import java.util.Timer
import java.util.TimerTask
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/** One loop tick. Mirrors C# `GameTick`. */
data class GameTick(val frame: Int, val elapsed: Duration)

/** One input event. Mirrors C# `InputEvent`. */
data class InputEvent(val action: String, val payload: Map<String, String>? = null)

/** One scene node. Mirrors C# `SceneNode`. */
data class SceneNode(val nodeId: String, val kind: String, val x: Double, val y: Double, val z: Double)

/** A tick handler. Mirrors C# `Func<GameTick, ValueTask>`. */
fun interface GameTickHandler {
    suspend fun invoke(tick: GameTick)
}

/** An input handler. Mirrors C# `Func<InputEvent, ValueTask>`. */
fun interface InputHandler {
    suspend fun invoke(ev: InputEvent)
}

/**
 * A driven game loop. Mirrors C# `IGameLoop` (which is `IAsyncDisposable`).
 */
interface IGameLoop : AutoCloseable {
    /** Backend self-id. */
    val backendId: String

    /** Start ticking at the target FPS. */
    suspend fun startAsync(targetFps: Double = 60.0)

    /** Stop ticking. */
    suspend fun stopAsync()

    /** Subscribe a tick handler; dispose the returned token to unsubscribe. */
    fun subscribe(handler: GameTickHandler): AutoCloseable

    /** Release the loop. Mirrors C# `IAsyncDisposable.DisposeAsync`. */
    suspend fun disposeAsync()

    /** [AutoCloseable] bridge — runs [disposeAsync] synchronously. */
    override fun close() {
        runBlocking { disposeAsync() }
    }
}

/** An input map. Mirrors C# `IInputMap`. */
interface IInputMap {
    /** Backend self-id. */
    val backendId: String

    /** Subscribe an input handler; dispose the returned token to unsubscribe. */
    fun subscribe(handler: InputHandler): AutoCloseable
}

/** A scene graph. Mirrors C# `ISceneGraph`. */
interface ISceneGraph {
    /** Backend self-id. */
    val backendId: String

    /** Add or replace a node (blank NodeId throws). */
    suspend fun addAsync(node: SceneNode)

    /** Remove a node (blank nodeId throws). */
    suspend fun removeAsync(nodeId: String)

    /** Snapshot all nodes. */
    suspend fun snapshotAsync(): List<SceneNode>
}

// =====================================================================
// InMemoryGames (InMemoryGames.cs)
// =====================================================================

/**
 * Timer-driven [IGameLoop]. Fans ticks out to subscribers on a background
 * scope. Mirrors C# `TimerGameLoop`.
 */
class TimerGameLoop : IGameLoop {
    private val subs = mutableListOf<GameTickHandler>()
    private val lock = Any()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private var timer: Timer? = null
    private val frame = AtomicInteger(0)
    private var start: Instant = Instant.now()

    override val backendId: String get() = "timer"

    override suspend fun startAsync(targetFps: Double) {
        if (targetFps <= 0) throw IllegalArgumentException("targetFps")
        if (timer != null) throw IllegalStateException("already started")
        val ms = maxOf(1L, (1000.0 / targetFps).toLong())
        start = Instant.now()
        val t = Timer("games-loop", true)
        t.scheduleAtFixedRate(object : TimerTask() {
            override fun run() { onTick() }
        }, ms, ms)
        timer = t
    }

    override suspend fun stopAsync() {
        timer?.cancel()
        timer = null
    }

    override fun subscribe(handler: GameTickHandler): AutoCloseable {
        synchronized(lock) { subs.add(handler) }
        return Token(this, handler)
    }

    override suspend fun disposeAsync() {
        stopAsync()
        scope.cancel()
    }

    private fun onTick() {
        val f = frame.incrementAndGet()
        val tick = GameTick(f, Duration.between(start, Instant.now()))
        val snap = synchronized(lock) { subs.toList() }
        for (s in snap) {
            // Fire-and-forget per subscriber (mirrors C# `_ = s(tick)`); a throwing
            // subscriber is isolated and logged, never crashing the loop.
            scope.launch {
                try {
                    s.invoke(tick)
                } catch (ex: Exception) {
                    System.err.println("[CircleAI.Games] tick subscriber threw: ${ex.message}")
                }
            }
        }
    }

    private class Token(private val owner: TimerGameLoop, private val handler: GameTickHandler) : AutoCloseable {
        override fun close() {
            synchronized(owner.lock) { owner.subs.remove(handler) }
        }
    }
}

/** In-memory [IInputMap] with a `raise` entry point. Mirrors C# `InMemoryInputMap`. */
class InMemoryInputMap : IInputMap {
    private val subs = mutableListOf<InputHandler>()
    private val lock = Any()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    override val backendId: String get() = "in-memory"

    /** Raise an input event to all subscribers. Mirrors C# `Raise`. */
    fun raise(ev: InputEvent) {
        val snap = synchronized(lock) { subs.toList() }
        for (s in snap) {
            scope.launch {
                try {
                    s.invoke(ev)
                } catch (ex: Exception) {
                    System.err.println("[CircleAI.Games] input subscriber threw: ${ex.message}")
                }
            }
        }
    }

    override fun subscribe(handler: InputHandler): AutoCloseable {
        synchronized(lock) { subs.add(handler) }
        return Token(this, handler)
    }

    private class Token(private val owner: InMemoryInputMap, private val handler: InputHandler) : AutoCloseable {
        override fun close() {
            synchronized(owner.lock) { owner.subs.remove(handler) }
        }
    }
}

/** In-memory [ISceneGraph] backed by a concurrent map. Mirrors C# `InMemorySceneGraph`. */
class InMemorySceneGraph : ISceneGraph {
    private val nodes = ConcurrentHashMap<String, SceneNode>()

    override val backendId: String get() = "in-memory"

    override suspend fun addAsync(node: SceneNode) {
        if (node.nodeId.isBlank()) throw IllegalArgumentException("NodeId required")
        nodes[node.nodeId] = node
    }

    override suspend fun removeAsync(nodeId: String) {
        if (nodeId.isBlank()) throw IllegalArgumentException("nodeId required")
        nodes.remove(nodeId)
    }

    override suspend fun snapshotAsync(): List<SceneNode> = nodes.values.toList()
}

// =====================================================================
// NullImplementations (NullImplementations.cs)
// =====================================================================

/** A no-op [IGameLoop]. Mirrors C# `NullGameLoop`. */
class NullGameLoop : IGameLoop {
    override val backendId: String get() = "null"
    override suspend fun startAsync(targetFps: Double) { /* no-op */ }
    override suspend fun stopAsync() { /* no-op */ }
    override fun subscribe(handler: GameTickHandler): AutoCloseable = EmptyDisposable
    override suspend fun disposeAsync() { /* no-op */ }

    private companion object EmptyDisposable : AutoCloseable {
        override fun close() { /* no-op */ }
    }
}

/** A no-op [IInputMap]. Mirrors C# `NullInputMap`. */
class NullInputMap : IInputMap {
    override val backendId: String get() = "null"
    override fun subscribe(handler: InputHandler): AutoCloseable = EmptyDisposable

    companion object {
        /** Shared instance. Mirrors C# `NullInputMap.Instance`. */
        val Instance: NullInputMap = NullInputMap()
        private val EmptyDisposable = AutoCloseable { }
    }
}

/** A no-op [ISceneGraph]. Mirrors C# `NullSceneGraph`. */
class NullSceneGraph : ISceneGraph {
    override val backendId: String get() = "null"
    override suspend fun addAsync(node: SceneNode) { /* no-op */ }
    override suspend fun removeAsync(nodeId: String) { /* no-op */ }
    override suspend fun snapshotAsync(): List<SceneNode> = emptyList()

    companion object {
        /** Shared instance. Mirrors C# `NullSceneGraph.Instance`. */
        val Instance: NullSceneGraph = NullSceneGraph()
    }
}
