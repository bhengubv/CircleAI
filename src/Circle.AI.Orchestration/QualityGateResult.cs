namespace Circle.AI.Orchestration;

/// <summary>
/// The verdict produced by <see cref="IAgentDispatcher.RunQualityGateAsync"/>
/// after evaluating a <see cref="SwarmResult"/>.
/// </summary>
/// <param name="Passed">
/// <c>true</c> when there are no <see cref="Blockers"/>; the task output may
/// proceed to the next pipeline stage.
/// </param>
/// <param name="Blockers">
/// Critical or high-severity issues that must be resolved before the output
/// may be deployed. Non-empty when <see cref="Passed"/> is <c>false</c>.
/// </param>
/// <param name="Warnings">
/// Low-severity or cosmetic issues. These are surfaced for visibility but do
/// not prevent deployment.
/// </param>
public sealed record QualityGateResult(
    bool Passed,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings);
