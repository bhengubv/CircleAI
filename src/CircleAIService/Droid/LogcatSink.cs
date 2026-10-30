// LogcatSink.cs
//
// CircleLog's lines in logcat, under "CircleAI.<area>" tags - the tags this service
// already wrote by hand (CircleAI.Heal, CircleAI.Memory, CircleAI.Voice), so existing
// filters keep working. An exception goes out with its stack, which is what a person
// reading `adb logcat --pid` needs.

using Android.Util;
using CircleAI.Core.Logging;

namespace CircleAI.Service.Droid;

/// <summary>Writes <see cref="CircleLog"/> lines to logcat.</summary>
public sealed class LogcatSink : ICircleLogSink
{
    /// <inheritdoc />
    public void Write(in CircleLogLine line)
    {
        var tag = Tag(line.Area);
        var text = line.Exception is null ? line.Message : line.Message + "\n" + line.Exception;
        switch (line.Level)
        {
            case CircleLogLevel.Verbose: Log.Verbose(tag, text); break;
            case CircleLogLevel.Debug: Log.Debug(tag, text); break;
            case CircleLogLevel.Info: Log.Info(tag, text); break;
            case CircleLogLevel.Warn: Log.Warn(tag, text); break;
            case CircleLogLevel.Error: Log.Error(tag, text); break;
        }
    }

    // A type name as an area becomes its last segment: CircleAI.Hosting.AIService -> AIService.
    private static string Tag(string area)
    {
        var dot = area.LastIndexOf('.');
        var shortArea = area.StartsWith("CircleAI.", StringComparison.Ordinal) && dot >= 0 ? area[(dot + 1)..] : area;
        var tag = "CircleAI." + shortArea;
        return tag.Length <= 64 ? tag : tag[..64];
    }
}
