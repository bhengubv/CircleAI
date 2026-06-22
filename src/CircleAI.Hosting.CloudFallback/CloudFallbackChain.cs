// CloudFallbackChain.cs
//
// (3.2.0) Composite IChatGenerator that walks an ordered list of
// generators and uses the first one ready to serve a call. Lets a
// host wire (on-device-Qwen, OpenAI, Anthropic, Gemini) so that
// network outage / missing keys / cloud throttling fall through
// without the consumer code knowing.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Inference;

namespace CircleAI.Hosting.CloudFallback;

/// <summary>
/// (3.2.0) Reports whether a generator is currently in a state where
/// it can serve calls. Cloud generators expose this via the API-key
/// check; on-device generators that don't implement it are presumed
/// always ready (the chain falls through on failure anyway).
/// </summary>
public interface IConfigurableChatGenerator : IChatGenerator
{
    /// <summary>True when the generator can serve calls (e.g. API key present).</summary>
    bool IsConfigured { get; }

    /// <summary>Display name (e.g. "OpenAI · gpt-4o-mini").</summary>
    string EngineLabel { get; }

    /// <summary>Human-readable explanation of the current state.</summary>
    string StatusMessage { get; }
}

/// <summary>
/// (3.2.0) Tries an ordered list of <see cref="IChatGenerator"/>s and
/// streams from the first one ready. A generator that yields a
/// fail-soft "[provider not configured]" frame doesn't count as ready
/// — the chain skips it and moves on. Generators that throw are also
/// skipped (the chain logs and continues).
/// </summary>
public sealed class CloudFallbackChain : IChatGenerator
{
    private readonly IReadOnlyList<IChatGenerator> _generators;

    /// <summary>
    /// (3.2.0) Build a chain. Order matters — the first ready generator
    /// wins, so put on-device first if you want sovereign-by-default.
    /// </summary>
    public CloudFallbackChain(IEnumerable<IChatGenerator> generators)
    {
        ArgumentNullException.ThrowIfNull(generators);
        _generators = generators.ToList();
    }

    public IReadOnlyList<IChatGenerator> Generators => _generators;

    public async Task<string> GenerateAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions?         options = null,
        CancellationToken          ct      = default)
    {
        foreach (var g in _generators)
        {
            if (!IsReady(g)) continue;
            try
            {
                return await g.GenerateAsync(messages, options, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Fall through to the next generator.
            }
        }
        return "[CloudFallbackChain: no configured generator could serve the request]";
    }

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions?         options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var g in _generators)
        {
            if (!IsReady(g)) continue;

            // We need to attempt the stream and only commit to this
            // generator if it produces a real frame. The "[provider not
            // configured]" sentinel is filtered out so we can move on.
            var enumerator = g.StreamAsync(messages, options, ct).GetAsyncEnumerator(ct);
            bool yielded = false;
            try
            {
                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // This generator faulted mid-stream; move to next
                        // generator iff we haven't yielded anything yet.
                        if (yielded) yield break;
                        else         break;
                    }

                    if (!hasNext) yield break;

                    var chunk = enumerator.Current;
                    if (!yielded && IsFailSoftFrame(chunk))
                    {
                        // Generator declined the call (e.g. no API key).
                        break;
                    }

                    yielded = true;
                    yield return chunk;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            if (yielded) yield break;
        }

        yield return "[CloudFallbackChain: no configured generator could serve the request]";
    }

    public void Dispose()
    {
        foreach (var g in _generators)
        {
            try { g.Dispose(); } catch { /* swallow — best effort */ }
        }
        GC.SuppressFinalize(this);
    }

    private static bool IsReady(IChatGenerator g) =>
        g is not IConfigurableChatGenerator c || c.IsConfigured;

    private static bool IsFailSoftFrame(string chunk) =>
        chunk.StartsWith("[", StringComparison.Ordinal)
        && (chunk.Contains("not configured", StringComparison.OrdinalIgnoreCase)
            || chunk.Contains("CloudFallbackChain", StringComparison.OrdinalIgnoreCase));
}
