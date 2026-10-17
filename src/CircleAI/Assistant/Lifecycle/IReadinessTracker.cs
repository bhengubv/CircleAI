// IReadinessTracker.cs
//
// ONE WRITER OF THE SERVICE'S STATE, MANY READERS. The brain host, the worker
// supervisor, the download coordinator and the wake listener each report what they
// are doing here; the link's Status verb, the desktop's /v1/status, the setup
// readiness row and the questions waiting in the queue all read it back. Nothing else
// is allowed to decide what "ready" means.

using System;
using System.Collections.Generic;

namespace CircleAI.Assistant.Lifecycle;

/// <summary>Collects component states and failures into one sequenced <see cref="ReadinessSnapshot"/>.</summary>
public interface IReadinessTracker
{
    /// <summary>The current snapshot. Never null.</summary>
    ReadinessSnapshot Current { get; }

    /// <summary>The current sequence number. Increases only when something changed.</summary>
    long Seq { get; }

    /// <summary>A new value for every process start.</summary>
    string BootId { get; }

    /// <summary>Report a component's state. A report identical to the current one changes nothing.</summary>
    void Report(ComponentState state);

    /// <summary>Record a classified failure (kept, newest first, a few at a time).</summary>
    void Failed(Failure failure);

    /// <summary>Set one supported-setting key. A value identical to the current one changes nothing.</summary>
    void Setting(string key, string value);

    /// <summary>Raised after every change, with the new snapshot. Handlers must not throw or block.</summary>
    event EventHandler<ReadinessSnapshot>? Changed;
}
