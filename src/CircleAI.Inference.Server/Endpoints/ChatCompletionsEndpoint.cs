// ChatCompletionsEndpoint.cs
//
// POST /v1/chat/completions — OpenAI-compatible chat-completion endpoint.
// Routes to the IInferenceBridge registered for the requested model.
// Streaming mode emits OpenAI-shaped SSE frames; non-streaming returns
// a single ChatCompletionResponse.

using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using CircleAI.Hosting.InferenceBridge;
using CircleAI.Inference.Server.Auth;
using CircleAI.Inference.Server.Hosting;
using CircleAI.Inference.Server.Models;
using CircleAI.Inference.Server.Models.OpenAI;
using CircleAI.Inference.Server.Options;
using CircleAI.Inference.Server.Streaming;

namespace CircleAI.Inference.Server.Endpoints;

/// <summary>
/// Registration helper — wires <c>POST /v1/chat/completions</c> into the
/// supplied <see cref="IEndpointRouteBuilder"/>.
/// </summary>
public static class ChatCompletionsEndpoint
{
    public static IEndpointConventionBuilder MapChatCompletions(this IEndpointRouteBuilder app) =>
        app.MapPost("/v1/chat/completions", HandleAsync)
           .RequireAuthorization(AuthSchemes.AuthenticatedPolicy);

