// The MAUI head's composition root.
//
// Registers this device's answers to the shared UI's questions and hands it a
// BlazorWebView to render in.

using CircleAI.Samples.It;
using CircleAI.Samples.It.App.Services;
using Microsoft.Extensions.Logging;

namespace CircleAI.Samples.It.App;

/// <summary>Builds the app.</summary>
public static class MauiProgram
{
    /// <summary>Compose and return the app.</summary>
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // Device-specific services the shared UI depends on. This is the seam that
        // lets one set of pages render on a phone and in a browser: the pages ask
        // these interfaces what is possible, and each head answers for itself.
        // ONE FILE FOR ALL OF THE APP'S STATE, beside the career database rather
        // than in SharedPreferences: state spread across four mechanisms is state
        // that cannot be backed up, moved to another phone, or restored after a
        // reinstall - which is why setting up again means setting up everything.
        builder.Services.AddSingleton(_ => new SqliteAppStore(
            System.IO.Path.Combine(FileSystem.AppDataDirectory, "CircleAI", "app.db")));

        builder.Services.AddSingleton<IFormFactor, DeviceFormFactor>();
        builder.Services.AddSingleton<IVoiceHost, DeviceVoiceHost>();
        builder.Services.AddSingleton<IDeviceFacts, DeviceFacts>();
        builder.Services.AddSingleton<ISpokenLanguage, StoredSpokenLanguage>();
        // One brain for the app, shared by the chat screen and the job-spec
        // tailoring: loading a model is seconds and hundreds of megabytes.
        builder.Services.AddSingleton<IBrain, DeviceBrain>();
        builder.Services.AddSingleton<ICareerInterview, CareerInterviewHost>();
        builder.Services.AddSingleton<IJobSpecTailor, JobSpecTailor>();
        builder.Services.AddSingleton<IWakeWord, DeviceWakeWord>();
        builder.Services.AddSingleton<ISettings, DeviceSettings>();
        builder.Services.AddSingleton<ISetup, DeviceSetup>();
        builder.Services.AddSingleton<IConversation, DeviceConversation>();
        builder.Services.AddSingleton<IProfile, DeviceProfile>();

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        // Lets the web view be inspected from Chrome's remote devtools, which is
        // the only way to see a Blazor error on a phone: a component that throws
        // during render leaves a BLANK PAGE and writes nothing to logcat.
        builder.Services.AddBlazorWebViewDeveloperTools();

        // NO ILogger PROVIDER IS ADDED HERE, and that is deliberate rather than an
        // omission. AddDebug() reaches nothing on Android - every warning goes
        // invisible while the radios stay audible in logcat - and AddConsole()
        // needs a package this head does not reference, which is how a build that
        // only ever ran in Release compiled a Debug block nobody had tried.
        // Platform logging is what actually arrives; use logcat.
#endif

        return builder.Build();
    }
}
