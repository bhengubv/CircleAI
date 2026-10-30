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
        StartKernelBench();
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

    /// <summary>
    /// THE C# ENGINE'S KERNELS ARE TIMED ON EVERY LAUNCH (S0.2), on a thread of their
    /// own, and logged under CircleAI.Bench. A phone runs .NET on Mono, which accelerates
    /// only part of the vector API and runs the rest as plain code without a word, so
    /// which kernel path is fastest is a measurement, not a reading of IsSupported.
    /// It takes a few seconds and finishes before anybody can start a test run.
    /// </summary>
    /// <remarks>
    /// Started here rather than from an Application.OnCreate override: a new override
    /// needs a JNI registration that a Debug build did not make, and the app died on
    /// launch with UnsatisfiedLinkError (n_onCreate).
    /// </remarks>
    private static void StartKernelBench() =>
        new Thread(() =>
        {
            try
            {
                foreach (var line in CircleAI.Inference.Managed.Kernels.KernelBench.Report())
                    global::Android.Util.Log.Info("CircleAI.Bench", line);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("CircleAI.Bench", ex.ToString());
            }
        }) { IsBackground = true, Name = "kernel-bench" }.Start();
}
