// LinkedMemory.cs
//
// An IRemembers that asks the CircleAI service instead of keeping its own store.
//
// THIS IS THE REFERENCE THAT KEPT THE ENGINE IN THE APP. DeviceMemory is backed by
// CircleAI.Memory, which references CircleAI.Embeddings, which references
// CircleAI.Inference — so an app that only wanted to remember a name was shipping the
// inference engine transitively, and no line of its own code said so. Nothing in the
// app called CircleAI.Inference; the graph did.
//
// AND IT IS THE RIGHT SPLIT ANYWAY, not just a way to drop a reference. What somebody
// told their phone is ONE person's memory on ONE device, not one copy per app that
// asked: the service owns the store, every linked app reads and writes the same one,
// and uninstalling a client does not take somebody's name with it.
//
// THE WIRE ALREADY CARRIED IT. LinkVerb.Recall and LinkVerb.Remember have been on the
// protocol the whole time, behind LinkScope.Memory so a person approves remembering
// separately from chat. This is the client side of verbs that already existed.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;

namespace CircleAI.Client;

/// <summary>The person's long-term memory, reached over the cross-app link.</summary>
/// <param name="brain">
/// The linked brain, whose connection this rides. Not a layering accident: same
/// service, same consent, same bind — see <see cref="LinkedBrain.LinkAsync"/>.
/// </param>
public sealed class LinkedMemory(LinkedBrain brain) : IRemembers
{
    /// <inheritdoc />
    /// <remarks>
    /// WHAT IS WORTH KEEPING IS THE SERVICE'S CALL, exactly as the contract says it is
    /// the implementation's. It has the model, the existing atoms and the person's own
    /// history; this side has a string. Sending everything and letting the far end
    /// decide is the only version of this that can improve without an app release.
    /// <para>
    /// A failed link is silent here. Learning sits inside a turn somebody is waiting
    /// on, and an unlinked app must not interrupt an answer to report that it could
    /// not write a note.
    /// </para>
    /// </remarks>
    public async Task LearnAsync(string said, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(said)) return;

        var client = await brain.LinkAsync(ct).ConfigureAwait(false);
        if (client is null) return;

        await client.RememberAsync(said, subject: null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// NOTHING, NOT AN ERROR, when the link is down — recall sits directly in front of
    /// an answer, and the contract says a head that cannot answer quickly returns
    /// nothing rather than making somebody wait.
    /// <para>
    /// Confidence comes back as 1: the wire carries rows of text and no score, and the
    /// contract is explicit that an honest "this is what I have" beats a fabricated
    /// ranking. Adding a real one is a column on the wire, not a number invented here.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Remembered>> RecallAsync(
        string about, int limit = 4, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(about)) return [];

        var client = await brain.LinkAsync(ct).ConfigureAwait(false);
        if (client is null) return [];

        var reply = await client.RecallAsync(about, limit, ct).ConfigureAwait(false);
        if (!reply.Ok) return [];

        return [.. reply.Rows
            .Select(r => r.Count > 0 ? r[0] : null)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => new Remembered(t!))];
    }
}
