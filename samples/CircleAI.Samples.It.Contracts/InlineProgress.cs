// InlineProgress.cs
//
// An IProgress that reports on the thread that called it.
//
// WHY THIS EXISTS AND HAS A NAME. System.Progress<T> does NOT run its callback
// inline: it captures the SynchronizationContext at construction and posts to
// it, so the callback runs later, on somebody else's thread. That is exactly
// right for updating a screen and exactly wrong for reading a value back, and
// the difference has cost three separate bugs in one night:
//
//   · a cancel that landed after the fake's delay, so a test failed at 15 s and
//     was "fixed" three times by raising the timeout
//   · the meeting transcript rendering twice, because the session's last report
//     arrived after the code that cleared the partial line
//   · a turn's own "did it hear anything" flag read before the report that set it
//
// Every one of them looked like a race in the feature and was the same property
// of Progress<T>. Reaching for this type is the decision to read a report
// synchronously; reaching for Progress<T> is the decision to draw something.
//
// It does no marshalling of its own on purpose. A caller that needs the UI
// thread still has to ask for it - which is the honest shape, because the
// alternative is a type that quietly does both and is wrong for one of them.

using System;

namespace CircleAI.Samples.It;

/// <summary>Reports synchronously, on the caller's thread.</summary>
/// <param name="report">
/// Runs on whatever thread called <see cref="Report"/> - an audio loop, a
/// background service - so it must be safe there and must not block.
/// </param>
public sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _report;

    public InlineProgress(Action<T> report) =>
        _report = report ?? throw new ArgumentNullException(nameof(report));

    /// <inheritdoc />
    public void Report(T value) => _report(value);
}
