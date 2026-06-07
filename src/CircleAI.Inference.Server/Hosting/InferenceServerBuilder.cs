// InferenceServerBuilder.cs
//
// Reusable host builder for the inference server. Both Program.cs and the
// test WebApplicationFactory call into this to wire identical DI/middleware,
// so the test surface is exactly the same as production.

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using CircleAI.Companion;
using CircleAI.Inference.Server.Auth;
using CircleAI.Inference.Server.Endpoints;
using CircleAI.Inference.Server.Lifecycle;
using CircleAI.Inference.Server.Models;
using CircleAI.Inference.Server.Options;
using CircleAI.Runtime;
using CircleAI.Runtime.Backends;
using CircleAI.Runtime.Capabilities;
using CircleAI.Runtime.NativeRuntimes;

namespace CircleAI.Inference.Server.Hosting;

public static class InferenceServerBuilder
{
    /// <summary>
    /// Wires every CircleAI.Inference.Server service into <paramref name="services"/>,
    /// reading config from <paramref name="config"/> under the
    /// <c>CircleAIServer</c> section.
    /// </summary>
    public static IServiceCollection AddCircleAIInferenceServer(
        this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.Configure<InferenceServerOptions>(config.GetSection(InferenceServerOptions.SectionName));

        services.AddSingleton<ServerCounters>();
        services.AddSingleton<AdmissionControl>();
        services.AddSingleton<IInferenceServerModelRegistry, InferenceServerModelRegistry>();

        // Phase 3 — lifecycle + admin surface. Default IBridgeFactory is the
        // real MnnInferenceBridgeFactory which composes ModelRegistryService +
        // ModelDownloadService + NativeRuntimeFetcher + QwenTextGenerator into
        // a working IInferenceBridge. Hosts that need a different materialiser
        // (custom model cache, dual-backend fan-out, etc.) replace it via
        // services.AddSingleton<IBridgeFactory, MyFactory>() AFTER calling this.
        services.AddSingleton<IModelLifecycleManager, ModelLifecycleManager>();
        services.TryAddSingleton<IBridgeFactory, MnnInferenceBridgeFactory>();

        // Companion session pipeline — same TryAdd pattern as IBridgeFactory.
        // Without these defaults the /v1/companion/turn handler can't bind
        // its resolver parameter and the host crashes at startup
        // (AuthorizationPolicyCache enumeration). Hosts that ship their own
        // session-storage scheme (Redis, SQL, mesh-synced) override either
        // type via services.AddSingleton<…>() before/after this call.
        services.TryAddSingleton<ICompanionSessionFactory, CompanionSessionFactory>();
        services.TryAddSingleton<ICompanionSessionResolver, InMemoryCompanionSessionResolver>();

        // CircleAI.Runtime wiring — paths are expanded at AddSingleton time so
        // the directories exist before any request lands.
        services.AddSingleton<ICapabilityProbe>(_ => new CapabilityProbe());
        services.AddSingleton<IBackendSelector, BackendSelector>();
        services.AddSingleton<INativeRuntimeFetcher>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<InferenceServerOptions>>().Value;
            var root = PathExpansion.ExpandUserPath(opts.RuntimeCacheRoot);
            return new NativeRuntimeFetcher(root);
        });

        AddAuthentication(services, config);
        services.AddAuthorization(o =>
        {
            o.AddPolicy(AuthSchemes.AuthenticatedPolicy, policy =>
                policy.RequireAuthenticatedUser());
        });

        return services;
    }

    /// <summary>
    /// Registers the auth schemes (API-key always, JWT when enabled in
    /// config). Endpoints opt in via .RequireAuthorization(AuthSchemes.AuthenticatedPolicy).
    /// </summary>
    private static void AddAuthentication(IServiceCollection services, IConfiguration config)
    {
        var section = config.GetSection(InferenceServerOptions.SectionName);
        var jwtCfg = section.GetSection("Auth:Jwt");
        var jwtEnabled = jwtCfg.GetValue<bool>("Enabled");

        var authBuilder = services.AddAuthentication(AuthSchemes.ApiKey);

        authBuilder.AddScheme<ApiKeyAuthSchemeOptions, ApiKeyAuthHandler>(
            AuthSchemes.ApiKey, _ => { });

        if (jwtEnabled)
        {
            var issuer   = jwtCfg.GetValue<string>("Issuer")   ?? "";
            var audience = jwtCfg.GetValue<string>("Audience") ?? "";
            var signing  = jwtCfg.GetValue<string>("SigningKey") ?? "";

            authBuilder.AddJwtBearer(AuthSchemes.Jwt, jwt =>
            {
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer           = !string.IsNullOrEmpty(issuer),
                    ValidIssuer              = issuer,
                    ValidateAudience         = !string.IsNullOrEmpty(audience),
                    ValidAudience            = audience,
                    ValidateLifetime         = true,
                    ValidateIssuerSigningKey = !string.IsNullOrEmpty(signing),
                    IssuerSigningKey         = string.IsNullOrEmpty(signing)
                        ? null
                        : new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signing)),
                };
            });
        }
    }

    /// <summary>
    /// Maps the v1 endpoints. Call after <c>app.UseAuthentication()</c> +
    /// <c>app.UseAuthorization()</c>.
    /// </summary>
    public static void MapCircleAIEndpoints(this WebApplication app)
    {
        app.MapChatCompletions();
        app.MapEmbeddings();
        app.MapCompanion();
        app.MapDiagnostics();
        app.MapAdminLifecycle();
    }
}
