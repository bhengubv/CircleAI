// © 2024–2026 The Other Bhengu (Pty) Ltd t/a The Geek Network. MIT-licensed.

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CircleAI.Core.Diagnostics;

/// <summary>
/// Process-wide OpenTelemetry sources for the CircleAI SDK. Every
/// <see cref="CircleAI.Core.Components.CircleAIComponentBase"/> wrapper
/// reports against these instruments, so any host that subscribes to the
/// <see cref="ActivitySourceName"/>/<see cref="MeterName"/> names gets a
/// complete dashboard without writing any per-component plumbing.
///
/// <para>This mirrors <c>Bhengu.Finance.Payments.Core.Observability.BhenguPaymentDiagnostics</c>.</para>
/// </summary>
public static class CircleAIDiagnostics
{
    /// <summary>ActivitySource name for CircleAI SDK spans.</summary>
    public const string ActivitySourceName = "CircleAI";

    /// <summary>Meter name for CircleAI SDK metrics.</summary>
    public const string MeterName = "CircleAI";

    /// <summary>The shared <see cref="ActivitySource"/>. Versioned with the SDK version.</summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.1.0");

    /// <summary>The shared <see cref="Meter"/>. Versioned with the SDK version.</summary>
    public static readonly Meter Meter = new(MeterName, "1.1.0");

    /// <summary>Total operations the SDK has run, tagged with component, operation, and outcome.</summary>
    public static readonly Counter<long> OperationsTotal =
        Meter.CreateCounter<long>(
            "circleai.operations.total",
            unit: "{operation}",
            description: "Total operations executed by CircleAI components.");

    /// <summary>Operation duration histogram, tagged with component, operation, and outcome.</summary>
    public static readonly Histogram<double> OperationDurationMs =
        Meter.CreateHistogram<double>(
            "circleai.operation.duration",
            unit: "ms",
            description: "Duration of CircleAI component operations in milliseconds.");

    /// <summary>Total signals observed by ISecurityWatchdog implementations, tagged with vector + confidence band.</summary>
    public static readonly Counter<long> AnomalySignalsTotal =
        Meter.CreateCounter<long>(
            "circleai.anomaly.signals.total",
            unit: "{signal}",
            description: "Total AnomalySignal instances observed by CircleAI watchdogs.");

    /// <summary>Total inference requests dispatched, tagged with bridge implementation + model id + outcome.</summary>
    public static readonly Counter<long> InferenceRequestsTotal =
        Meter.CreateCounter<long>(
            "circleai.inference.requests.total",
            unit: "{request}",
            description: "Total inference requests dispatched by CircleAI inference bridges.");

    /// <summary>Canonical outcome strings — keep in sync across all components.</summary>
    public static class Outcomes
    {
        /// <summary>Operation completed normally.</summary>
        public const string Success     = "success";
        /// <summary>Operation was cancelled by the caller (CancellationToken honoured).</summary>
        public const string Cancelled   = "cancelled";
        /// <summary>Operation failed because an external dependency was unavailable.</summary>
        public const string Unavailable = "unavailable";
        /// <summary>Operation failed because of a rate-limit response from an external dependency.</summary>
        public const string RateLimited = "rate_limited";
        /// <summary>Operation failed because of unverified input (signature, schema, scope).</summary>
        public const string Invalid     = "invalid";
        /// <summary>Catch-all for any other failure.</summary>
        public const string Error       = "error";
    }

    /// <summary>Open an activity for a CircleAI component operation.</summary>
    /// <param name="componentName">Canonical component name (e.g. "JsonPersonaProvider").</param>
    /// <param name="operationName">Logical operation name (e.g. "GetAsync", "SaveAsync").</param>
    public static Activity? StartOperationActivity(string componentName, string operationName)
    {
        var activity = ActivitySource.StartActivity(operationName, ActivityKind.Internal);
        activity?.SetTag("circleai.component", componentName);
        activity?.SetTag("circleai.operation", operationName);
        return activity;
    }

    /// <summary>Stamp the activity (if any) with the canonical outcome tag.</summary>
    public static void SetOutcome(this Activity? activity, string outcome)
    {
        if (activity is null) return;
        activity.SetTag("circleai.outcome", outcome);
        activity.SetStatus(outcome == Outcomes.Success
            ? ActivityStatusCode.Ok
            : ActivityStatusCode.Error);
    }
}
