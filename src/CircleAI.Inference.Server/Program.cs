// Program.cs
//
// The app that runs CircleAI.Inference.Server. The server itself - configuration,
// DI, the /v1/* endpoints, Windows-service and systemd hosting - is
// InferenceServerHost, inside the CircleAI package, because Circle AI ships as ONE
// package. This line is all a host needs.

await CircleAI.Inference.Server.Hosting.InferenceServerHost.RunAsync(args);

/// <summary>
/// Top-level <c>Program</c> entry point - needs to be public so the test
/// project's <c>Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory&lt;TEntryPoint&gt;</c>
/// can bind to it. The body lives in the file's top-level statement above;
/// this partial just opens the visibility surface.
/// </summary>
public partial class Program;
