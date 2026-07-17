// IChatRuntime.cs
//
// Host-neutral chat surface pulled down from circle-concierge so the Neuron
// (and any harness that rides it) can drive the on-device engine without
// leaking CircleAI.Inference types into UI/host code. Zero CircleAI
// dependencies — BCL types only — so it drops in cleanly.
//
// CircleAI already owns the warm-loader (IAIService.StartAsync / PrewarmAsync,
// BackgroundInferenceWorker) and the KV session snapshot
// (IChatGenerator.SaveSessionAsync / LoadSessionAsync). Only this thin
// interface travels down; NeuronNode implements it over IAIService.

namespace CircleAI.Hosting.Chat;

/// <summary>
/// Host-neutral chat surface that a UI or harness calls. Implementations wrap
/// an engine — <see cref="Neuron.NeuronNode"/> wraps the CircleAI on-device
/// Neuron — so callers never see which model answered.
/// </summary>
public interface IChatRuntime
{
    /// <summary>
    /// Short stable identifier used by callers that want to route to a specific
    /// runtime (e.g. <c>"circleai-neuron"</c>). Must be unique across every
    /// registered runtime in a host.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Display label for the active engine (e.g. <c>"Qwen3-30B-A3B-Q4 (CircleAI)"</c>).
    /// Reflects the model the runtime resolved. Persisted alongside assistant
    /// messages so the UI can label past turns even after the runtime swaps out.
    /// </summary>
    string EngineLabel { get; }

    /// <summary>
    /// <c>true</c> once the runtime has finished loading. While <c>false</c>, the
    /// UI keeps the composer disabled and shows whatever <see cref="StatusMessage"/>
    /// says.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Human-readable status line — "loading model…", "engine offline: file not
    /// found", "ready", etc. Surfaced verbatim in the UI status pill, so avoid
    /// jargon.
    /// </summary>
    string StatusMessage { get; }

    /// <summary>
    /// Streams the assistant reply chunk-by-chunk. Each yielded string is the
    /// next fragment to append. Callers concatenate in order and re-render
    /// between yields.
    /// </summary>
    IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatTurn> messages,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Host-neutral chat turn. Mirrors <c>CircleAI.Inference.ChatMessage</c> so the
/// adapter can translate without leaking the upstream type into UI code.
/// </summary>
/// <param name="Role">"system" / "user" / "assistant".</param>
/// <param name="Content">Text content.</param>
public sealed record ChatTurn(string Role, string Content);

/// <summary>
/// Optional capability for chat runtimes whose backend supports snapshotting
/// the in-memory conversation state (KV cache + history) to disk. A MAUI host
/// calls <see cref="SaveSessionAsync"/> in its <c>OnSleep</c> hook so the active
/// conversation survives an Android OOM kill, and <see cref="LoadSessionAsync"/>
/// in <c>OnResume</c> to skip the prefill cost on wake-up.
/// </summary>
/// <remarks>
/// The host detects support via a pattern-match on the active
/// <see cref="IChatRuntime"/>; runtimes that don't implement
/// <see cref="IPersistableChatRuntime"/> are skipped.
/// </remarks>
public interface IPersistableChatRuntime
{
    /// <summary>
    /// Default snapshot path the host should pass to <see cref="SaveSessionAsync"/>
    /// / <see cref="LoadSessionAsync"/> when it has no opinion of its own. The
    /// implementation owns this so the host doesn't need to know per-adapter
    /// folder conventions. Null when snapshotting is disabled by configuration.
    /// </summary>
    string? SessionSnapshotPath { get; }

    /// <summary>
    /// Serialise the runtime's in-memory session (KV cache + token history) to
    /// <paramref name="path"/>. Returns <c>true</c> on success. Implementations
    /// must be safe to call from a lifecycle thread and should never throw —
    /// surface the failure as <c>false</c> instead.
    /// </summary>
    Task<bool> SaveSessionAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hydrate the runtime from a previously-saved snapshot. Returns <c>true</c>
    /// on success. Same no-throw contract as <see cref="SaveSessionAsync"/> — a
    /// corrupt or missing snapshot falls back to a cold session.
    /// </summary>
    Task<bool> LoadSessionAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// Null implementation used until an adapter is wired. Surfaces an honest
/// "engine offline" message instead of pretending to stream — behaviour stays
/// consistent either way.
/// </summary>
public sealed class NullChatRuntime : IChatRuntime
{
    public string Id => "null";

    public string EngineLabel => "No engine wired";

    public bool IsReady => false;

    public string StatusMessage => "No chat engine is wired. Add a NeuronNode (or another IChatRuntime adapter) to enable conversations.";

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatTurn> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield return StatusMessage;
    }
}
