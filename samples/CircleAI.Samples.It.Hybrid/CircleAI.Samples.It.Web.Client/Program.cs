// The WebAssembly client's entry point.
//
// Registers the browser's answers to the shared UI's two questions - which head
// am I, and can you speak - and hands the same Routes component the phone uses to
// the browser's renderer.

using CircleAI.Samples.It;
using CircleAI.Samples.It.Web.Client.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Device-specific services the shared UI depends on. Each head answers for
// itself; the UI never asks what platform it is on directly.
builder.Services.AddSingleton<IFormFactor, BrowserFormFactor>();
builder.Services.AddSingleton<IVoiceHost, BrowserVoiceHost>();

await builder.Build().RunAsync();
