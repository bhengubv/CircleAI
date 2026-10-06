// IRemembers.cs
//
// The long-term half of the memory loop, as the shared UI is allowed to see it.
//
// WHY AN INTERFACE HERE RATHER THAN THE REAL MemoryService. The store that keeps
// what somebody told the phone lives in CircleAI.Memory, which is a src project
// with a file-backed append-only log - the shared Razor library cannot reference
// it for the same reason it cannot reference the ONNX runtime: a WebAssembly
// client would have to load it and cannot. So the contract lives here and each
// head satisfies it with whatever it actually has: the phone with the real
// store, a browser with nothing.
//
// THE LOOP THIS CLOSES. Until now the app WROTE to long-term memory on every
// utterance and never READ from it - LearnAsync was called from the turn,
// RecallAsync only from a diagnostic screen. So it accumulated everything
// somebody ever said and could not tell them their own name the next morning.
// The store is the short-term half; this is the long-term half; the effects in
// the shared UI are the loop between them.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Assistant;

/// <summary>One thing worth remembering, and how sure we are of it.</summary>
/// <param name="Text">The fact, in the words it can be read back in.</param>
/// <param name="Confidence">
/// 0 to 1. A head that cannot score its own recall reports 1 rather than
/// inventing a spread — an honest "this is what I have" beats a fabricated
/// ranking.
/// </param>
public sealed record Remembered(string Text, double Confidence = 1.0);

/// <summary>What the phone has been told, across sessions.</summary>
public interface IRemembers
{
    /// <summary>Keep what was just said, if any of it is worth keeping.</summary>
    /// <remarks>
    /// Deciding what is worth keeping belongs to the implementation, not the
    /// caller: the turn does not know an address from a pleasantry.
    /// </remarks>
    Task LearnAsync(string said, CancellationToken ct = default);

    /// <summary>
    /// What is worth knowing before answering <paramref name="about"/>.
    /// </summary>
    /// <remarks>
    /// TIME-BOXED BY THE CALLER, AND ORDERED BEST FIRST. This sits directly in
    /// front of an answer somebody is waiting for, so an implementation that
    /// cannot answer quickly should return nothing rather than make them wait -
    /// a reply that arrives without a remembered name is better than a reply
    /// that arrives late with one.
    /// </remarks>
    /// <param name="limit">At most this many, because they go into a prompt.</param>
    Task<IReadOnlyList<Remembered>> RecallAsync(
        string about, int limit = 4, CancellationToken ct = default);
}

/// <summary>A head with nowhere to keep anything.</summary>
/// <remarks>
/// The browser, and any head still being built. Says so by remembering nothing
/// rather than by throwing: a screen that works everywhere must not depend on a
/// store only the phone has.
/// </remarks>
public sealed class RemembersNothing : IRemembers
{
    public Task LearnAsync(string said, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<Remembered>> RecallAsync(
        string about, int limit = 4, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Remembered>>([]);
}
