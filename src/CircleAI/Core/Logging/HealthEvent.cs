// HealthEvent.cs
//
// A failure the way Wolverine reads it: what was being attempted, what went wrong and
// where, the device's context at that moment, what was done about it, and whether that
// worked. The fingerprint - CircleAI:<ErrorType>:<Site> - has the same shape as
// WolverineAPI's ApiName:ErrorType:Endpoint, so the phone and the server de-duplicate
// the same failure the same way.
//
// No field carries what a person said. The message is reduced to a template - numbers,
// hex, GUIDs, quoted text and paths replaced - and callers pass their context through
// Sensitive.

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CircleAI.Core.Logging;

/// <summary>One failure, as healing consumes it.</summary>
/// <param name="At">When it happened (UTC).</param>
/// <param name="Area">The part of the product it happened in.</param>
/// <param name="Attempted">What was being attempted.</param>
/// <param name="ErrorType">The exception's type name.</param>
/// <param name="Template">The message with its variable parts replaced.</param>
/// <param name="Site">The first CircleAI frame it was thrown through, as Type.Method.</param>
/// <param name="Fingerprint">CircleAI:ErrorType:Site - the de-duplication key.</param>
/// <param name="Context">The device context at that moment, if the caller had it.</param>
/// <param name="Remedy">What was done about it, once something was.</param>
/// <param name="Worked">Whether the remedy worked, once that is known.</param>
public sealed record HealthEvent(
    DateTimeOffset At,
    string Area,
    string Attempted,
    string ErrorType,
    string Template,
    string Site,
    string Fingerprint,
    string? Context = null,
    string? Remedy = null,
    bool? Worked = null);

/// <summary>Makes, keeps and announces <see cref="HealthEvent"/>s.</summary>
public static partial class HealthEvents
{
    private const int Keep = 50;
    private const int TemplateCap = 160;
    private static readonly object Gate = new();
    private static readonly Queue<HealthEvent> Ring = new();

    /// <summary>Raised for every event; healing subscribes here.</summary>
    public static event Action<HealthEvent>? Raised;

    /// <summary>The most recent events, newest first.</summary>
    public static IReadOnlyList<HealthEvent> Recent
    {
        get { lock (Gate) return Ring.Reverse().ToList(); }
    }

    /// <summary>Builds an event from an exception.</summary>
    public static HealthEvent From(string area, string attempted, Exception exception, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var type = exception.GetType().Name;
        var site = SiteOf(exception);
        return new HealthEvent(DateTimeOffset.UtcNow, area, attempted, type, Template(exception.Message), site, Fingerprint(type, site), context);
    }

    /// <summary>The de-duplication key, in WolverineAPI's shape.</summary>
    public static string Fingerprint(string errorType, string site) => $"CircleAI:{errorType}:{site}";

    /// <summary>A message with its variable parts replaced, so one failure reads the same every time.</summary>
    public static string Template(string? message)
    {
        var t = message ?? string.Empty;
        t = Quoted().Replace(t, "'…'");
        t = Paths().Replace(t, "<path>");
        t = Guids().Replace(t, "{guid}");
        t = Hex().Replace(t, "0x#");
        t = Numbers().Replace(t, "#");
        return t.Length <= TemplateCap ? t : t[..TemplateCap] + "…";
    }

    /// <summary>The first frame in CircleAI code, as Type.Method; else the first frame; else "unknown".</summary>
    public static string SiteOf(Exception exception)
    {
        string? first = null;
        foreach (var frame in new StackTrace(exception, fNeedFileInfo: false).GetFrames())
        {
            var method = frame.GetMethod();
            var type = method?.DeclaringType;
            if (method is null || type is null) continue;
            var name = $"{type.Name}.{method.Name}";
            first ??= name;
            if (type.FullName?.StartsWith("CircleAI", StringComparison.Ordinal) == true) return name;
        }
        return first ?? "unknown";
    }

    /// <summary>Keeps the event, writes it to the log, and tells every subscriber.</summary>
    public static void Raise(HealthEvent health)
    {
        ArgumentNullException.ThrowIfNull(health);
        lock (Gate)
        {
            Ring.Enqueue(health);
            while (Ring.Count > Keep) Ring.Dequeue();
        }

        CircleLog.Warn("Heal",
            $"HEALTH {health.Fingerprint} area={health.Area} attempted={health.Attempted} " +
            $"remedy={health.Remedy ?? "-"} worked={(health.Worked is { } w ? (w ? "yes" : "no") : "-")} " +
            $"message={health.Template}");

        foreach (var handler in Raised?.GetInvocationList() ?? [])
        {
            try { ((Action<HealthEvent>)handler)(health); }
            catch (Exception ex) { CircleLog.Warn("Heal", "a health subscriber threw", ex); }
        }
    }

    [GeneratedRegex("'[^']*'|\"[^\"]*\"")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"(?:[A-Za-z]:\\|/)[^\s:,;]+")]
    private static partial Regex Paths();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex Guids();

    [GeneratedRegex(@"\b0x[0-9a-fA-F]+\b")]
    private static partial Regex Hex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Numbers();
}
