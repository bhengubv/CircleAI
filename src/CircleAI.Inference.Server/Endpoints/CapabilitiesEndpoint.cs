// CapabilitiesEndpoint.cs
//
// GET /v1/capabilities — Circle AI's honest self-catalogue over HTTP.
//
// The same ICapabilityCatalog a consumer resolves in-process, served to a networked
// consumer: one entry per capability, each with its status, so a caller can tell what
// Circle AI can rely on before it relies on it. No model, no session — pure read.

using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using CircleAI.Inference.Server.Auth;
using CircleAI.Skills;

namespace CircleAI.Inference.Server.Endpoints;

public static class CapabilitiesEndpoint
{
    public static void MapCapabilities(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/capabilities", Handle)
           .RequireAuthorization(AuthSchemes.AuthenticatedPolicy);
    }

    private static IResult Handle(ICapabilityCatalog catalog)
        => Results.Json(new
        {
            capabilities = catalog.All().Select(c => new
            {
                id       = c.Id,
                name     = c.Name,
                status   = c.Status,
                summary  = c.Summary,
                requires = c.Requires,
                limits   = c.Limits,
            }),
        });
}
