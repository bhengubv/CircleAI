// CircleLog.cs
//
// THE ONE PLACE A LOG LINE GOES, in every process and on every head.
//
// Until now Android had no logging framework at all: the service called
// Android.Util.Log directly, library code wrote to four Action<string> sinks
// (VoiceTrace.Sink, SelfHeal.Say, ModelCosts.Say, VoiceTurnRouter.Trace), and ILogger,
// used in some seventy files, wrote nowhere - AddDebug reaches nothing on Android
// (memory decision 241f13cd). Wolverine cannot heal what nobody can read, and the
// standing ruling is to set up verbose error logs for it to consume (784a4cdf).
//
// A host registers sinks - logcat on Android, a rolling file everywhere - and every
// line fans out to all of them. A sink that throws loses that line, never the process.
// Failures also become HealthEvents, which is what healing consumes.
//
// VERBOSE BY DEFAULT where healing reads: Engine, Context, Models, Heal, and the
// inference, self-knowledge and self-healing namespaces. Everything else starts at
// Info. Levels are per area and match by prefix, so "Engine" covers "Engine.Kernels".
//
// What a person said or was told never reaches a sink as text: see Sensitive.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CircleAI.Core.Logging;

/// <summary>How much a line matters. <see cref="Off"/> silences an area.</summary>
public enum CircleLogLevel
{
    /// <summary>Everything, for diagnosis.</summary>
    Verbose = 0,

    /// <summary>Detail a developer reads.</summary>
    Debug = 1,

    /// <summary>What happened, in normal running.</summary>
    Info = 2,

    /// <summary>Something went wrong and was coped with.</summary>
    Warn = 3,

    /// <summary>Something went wrong and was not.</summary>
    Error = 4,

    /// <summary>Nothing at all.</summary>
    Off = 5,
}

/// <summary>One log line on its way to the sinks.</summary>
/// <param name="At">When it was written (UTC).</param>
/// <param name="Level">How much it matters.</param>
/// <param name="Area">Where it came from: a short area ("Engine") or a type name.</param>
/// <param name="Message">The line itself.</param>
/// <param name="Exception">The failure behind it, if any.</param>
public readonly record struct CircleLogLine(DateTimeOffset At, CircleLogLevel Level, string Area, string Message, Exception? Exception);

/// <summary>Somewhere log lines go: logcat, a file, a test's list.</summary>
public interface ICircleLogSink
{
    /// <summary>Writes one line. May throw; <see cref="CircleLog"/> contains it.</summary>
    void Write(in CircleLogLine line);
}

/// <summary>The log hub every process writes through.</summary>
public static class CircleLog
{
    private static readonly object Gate = new();
    private static ICircleLogSink[] _sinks = [];
    private static int _crashHooks;

