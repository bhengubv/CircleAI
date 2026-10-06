// ImageGeneratorFallbackChain.cs
//
// (3.2.0) Composite IImageGenerator that walks a configured chain in
// order, returning the first non-empty artifact set. Skips generators
// whose IsConfigured is false. Mirrors CircleAI.Hosting.CloudFallback's
// CloudFallbackChain semantics for chat.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Vision.Cloud;

/// <summary>
/// (3.2.0) Composite <see cref="IImageGenerator"/> — tries each child in
/// order, skipping those that report <see cref="IImageGenerator.IsConfigured"/> = false.
/// Returns the first non-empty artifact list, or empty if everyone failed.
/// </summary>
public sealed class ImageGeneratorFallbackChain : IImageGenerator
{
    private readonly IReadOnlyList<IImageGenerator> _chain;

    public ImageGeneratorFallbackChain(IEnumerable<IImageGenerator> chain)
    {
        _chain = chain?.ToList() ?? new List<IImageGenerator>();
    }

    public string GeneratorId   => "fallback-chain";
    public string DisplayLabel  => $"Fallback ({_chain.Count})";
    public bool   IsConfigured  => _chain.Any(g => g.IsConfigured);
    public string StatusMessage => IsConfigured
        ? $"Ready · {string.Join(" → ", _chain.Where(g => g.IsConfigured).Select(g => g.GeneratorId))}"
        : "No configured generator in chain.";

    public async Task<IReadOnlyList<ImageArtifact>> GenerateAsync(
        ImageGenerationRequest request,
        CancellationToken      ct = default)
    {
        foreach (var g in _chain)
        {
            if (!g.IsConfigured) continue;
            var result = await g.GenerateAsync(request, ct).ConfigureAwait(false);
            if (result.Count > 0) return result;
        }
        return System.Array.Empty<ImageArtifact>();
    }
}
