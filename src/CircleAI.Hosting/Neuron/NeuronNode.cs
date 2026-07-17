// NeuronNode.cs
//
// The Neuron facade: a host-neutral IChatRuntime over the CircleAI on-device
// brain (IAIService). Streaming rides the brain's full enrichment pipeline —
// persona + memory + RAG + concierge routing + two-slot residency — so a host
// or UI drives the whole Neuron without ever touching CircleAI.Inference types.
// This is the fix for the pattern circle-concierge got wrong: it bypassed the
// brain and talked to a raw generator, losing memory/persona/routing. NeuronNode
// goes THROUGH the brain, and exposes it (Brain) so a CompanionSession can sit on
// top unchanged.

using CircleAI.Hosting.Chat;
using CircleAI.Inference;

namespace CircleAI.Hosting.Neuron;

/// <summary>
/// Host-neutral <see cref="IChatRuntime"/> over the on-device Neuron brain. Also
/// <see cref="IPersistableChatRuntime"/> — it snapshots the always-warm
/// generalist floor so a conversation survives an OOM kill / restart.
/// </summary>
public sealed class NeuronNode : IChatRuntime, IPersistableChatRuntime
{
    private readonly IAIService _brain;
    private readonly string _id;

    /// <param name="brain">The on-device brain (an <see cref="AIService"/> in practice).</param>
    /// <param name="id">Stable runtime id for host routing. Defaults to <c>"circleai-neuron"</c>.</param>
    /// <param name="sessionSnapshotPath">
    /// Snapshot path for <see cref="IPersistableChatRuntime"/>. Defaults to
    /// <c>{LocalAppData}/CircleAI/sessions/active.session</c>.
    /// </param>
    public NeuronNode(IAIService brain, string id = "circleai-neuron", string? sessionSnapshotPath = null)
    {
        _brain = brain ?? throw new ArgumentNullException(nameof(brain));
        _id = string.IsNullOrWhiteSpace(id) ? "circleai-neuron" : id;
        SessionSnapshotPath = sessionSnapshotPath ?? DefaultSnapshotPath();
    }

    /// <summary>
    /// The on-device brain the Neuron rides. A <c>CompanionSession</c> consumes
    /// this unchanged, gaining identity / fused-memory / persona / proactive.
    /// </summary>
    public IAIService Brain => _brain;

    /// <inheritdoc />
    public string Id => _id;

    /// <inheritdoc />
    public string EngineLabel
    {
        get
        {
            var model = (_brain as AIService)?.ResolvedModelId;
            return string.IsNullOrWhiteSpace(model) ? "CircleAI Neuron" : $"{model} (CircleAI)";
        }
    }

    /// <inheritdoc />
    public bool IsReady => _brain.IsReady;

    /// <inheritdoc />
    public string StatusMessage => _brain.IsReady ? "ready" : "loading model…";

    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatTurn> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // Translate host-neutral turns into the brain's ChatMessage — the only
        // place the upstream type is touched.
        var mapped = new List<ChatMessage>(messages.Count);
        foreach (var turn in messages)
            mapped.Add(new ChatMessage(turn.Role, turn.Content));

        await foreach (var chunk in _brain
            .StreamAsync(mapped, options: null, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    // ------------------------------------------------------------------
    // IPersistableChatRuntime — generalist floor snapshot (RT-02)
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public string? SessionSnapshotPath { get; }

    /// <inheritdoc />
    public async Task<bool> SaveSessionAsync(string path, CancellationToken cancellationToken = default)
    {
        // No-throw contract — safe to call from a lifecycle (OnSleep) thread.
        try { return await _brain.SaveSessionAsync(path, cancellationToken).ConfigureAwait(false); }
        catch { return false; }
    }

    /// <inheritdoc />
    public async Task<bool> LoadSessionAsync(string path, CancellationToken cancellationToken = default)
    {
        try { return await _brain.LoadSessionAsync(path, cancellationToken).ConfigureAwait(false); }
        catch { return false; }
    }

    private static string DefaultSnapshotPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CircleAI", "sessions");
        return Path.Combine(dir, "active.session");
    }
}
