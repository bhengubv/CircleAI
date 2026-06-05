// ModelRegistry.cs
//
// In-process registry mapping logical model IDs to the IInferenceBridge
// that serves them. The host populates this at startup (one bridge per
// loaded model) and the endpoints look up by request.Model. Lock-free
// reads; copy-on-write registration.

using System.Collections.Concurrent;
using CircleAI.Embeddings;
using CircleAI.Hosting.InferenceBridge;

namespace CircleAI.Inference.Server.Models;

/// <summary>
/// In-process registry of bridge instances keyed by logical model ID
/// (the value clients pass in the <c>model</c> field of an OpenAI request).
/// </summary>
public interface IInferenceServerModelRegistry
{
    /// <summary>Register a bridge under <paramref name="modelId"/>.</summary>
    void Register(string modelId, IInferenceBridge bridge);

    /// <summary>Register an embedder under <paramref name="modelId"/>.</summary>
    void RegisterEmbedder(string modelId, ITextEmbedder embedder);

    /// <summary>Look up a bridge. Returns <c>null</c> when the model is not registered.</summary>
    IInferenceBridge? Resolve(string modelId);

    /// <summary>Look up an embedder.</summary>
    ITextEmbedder? ResolveEmbedder(string modelId);

    /// <summary>List every model ID currently served (chat + embedding).</summary>
    IReadOnlyList<string> AllModelIds();

    /// <summary>List chat-capable model IDs only.</summary>
    IReadOnlyList<string> ChatModelIds();
}

/// <summary>Default thread-safe implementation.</summary>
public sealed class InferenceServerModelRegistry : IInferenceServerModelRegistry
{
    private readonly ConcurrentDictionary<string, IInferenceBridge> _chat    = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ITextEmbedder>    _embed   = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public void Register(string modelId, IInferenceBridge bridge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(bridge);
        _chat[modelId] = bridge;
    }

    /// <inheritdoc/>
    public void RegisterEmbedder(string modelId, ITextEmbedder embedder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(embedder);
        _embed[modelId] = embedder;
    }

    /// <inheritdoc/>
    public IInferenceBridge? Resolve(string modelId) =>
        _chat.TryGetValue(modelId, out var b) ? b : null;

    /// <inheritdoc/>
    public ITextEmbedder? ResolveEmbedder(string modelId) =>
        _embed.TryGetValue(modelId, out var e) ? e : null;

    /// <inheritdoc/>
    public IReadOnlyList<string> AllModelIds() =>
        _chat.Keys.Concat(_embed.Keys).Distinct(StringComparer.Ordinal).ToList();

    /// <inheritdoc/>
    public IReadOnlyList<string> ChatModelIds() => _chat.Keys.ToList();
}
