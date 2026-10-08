// AdminEndpoints.cs
//
// POST   /v1/admin/models/load    — request a model load (passes through to lifecycle manager)
// DELETE /v1/admin/models/{id}    — unload a model
// GET    /v1/admin/lifecycle      — show current loaded-model footprint
//
// Admin endpoints require auth like the rest, but a future Phase will add
// a role-based policy ("admin" claim required). For 1.0 the API-key gate
// is sufficient — operator-only callers hold the keys.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using CircleAI.Hosting.InferenceBridge;
using CircleAI.Inference.Server.Auth;
using CircleAI.Inference.Server.Lifecycle;
using CircleAI.Inference.Server.Models.OpenAI;
using CircleAI.Runtime.Backends;

namespace CircleAI.Inference.Server.Endpoints;

/// <summary>
/// DI factory delegate — the host registers one of these so the admin
/// endpoint knows how to materialise an <see cref="IInferenceBridge"/>
/// for a given model id + backend.
/// </summary>
/// <remarks>
/// A host that ships its own MNN-loaded Qwen tower binds this to a closure
/// over its model-cache / mnnbridge factory. The default implementation
/// throws — admin loads require explicit host opt-in.
/// </remarks>
public interface IBridgeFactory
{
    Task<IInferenceBridge> CreateAsync(
        string modelId, BackendKind backend, CapabilityTier tier, CancellationToken ct);
}

/// <summary>Default implementation — refuses every load with a clear error.</summary>
public sealed class UnconfiguredBridgeFactory : IBridgeFactory
{
    public Task<IInferenceBridge> CreateAsync(
        string modelId, BackendKind backend, CapabilityTier tier, CancellationToken ct) =>
        throw new InvalidOperationException(
            "No IBridgeFactory is configured. Register one with " +
            "services.AddSingleton<IBridgeFactory, MyFactory>() before " +
            "calling /v1/admin/models/load.");
}

/// <summary>Request body for POST /v1/admin/models/load.</summary>
public sealed class AdminLoadRequest
{
    public string ModelId        { get; set; } = "";
    public string Backend        { get; set; } = "Cpu";
    public string Tier           { get; set; } = "Tier1_Small";
    public long   VramRequiredBytes { get; set; }
    public long   RamRequiredBytes  { get; set; }
}

/// <summary>Response body for /v1/admin/lifecycle.</summary>
public sealed class AdminLifecycleResponse
{
    public long TotalAllocatedVramBytes { get; set; }
    public long TotalAllocatedRamBytes  { get; set; }
    public IList<ModelLoadState> Loaded { get; set; } = new List<ModelLoadState>();
}

public static class AdminEndpoints
{
    public static void MapAdminLifecycle(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/admin")
            .RequireAuthorization(AuthSchemes.AuthenticatedPolicy);

        group.MapGet("/lifecycle", (IModelLifecycleManager mgr) =>
        {
            var resp = new AdminLifecycleResponse
            {
                TotalAllocatedVramBytes = mgr.TotalAllocatedVramBytes,
                TotalAllocatedRamBytes  = mgr.TotalAllocatedRamBytes,
                Loaded = mgr.List().ToList(),
            };
            return Results.Json(resp);
        });

        group.MapPost("/models/load", async (
            AdminLoadRequest body,
            IModelLifecycleManager mgr,
            IBridgeFactory factory,
            CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.ModelId))
                return Results.BadRequest(ErrorResponse.Of(
                    "Missing 'modelId'.", "invalid_request_error", "missing_model"));

            if (!Enum.TryParse<BackendKind>(body.Backend, ignoreCase: true, out var backend))
                return Results.BadRequest(ErrorResponse.Of(
                    $"Unknown backend '{body.Backend}'. Valid: Cpu, Cuda, Vulkan, OpenCL, Metal, Ascend, Cambricon, CoreML.",
                    "invalid_request_error", "invalid_backend"));

            if (!Enum.TryParse<CapabilityTier>(body.Tier, ignoreCase: true, out var tier))
                return Results.BadRequest(ErrorResponse.Of(
                    $"Unknown tier '{body.Tier}'. Valid: Tier0_Tiny..Tier4_Frontier.",
                    "invalid_request_error", "invalid_tier"));

            var descriptor = new ModelLoadDescriptor(
                ModelId: body.ModelId,
                Backend: backend,
                RequestedTier: tier,
                VramRequiredBytes: Math.Max(0, body.VramRequiredBytes),
                RamRequiredBytes:  Math.Max(0, body.RamRequiredBytes),
                BridgeFactory:  cancel => factory.CreateAsync(body.ModelId, backend, tier, cancel));

            var result = await mgr.LoadAsync(descriptor, ct);
            return result.Outcome switch
            {
                LoadOutcome.Loaded
                  or LoadOutcome.AlreadyLoaded => Results.Json(new
                  {
                      outcome   = result.Outcome.ToString(),
                      state     = result.State,
                      rationale = result.Rationale,
                  }),
                LoadOutcome.InsufficientVram
                  or LoadOutcome.InsufficientRam => Results.Json(
                    ErrorResponse.Of(result.Rationale, "resource_exhausted", result.Outcome.ToString()),
                    statusCode: StatusCodes.Status507InsufficientStorage),
                LoadOutcome.FactoryFailed => Results.Json(
                    ErrorResponse.Of(result.Rationale, "internal_error", "factory_failed"),
                    statusCode: StatusCodes.Status500InternalServerError),
                _ => Results.Json(
                    ErrorResponse.Of(result.Rationale, "internal_error", "unknown"),
                    statusCode: StatusCodes.Status500InternalServerError),
            };
        });

        group.MapDelete("/models/{modelId}", async (
            string modelId,
            IModelLifecycleManager mgr,
            CancellationToken ct) =>
        {
            var outcome = await mgr.UnloadAsync(modelId, ct);
            return outcome switch
            {
                UnloadOutcome.Unloaded  => Results.Json(new { outcome = "Unloaded", modelId }),
                UnloadOutcome.NotLoaded => Results.Json(
                    ErrorResponse.Of($"Model '{modelId}' is not loaded.", "invalid_request_error", "not_loaded"),
                    statusCode: StatusCodes.Status404NotFound),
                _                       => Results.Json(
                    ErrorResponse.Of("Unknown unload outcome.", "internal_error", "unknown"),
                    statusCode: StatusCodes.Status500InternalServerError),
            };
        });
    }
}
