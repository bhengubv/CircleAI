// NullImplementations.cs
//
// (2.6.0) Fail-closed defaults — when there is no real backend wired we
// treat content as refused (safest default).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.ContentPolicy;

public sealed class NullContentFilter : IContentFilter
{
    public static readonly NullContentFilter Instance = new();
    public string BackendId => "null";
    public ValueTask<SafetyFinding> ClassifyAsync(string text, CancellationToken ct = default)
        => ValueTask.FromResult(new SafetyFinding(
            Verdict:    SafetyVerdict.Refuse,
            Category:   "no-filter-configured",
            Reason:     "Fail-closed default — wire a real IContentFilter to relax.",
            Confidence: 1f));
}

public sealed class NullRefusalPolicy : IRefusalPolicy
{
    public static readonly NullRefusalPolicy Instance = new();
    public string BackendId => "null";
    public ValueTask<bool> ShouldRefuseAsync(IReadOnlyList<SafetyFinding> findings, CancellationToken ct = default)
        => ValueTask.FromResult(true);
}

public sealed class NullPromptInjectionDetector : IPromptInjectionDetector
{
    public static readonly NullPromptInjectionDetector Instance = new();
    public string BackendId => "null";
    public ValueTask<SafetyFinding> InspectAsync(string content, string source, CancellationToken ct = default)
        => ValueTask.FromResult(new SafetyFinding(
            Verdict:    SafetyVerdict.Refuse,
            Category:   "no-detector-configured",
            Reason:     "Fail-closed default.",
            Confidence: 1f));
}

public sealed class NullSafetyAuditLog : ISafetyAuditLog
{
    public static readonly NullSafetyAuditLog Instance = new();
    public string BackendId => "null";
    public ValueTask LogAsync(SafetyAuditEntry entry, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<IReadOnlyList<SafetyAuditEntry>> ReadAsync(string? userId, int limit = 100, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<SafetyAuditEntry>>(Array.Empty<SafetyAuditEntry>());
}
