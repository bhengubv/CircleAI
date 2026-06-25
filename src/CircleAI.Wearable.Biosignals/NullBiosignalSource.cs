// NullBiosignalSource.cs
//
// A no-op biosignal source for tests and for the "no wearable connected" case.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using CircleAI.Core.Validation;

namespace CircleAI.Wearable.Biosignals;

/// <summary>
/// A biosignal source that supports nothing and emits nothing.
/// Use for tests and as the default when no wearable is connected.
/// </summary>
/// <remarks>
/// Marked <see cref="VerificationLevel.Reference"/>: by design, this type
/// represents the "no wearable connected" case. Production deployments
/// wire a real <see cref="IBiosignalSource"/> in place of it.
/// </remarks>
[Experimental("CIRCLEAI_BIO_001", UrlFormat = "https://github.com/bhengubv/CircleAI/blob/master/docs/experimental.md#{0}")]
[CircleAIVerificationStatus(VerificationLevel.Reference)]
public sealed class NullBiosignalSource : IBiosignalSource
{
    /// <inheritdoc />
    public BiosignalKind[] SupportedKinds => Array.Empty<BiosignalKind>();

    /// <inheritdoc />
    public Task<bool> IsSupportedAsync(BiosignalKind kind, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    /// <inheritdoc />
    public async IAsyncEnumerable<BiosignalSample> StreamAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Yield nothing. The await keeps the method genuinely async (no warning) and
        // honours the cancellation token in case callers test for it.
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}
