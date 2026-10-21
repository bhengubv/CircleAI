// InferenceServerHost.cs
//
// The whole server, callable: everything CircleAI.Inference.Server's Program.cs used
// to do, here, so it ships in the CircleAI package - Circle AI ships as ONE package -
// and the app that runs it is one line.
//
//   - reads appsettings.json, then environment variables, then the command line
//   - wires DI through AddCircleAIInferenceServer
//   - maps the /v1/* endpoints behind authentication
//   - honours the Windows service controller and systemd when launched by them

using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CircleAI.Inference.Server.Hosting;

/// <summary>Builds and runs the OpenAI-compatible inference server.</summary>
public static class InferenceServerHost
{
    /// <summary>The name the server registers under as a Windows service.</summary>
    public const string ServiceName = "CircleAI.Inference.Server";

    /// <summary>Builds the server, ready to run.</summary>
    /// <param name="args">The command line, as handed to the host's entry point.</param>
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Both lifecycles, and each is a no-op unless its service controller launched
        // the process.
        builder.Host.UseWindowsService(o => o.ServiceName = ServiceName);
        builder.Host.UseSystemd();

        builder.Services.AddCircleAIInferenceServer(builder.Configuration);
        builder.Services.AddProblemDetails();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCircleAIEndpoints();
        return app;
    }

    /// <summary>Builds the server and runs it until the host is told to stop.</summary>
    /// <param name="args">The command line, as handed to the host's entry point.</param>
    /// <param name="cancellationToken">Stops the server when cancelled.</param>
    public static async Task RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        await using var app = Build(args);
        await app.RunAsync(cancellationToken);
    }
}
