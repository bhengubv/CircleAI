// NullImplementations.cs
//
// (3.1.0) Safe null defaults — every interface has a working
// implementation that returns empty or fail-closed answers. Lets the
// hosting layer wire CircleAI.Video optionally; absence of a real
// backend degrades to deterministic empty answers, never to a crash.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Video;

/// <summary>
/// (3.1.0) Returns an empty video — zero bytes, declared mime type
/// "video/mp4". Useful as the DI default. A real consumer that ends
/// up with this backend should fall back to audio-only style mail.
/// </summary>
public sealed class NullVideoGenerator : IVideoGenerator
{
    public static readonly NullVideoGenerator Instance = new();

    public string BackendId => "null";

    public ValueTask<VideoGenerationResult> GenerateAsync(
        VideoGenerationRequest request,
        CancellationToken      ct = default)
        => ValueTask.FromResult(new VideoGenerationResult(
            VideoBytes: ReadOnlyMemory<byte>.Empty,
            MimeType:   "video/mp4",
            Duration:   TimeSpan.Zero,
            FrameCount: 0,
            Resolution: request.Resolution,
            BackendId:  "null"));
}

/// <summary>
/// (3.1.0) Returns the source message unchanged with a zero estimated
/// duration. Useful so that consumers can swap in a real LLM-backed
/// rewriter (typically thin wrapper over IChatGenerator + a per-style
/// system prompt) without changing the wiring.
/// </summary>
public sealed class NullStyleScript : IStyleScript
{
    public static readonly NullStyleScript Instance = new();

    public string BackendId => "null";

    public ValueTask<StyleScriptResult> RewriteAsync(
        StyleScriptRequest request,
        CancellationToken  ct = default)
        => ValueTask.FromResult(new StyleScriptResult(
            RewrittenText:           request.SourceMessage,
            Style:                   request.Style,
            VoicePersonaId:          null,
            EstimatedSpokenDuration: TimeSpan.Zero));
}

/// <summary>
/// (3.1.0) Thread-safe in-memory style catalogue. Default
/// implementation — hosting layers (txtMe, content authoring tools)
/// register their style packs on startup and the picker reads from
/// here. Suitable for production use until a persistent store lands.
/// </summary>
public sealed class InMemoryStyleReference : IStyleReference
{
    private readonly Dictionary<string, StyleReference> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public string BackendId => "in-memory";

    public ValueTask RegisterAsync(StyleReference style, CancellationToken ct = default)
    {
        lock (_gate) _byId[style.Id.Value] = style;
        return ValueTask.CompletedTask;
    }

    public ValueTask<StyleReference?> GetAsync(StyleId id, CancellationToken ct = default)
    {
        lock (_gate)
            return ValueTask.FromResult(_byId.TryGetValue(id.Value, out var s) ? s : null);
    }

    public ValueTask<IReadOnlyList<StyleReference>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var copy = new List<StyleReference>(_byId.Values);
            return ValueTask.FromResult<IReadOnlyList<StyleReference>>(copy);
        }
    }
}
