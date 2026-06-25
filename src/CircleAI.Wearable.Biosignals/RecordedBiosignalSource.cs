// RecordedBiosignalSource.cs
//
// (3.3.0) Real biosignal source backed by a fixed list of samples.
// Replays them on subscription — gives tests AND host integration
// scenarios a deterministic source without needing a real wearable.

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Wearable.Biosignals;

/// <summary>(3.3.0) Replays a recorded biosignal stream. Useful for tests, training data,
/// and host integration when no live wearable is connected.</summary>
public sealed class RecordedBiosignalSource : IBiosignalSource
{
    private readonly IReadOnlyList<BiosignalSample> _samples;
    private readonly BiosignalKind[] _kinds;
    private readonly TimeSpan _replayDelay;

    public RecordedBiosignalSource(IReadOnlyList<BiosignalSample> samples, TimeSpan? replayDelay = null)
    {
        _samples = samples ?? throw new ArgumentNullException(nameof(samples));
        _replayDelay = replayDelay ?? TimeSpan.Zero;
        var seen = new HashSet<BiosignalKind>();
        foreach (var s in samples) seen.Add(s.Kind);
        _kinds = seen.ToArray();
    }

    public BiosignalKind[] SupportedKinds => _kinds;

    public Task<bool> IsSupportedAsync(BiosignalKind kind, CancellationToken cancellationToken)
    {
        foreach (var k in _kinds) if (k == kind) return Task.FromResult(true);
        return Task.FromResult(false);
    }

    public async IAsyncEnumerable<BiosignalSample> StreamAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var s in _samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_replayDelay > TimeSpan.Zero) await Task.Delay(_replayDelay, cancellationToken).ConfigureAwait(false);
            yield return s;
        }
    }
}
