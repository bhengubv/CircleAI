using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Inference
{
    /// <summary>
    /// Contract for an on-device chat-style text generator. Implementations
    /// own native model state and must be disposed.
    /// </summary>
    public interface IChatGenerator : IDisposable
    {
        /// <summary>
        /// Generates a complete assistant reply for the given conversation.
        /// </summary>
        Task<string> GenerateAsync(
            IReadOnlyList<ChatMessage> messages,
            GenerationOptions? options = null,
            CancellationToken ct = default);

        /// <summary>
        /// Streams the assistant reply token-by-token (or piece-by-piece) as
        /// it is decoded. Each yielded string is the next chunk to append to
        /// the output — callers should concatenate them in order. Content
        /// only — any reasoning emitted inside <c>&lt;think&gt;…&lt;/think&gt;</c>
        /// is filtered out. Use <see cref="StreamFragmentsAsync"/> when you
        /// also need the reasoning stream.
        /// </summary>
        IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatMessage> messages,
            GenerationOptions? options = null,
            CancellationToken ct = default);

        /// <summary>
        /// Fragment-aware streaming variant. Yields each piece tagged as either
        /// <see cref="ChatFragmentKind.Content"/> or
        /// <see cref="ChatFragmentKind.Reasoning"/> so the caller can route the
        /// model's <c>&lt;think&gt;</c> block into a separate
        /// <c>reasoning_content</c> field (o1 / DeepSeek style). Default
        /// implementation wraps <see cref="StreamAsync"/> and tags every chunk
        /// as <see cref="ChatFragmentKind.Content"/>; generators that surface
        /// reasoning override.
        /// </summary>
        async IAsyncEnumerable<ChatFragment> StreamFragmentsAsync(
            IReadOnlyList<ChatMessage> messages,
            GenerationOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var chunk in StreamAsync(messages, options, ct).ConfigureAwait(false))
                yield return new ChatFragment(ChatFragmentKind.Content, chunk);
        }

        /// <summary>
        /// (RT-02) Save the current model session — KV cache + history — to
        /// <paramref name="path"/> so the conversation can survive an OOM kill
        /// and resume later via <see cref="LoadSessionAsync"/>. The on-disk
        /// format is owned by the underlying inference engine; treat the path
        /// as opaque.
        /// <para>
        /// Default implementation writes a portable marker file containing the
        /// generator type name + a UTC timestamp so callers always get a
        /// non-throwing round-trip. Native generators (Qwen, KimiVl) override
        /// to call the MNN session primitives under their per-handle
        /// serialisation lock for a true KV-cache snapshot. Returns
        /// <c>true</c> on success.
        /// </para>
        /// </summary>
        async Task<bool> SaveSessionAsync(string path, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path required", nameof(path));
            var marker = $"circleai-session-marker\ntype:{GetType().FullName}\nsaved_utc:{DateTimeOffset.UtcNow:O}\n";
            await System.IO.File.WriteAllTextAsync(path, marker, ct).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// (RT-02) Load a previously-saved session from <paramref name="path"/>.
        /// Default implementation verifies the marker file written by the default
        /// <see cref="SaveSessionAsync"/>. Native generators override to restore
        /// real KV-cache state. Returns <c>true</c> on success.
        /// </summary>
        async Task<bool> LoadSessionAsync(string path, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path required", nameof(path));
            if (!System.IO.File.Exists(path)) return false;
            var text = await System.IO.File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return text.StartsWith("circleai-session-marker", StringComparison.Ordinal);
        }

        /// <summary>
        /// Structured-response variant: returns the assistant reply alongside
        /// token counts, finish reason, and latency. Default implementation
        /// wraps <see cref="GenerateAsync"/> with an approximate token count
        /// (word split) and <see cref="FinishReason.Stop"/>; native generators
        /// override to report the exact native-reported values (and to surface
        /// <see cref="ChatResponse.ReasoningContent"/>).
        /// </summary>
        async Task<ChatResponse> GenerateResponseAsync(
            IReadOnlyList<ChatMessage> messages,
            GenerationOptions? options = null,
            CancellationToken ct = default)
        {
            var started = Environment.TickCount64;
            var text    = await GenerateAsync(messages, options, ct).ConfigureAwait(false);
            var latency = TimeSpan.FromMilliseconds(Environment.TickCount64 - started);

            // Approximate token count for the fallback default — generators
            // that can report real counts override the whole method.
            var tokensIn  = ApproximateTokens(messages);
            var tokensOut = ApproximateTokens(text);

            return new ChatResponse(
                Text:         text,
                TokensIn:     tokensIn,
                TokensOut:    tokensOut,
                Latency:      latency,
                FinishReason: FinishReason.Stop);
        }

        private static int ApproximateTokens(IReadOnlyList<ChatMessage> messages)
        {
            var total = 0;
            foreach (var m in messages) total += ApproximateTokens(m.Content);
            return total;
        }

        private static int ApproximateTokens(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            // Crude approximation — 1 token ≈ 4 chars in English. Replaced
            // by native count in implementations that have one.
            return Math.Max(1, text.Length / 4);
        }
    }

    /// <summary>
    /// Structured response from <see cref="IChatGenerator.GenerateResponseAsync"/>.
    /// Carries the generated text alongside the metadata callers need for
    /// rate-limiting, billing, telemetry, and trace stitching.
    /// </summary>
    /// <param name="Text">The assistant's reply (content only — reasoning excluded).</param>
    /// <param name="TokensIn">
    /// Input prompt token count. Approximate for generators that don't
    /// expose a native count; exact when the native bridge reports one.
    /// </param>
    /// <param name="TokensOut">Output token count. Same accuracy caveat.</param>
    /// <param name="Latency">Total wall-clock time for the call.</param>
    /// <param name="FinishReason">Why generation stopped.</param>
    /// <param name="ReasoningContent">
    /// Optional chain-of-thought emitted by reasoning models inside
    /// <c>&lt;think&gt;…&lt;/think&gt;</c> (Qwen3, DeepSeek, o1-style). <c>null</c>
    /// when the model emitted no reasoning or
    /// <see cref="GenerationOptions.IncludeReasoning"/> was <c>false</c>. The
    /// <c>&lt;think&gt;</c> tags themselves are stripped — the value is the
    /// text content only.
    /// </param>
    public sealed record ChatResponse(
        string       Text,
        int          TokensIn,
        int          TokensOut,
        TimeSpan     Latency,
        FinishReason FinishReason,
        string?      ReasoningContent = null);

    /// <summary>Kind of fragment a streaming generator emits.</summary>
    public enum ChatFragmentKind
    {
        /// <summary>Part of the user-facing answer (goes into <c>content</c>).</summary>
        Content   = 0,
        /// <summary>Part of the model's reasoning trace (goes into <c>reasoning_content</c>).</summary>
        Reasoning = 1,
    }

    /// <summary>
    /// A single fragment emitted by <see cref="IChatGenerator.StreamFragmentsAsync"/>.
    /// </summary>
    /// <param name="Kind">Which sink this fragment belongs to.</param>
    /// <param name="Text">The decoded fragment text.</param>
    public readonly record struct ChatFragment(ChatFragmentKind Kind, string Text);

    /// <summary>Why a generation call stopped emitting tokens.</summary>
    public enum FinishReason
    {
        /// <summary>Hit a stop sequence (e.g. <c>&lt;|im_end|&gt;</c>) — normal completion.</summary>
        Stop           = 0,

        /// <summary>Hit <see cref="GenerationOptions.MaxTokens"/>.</summary>
        Length         = 1,

        /// <summary>The cancellation token fired.</summary>
        Cancelled      = 2,

        /// <summary>Native generation reported an error before a stop sequence fired.</summary>
        Error          = 3,

        /// <summary>Native bridge didn't surface a finish reason; treat as <see cref="Stop"/>.</summary>
        Unknown        = 4,
    }

    /// <summary>
    /// A single message in a chat history. <see cref="Role"/> is one of
    /// <c>"system"</c>, <c>"user"</c>, <c>"assistant"</c>, or <c>"tool"</c>.
    /// </summary>
    public sealed record ChatMessage(string Role, string Content)
    {
        /// <summary>
        /// Optional raw image bytes (JPEG / PNG / WebP) attached to this turn.
        /// Consumed by <c>KimiVlGenerator</c> (or any vision-capable
        /// <see cref="IChatGenerator"/>); text-only generators ignore it.
        /// <c>null</c> for plain text turns.
        /// </summary>
        public byte[]? ImageBytes { get; init; }
    }

    /// <summary>
    /// Knobs for a single generation call.
    /// </summary>
    public sealed class GenerationOptions
    {
        /// <summary>Maximum number of new tokens to produce.</summary>
        public int MaxTokens { get; init; } = 512;

        /// <summary>Sampling temperature. 0 = greedy; higher = more random.</summary>
        public float Temperature { get; init; } = 0.7f;

        /// <summary>Nucleus sampling cutoff (top-p). 1.0 disables.</summary>
        public float TopP { get; init; } = 0.9f;

        /// <summary>Top-k cutoff. 0 disables.</summary>
        public int TopK { get; init; } = 40;

        /// <summary>Optional RNG seed. <c>null</c> means non-deterministic.</summary>
        public int? Seed { get; init; }

        /// <summary>
        /// Optional substrings that will end generation when matched in the
        /// emitted output (e.g. role-tag boundaries).
        /// </summary>
        public string[]? StopSequences { get; init; }

        /// <summary>
        /// Whether to surface the model's reasoning trace (Qwen3
        /// <c>&lt;think&gt;…&lt;/think&gt;</c>) on the call.
        /// <para>
        /// When <c>true</c> (default) the generator separates reasoning from
        /// the final answer: <see cref="ChatResponse.ReasoningContent"/> gets
        /// the reasoning, <see cref="ChatResponse.Text"/> gets the answer.
        /// Streaming callers see fragments tagged with
        /// <see cref="ChatFragmentKind.Reasoning"/>.
        /// </para>
        /// <para>
        /// When <c>false</c> the generator still <i>runs</i> reasoning (this
        /// is per-call output gating, NOT a thinking disable) but the
        /// reasoning text is dropped — only the final answer reaches the
        /// caller. Use this for JSON-strict consumers that cannot tolerate
        /// surface-level reasoning.
        /// </para>
        /// </summary>
        public bool IncludeReasoning { get; init; } = true;

        /// <summary>
        /// (RT-11) Declarative power budget for this call. The runtime maps
        /// the budget to context size, KV compression, decode token limit,
        /// and (when fallback chains are configured) model size. Default
        /// <see cref="PowerBudget.Normal"/> auto-downgrades to <c>Low</c>
        /// when battery is below 15%.
        /// <para>
        /// Pass <see cref="PowerBudget.None"/> to opt out of automatic
        /// budget control and have the runtime honour
        /// <see cref="MaxTokens"/> literally.
        /// </para>
        /// </summary>
        public PowerBudget Budget { get; init; } = PowerBudget.Normal;

        /// <summary>
        /// (RT-06) Whether the runtime should consult the cross-session
        /// prefix cache for a warm (modelId, systemPrompt) snapshot before
        /// resetting the model handle. Default <c>false</c> — opt in per
        /// call when the system prompt is stable across chats and you want
        /// sub-200 ms first-token latency.
        /// <para>
        /// The first call with <c>UsePrefixCache = true</c> for a given
        /// (modelId, systemPrompt) populates the cache; subsequent calls
        /// reload it instead of running the prefill. Cache lives at
        /// <c>%LOCALAPPDATA%/CircleAI/prefix-cache/</c>.
        /// </para>
        /// </summary>
        public bool UsePrefixCache { get; init; } = false;

        /// <summary>
        /// Keep the KV cache between calls and prefill only the NEW text, when
        /// this call continues the same conversation the last one ended.
        /// </summary>
        /// <remarks>
        /// <para>
        /// WHY IT DEFAULTS OFF. The runtime resets the handle before every
        /// generation because the OpenAI-compatible contract is multi-turn-by-
        /// replay: the client sends the whole history each time, so retained
        /// state would prefill the earlier turns twice. That is right for a
        /// server with many clients on one handle, and wrong for a phone with
        /// one person and one conversation — which paid for it by re-reading the
        /// entire transcript on every question.
        /// </para>
        /// <para>
        /// MEASURED ON A P30 LITE, Qwen2.5-1.5B: first token at 32.8 s on
        /// question one, rising to 47.1 s by question five. The climb is the
        /// tell — the model was not getting slower, the transcript was getting
        /// longer and being re-read from the top every time. Nothing about that
        /// wait is the model's size.
        /// </para>
        /// <para>
        /// Safe because it is checked, not assumed: the runtime keeps the exact
        /// text its KV cache represents and reuses it only when the new prompt
        /// begins with that text verbatim. Edit an earlier turn, switch system
        /// prompts, or start a new chat and the prefix stops matching, so it
        /// resets and prefills in full. There is no state to get stale.
        /// </para>
        /// </remarks>
        public bool ContinueConversation { get; init; } = false;
    }
}
