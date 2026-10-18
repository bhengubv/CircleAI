// FailureClassifier.cs
//
// FROM WHAT ANDROID SAID, AND WHAT WE SAW, TO ONE KIND.
//
// On API 30+ Android records why every process ended (ApplicationExitInfo). On API 29 -
// the P30 - it records nothing, and the old rule turned every death that left a
// breadcrumb into a strike against the model: a reboot, an `adb install -r`, the low-
// memory killer and a native abort all counted the same, and two of them refused the
// only brain the phone could run. This reads every piece of evidence there is, says how
// sure it is, and never upgrades "probably" to "certainly".
//
// PURE, so every row of the table is a unit test on both framework legs. The Android
// side only gathers the facts (ExitEvidence, in the service head).

using System;

namespace CircleAI.Assistant.Lifecycle;

/// <summary>Everything known about how a piece of work ended.</summary>
/// <param name="ApiLevel">The Android API level (0 off Android).</param>
/// <param name="Phase">What was running.</param>
/// <param name="ModelId">The model involved.</param>
/// <param name="At">When the end was noticed.</param>
/// <param name="ExitReason">ApplicationExitInfo REASON_* for the process that died (API 30+).</param>
/// <param name="ExitStatus">The signal, for REASON_SIGNALED; the exit code otherwise.</param>
/// <param name="ExitDescription">Android's own description, when it gave one.</param>
/// <param name="WorkerDiedAlone">The inference process died and the process that noticed survived.</param>
/// <param name="KilledByUs">We ended it ourselves (a watchdog on a hung load or answer).</param>
/// <param name="BootChanged">The phone restarted since the work began.</param>
/// <param name="PackageUpdated">The app was updated since the work began.</param>
/// <param name="MemAvailableFractionAtDeath">The last free-memory reading before the end, as a share of total.</param>
/// <param name="LastTrimLevel">The last onTrimMemory level seen before the end.</param>
/// <param name="CrashBufferShowsSignal">Our own crash log names a fatal signal for that process.</param>
/// <param name="LoadReturnedError">The native load returned an error code (no death).</param>
/// <param name="FilesVerified">The model's files hashed clean afterwards (null: not checked).</param>
/// <param name="ForegroundRefused">Android refused to let a service go foreground.</param>
/// <param name="TimeLimitReached">A platform time budget fired (Service.onTimeout, an ANR).</param>
/// <param name="ManagedException">The managed exception's type name, when one was thrown.</param>
/// <param name="KilledForTest">We ended it on purpose to test recovery (not a watchdog).</param>
public sealed record ExitFacts(
    int ApiLevel,
    WorkPhase Phase,
    string? ModelId,
    DateTimeOffset At,
    int? ExitReason = null,
    int? ExitStatus = null,
    string? ExitDescription = null,
    bool WorkerDiedAlone = false,
    bool? KilledByUs = null,
    bool? BootChanged = null,
    bool? PackageUpdated = null,
    double? MemAvailableFractionAtDeath = null,
    int? LastTrimLevel = null,
    bool? CrashBufferShowsSignal = null,
    bool? LoadReturnedError = null,
    bool? FilesVerified = null,
    bool? ForegroundRefused = null,
    bool? TimeLimitReached = null,
    string? ManagedException = null,
    bool? KilledForTest = null);

/// <summary>Turns <see cref="ExitFacts"/> into a <see cref="Failure"/>.</summary>
public static class FailureClassifier
{
    // android.app.ApplicationExitInfo.REASON_*
    private const int ExitSelf = 1, Signaled = 2, LowMemory = 3, Crash = 4, CrashNative = 5, Anr = 6,
        InitializationFailure = 7, PermissionChange = 8, ExcessiveResourceUsage = 9, UserRequested = 10,
        UserStopped = 11, DependencyDied = 12, Freezer = 14, PackageStateChange = 15, PackageUpdatedReason = 16;

    // ComponentCallbacks2.TRIM_MEMORY_RUNNING_CRITICAL; COMPLETE is 80.
    private const int TrimRunningCritical = 15;

