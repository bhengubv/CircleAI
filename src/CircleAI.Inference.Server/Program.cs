// Program.cs
//
// Top-level entry point for the CircleAI.Inference.Server.
//   - Reads appsettings.json (+ overrides via env vars / cmd line)
//   - Wires DI through InferenceServerBuilder
//   - Maps /v1/* endpoints
//   - Honours Windows-service + systemd hosting when invoked by the
//     respective service controller

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Hosting.Systemd;
using CircleAI.Inference.Server.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Honour both Windows-service and systemd lifecycles — no-op when the host
// process isn't launched by the corresponding service controller.
builder.Host.UseWindowsService(o => o.ServiceName = "CircleAI.Inference.Server");
builder.Host.UseSystemd();

builder.Services.AddCircleAIInferenceServer(builder.Configuration);
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapCircleAIEndpoints();

await app.RunAsync();

/// <summary>
/// Top-level <c>Program</c> entry point — needs to be public so the test
/// project's <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
/// can bind to it. The body lives in the file's top-level statements above;
/// this partial just opens the visibility surface.
/// </summary>
public partial class Program;
