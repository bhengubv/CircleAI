// IBiosignalSource.cs
//
// Contract for a streaming biosignal source (a connected wearable, a simulator, etc.).

namespace CircleAI.Wearable.Biosignals;

/// <summary>
/// A streaming source of biosignal samples — typically backed by a wearable device,
/// a platform health API, or a simulator for tests.
/// </summary>
public interface IBiosignalSource
{
    /// <summary>
    /// The kinds of signals this source can emit. May be empty for the null source.
    /// </summary>
    BiosignalKind[] SupportedKinds { get; }

    /// <summary>
    /// Streams biosignal samples until <paramref name="cancellationToken"/> is cancelled
    /// or the underlying device disconnects.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async stream of samples.</returns>
    IAsyncEnumerable<BiosignalSample> StreamAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reports whether this source can produce samples of the given kind.
    /// </summary>
    /// <param name="kind">The kind to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if supported.</returns>
    Task<bool> IsSupportedAsync(BiosignalKind kind, CancellationToken cancellationToken);
}
