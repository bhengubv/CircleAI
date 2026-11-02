#nullable enable

// RiskySteps.cs
//
// A MARKER BEFORE EVERY STEP THAT CAN DIE WITHOUT AN EXCEPTION (S6).
//
// An illegal instruction, a SIGSEGV in unsafe code, the low-memory killer: none of them runs a
// catch block, so none of them can become a HealthEvent in the process that died. What CAN
// survive is a line written to disk before the step and deleted after it - DeviceDiagnostics.
// BeginRisky, which already does the hard part (one slot, flushed past the OS buffer, never
// throws). A marker still there at the next look is the fault, and it says what was being done
// and with which engine knobs in force - which is what a remedy needs to choose.
//
// ONE SLOT PER PROCESS, IN A FOLDER OF ITS OWN. The supervisor already keeps its load
// breadcrumb in the state folder's one slot (CrashVerdict reads it); writing a second step into
// that slot would overwrite the model it names. So each process that runs risky steps gets
// {state}/risky/{process}, and the main process reads the inference process's slot when the
// inference process dies.

using System;
using System.IO;

namespace CircleAI.Assistant;

/// <summary>Markers written before steps that can end the process without an exception.</summary>
public static class RiskySteps
{
    /// <summary>
    /// Where this process keeps its marker: one folder per process. Null - the default, and
    /// every desktop test - writes nothing.
    /// </summary>
    public static string? Directory { get; set; }

    /// <summary>The marker folder for a process, under a state folder.</summary>
    public static string For(string stateDirectory, string process) =>
        Path.Combine(stateDirectory, "risky", process);

    /// <summary>
    /// Writes <paramref name="what"/> down before a risky step; dispose the result when the step
    /// survived. Never throws, and costs nothing when no folder is set.
    /// </summary>
    /// <param name="what">The step and the knobs in force - code-authored, never what a person said.</param>
    public static IDisposable Begin(string what)
    {
        var dir = Directory;
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(what)) return Survived.None;
        DeviceDiagnostics.BeginRisky(dir!, what);
        return new Survived(dir!);
    }

    /// <summary>What a process died inside, from its folder; null when it ended every step it began.</summary>
    public static string? Leftover(string directory) => DeviceDiagnostics.PreviousCrash(directory);

    /// <summary>Forgets a leftover once it has been read and reported.</summary>
    public static void Clear(string directory) => DeviceDiagnostics.EndRisky(directory);

    private sealed class Survived(string? directory) : IDisposable
    {
        public static readonly Survived None = new(null);

        public void Dispose()
        {
            if (directory is not null) DeviceDiagnostics.EndRisky(directory);
        }
    }
}
