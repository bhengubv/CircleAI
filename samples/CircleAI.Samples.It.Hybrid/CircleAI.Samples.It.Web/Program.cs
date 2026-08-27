// The web head.
//
// Renders the shared pages on a server, then hands over to WebAssembly. Its
// IVoiceHost is the browser one - deliberately. A server-side synthesiser would
// make the button work by sending the text off the device, which is the promise
// the sample exists to demonstrate keeping.

using CircleAI.Samples.It;
using CircleAI.Samples.It.Web.Components;
using CircleAI.Samples.It.Web.Client.Services;
using CircleAI.Samples.It.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// Device-specific services the shared UI depends on. The server registers the
// same honest answers the client does: this head is not a device.
builder.Services.AddSingleton<IFormFactor, ServerFormFactor>();
builder.Services.AddSingleton<IVoiceHost, BrowserVoiceHost>();
builder.Services.AddSingleton<IDeviceFacts, BrowserDeviceFacts>();
builder.Services.AddSingleton<ISpokenLanguage, BrowserSpokenLanguage>();
builder.Services.AddSingleton<IBrain, BrowserBrain>();
builder.Services.AddSingleton<ICareerInterview, BrowserCareer>();
builder.Services.AddSingleton<IJobSpecTailor, BrowserTailor>();
builder.Services.AddSingleton<IWakeWord, BrowserWakeWord>();
builder.Services.AddSingleton<IWakePhrases, BrowserWakePhrases>();
builder.Services.AddSingleton<IShareTarget, BrowserShareTarget>();
builder.Services.AddSingleton<ISettings, BrowserSettings>();
builder.Services.AddSingleton<ISetup, BrowserSetup>();
builder.Services.AddSingleton<IConversation, BrowserConversation>();
builder.Services.AddSingleton<IProfile, BrowserProfile>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(
        typeof(CircleAI.Samples.It.Shared._Imports).Assembly,
        typeof(CircleAI.Samples.It.Web.Client._Imports).Assembly);

app.Run();