    /// <summary>Classify one ending. Never throws.</summary>
    public static Failure Classify(ExitFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        Failure F(FailureKind kind, Certainty certainty, string evidence, string detail) =>
            new(kind, certainty, facts.Phase, facts.ModelId, evidence, detail, facts.At);

        if (facts.KilledForTest == true)
            return F(FailureKind.FaultInjected, Certainty.Certain, "killed-for-test",
                     "ended on purpose to test recovery");
        if (facts.KilledByUs == true)
            return F(FailureKind.TimedOut, Certainty.Certain, "killed-by-watchdog",
                     "the work stopped making progress and was ended");
        if (facts.TimeLimitReached == true)
            return F(FailureKind.AndroidTimeout, Certainty.Certain, "time-limit",
                     "Android's time limit for this kind of work ran out");
        if (facts.ForegroundRefused == true)
            return F(FailureKind.AndroidStopped, Certainty.Certain, "foreground-refused",
                     "Android would not let the service run in the foreground");

        if (facts.ManagedException is { Length: > 0 } ex)
        {
            if (facts.FilesVerified == false)
                return F(FailureKind.ModelFilesDamaged, Certainty.Certain, "exception=" + ex + " files=bad",
                         "the model's files failed their check");
            return ex switch
            {
                "ModelNotInstalledException" => F(FailureKind.NotInstalled, Certainty.Certain, "exception=" + ex, "the model is not on this device"),
                "OperationCanceledException" or "TaskCanceledException" => F(FailureKind.Cancelled, Certainty.Certain, "exception=" + ex, "the work was cancelled"),
                "TimeoutException" => F(FailureKind.TimedOut, Certainty.Certain, "exception=" + ex, "the work ran out of time"),
                "OutOfMemoryException" => F(FailureKind.OutOfMemory, Certainty.Certain, "exception=" + ex, "the phone ran out of memory"),
                _ => F(FailureKind.ManagedError, Certainty.Certain, "exception=" + ex, "an error in Circle AI's own code"),
            };
        }

        if (facts.LoadReturnedError == true)
            return facts.FilesVerified == false
                ? F(FailureKind.ModelFilesDamaged, Certainty.Certain, "load-error files=bad", "the model's files failed their check")
                // ON THESE PHONES A NATIVE LOAD THAT RETURNS AN ERROR IS ALMOST ALWAYS AN
                // ALLOCATION THAT FAILED - the bridge reports memory and file damage with
                // one code, and the files have just been checked or were never in doubt.
                : F(FailureKind.OutOfMemory, Certainty.Inferred, "load-error files=" + Flag(facts.FilesVerified),
                    "the model could not be loaded, probably for lack of memory");

        var lowMemory = facts.MemAvailableFractionAtDeath is < 0.10 || facts.LastTrimLevel is >= TrimRunningCritical;
        var mem = "memavail=" + (facts.MemAvailableFractionAtDeath is { } m ? m.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "?")
                  + " trim=" + (facts.LastTrimLevel?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?");

        if (facts.ExitReason is { } reason and not 0 and not 13)
        {
            var ev = "exit-reason=" + reason.ToString(System.Globalization.CultureInfo.InvariantCulture);
            switch (reason)
            {
                case LowMemory:
                    return F(FailureKind.OutOfMemory, Certainty.Certain, ev, "Android stopped it to free memory");
                case CrashNative:
                    return F(FailureKind.NativeWorkerCrashed, Certainty.Certain, ev, "the inference engine crashed");
                case Crash:
                case InitializationFailure:
                    return F(FailureKind.ManagedError, Certainty.Certain, ev, "Circle AI's own code crashed");
                case Anr:
                    return F(FailureKind.AndroidTimeout, Certainty.Certain, ev, "Android judged it unresponsive");
                case ExitSelf:
                case PermissionChange:
                case ExcessiveResourceUsage:
                case UserRequested:
                case UserStopped:
                case DependencyDied:
                case Freezer:
                case PackageStateChange:
                case PackageUpdatedReason:
                    return F(FailureKind.AndroidStopped, Certainty.Certain, ev, "Android stopped the service");
                case Signaled:
                {
                    var signal = facts.ExitStatus ?? 0;
                    ev += " signal=" + signal.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (signal is 4 or 5 or 6 or 7 or 8 or 11)
                        return F(FailureKind.NativeWorkerCrashed, Certainty.Certain, ev, "the inference engine crashed");
                    if (signal == 9)
                        return lowMemory
                            ? F(FailureKind.OutOfMemory, Certainty.Inferred, ev + " " + mem, "probably stopped to free memory")
                            : F(FailureKind.AndroidStopped, Certainty.Inferred, ev + " " + mem, "probably stopped by the system");
                    return F(FailureKind.Unknown, Certainty.Inferred, ev, "ended by a signal nothing explains");
                }
            }
        }

        // API 29, OR NO RECORD. Everything below is circumstantial, and says so.
        if (facts.PackageUpdated == true)
            return F(FailureKind.AndroidStopped, Certainty.Inferred, "package-updated", "the app was updated while it ran");
        if (facts.BootChanged == true)
            return F(FailureKind.AndroidStopped, Certainty.Inferred, "boot-changed", "the phone restarted while it ran");
        if (facts.CrashBufferShowsSignal == true)
            return F(FailureKind.NativeWorkerCrashed, Certainty.Certain, "crash-log-signal", "the inference engine crashed");
        if (facts.FilesVerified == false)
            return F(FailureKind.ModelFilesDamaged, Certainty.Certain, "files=bad", "the model's files failed their check");
        if (facts.WorkerDiedAlone && lowMemory)
            return F(FailureKind.OutOfMemory, Certainty.Inferred, "worker-alone " + mem, "probably stopped to free memory");

        return F(FailureKind.Unknown, facts.WorkerDiedAlone ? Certainty.Inferred : Certainty.Unknown,
                 (facts.WorkerDiedAlone ? "worker-alone " : "") + "api" + facts.ApiLevel.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + mem,
                 "it stopped and nothing on this phone says why");
    }

    private static string Flag(bool? value) => value switch { true => "ok", false => "bad", _ => "?" };
}
