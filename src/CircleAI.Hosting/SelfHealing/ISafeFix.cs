// ISafeFix.cs
//
// One safe, reversible remedy the loop may run on its own — retry, reset a
// connection, trim a regenerable cache. A fix declares the failure category it
// handles; the loop runs it ONLY for a quick-fix verdict, ONLY when autonomy
// allows, and treats a false/throw as "did not take" and escalates. Nothing here
// ever touches code, money, or security — those are never a safe fix.

using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Hosting.SelfHealing;

/// <summary>A safe, reversible remedy the self-heal loop can apply automatically.</summary>
public interface ISafeFix
{
    /// <summary>
    /// The failure category this remedy handles (e.g. "cache", "memory",
    /// "transient-network"). Matched case-insensitively against the verdict's
    /// category. Keep it a single lowercase token.
    /// </summary>
    string Category { get; }

    /// <summary>
    /// Apply the remedy. Return true when it was applied, false when it could not
    /// be. MUST be reversible and idempotent — running it twice is harmless. It may
    /// throw; the loop treats a throw as "did not take" and escalates.
    /// </summary>
    Task<bool> TryFixAsync(FailureContext failure, CancellationToken ct = default);
}
