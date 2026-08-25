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
builder.Services.AddSingleton<IDeviceFacts, BrowserDeviceFacts>();
builder.Services.AddSingleton<ISpokenLanguage, BrowserSpokenLanguage>();
builder.Services.AddSingleton<IBrain, BrowserBrain>();
builder.Services.AddSingleton<ICareerInterview, BrowserCareer>();
builder.Services.AddSingleton<IJobSpecTailor, BrowserTailor>();
builder.Services.AddSingleton<IWakeWord, BrowserWakeWord>();
builder.Services.AddSingleton<IWakePhrases, BrowserWakePhrases>();
builder.Services.AddSingleton<ISettings, BrowserSettings>();
builder.Services.AddSingleton<ISetup, BrowserSetup>();
builder.Services.AddSingleton<IConversation, BrowserConversation>();
builder.Services.AddSingleton<IProfile, BrowserProfile>();

await builder.Build().RunAsync();
