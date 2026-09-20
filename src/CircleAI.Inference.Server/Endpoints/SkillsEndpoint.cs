// SkillsEndpoint.cs
//
// GET /v1/skills?q=...  — search Circle AI's skills over HTTP.
//
// The same ISkillStore a consumer resolves in-process. With a query it searches; without
// one it lists. No model needed — the built-in pack is a prebuilt database. A host that
// registers its own ISkillStore before AddCircleAIInferenceServer serves that instead.

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using CircleAI.Inference.Server.Auth;
using CircleAI.Skills;

namespace CircleAI.Inference.Server.Endpoints;

public static class SkillsEndpoint
{
    public static void MapSkills(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/skills", HandleAsync)
           .RequireAuthorization(AuthSchemes.AuthenticatedPolicy);
    }

    private static async Task<IResult> HandleAsync(string? q, ISkillStore skills, CancellationToken ct)
    {
        var hits = string.IsNullOrWhiteSpace(q)
            ? await skills.ListAsync(ct).ConfigureAwait(false)
            : await skills.SearchAsync(q, ct).ConfigureAwait(false);

        return Results.Json(new
        {
            query = q,
            skills = hits.Select(s => new
            {
                id          = s.Id,
                name        = s.Name,
                description = s.Description,
                tags        = s.Tags,
            }),
        });
    }
}
