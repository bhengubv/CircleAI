// ChatCompletion.cs
//
// OpenAI-compatible DTOs for /v1/chat/completions. Field names and JSON
// shape match the public OpenAI Chat Completions API (v1) so SDKs that
// target OpenAI (openai-python, @openai/sdk, etc.) work against CircleAI
// with only a base-URL change.

using System.Text.Json.Serialization;

namespace CircleAI.Inference.Server.Models.OpenAI;

/// <summary>OpenAI-shaped chat-completion request body.</summary>
public sealed class ChatCompletionRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("messages")]
    public IList<ChatCompletionMessage> Messages { get; set; } = new List<ChatCompletionMessage>();

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("stop")]
    public IList<string>? Stop { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }
}

/// <summary>One message in the chat completion conversation.</summary>
public sealed class ChatCompletionMessage
{
    /// <summary>OpenAI roles: <c>system</c>, <c>user</c>, <c>assistant</c>, <c>tool</c>.</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>OpenAI-shaped successful chat completion response.</summary>
public sealed class ChatCompletionResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("object")]
    public string Object { get; set; } = "chat.completion";

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("choices")]
    public IList<ChatCompletionChoice> Choices { get; set; } = new List<ChatCompletionChoice>();

    [JsonPropertyName("usage")]
    public UsageInfo Usage { get; set; } = new();
}

/// <summary>One choice in a non-streaming chat completion response.</summary>
public sealed class ChatCompletionChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    public ChatCompletionMessage Message { get; set; } = new();

    [JsonPropertyName("finish_reason")]
    public string FinishReason { get; set; } = "stop";
}

/// <summary>Token-usage block.</summary>
public sealed class UsageInfo
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}

/// <summary>One SSE delta frame in a streamed chat completion.</summary>
public sealed class ChatCompletionStreamChunk
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("object")]
    public string Object { get; set; } = "chat.completion.chunk";

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("choices")]
    public IList<ChatCompletionStreamChoice> Choices { get; set; } = new List<ChatCompletionStreamChoice>();
}

/// <summary>One delta in a streamed chat completion chunk.</summary>
public sealed class ChatCompletionStreamChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("delta")]
    public ChatCompletionDelta Delta { get; set; } = new();

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

/// <summary>Delta payload — only non-null fields are emitted between SSE frames.</summary>
public sealed class ChatCompletionDelta
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }
}
