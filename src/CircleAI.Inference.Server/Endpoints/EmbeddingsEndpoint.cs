// EmbeddingsEndpoint.cs
//
// POST /v1/embeddings — OpenAI-compatible embeddings endpoint.

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using CircleAI.Embeddings;
using CircleAI.Inference.Server.Auth;
using CircleAI.Inference.Server.Hosting;
using CircleAI.Inference.Server.Models;
using CircleAI.Inference.Server.Models.OpenAI;

namespace CircleAI.Inference.Server.Endpoints;

public static class EmbeddingsEndpoint
{
    public static IEndpointConventionBuilder MapEmbeddings(this IEndpointRouteBuilder app) =>
        app.MapPost("/v1/embeddings", HandleAsync)
           .RequireAuthorization(AuthSchemes.AuthenticatedPolicy);

    private static async Task<IResult> HandleAsync(
        EmbeddingsRequest body,
        IInferenceServerModelRegistry registry,
        AdmissionControl admission,
        ServerCounters counters,
        CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Model))
            return Results.BadRequest(ErrorResponse.Of(
                "Missing or empty 'model' field.", "invalid_request_error", "missing_model"));

        var embedder = registry.ResolveEmbedder(body.Model);
        if (embedder is null)
        {
            return Results.Json(
                ErrorResponse.Of($"Embedding model '{body.Model}' is not loaded.",
                    "invalid_request_error", "model_not_found"),
                statusCode: StatusCodes.Status404NotFound);
        }

        // Normalise the input into a list of strings — OpenAI accepts both a
        // single string and an array of strings.
        if (!TryNormaliseInput(body.Input, out var inputs, out var error))
            return Results.BadRequest(error);

        using var slot = admission.TryEnter();
        if (slot is null)
        {
            return Results.Json(
                ErrorResponse.Of("Server is at concurrency cap. Retry shortly.",
                    "server_busy", "concurrency_cap"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var data = new List<EmbeddingDatum>(inputs.Count);
        var totalChars = 0;
        try
        {
            for (var i = 0; i < inputs.Count; i++)
            {
                var vec = await embedder.GenerateAsync(inputs[i], ct).ConfigureAwait(false);
                data.Add(new EmbeddingDatum { Index = i, Embedding = vec });
                totalChars += inputs[i].Length;
            }
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
                ErrorResponse.Of(ex.Message, "internal_error", "embedding_failure"),
                statusCode: StatusCodes.Status500InternalServerError);
        }

        // Token usage estimation: OpenAI reports input tokens only for embeddings.
        var estimatedPromptTokens = Math.Max(1, totalChars / 4);
        return Results.Json(new EmbeddingsResponse
        {
            Data  = data,
            Model = body.Model,
            Usage = new UsageInfo
            {
                PromptTokens     = estimatedPromptTokens,
                CompletionTokens = 0,
                TotalTokens      = estimatedPromptTokens,
            }
        });
    }

    private static bool TryNormaliseInput(
        JsonElement input, out List<string> inputs, out ErrorResponse? error)
    {
        inputs = new List<string>();
        if (input.ValueKind == JsonValueKind.String)
        {
            var s = input.GetString();
            if (s is null) { error = ErrorResponse.Of("'input' string cannot be null.", "invalid_request_error", "invalid_input"); return false; }
            inputs.Add(s);
            error = null;
            return true;
        }
        if (input.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in input.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String)
                {
                    error = ErrorResponse.Of("Every 'input' array element must be a string.",
                        "invalid_request_error", "invalid_input");
                    return false;
                }
                inputs.Add(el.GetString() ?? "");
            }
            if (inputs.Count == 0)
            {
                error = ErrorResponse.Of("'input' array must not be empty.",
                    "invalid_request_error", "invalid_input");
                return false;
            }
            error = null;
            return true;
        }
        error = ErrorResponse.Of("'input' must be a string or array of strings.",
            "invalid_request_error", "invalid_input");
        return false;
    }
}
