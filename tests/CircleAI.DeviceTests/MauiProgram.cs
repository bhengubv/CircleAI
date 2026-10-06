using DeviceRunners.VisualRunners;
using Microsoft.Extensions.Logging;

namespace CircleAI.DeviceTests;

/// <summary>Hosts xUnit inside a MAUI app so the Android heads can be tested.</summary>
/// <remarks>
/// AddTestAssembly names THIS assembly, because the tests live here rather than in a
/// separate library: a separate library would itself have to target net10.0-android
/// and would add a project for nothing.
///
/// AddConsoleResultChannel is what makes a run readable over `adb logcat` — without
/// a result channel the outcome exists only on the device's screen, which is the
/// "screenshots are not device testing" problem in a new costume.
/// </remarks>
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder.UseVisualTestRunner(conf => conf
            .AddConsoleResultChannel()
            .AddTestAssembly(typeof(MauiProgram).Assembly)
            .AddXunit());

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
