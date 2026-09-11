#nullable enable

// DeviceDiagnostics.cs
//
// What to do about failures on a phone you cannot attach a debugger to.
//
// StackOverflowException is the honest starting point: it CANNOT be caught. Since
// .NET 2.0 the runtime tears the process down the moment the stack is exhausted —
// no catch block runs, no finally runs, nothing is written on the way out. Any
// "stack overflow handler" is theatre. So the four things that genuinely help are:
//
//   1. do not cause it       — bound input and recursion before they get deep
//   2. survive it            — leave a breadcrumb BEFORE risky work, so the next
//                              launch can say what killed the previous one
//   3. read the wreckage     — a concise summary on screen, full detail in a file
//   4. contain it            — one language's failure must not end the run
//
// Everything except a stack overflow IS catchable, including OutOfMemory, and on
// a 3.6 GB phone loading 110 MB models back to back, OOM is the likelier death.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CircleAI.Samples.It;

/// <summary>On-device failure reporting for a machine with no debugger attached.</summary>
public static class DeviceDiagnostics
{
    private const string BreadcrumbFile = "in-flight.txt";

    /// <summary>
    /// Deepest call chain we allow before refusing. Set well below the real limit:
    /// the point is to fail with a message while a message is still possible.
    /// </summary>
    public const int MaxRecursionDepth = 64;

    /// <summary>
    /// Longest text a single synthesis call may accept. A VITS voice allocates
    /// per-token; an unbounded paragraph is how a phone runs out of memory or
    /// stack. Callers should be splitting into sentences long before this.
    /// </summary>
    public const int MaxSynthesisChars = 5_000;

    /// <summary>
    /// Records what is about to be attempted, so an UNCATCHABLE death (stack
    /// overflow, OOM kill, SIGSEGV in a native runtime) is still diagnosable.
    /// </summary>
    /// <remarks>
    /// This is the only thing that works against a crash that runs no handler:
    /// write the intention down first, and check for it on the next start. Flushed
    /// immediately — a buffered write dies with the process and tells us nothing.
    /// </remarks>
    public static void BeginRisky(string stateDir, string what)
    {
        try
        {
            Directory.CreateDirectory(stateDir);
            using var fs = new FileStream(
                Path.Combine(stateDir, BreadcrumbFile), FileMode.Create, FileAccess.Write, FileShare.Read);
            using var w = new StreamWriter(fs);
            w.Write(what);
            w.Flush();
            fs.Flush(true);          // to disk, not just to the OS buffer
        }
        catch { /* diagnostics must never be the thing that fails */ }
    }

    /// <summary>Clears the breadcrumb after the risky work survived.</summary>
    public static void EndRisky(string stateDir)
    {
        try { File.Delete(Path.Combine(stateDir, BreadcrumbFile)); }
        catch { }
    }

    /// <summary>
    /// What the previous run died inside, or null if it exited cleanly. Non-null
    /// means the process was killed without running a single handler.
    /// </summary>
    public static string? PreviousCrash(string stateDir)
    {
        try
        {
            var p = Path.Combine(stateDir, BreadcrumbFile);
            if (!File.Exists(p)) return null;
            var what = File.ReadAllText(p).Trim();
            return what.Length == 0 ? null : what;
        }
        catch { return null; }
    }

    /// <summary>
    /// A short, readable account of a failure — for a screen, not a log file.
    /// </summary>
    /// <remarks>
    /// The probe used to print the exception verbatim, which on a phone is a
    /// full-screen wall of runtime frames that reads like a crash even when the
    /// app has handled it perfectly well. The cause and the frames from OUR code
    /// are what a person needs; forty frames of runtime plumbing are not.
    /// </remarks>
    public static string Summarise(Exception ex, int appFrames = 3)
    {
        var sb = new StringBuilder();

        var root = ex;
        while (root.InnerException is not null) root = root.InnerException;

        sb.Append(Explain(root));
        if (!ReferenceEquals(root, ex))
            sb.Append($"  (surfaced as {ex.GetType().Name})");
        sb.Append('\n');

        var shown = 0;
        foreach (var line in (root.StackTrace ?? "").Split('\n'))
        {
            if (shown >= appFrames) break;
            // Frames from our own assemblies are the ones worth reading.
            if (line.Contains("CircleAI", StringComparison.Ordinal))
            {
                sb.Append("   at ").Append(line.Trim().TrimStart('a', 't', ' ')).Append('\n');
                shown++;
            }
        }
        if (shown == 0 && root.StackTrace is { Length: > 0 } st)
            sb.Append("   ").Append(st.Split('\n')[0].Trim()).Append('\n');

        return sb.ToString();
    }

    /// <summary>Plain-language cause, where the exception type has a known meaning here.</summary>
    private static string Explain(Exception ex) => ex switch
    {
        OutOfMemoryException =>
            "OUT OF MEMORY — the model did not fit. These voices are ~110 MB each and the "
            + "phone has ~1.5 GB free; release the previous one before loading the next.",

        UnauthorizedAccessException =>
            "PERMISSION DENIED — Android 10+ scoped storage. The app may read its own "
            + $"external files dir and nothing else on /sdcard. ({ex.Message})",

        FileNotFoundException fnf =>
            $"MISSING FILE — {Path.GetFileName(fnf.FileName ?? "?")}. A voice needs its model, "
            + "its .onnx.json sidecar, and (for lexicon voices) lexicon.txt.",

        DllNotFoundException =>
            "NATIVE LIBRARY MISSING — the ONNX Runtime or espeak bridge is not present in "
            + "this build. Check the APK was built with -p:ItVoiceOnAndroid=true.",

        InvalidDataException =>
            $"MALFORMED ASSET — {ex.Message}",

        _ => $"{ex.GetType().Name}: {ex.Message}"
    };

    /// <summary>
    /// Rejects input too large to synthesise safely, with a reason rather than a
    /// crash. Deep recursion and huge allocations are the two ways a phone dies
    /// without a catchable exception, and both start with unbounded input.
    /// </summary>
    public static bool TooLargeToSynthesise(string? text, out string reason)
    {
        var n = text?.Length ?? 0;
        if (n > MaxSynthesisChars)
        {
            reason = $"text is {n:N0} characters; the limit is {MaxSynthesisChars:N0}. "
                   + "Split into sentences before synthesising.";
            return true;
        }
        reason = "";
        return false;
    }

    /// <summary>
    /// One directory where every diagnostic lands, set once at startup.
    /// </summary>
    /// <remarks>
    /// Without this, detail files were written beside whichever asset failed — so
    /// across 53 voices in several directories they scattered, and each new failure
    /// overwrote the one that happened to share its folder. A diagnostic you have
    /// to search for is not much of a diagnostic.
    /// </remarks>
    public static string? DiagnosticsDirectory { get; set; }

    /// <summary>
    /// Full detail for pulling over adb, kept OUT of the on-screen report.
    /// </summary>
    /// <remarks>
    /// Named per failure rather than a single "last-error", so a sweep that loses
    /// three languages keeps all three reports instead of only the last.
    /// </remarks>
    public static void WriteDetail(string fallbackDir, string label, Exception ex)
    {
        try
        {
            var dir = DiagnosticsDirectory ?? fallbackDir;
            Directory.CreateDirectory(dir);

            var safe = new StringBuilder(label.Length);
            foreach (var c in label)
                safe.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-');

            File.WriteAllText(
                Path.Combine(dir, $"error-{safe}.txt"),
                $"{label}\n{DateTimeOffset.Now:O}\n\n{ex}\n");
        }
        catch { }
    }
}