    private static readonly ConcurrentDictionary<string, CircleLogLevel> Levels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Engine"] = CircleLogLevel.Verbose,
        ["Context"] = CircleLogLevel.Verbose,
        ["Models"] = CircleLogLevel.Verbose,
        ["Heal"] = CircleLogLevel.Verbose,
        ["CircleAI.Inference"] = CircleLogLevel.Verbose,
        ["CircleAI.SelfKnowledge"] = CircleLogLevel.Verbose,
        ["CircleAI.Hosting.SelfHealing"] = CircleLogLevel.Verbose,
    };

    /// <summary>The level for any area no prefix rule names.</summary>
    public static CircleLogLevel DefaultLevel { get; set; } = CircleLogLevel.Info;

    /// <summary>Adds a sink; dispose the result to remove it again.</summary>
    public static IDisposable AddSink(ICircleLogSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (Gate) _sinks = [.. _sinks, sink];
        return new Removal(sink);
    }

    /// <summary>Sets the level for an area and everything under it.</summary>
    public static void SetLevel(string area, CircleLogLevel level) => Levels[area] = level;

    /// <summary>
    /// Applies a list such as <c>Engine=Verbose,Voice=Debug</c>, from a setting or a test
    /// switch. Words that are not an area and a level are skipped; returns how many applied.
    /// </summary>
    public static int ApplyLevels(string? spec)
    {
        var applied = 0;
        foreach (var part in (spec ?? string.Empty).Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length == 2 && pair[0].Length > 0 && Enum.TryParse<CircleLogLevel>(pair[1], ignoreCase: true, out var level))
            {
                SetLevel(pair[0], level);
                applied++;
            }
        }
        return applied;
    }

    /// <summary>The level that governs <paramref name="area"/>: the longest matching prefix rule, else the default.</summary>
    public static CircleLogLevel LevelFor(string area)
    {
        CircleLogLevel? best = null;
        var bestLength = -1;
        foreach (var (key, value) in Levels)
        {
            if (key.Length <= bestLength) continue;
            if (area.Equals(key, StringComparison.OrdinalIgnoreCase)
                || (area.Length > key.Length && area[key.Length] == '.' && area.StartsWith(key, StringComparison.OrdinalIgnoreCase)))
            {
                best = value;
                bestLength = key.Length;
            }
        }
        return best ?? DefaultLevel;
    }

    /// <summary>Whether a line at <paramref name="level"/> from <paramref name="area"/> would be written.</summary>
    public static bool IsEnabled(string area, CircleLogLevel level) => level != CircleLogLevel.Off && level >= LevelFor(area);

    /// <summary>Writes a line to every sink, if its area lets it through.</summary>
    public static void Write(CircleLogLevel level, string area, string message, Exception? exception = null)
    {
        if (!IsEnabled(area, level)) return;
        var line = new CircleLogLine(DateTimeOffset.UtcNow, level, area, message, exception);
        foreach (var sink in Volatile.Read(ref _sinks))
        {
            try { sink.Write(line); }
            catch { /* A broken sink loses this line; the caller must not lose its work over it. */ }
        }
    }

    /// <summary>Writes at <see cref="CircleLogLevel.Verbose"/>.</summary>
    public static void Verbose(string area, string message) => Write(CircleLogLevel.Verbose, area, message);

    /// <summary>Writes at <see cref="CircleLogLevel.Debug"/>.</summary>
    public static void Debug(string area, string message) => Write(CircleLogLevel.Debug, area, message);

    /// <summary>Writes at <see cref="CircleLogLevel.Info"/>.</summary>
    public static void Info(string area, string message) => Write(CircleLogLevel.Info, area, message);

    /// <summary>Writes at <see cref="CircleLogLevel.Warn"/>.</summary>
    public static void Warn(string area, string message, Exception? exception = null) => Write(CircleLogLevel.Warn, area, message, exception);

    /// <summary>Writes at <see cref="CircleLogLevel.Error"/>.</summary>
    public static void Error(string area, string message, Exception? exception = null) => Write(CircleLogLevel.Error, area, message, exception);

    /// <summary>An <see cref="ILogger"/> that writes through this hub, for code that takes one.</summary>
    public static ILogger Logger(string area) => new CircleLoggerProvider.CircleLogger(area);

    /// <summary>An <see cref="ILogger{T}"/> that writes through this hub under the type's full name.</summary>
    public static ILogger<T> LoggerFor<T>() => new CircleLoggerProvider.CircleLogger<T>();

    /// <summary>
    /// Catches what nothing else catches, once per process: an unhandled exception and an
    /// unobserved task fault are written as errors and raised as HealthEvents. A stack
    /// overflow still cannot be caught; DeviceDiagnostics' breadcrumbs exist for that.
    /// </summary>
    public static void InstallCrashHooks()
    {
        if (Interlocked.Exchange(ref _crashHooks, 1) == 1) return;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) ReportUnhandled(ex, e.IsTerminating ? "unhandled, terminating" : "unhandled");
        };
        TaskScheduler.UnobservedTaskException += (_, e) => ReportUnhandled(e.Exception, "unobserved task");
    }

    /// <summary>Records a failure nothing handled: one error line with its stack, and one HealthEvent.</summary>
    public static HealthEvent ReportUnhandled(Exception exception, string how)
    {
        var health = HealthEvents.From("Crash", how, exception);
        Error("Crash", $"{how}: {health.Fingerprint}", exception);
        HealthEvents.Raise(health);
        return health;
    }

    private sealed class Removal(ICircleLogSink sink) : IDisposable
    {
        public void Dispose()
        {
            lock (Gate) _sinks = _sinks.Where(s => !ReferenceEquals(s, sink)).ToArray();
        }
    }
}
