// OnDeviceImageGeneratorAdapter.cs
//
// Letting the on-device generator take its turn in the fallback chain.
//
// THE GAP. There are two interfaces named IImageGenerator:
//
//   CircleAI.Core.IImageGenerator         OnnxImageGenerator implements it
//   CircleAI.Vision.Cloud.IImageGenerator OpenAiImageGenerator and
//                                         StabilityImageGenerator implement it,
//                                         and ImageGeneratorFallbackChain
//                                         composes it
//
// Same name, different namespaces, so a host could not ask for "an image
// generator" and get whichever was available, and the device could never be
// preferred over a paid API call.
//
// WHY THIS IS AN ADAPTER AND NOT A MERGE. I first called an adapter a way of
// making the collision "look solved" and said the interfaces should be unified.
// Reading both, that was wrong: they are not an accidental duplicate, they are
// two different contracts.
//
//   Core's        is a local diffusion session: IDisposable because it owns
//                 native model state, one image per call, IProgress per
//                 denoising step because on a phone this is tens of seconds,
//                 and SupportedSizes because a diffusion graph is built for
//                 fixed resolutions.
//   Cloud's       is a remote provider: not disposable, N artifacts per call,
//                 each either a URL or bytes, plus GeneratorId / DisplayLabel /
//                 IsConfigured / StatusMessage so a UI can list providers and
//                 say why one is unavailable.
//
// Merging them would force the local one to carry credential status and return
// a list, and force the cloud ones to be disposable and declare fixed sizes.
// Each would be worse at its own job. The adapter is the integration, which is
// what was actually missing.
//
// WHAT IT DELIBERATELY DOES NOT DO: dispose what it wraps. The Core generator is
// a container singleton holding InferenceSessions; the chain that owns this
// adapter does not own that. Disposing it here would close the sessions out from
// under every other holder on the first chain teardown.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Vision;

/// <summary>
/// Presents a <see cref="CircleAI.Core.IImageGenerator"/> as a
/// <see cref="CircleAI.Vision.Cloud.IImageGenerator"/> so the on-device model can
/// sit in <c>ImageGeneratorFallbackChain</c> ahead of any paid API.
/// </summary>
public sealed class OnDeviceImageGeneratorAdapter : CircleAI.Vision.Cloud.IImageGenerator
{
    private readonly CircleAI.Core.IImageGenerator _inner;

    /// <summary>Wraps a device generator. The instance is NOT owned or disposed.</summary>
    public OnDeviceImageGeneratorAdapter(CircleAI.Core.IImageGenerator inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc/>
    public string GeneratorId => VisionServiceCollectionExtensions.GeneratorIds.OnDeviceOnnx;

    /// <inheritdoc/>
    public string DisplayLabel => "On this phone";

    /// <summary>Always true, and that is a statement about registration, not a guess.</summary>
    /// <remarks>
    /// IsConfigured exists for the cloud providers, where it means "has an API
    /// key". The device equivalent is "has the model files", and that is checked
    /// before this object can exist: AddOnnxImageGenerator refuses to build the
    /// Core generator without a UNet and a VAE decoder on disk, and
    /// AddOnDeviceVision does not register it at all when they are missing. So if
    /// something is holding this adapter, the model is present.
    /// </remarks>
    public bool IsConfigured => true;

    /// <inheritdoc/>
    public string StatusMessage =>
        "Generating on this phone — no account, no API key, and it works with no signal.";

    /// <summary>Generates <c>request.Count</c> images locally.</summary>
    /// <remarks>
    /// SEQUENTIALLY, NOT IN PARALLEL. Each call runs a diffusion graph through
    /// ONNX Runtime on a phone; two at once would compete for the same cores and
    /// the same memory, and on a 3.6 GB device the second one is what gets the
    /// process killed. The cloud providers can fan out because the work is not
    /// here.
    ///
    /// Size maps from the cloud contract's single Size to a square, because that
    /// is what it means there - OpenAI and Stability both take one dimension -
    /// and SupportedSizes is left to the Core implementation to honour: it
    /// already documents that it returns the nearest supported resolution rather
    /// than failing.
    ///
    /// FAIL-SOFT, matching the contract it is implementing: the chain's whole
    /// purpose is to try the next provider, so a local failure returns what it
    /// has rather than throwing and taking the chain down. The exception is not
    /// swallowed silently - it goes to VoiceTrace's sibling, Debug - because a
    /// device that cannot generate and says nothing is the plausible zero this
    /// codebase keeps paying for.
    /// </remarks>
    public async Task<IReadOnlyList<CircleAI.Vision.Cloud.ImageArtifact>> GenerateAsync(
        CircleAI.Vision.Cloud.ImageGenerationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var count = Math.Max(1, request.Count);
        var results = new List<CircleAI.Vision.Cloud.ImageArtifact>(count);

        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var produced = await _inner.GenerateAsync(
                    new CircleAI.Core.ImageRequest(
                        Prompt: request.Prompt,
                        NegativePrompt: request.NegativePrompt,
                        Width: request.Size,
                        Height: request.Size),
                    progress: null,
                    ct: ct).ConfigureAwait(false);

                results.Add(new CircleAI.Vision.Cloud.ImageArtifact(
                    GeneratorId: GeneratorId,
                    Prompt: request.Prompt,
                    MimeType: "image/png",
                    Url: null,                 // bytes OR url, never both
                    Bytes: produced.Png,
                    GeneratedAtUtc: DateTimeOffset.UtcNow));
            }
            catch (OperationCanceledException)
            {
                throw;                         // cancellation is the caller's, not a failure to absorb
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[OnDeviceImageGeneratorAdapter] image {i + 1} of {count} failed, "
                    + $"returning {results.Count} and letting the chain continue: "
                    + $"{ex.GetType().Name}: {ex.Message}");
                break;
            }
        }

        return results;
    }
}
