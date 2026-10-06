// SecurityResponse.cs
//
// Describes the action taken by ISecurityWatchdog in response to an
// AnomalySignal. Returned from OnAnomalyDetectedAsync so calling code
// (e.g. ops-security agent, host application) knows what was done.

namespace CircleAI.Security;

/// <summary>
/// The type of protective action taken in response to an
/// <see cref="AnomalySignal"/>.
/// </summary>
public enum SecurityResponseKind
{
    /// <summary>No action — confidence below threshold or vector is informational.</summary>
    NoAction,

    /// <summary>
    /// The session's ephemeral UHID key ring was regenerated; prior session
    /// keys are revoked and all in-flight requests using old keys will fail.
    /// </summary>
    KeyRotation,

    /// <summary>
    /// The affected session or execution sandbox was marked untrusted and
    /// isolated from the rest of the runtime.
    /// </summary>
    SessionRevocation,

    /// <summary>
    /// A <see cref="PeerDirective"/> was issued to surrounding mesh nodes
    /// to isolate the suspected attack origin.
    /// </summary>
    MeshIsolationSignal,

    /// <summary>
    /// State was rolled back to the most recent verified
    /// <see cref="SecurityCheckpoint"/>.
    /// </summary>
    StateRollback,

    /// <summary>
    /// A combination of responses was applied (e.g. key rotation + mesh
    /// isolation). See <see cref="SecurityResponse.AppliedActions"/> for the
    /// full list.
    /// </summary>
    Composite,
}

/// <summary>
/// Describes the protective action taken by <see cref="ISecurityWatchdog"/>
/// in response to an <see cref="AnomalySignal"/>.
/// </summary>
/// <param name="SignalId">Identifier of the <see cref="AnomalySignal"/> that triggered this response.</param>
/// <param name="Kind">Primary response kind.</param>
/// <param name="AppliedActions">
/// When <see cref="Kind"/> is <see cref="SecurityResponseKind.Composite"/>,
/// lists each individual action applied. Empty for single-action responses.
/// </param>
/// <param name="Description">Human-readable description of what was done and why.</param>
/// <param name="RestoredCheckpoint">
/// The <see cref="SecurityCheckpoint"/> that was restored, if any.
/// <c>null</c> when <see cref="Kind"/> is not <see cref="SecurityResponseKind.StateRollback"/>.
/// </param>
/// <param name="RespondedAt">UTC timestamp of the response.</param>
public sealed record SecurityResponse(
    Guid SignalId,
    SecurityResponseKind Kind,
    IReadOnlyList<SecurityResponseKind> AppliedActions,
    string Description,
    SecurityCheckpoint? RestoredCheckpoint,
    DateTimeOffset RespondedAt)
{
    /// <summary>Creates a no-action response for low-confidence or informational signals.</summary>
    public static SecurityResponse NoAction(Guid signalId, string reason) =>
        new(signalId, SecurityResponseKind.NoAction,
            [], reason, null, DateTimeOffset.UtcNow);

    /// <summary>Creates a key-rotation response.</summary>
    public static SecurityResponse ForKeyRotation(Guid signalId, string description) =>
        new(signalId, SecurityResponseKind.KeyRotation,
            [], description, null, DateTimeOffset.UtcNow);

    /// <summary>Creates a state-rollback response, recording the restored checkpoint.</summary>
    public static SecurityResponse ForRollback(Guid signalId, SecurityCheckpoint restored) =>
        new(signalId, SecurityResponseKind.StateRollback,
            [], $"State rolled back to checkpoint {restored.Id} ({restored.ModuleLabel}).",
            restored, DateTimeOffset.UtcNow);

    /// <summary>Creates a composite response from multiple individual actions.</summary>
    public static SecurityResponse Composite(
        Guid signalId,
        IReadOnlyList<SecurityResponseKind> actions,
        string description,
        SecurityCheckpoint? restoredCheckpoint = null) =>
        new(signalId, SecurityResponseKind.Composite,
            actions, description, restoredCheckpoint, DateTimeOffset.UtcNow);
}