    private static async Task<IResult> HandleAsync(
        HttpContext http,
        ChatCompletionRequest body,
        IInferenceServerModelRegistry registry,
        AdmissionControl admission,
        ServerCounters counters,
        IOptions<InferenceServerOptions> options,
        CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Model))
            return Results.BadRequest(ErrorResponse.Of(
                "Missing or empty 'model' field.", "invalid_request_error", "missing_model"));
        if (body.Messages is null || body.Messages.Count == 0)
            return Results.BadRequest(ErrorResponse.Of(
                "Missing 'messages' array.", "invalid_request_error", "missing_messages"));

        var bridge = registry.Resolve(body.Model);
        if (bridge is null)
        {
            return Results.Json(
                ErrorResponse.Of($"Model '{body.Model}' is not loaded.", "invalid_request_error", "model_not_found"),
                statusCode: StatusCodes.Status404NotFound);
        }

        using var slot = admission.TryEnter();
        if (slot is null)
        {
            http.Response.Headers["Retry-After"] = "1";
            return Results.Json(
                ErrorResponse.Of(
                    $"Server is at concurrency cap ({admission.MaxConcurrentRequests}). Retry after a brief delay.",
                    "server_busy", "concurrency_cap"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Per-request timeout overlay on top of the connection token.
        var timeoutSeconds = Math.Max(1, options.Value.RequestTimeoutSeconds);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var request = BuildInferenceRequest(body);

        if (body.Stream)
        {
            await StreamResponseAsync(http, bridge, request, body, counters, timeoutCts.Token);
            return Results.Empty; // headers/body already written.
        }
        else
        {
            return await NonStreamResponseAsync(bridge, request, body, counters, timeoutCts.Token);
        }
    }

    // ── Non-streaming branch ─────────────────────────────────────────────────

    private static async Task<IResult> NonStreamResponseAsync(
        IInferenceBridge bridge,
        InferenceRequest request,
        ChatCompletionRequest body,
        ServerCounters counters,
        CancellationToken ct)
    {
        InferenceResponse resp;
        try
        {
            resp = await bridge.CompleteAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            counters.AccountFailed();
            return Results.Json(
                ErrorResponse.Of("Request cancelled or timed out.", "timeout", "request_timeout"),
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (Exception ex)
        {
            counters.AccountFailed();
            return Results.Json(
                ErrorResponse.Of(ex.Message, "internal_error", "bridge_failure"),
                statusCode: StatusCodes.Status500InternalServerError);
        }

        if (resp.Status == InferenceStatus.Failed)
        {
            counters.AccountFailed();
            return Results.Json(
                ErrorResponse.Of(
                    resp.FailureMessage ?? "Inference failed.", "internal_error", "inference_failed"),
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var response = new ChatCompletionResponse
        {
            Id      = $"chatcmpl-{Guid.NewGuid():N}",
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model   = body.Model,
            Choices = new List<ChatCompletionChoice>
            {
                new()
                {
                    Index = 0,
                    Message = new ChatCompletionMessage { Role = "assistant", Content = resp.OutputText },
                    FinishReason = MapFinish(resp.Status),
                }
            },
            Usage = new UsageInfo
            {
                PromptTokens     = resp.PromptTokenCount,
                CompletionTokens = resp.OutputTokenCount,
                TotalTokens      = resp.PromptTokenCount + resp.OutputTokenCount,
            }
        };
        return Results.Json(response);
    }

    // ── Streaming branch ────────────────────────────────────────────────────

    private static async Task StreamResponseAsync(
        HttpContext http,
        IInferenceBridge bridge,
        InferenceRequest request,
        ChatCompletionRequest body,
        ServerCounters counters,
        CancellationToken ct)
    {
        var sse = new ServerSentEventsWriter(http.Response);
        var id  = $"chatcmpl-{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // First frame: role announcement.
        await sse.WriteAsync(new ChatCompletionStreamChunk
        {
            Id = id, Created = created, Model = body.Model,
            Choices = new List<ChatCompletionStreamChoice>
            {
                new() { Index = 0, Delta = new ChatCompletionDelta { Role = "assistant" } }
            },
        }, ct).ConfigureAwait(false);

        try
        {
            await foreach (var chunk in bridge.StreamCompletionAsync(request, ct).ConfigureAwait(false))
            {
                if (string.IsNullOrEmpty(chunk)) continue;
                await sse.WriteAsync(new ChatCompletionStreamChunk
                {
                    Id = id, Created = created, Model = body.Model,
                    Choices = new List<ChatCompletionStreamChoice>
                    {
                        new() { Index = 0, Delta = new ChatCompletionDelta { Content = chunk } }
                    },
                }, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { counters.AccountFailed(); /* terminate gracefully below */ }
        catch (Exception ex)
        {
            counters.AccountFailed();
            await sse.WriteAsync(new ChatCompletionStreamChunk
            {
                Id = id, Created = created, Model = body.Model,
                Choices = new List<ChatCompletionStreamChoice>
                {
                    new() { Index = 0, Delta = new ChatCompletionDelta { Content = $"[error: {ex.Message}]" }, FinishReason = "error" }
                },
            }, CancellationToken.None).ConfigureAwait(false);
        }

        // Final frame: stop reason + [DONE].
        await sse.WriteAsync(new ChatCompletionStreamChunk
        {
            Id = id, Created = created, Model = body.Model,
            Choices = new List<ChatCompletionStreamChoice>
            {
                new() { Index = 0, Delta = new ChatCompletionDelta(), FinishReason = "stop" }
            },
        }, CancellationToken.None).ConfigureAwait(false);
        await sse.WriteTerminatorAsync(CancellationToken.None).ConfigureAwait(false);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static InferenceRequest BuildInferenceRequest(ChatCompletionRequest body)
    {
        // Concatenate messages into a single prompt — the bridge's underlying
        // generator does its own chat-templating; we just give it the
        // OpenAI-conversation transcript joined with role markers.
        var prompt = string.Join("\n", body.Messages.Select(m =>
            $"<|{m.Role}|>\n{m.Content}\n<|end|>"));

        var metadata = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(body.User)) metadata["user"] = body.User!;

        return new InferenceRequest(
            Id: Guid.NewGuid(),
            ModelId: body.Model,
            Prompt: prompt,
            MaxOutputTokens: body.MaxTokens ?? 512,
            Temperature: body.Temperature ?? 0.7f,
            TopP: body.TopP ?? 0.9f,
            StopSequences: (IReadOnlyList<string>)(body.Stop ?? (IList<string>)new List<string>()),
            Metadata: metadata,
            RequestedAt: DateTimeOffset.UtcNow);
    }

    private static string MapFinish(InferenceStatus status) => status switch
    {
        InferenceStatus.Completed       => "stop",
        InferenceStatus.StoppedByToken  => "stop",
        InferenceStatus.StoppedByLength => "length",
        InferenceStatus.Cancelled       => "cancelled",
        _                                => "error",
    };
}
