// CompanionEndpoint.cs
//
// POST /v1/companion/turn  — send a message to a CircleAI Companion
//                            session and return the reply (optionally agentic,
//                            optionally streamed via SSE).
//
// The endpoint resolves an ICompanionSession from the
// ICompanionSessionResolver DI singleton — the host provides the resolver
// at startup. Sessions are keyed by session_id (the host's own scheme:
// typically tied to a UHID).

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using CircleAI.Companion;
using CircleAI.Inference.Server.Auth;
using CircleAI.Inference.Server.Hosting;
using CircleAI.Inference.Server.Models;
using CircleAI.Inference.Server.Models.Companion;
using CircleAI.Inference.Server.Models.OpenAI;
using CircleAI.Inference.Server.Streaming;

namespace CircleAI.Inference.Server.Endpoints;

/// <summary>
/// Resolves an <see cref="ICompanionSession"/> for a given
/// <c>session_id</c> + <c>identity_id</c>. The host implements this and
/// registers it as a singleton; the server endpoint doesn't know how
/// sessions are stored or constructed.
/// </summary>
public interface ICompanionSessionResolver
{
    Task<ICompanionSession?> ResolveAsync(
        string sessionId, string identityId, CancellationToken ct);
}

public static class CompanionEndpoint
{
    public static void MapCompanion(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/companion/turn", HandleTurnAsync)
           .RequireAuthorization(AuthSchemes.AuthenticatedPolicy);
    }

    private static async Task<IResult> HandleTurnAsync(
        HttpContext http,
        CompanionTurnRequest body,
        ICompanionSessionResolver resolver,
        AdmissionControl admission,
        ServerCounters counters,
        CancellationToken ct)
    {
        if (body is null
            || string.IsNullOrWhiteSpace(body.SessionId)
            || string.IsNullOrWhiteSpace(body.IdentityId)
            || string.IsNullOrWhiteSpace(body.Message))
        {
            return Results.BadRequest(ErrorResponse.Of(
                "session_id, identity_id, and message are all required.",
                "invalid_request_error", "missing_field"));
        }

        var session = await resolver.ResolveAsync(body.SessionId, body.IdentityId, ct)
                                    .ConfigureAwait(false);
        if (session is null)
        {
            return Results.Json(
                ErrorResponse.Of(
                    $"No Companion session for session_id='{body.SessionId}', identity_id='{body.IdentityId}'.",
                    "invalid_request_error", "session_not_found"),
                statusCode: StatusCodes.Status404NotFound);
        }

        using var slot = admission.TryEnter();
        if (slot is null)
        {
            return Results.Json(
                ErrorResponse.Of("Server is at concurrency cap. Retry shortly.",
                    "server_busy", "concurrency_cap"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (body.Stream)
        {
            await StreamReplyAsync(http, session, body, counters, ct).ConfigureAwait(false);
            return Results.Empty;
        }

        try
        {
            var reply = body.Agentic
                ? await session.AgentAsync(body.Message, ct).ConfigureAwait(false)
                : await session.SendAsync(body.Message, ct).ConfigureAwait(false);

            return Results.Json(new CompanionTurnResponse
            {
                SessionId = body.SessionId,
                Reply     = reply,
                Agentic   = body.Agentic,
                TurnIndex = session.History.Count,
            });
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
                ErrorResponse.Of(ex.Message, "internal_error", "companion_failure"),
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task StreamReplyAsync(
        HttpContext http,
        ICompanionSession session,
        CompanionTurnRequest body,
        ServerCounters counters,
        CancellationToken ct)
    {
        var sse = new ServerSentEventsWriter(http.Response);
        try
        {
            await foreach (var chunk in session.StreamAsync(body.Message, ct).ConfigureAwait(false))
            {
                if (string.IsNullOrEmpty(chunk)) continue;
                await sse.WriteAsync(new
                {
                    session_id = body.SessionId,
                    delta = chunk,
                }, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { counters.AccountFailed(); }
        catch (Exception ex)
        {
            counters.AccountFailed();
            await sse.WriteAsync(new
            {
                session_id = body.SessionId,
                error = ex.Message,
            }, CancellationToken.None).ConfigureAwait(false);
        }
        await sse.WriteTerminatorAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
