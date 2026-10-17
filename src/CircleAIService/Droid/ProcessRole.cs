// ProcessRole.cs
//
// WHICH PROCESS THIS IS, BECAUSE APPLICATION.ONCREATE RUNS IN BOTH.
//
// Android starts the Application class in every process of the package, so the moment
// the model moved into ":inference", ServiceApplication.OnCreate began running there too
// - and everything it does at start (the grant store, the memory store, the voice, the
// wake word, the catalogue bootstrap, the download resume) would have run twice, on one
// set of files, from two processes. The inference process sets up the brain's options
// and nothing else.

using Android.App;

namespace CircleAI.Service.Droid;

/// <summary>Tells the main process from the inference process.</summary>
public static class ProcessRole
{
    /// <summary>The running process's name, e.g. "com.bhengubv.circleai.service:inference".</summary>
    public static string CurrentProcessName()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            try
            {
                var name = Application.ProcessName;
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch { /* fall through to the kernel's answer */ }
        }

        try
        {
            var raw = File.ReadAllText("/proc/self/cmdline");
            var end = raw.IndexOf('\0');
            return end >= 0 ? raw[..end] : raw.Trim();
        }
        catch { return string.Empty; }
    }

    /// <summary>Whether this is the ":inference" process.</summary>
    public static bool IsInferenceProcess() =>
        CurrentProcessName().EndsWith(":inference", StringComparison.Ordinal);
}
