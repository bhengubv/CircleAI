// ISelfHealPolicy.cs
//
// The decisions the loop needs but does not own: how autonomous the person has set
// it, and whether the brain is ready to diagnose. Kept out of SelfHealer so the
// orchestrator stays brain-agnostic and desktop-testable — a head supplies the
// real policy (reading the autonomy setting and the on-device brain's readiness),
// a test supplies a fake.

using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Hosting.SelfHealing;

/// <summary>The runtime decisions the self-heal loop consults.</summary>
public interface ISelfHealPolicy
{
    /// <summary>May the loop diagnose at all? False only when autonomy is Off (record and stop).</summary>
    Task<bool> ShouldAnalyseAsync(CancellationToken ct = default);

    /// <summary>May the loop run a safe fix automatically? True only at the highest autonomy.</summary>
    Task<bool> MayAutoFixAsync(CancellationToken ct = default);

    /// <summary>
    /// Is the brain ready to be asked? Gates the analyst so it is never called cold —
    /// a cold call triggers a full model load and can block for many seconds.
    /// </summary>
    Task<bool> BrainReadyAsync(CancellationToken ct = default);
}
