// IOffloadGateway.cs
//
// The borrow half of pooled intelligence, as a browser-safe seam the shared turn
// can call. The SERVE half (a node answering a peer) is CircleAI.Mesh.Hosting's
// BridgeLocalInferenceFallback; this is the other side — a weak phone borrowing a
// trusted node's brain when it cannot run the model itself.
//
// In CircleAI.Assistant on purpose: BrainToolFlow lives here (zero project
// references, a browser loads it), so the borrow decision cannot touch
// CircleAI.Mesh directly. It calls THIS interface; the phone provides the mesh
// implementation, the browser gets NullOffloadGateway — the same pattern as IBrain.

using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Assistant;

/// <summary>
/// What a person has allowed for borrowing a nearby node's brain (v1): whether
/// borrowing is on at all, and which single trusted node to borrow from.
/// </summary>
/// <param name="Enabled">The person turned borrowing on. Off by default.</param>
/// <param name="ChosenNodeId">
/// The hand-picked Circle node to borrow from, or null. v1 is a curated floor —
/// the person names the node; CircleAI does not admit one on its own.
/// </param>
public sealed record OffloadConsent(bool Enabled = false, string? ChosenNodeId = null)
{
    /// <summary>Nothing borrows until the person opts in and names a node.</summary>
    public static OffloadConsent Off { get; } = new();

    /// <summary>True only when borrowing is on AND a node has been chosen.</summary>
    public bool CanBorrow => Enabled && !string.IsNullOrWhiteSpace(ChosenNodeId);
}

/// <summary>
/// Borrows an answer from a trusted nearby node when this phone cannot run the
/// model itself.
/// </summary>
/// <remarks>
/// THE PRIVACY GUARANTEE IS STRUCTURAL. The caller hands this the RAW question the
/// person typed or spoke — never the memory-composed prompt — so no memory,
/// persona, or device context can ride along to the borrowed node. Enforced by the
/// call site (BrainToolFlow passes <c>question</c>, not <c>asked</c>), not by a
/// content filter that could leak.
/// </remarks>
public interface IOffloadGateway
{
    /// <summary>
    /// Try to borrow an answer to <paramref name="question"/> (the raw question).
    /// Returns null — and the caller falls through to its normal local flow — when
    /// borrowing is off, no node is chosen, this device can run the model itself,
    /// or the borrow failed. Only <see cref="System.OperationCanceledException"/>
    /// propagates.
    /// </summary>
    Task<string?> TryBorrowAsync(string question, CancellationToken ct = default);
}

/// <summary>The borrow-nothing default: the browser, or borrowing turned off.</summary>
public sealed class NullOffloadGateway : IOffloadGateway
{
    /// <summary>Shared stateless instance.</summary>
    public static readonly NullOffloadGateway Instance = new();

    /// <inheritdoc/>
    public Task<string?> TryBorrowAsync(string question, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
