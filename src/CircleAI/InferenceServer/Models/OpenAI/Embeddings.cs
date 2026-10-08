// Embeddings.cs
//
// OpenAI-compatible DTOs for /v1/embeddings.

using System.Text.Json.Serialization;

namespace CircleAI.Inference.Server.Models.OpenAI;

/// <summary>OpenAI-shaped embeddings request.</summary>
public sealed class EmbeddingsRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    /// <summary>Either a single string or an array of strings.</summary>
    [JsonPropertyName("input")]
    public System.Text.Json.JsonElement Input { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }
}

/// <summary>OpenAI-shaped embeddings response.</summary>
public sealed class EmbeddingsResponse
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = "list";

    [JsonPropertyName("data")]
    public IList<EmbeddingDatum> Data { get; set; } = new List<EmbeddingDatum>();

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("usage")]
    public UsageInfo Usage { get; set; } = new();
}

/// <summary>One embedding row in the response.</summary>
public sealed class EmbeddingDatum
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = "embedding";

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("embedding")]
    public IList<float> Embedding { get; set; } = new List<float>();
}
