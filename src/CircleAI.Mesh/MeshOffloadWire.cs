// MeshOffloadWire.cs
//
// The on-transport wire format. Three message kinds ride any INetworkTransport,
// distinguished by ContentType so a shared transport carrying other CircleAI
// traffic is safely ignored:
//   request : "borrow your brain for this turn"   (consumer -> serving peer)
//   reply   : "here is the completion"            (serving peer -> consumer)
//   advert  : "here is what I can serve right now" (any node -> all peers)
//
// Payloads are UTF-8 JSON, source-generated (System.Text.Json) so the codec is
// trim- / AOT-safe for on-device MAUI builds. Envelopes are internal - they are
// an implementation detail, not part of the public contract. Nothing here knows
// how peers are discovered; that is AetherNet's job (aether-protocol repo).

using System.Text.Json;
using System.Text.Json.Serialization;
using CircleAI.Networking;

namespace CircleAI.Mesh;

/// <summary>Encodes / decodes offload messages to and from <see cref="NetworkPayload"/>.</summary>
internal static class MeshOffloadWire
{
    public const string RequestContentType = "application/x-circleai-offload-request+json";
    public const string ReplyContentType   = "application/x-circleai-offload-reply+json";
    public const string AdvertContentType  = "application/x-circleai-mesh-advert+json";

    /// <summary>Metadata key carrying the correlation id (also inside the JSON body).</summary>
    public const string CorrelationMetaKey = "circleai-offload-corr";

    public static NetworkPayload EncodeRequest(
        string sourceNodeId, string destinationPeerId, OffloadRequestEnvelope env, TimeSpan? ttl)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(env, MeshOffloadJsonContext.Default.OffloadRequestEnvelope);
        return Build(sourceNodeId, destinationPeerId, body, RequestContentType, env.CorrelationId, MessagePriority.High, ttl);
    }

    public static NetworkPayload EncodeReply(
        string sourceNodeId, string destinationNodeId, OffloadReplyEnvelope env, TimeSpan? ttl)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(env, MeshOffloadJsonContext.Default.OffloadReplyEnvelope);
        return Build(sourceNodeId, destinationNodeId, body, ReplyContentType, env.CorrelationId, MessagePriority.High, ttl);
    }

    public static NetworkPayload EncodeAdvert(
        string sourceNodeId, MeshAdvertEnvelope env, TimeSpan? ttl)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(env, MeshOffloadJsonContext.Default.MeshAdvertEnvelope);
        // destination null = broadcast to all reachable peers on the transport.
        return Build(sourceNodeId, null, body, AdvertContentType, env.PeerId, MessagePriority.Normal, ttl);
    }

    public static OffloadRequestEnvelope? DecodeRequest(NetworkPayload payload)
        => JsonSerializer.Deserialize(payload.Data.Span, MeshOffloadJsonContext.Default.OffloadRequestEnvelope);

    public static OffloadReplyEnvelope? DecodeReply(NetworkPayload payload)
        => JsonSerializer.Deserialize(payload.Data.Span, MeshOffloadJsonContext.Default.OffloadReplyEnvelope);

    public static MeshAdvertEnvelope? DecodeAdvert(NetworkPayload payload)
        => JsonSerializer.Deserialize(payload.Data.Span, MeshOffloadJsonContext.Default.MeshAdvertEnvelope);

    private static NetworkPayload Build(
        string? sourceId, string? destinationId, byte[] body,
        string contentType, string correlation, MessagePriority priority, TimeSpan? ttl)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CorrelationMetaKey] = correlation,
        };
        return new NetworkPayload(
            Id: Guid.NewGuid().ToString("N"),
            SourceId: sourceId,
            DestinationId: destinationId,
            Data: body,
            Priority: priority,
            Ttl: ttl,
            ContentType: contentType,
            Metadata: metadata,
            CreatedAt: DateTimeOffset.UtcNow);
    }
}

/// <summary>Wire form of an offload request. Carries the reply-to node so the peer knows where to answer.</summary>
internal sealed record OffloadRequestEnvelope(
    string CorrelationId,
    string ReplyToNodeId,
    string ModelId,
    string Prompt,
    int MaxOutputTokens,
    float Temperature,
    float TopP,
    string[] StopSequences,
    DateTimeOffset CreatedAtUtc);

/// <summary>Wire form of an offload reply.</summary>
internal sealed record OffloadReplyEnvelope(
    string CorrelationId,
    bool Success,
    string OutputText,
    int OutputTokenCount,
    string? FailureReason,
    string? ReasoningText,
    DateTimeOffset CompletedAtUtc);

/// <summary>
/// Wire form of a capability advertisement. <c>Tier</c> is the raw
/// <c>DeviceTier</c> ordinal so the wire stays independent of the enum.
/// </summary>
internal sealed record MeshAdvertEnvelope(
    string PeerId,
    string ModelId,
    int FreeKvTokens,
    int Tier,
    int ContextWindowTokens,
    DateTimeOffset AdvertisedAtUtc,
    int? LatencyHintMs);

/// <summary>Source-generated JSON metadata for the wire envelopes (trim / AOT safe).</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(OffloadRequestEnvelope))]
[JsonSerializable(typeof(OffloadReplyEnvelope))]
[JsonSerializable(typeof(MeshAdvertEnvelope))]
internal sealed partial class MeshOffloadJsonContext : JsonSerializerContext
{
}
