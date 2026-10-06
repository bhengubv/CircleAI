// ServerSentEventsReader.cs
//
// (3.2.0) Minimal SSE parser shared by every cloud provider. Direct
// lift from Concierge.Chat.Cloud — same shape, same semantics.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace CircleAI.Hosting.CloudFallback;

/// <summary>
/// (3.2.0) Reads <c>data: …</c> frames from a streaming HTTP body and
/// yields each frame's payload. OpenAI / Anthropic / Gemini all share
/// this format; each provider's runtime then parses the JSON payload
/// per its own schema.
/// </summary>
internal static class ServerSentEventsReader
{
    /// <summary>
    /// Yields the payload of every <c>data:</c> frame. Frames containing
    /// the <c>[DONE]</c> sentinel terminate the stream cleanly.
    /// </summary>
    public static async IAsyncEnumerable<string> ReadFramesAsync(
        Stream source,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(source);
        // Drive the loop off ReadLineAsync's null sentinel rather than
        // EndOfStream — the latter does a sync read under the hood
        // (CA2024) and stalls the request thread on slow links.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[5..].TrimStart();
            if (string.Equals(payload, "[DONE]", StringComparison.Ordinal))
            {
                yield break;
            }

            yield return payload;
        }
    }
}
