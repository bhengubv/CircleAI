// DiagnosticsEndpoint.cs
//
// GET /v1/diagnostics — uptime + loaded models + host profile + backend pick
// GET /v1/healthz     — liveness (HTTP 200 if the process is alive)
// GET /v1/readyz      — readiness (HTTP 200 only when ≥ 1 model registered)
// GET /v1/models      — OpenAI-shaped list of loaded models

using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using CircleAI.Inference.Server.Auth;
using CircleAI.Inference.Server.Lifecycle;
using CircleAI.Inference.Server.Models;
using CircleAI.Inference.Server.Models.Diagnostics;
using CircleAI.Inference.Server.Options;
using CircleAI.Runtime.Backends;
using CircleAI.Runtime.Capabilities;

namespace CircleAI.Inference.Server.Endpoints;

public static class DiagnosticsEndpoint
{
    public static void MapDiagnostics(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/healthz", () => Results.Json(new HealthResponse { Status = "alive" }))
           .AllowAnonymous();

        app.MapGet("/v1/readyz", (IInferenceServerModelRegistry registry) =>
        {
            var any = registry.AllModelIds().Count > 0;
            return any
                ? Results.Json(new HealthResponse { Status = "ready" })
                : Results.Json(new HealthResponse { Status = "no_models_loaded" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous();

        app.MapGet("/v1/diagnostics", HandleDiagnosticsAsync)
           .RequireAuthorization(AuthSchemes.AuthenticatedPolicy);

        app.MapGet("/v1/models", (IInferenceServerModelRegistry registry) =>
        {
            var list = registry.AllModelIds()
                .Select(id => new LoadedModelInfo { Id = id, SupportsStreaming = registry.Resolve(id) is not null })
                .ToList();
            return Results.Json(new { @object = "list", data = list });
        }).RequireAuthorization(AuthSchemes.AuthenticatedPolicy);
    }

    private static async Task<IResult> HandleDiagnosticsAsync(
        IInferenceServerModelRegistry registry,
        ServerCounters counters,
        ICapabilityProbe probe,
        IBackendSelector selector,
        INativeRuntimeStatus nativeStatus,
        CancellationToken ct)
    {
        var profile = await probe.ProbeAsync(ct).ConfigureAwait(false);
        var selection = selector.Select(profile, CapabilityTier.Tier2_Medium);

        var loadedModels = registry.AllModelIds()
            .Select(id => new LoadedModelInfo
            {
                Id = id,
                SupportsStreaming = registry.Resolve(id) is not null,
            })
            .ToList();

        var asmVer = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
        var resp = new DiagnosticsResponse
        {
            ServerVersion    = asmVer,
            StartedAt        = counters.StartedAt,
            UptimeSeconds    = (DateTimeOffset.UtcNow - counters.StartedAt).TotalSeconds,
            LoadedModels     = loadedModels,
            HostProfile      = HostProfileDto.From(profile),
            BackendSelection = BackendSelectionDto.From(selection),
            Counters         = new CounterSnapshot
            {
                TotalRequests    = counters.TotalRequests,
                ActiveRequests   = counters.ActiveRequests,
                RejectedRequests = counters.RejectedRequests,
                FailedRequests   = counters.FailedRequests,
            },
            NativeRuntime    = ToNativeRuntimeDto(nativeStatus.Latest),
        };
        return Results.Json(resp);
    }

    private static NativeRuntimePathsDto? ToNativeRuntimeDto(
        CircleAI.Inference.NativeRuntimePrep.NativeRuntimePaths? paths)
    {
        if (paths is null) return null;
        return new NativeRuntimePathsDto
        {
            Rid                  = paths.Rid,
            ExpectedNativeDir    = paths.ExpectedNativeDir,
            MnnBridgePath        = paths.MnnBridgePath,
            MnnBridgeLoaded      = paths.MnnBridgeLoaded,
            MnnCoreFetchedPath   = paths.MnnCoreFetchedPath,
            MnnCoreFlattenedPath = paths.MnnCoreFlattenedPath,
            MnnCorePreloaded     = paths.MnnCorePreloaded,
            FlattenError         = paths.FlattenError,
            PreloadError         = paths.PreloadError,
        };
    }
}
