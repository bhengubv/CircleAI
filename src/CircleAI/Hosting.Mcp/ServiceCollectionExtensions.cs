// ServiceCollectionExtensions.cs
//
// (3.2.0) DI helpers — register IMcpTool + IMcpResourceProvider impls.
// The dispatcher pulls them via GetServices<T>(), so anything added to
// the container is exposed automatically.

using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Hosting.Mcp;

public static class McpServiceCollectionExtensions
{
    /// <summary>(3.2.0) Register one MCP tool singleton.</summary>
    public static IServiceCollection AddMcpTool<T>(this IServiceCollection services)
        where T : class, IMcpTool
    {
        services.AddSingleton<IMcpTool, T>();
        return services;
    }

    /// <summary>(3.2.0) Register one MCP resource provider singleton.</summary>
    public static IServiceCollection AddMcpResourceProvider<T>(this IServiceCollection services)
        where T : class, IMcpResourceProvider
    {
        services.AddSingleton<IMcpResourceProvider, T>();
        return services;
    }
}
