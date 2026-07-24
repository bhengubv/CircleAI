// ThreatIndicator.cs
//
// A normalized (and, for identities, already-hashed) key used to look an indicator
// up in the local corpus. Subjects (FileArtifact / NetworkIndicator /
// IdentityIndicator) are turned into one of these by the assessors before lookup.

namespace CircleAI.Security.Antibodies.Awareness;

/// <summary>
/// A normalized indicator ready for corpus lookup: a <see cref="IndicatorKind"/>
/// plus the canonical string key for that kind. For identity kinds the
/// <see cref="Value"/> is a SHA-256 hex digest, never the raw identity.
/// </summary>
/// <param name="Kind">The kind of indicator.</param>
/// <param name="Value">The canonical lookup key (lowercased / hashed as appropriate).</param>
public sealed record ThreatIndicator(IndicatorKind Kind, string Value);
