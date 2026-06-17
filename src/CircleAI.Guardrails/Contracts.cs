// Contracts.cs
//
// (2.6.0) Safety-guardrails contracts (Sponsio pattern-adoption).
// Namespace `CircleAI.Guardrails` to avoid collision with the personal-
// safety domain pack `CircleAI.Safety`.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Guardrails;

public enum SafetyVerdict { Allow, Flag, Refuse }

public sealed record SafetyFinding(SafetyVerdict Verdict, string Category, string Reason, float Confidence);

/// <summary>(2.6.0) Per-token / per-message content filter.</summary>
public interface IContentFilter
{
    string BackendId { get; }

    ValueTask<SafetyFinding> ClassifyAsync(string text, CancellationToken ct = default);
}

/// <summary>(2.6.0) Refusal policy — decides whether a finding becomes a refusal.</summary>
public interface IRefusalPolicy
{
    string BackendId { get; }

    ValueTask<bool> ShouldRefuseAsync(
        IReadOnlyList<SafetyFinding> findings,
        CancellationToken            ct = default);
}

/// <summary>(2.6.0) Prompt-injection detector — catches second-order attacks (RAG/web/tool output).</summary>
public interface IPromptInjectionDetector
{
    string BackendId { get; }

    ValueTask<SafetyFinding> InspectAsync(
        string            untrustedContent,
        string            sourceLabel,
        CancellationToken ct = default);
}

public sealed record SafetyAuditEntry(
    DateTimeOffset AtUtc,
    string         UserId,
    string         Action,
    SafetyVerdict  Verdict,
    string         Reason);

/// <summary>(2.6.0) Append-only safety audit log.</summary>
public interface ISafetyAuditLog
{
    string BackendId { get; }

    ValueTask LogAsync(SafetyAuditEntry entry, CancellationToken ct = default);
    ValueTask<IReadOnlyList<SafetyAuditEntry>> ReadAsync(string? userId, int limit = 100, CancellationToken ct = default);
}
