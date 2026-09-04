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

// What the circle can be asked to DO, as opposed to what Services lists. One
// instance: every voice button consults it, and two would be two answers.
builder.Services.AddSingleton(sp => CapabilityRegistry.For(sp.GetService<IBrain>(), sp.GetService<ISettings>()));
builder.Services.AddSingleton<ICareerInterview, BrowserCareer>();
builder.Services.AddSingleton<IJobSpecTailor, BrowserTailor>();
builder.Services.AddSingleton<IWakeWord, BrowserWakeWord>();
builder.Services.AddSingleton<IWakePhrases, BrowserWakePhrases>();
builder.Services.AddSingleton<IShareTarget, BrowserShareTarget>();
builder.Services.AddSingleton<ISettings, BrowserSettings>();
builder.Services.AddSingleton<ISetup, BrowserSetup>();
builder.Services.AddSingleton<IConversation, BrowserConversation>();
builder.Services.AddSingleton<IProfile, BrowserProfile>();
builder.Services.AddSingleton<IResidentAssistant, BrowserResidentAssistant>();

// ONE MICROPHONE, SO ONE ANSWER TO WHAT IT IS DOING. Home's circle and the
// middle of the tab bar are the same control offered twice, and each used to keep
// its own copy of the phase - so a turn started from one left the other drawn
// idle. Scoped rather than singleton: on the server head that is one per circuit,
// and a singleton would show every visitor whoever spoke last.
builder.Services.AddScoped<VoiceMark>();

// WHAT IS ACTUALLY WIRED, as opposed to what is offered. The setup census
// counts downloads; this asks the runtime hooks and the real speech path
// whether they work. Scoped, because it holds no state worth sharing.
builder.Services.AddScoped<IWiringProbe, BrowserWiringProbe>();

// WHERE THE PHONE IS, WHICH IS NOT WHERE ITS OWNER IS FROM. The
// interpreter needs both: your language, and the language of the
// people around you.
builder.Services.AddSingleton<IWhereAmI, BrowserWhereAmI>();

await builder.Build().RunAsync();
