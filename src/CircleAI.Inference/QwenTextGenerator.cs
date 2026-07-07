// QwenTextGenerator.cs
//
// IChatGenerator backed by MNN-LLM running a Qwen-family model.
// (Qwen3 / Qwen3.5 — design targets; any model using the Qwen ChatML
// template will work, including Kimi-VL-A3B for the vision path.)
//
// Key differences from the old llama.cpp implementation:
//   - Tokenisation, context management, and sampling are all handled by
//     MNN-LLM internally. QwenTextGenerator only needs the model path
//     and GenerationOptions knobs.
//   - Streaming uses a native callback (MnnTokenCallback) — MNN delivers
//     decoded UTF-8 text fragments directly, no manual byte-accumulation.
//   - No global backend reference count: MNN initialises per model handle.
//   - Stop-sequence safety check is kept (BuildQwenChatPrompt + TryFindStopSequence)
//     as a belt-and-suspenders guard; Qwen models natively stop on <|im_end|>.
//
// MNN performance vs llama.cpp on Android ARM64:
//   Prefill: 8.6×  |  Decode: 3.7×  |  Peak RAM: −40%

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CircleAI.Inference;

/// <summary>
/// On-device chat generator backed by MNN-LLM running a Qwen / Kimi GGUF or MNN model.
/// </summary>
public sealed class QwenTextGenerator : IChatGenerator
{
    // ChatML role tags used by Qwen 1.5 / 2 / 3 / Qwen-VL family.
    private const string ImStart    = "<|im_start|>";
    private const string ImEnd      = "<|im_end|>";
    // Qwen3 also emits the GPT-2-style end-of-text marker when continuing past
    // </think>; without it in the stop list the literal "<|endoftext|>" leaks
    // into the content channel.
    private const string EndOfText  = "<|endoftext|>";

    private static readonly string[] DefaultStopSequences = [ImEnd, ImStart, EndOfText];

    private readonly MnnModelHandle _model;
    private readonly int _maxNewTokens;
    private readonly IPromptTemplateEngine? _templateEngine;
    private readonly string? _modelDirectory;
    private readonly string  _modelPath;
    private readonly PrefixCacheService _prefixCache;

    // The native mnnbridge contract states that "concurrent calls on the SAME
    // handle are undefined behaviour" (mnnbridge.h:22-24). The server may
    // dispatch multiple /v1/chat/completions requests against a single handle,
    // so we serialize generation at the generator level. Pool handles in the
    // bridge factory if higher concurrency is needed.
    private readonly SemaphoreSlim _generationLock = new(initialCount: 1, maxCount: 1);

    private bool _disposed;

    /// <summary>
    /// Loads a model from disk (GGUF or native MNN format) and prepares it for generation.
    /// </summary>
    /// <param name="modelPath">Absolute path to the model file.</param>
    /// <param name="contextSize">
    /// Maximum context window in tokens. Passed as a hint; MNN uses the model's
    /// built-in context size when this exceeds the model's maximum.
    /// </param>
    /// <param name="threads">
    /// Number of CPU threads. <c>null</c> lets MNN pick a default
    /// (usually <c>Environment.ProcessorCount / 2</c>).
    /// </param>
    /// <exception cref="ArgumentException">Path is null or empty.</exception>
    /// <exception cref="System.IO.FileNotFoundException">Model file not found.</exception>
    /// <exception cref="InvalidOperationException">Native load failed.</exception>
    public QwenTextGenerator(string modelPath, uint contextSize = 4096, int? threads = null)
        : this(modelPath, contextSize, threads, templateEngine: null) { }

    /// <summary>
    /// Loads a model and uses the supplied <see cref="IPromptTemplateEngine"/>
    /// to render chat history through the model's own <c>chat_template</c>
    /// (read from <c>tokenizer_config.json</c> in the model's directory). This
    /// is the catalog-driven path — new model families that publish a
    /// chat_template work without any SDK code change.
    /// </summary>
    /// <param name="modelPath">Absolute path to the model file.</param>
    /// <param name="contextSize">Maximum context window in tokens.</param>
    /// <param name="threads">CPU thread count (<c>null</c> for MNN default).</param>
    /// <param name="templateEngine">
    /// Prompt template engine to use, or <c>null</c> to fall back to the
    /// hardcoded Qwen ChatML builder. Resolved via DI when registered
    /// through <c>AddCircleAI</c>.
    /// </param>
    public QwenTextGenerator(
        string                   modelPath,
        uint                     contextSize,
        int?                     threads,
        IPromptTemplateEngine?   templateEngine)
    {
        // Ensure P/Invoke can find mnnbridge + its MNN core before the first
        // native call — works even on a bare `new QwenTextGenerator(path)`.
        NativeLibraryResolver.EnsureRegistered();

        if (string.IsNullOrWhiteSpace(modelPath))
            throw new ArgumentException("Model path is required.", nameof(modelPath));

        if (!System.IO.File.Exists(modelPath))
            throw new System.IO.FileNotFoundException("Model file not found.", modelPath);

        if (contextSize == 0)
            throw new ArgumentOutOfRangeException(nameof(contextSize), "Context size must be > 0.");

        var handle = MnnInterop.mnn_llm_create(modelPath);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new InvalidOperationException(
                $"MNN failed to create model from '{modelPath}'. " +
                "Verify the file is a valid GGUF or MNN model and that " +
                "libmnnbridge is on the native library search path.");
        }

        int rc = MnnInterop.mnn_llm_load(handle);
        if (rc != 0)
        {
            handle.Dispose();
            throw new InvalidOperationException(
                $"MNN model load failed with code {rc} for '{modelPath}'. " +
                "Check available RAM and that the model file is not corrupt.");
        }

        _model = handle;
        _maxNewTokens = (int)Math.Min(contextSize, int.MaxValue);
        _templateEngine = templateEngine;
        _modelDirectory = System.IO.Path.GetDirectoryName(modelPath);
        _modelPath = modelPath;
        _prefixCache = PrefixCacheService.Default;
    }

    /// <inheritdoc />
    public async Task<string> GenerateAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ThrowIfDisposed();

        var sb = new StringBuilder();
        await foreach (var piece in StreamAsync(messages, options, ct).ConfigureAwait(false))
            sb.Append(piece);

        return sb.ToString();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Filter the fragment stream down to content-only — reasoning is
        // dropped for back-compat with callers that only know about
        // <c>StreamAsync</c> (the legacy contract).
        await foreach (var f in StreamFragmentsAsync(messages, options, ct).ConfigureAwait(false))
        {
            if (f.Kind == ChatFragmentKind.Content && !string.IsNullOrEmpty(f.Text))
                yield return f.Text;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatFragment> StreamFragmentsAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ThrowIfDisposed();

        options ??= new GenerationOptions();

        // RT-11: translate the declarative PowerBudget into a per-call token
        // cap. KV-mode preferences are advisory (current MNN builds set the
        // mode at load time) but the token cap takes effect immediately.
        var resolvedBudget = PowerBudgetPolicy.Resolve(
            budget:              options.Budget,
            requestedMaxTokens:  options.MaxTokens > 0 ? options.MaxTokens : _maxNewTokens);

        // RT-06: derive the prefix-cache key from (modelPath, systemPrompt).
        // If the caller opted in and the cache has a warm snapshot, the
        // native side will skip the system-prompt prefill.
        string? prefixCacheKey = null;
        if (options.UsePrefixCache)
        {
            var systemPrompt = ExtractSystemPrompt(messages);
            prefixCacheKey = PrefixCacheService.KeyFor(_modelPath, systemPrompt);
        }

        var channel = Channel.CreateUnbounded<ChatFragment>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });

        // Catalog-driven: render through the model's own chat_template
        // when the engine + bundle directory are available. Falls back
        // to the hardcoded Qwen ChatML builder otherwise.
        var prompt = (_templateEngine is not null && !string.IsNullOrEmpty(_modelDirectory))
            ? _templateEngine.Render(_modelDirectory!, messages, addGenerationPrompt: true)
            : BuildQwenChatPrompt(messages);
        var stopSequences = (options.StopSequences is { Length: > 0 }
            ? options.StopSequences
            : DefaultStopSequences);
        var includeReasoning = options.IncludeReasoning;

        var work = Task.Run(async () =>
        {
            // Per-handle serialization (mnnbridge.h:22-24 — concurrent calls
            // on the same handle are undefined behaviour). Await with the
            // caller's cancellation so a queued request can be cancelled
            // before it starts running.
            await _generationLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                RunGeneration(prompt, resolvedBudget.MaxTokens, stopSequences, includeReasoning,
                              prefixCacheKey, channel.Writer, ct);
                channel.Writer.TryComplete();
            }
            catch (OperationCanceledException oce)
            {
                channel.Writer.TryComplete(oce);
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
            finally
            {
                _generationLock.Release();
            }
        }, ct);

        await foreach (var f in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return f;

        await work.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ChatResponse> GenerateResponseAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ThrowIfDisposed();

        var started = Environment.TickCount64;
        var content   = new StringBuilder();
        var reasoning = new StringBuilder();

        await foreach (var f in StreamFragmentsAsync(messages, options, ct).ConfigureAwait(false))
        {
            if (f.Kind == ChatFragmentKind.Reasoning) reasoning.Append(f.Text);
            else                                      content.Append(f.Text);
        }

        var latency = TimeSpan.FromMilliseconds(Environment.TickCount64 - started);

        return new ChatResponse(
            Text:             content.ToString(),
            TokensIn:         0,           // MNN does not surface a per-call prompt token count yet.
            TokensOut:        0,           // Streaming count not yet aggregated; bridge estimates.
            Latency:          latency,
            FinishReason:     FinishReason.Stop,
            ReasoningContent: reasoning.Length == 0 ? null : reasoning.ToString());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _model.Dispose();
        _generationLock.Dispose();
        GC.SuppressFinalize(this);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Internals
    // ──────────────────────────────────────────────────────────────────────────

    private unsafe void RunGeneration(
        string prompt,
        int maxTokens,
        string[] stopSequences,
        bool includeReasoning,
        string? prefixCacheKey,
        ChannelWriter<ChatFragment> writer,
        CancellationToken ct)
    {
        // RT-06: prefix-cache check BEFORE reset. If we have a cached session
        // for this (modelId, systemPrompt) pair, load it — the system prefill
        // is already baked in. Otherwise fall through to the legacy reset path.
        bool loadedFromCache = false;
        if (prefixCacheKey is not null && File.Exists(_prefixCache.PathFor(prefixCacheKey)))
        {
            loadedFromCache = MnnInterop.LoadSession(_model, _prefixCache.PathFor(prefixCacheKey));
            if (loadedFromCache) _prefixCache.Touch(prefixCacheKey);
        }

        if (!loadedFromCache)
        {
            // Stateless generation: clear KV cache + sliding-window history before every call.
            // The OpenAI-compatible /v1/chat/completions contract is multi-turn-via-replay
            // (clients send the full message history), so server-side memory between calls
            // would replay the prior request's tokens — which we observed on the shared handle.
            MnnInterop.mnn_llm_reset_session(_model);
        }

        var sink = new MnnTokenSink
        {
            Model            = _model,
            Pending          = new List<byte>(8),
            Emitted          = new StringBuilder(),
            StopSequences    = stopSequences,
            Writer           = writer,
            Ct               = ct,
            IncludeReasoning = includeReasoning,
        };
        var sinkHandle = GCHandle.Alloc(sink);

        try
        {
            maxTokens = Math.Max(1, maxTokens);

            ct.ThrowIfCancellationRequested();

            // mnnbridge.h: (handle, prompt, max_new_tokens, cb, user_data). MNN samples
            // using the model's config.json defaults — no per-call sampling knobs.
            int rc = MnnInterop.mnn_llm_generate_stream_ex(
                _model, prompt, maxTokens, &MnnTokenRouter.OnTokenNative, GCHandle.ToIntPtr(sinkHandle));

            if (rc < 0)
                throw new InvalidOperationException($"MNN generation failed with code {rc}.");

            MnnTokenRouter.DrainRemainder(sink);

            // RT-06: populate the prefix cache after a successful generation.
            // The snapshot includes the system prompt's prefill + this turn's
            // prefill — slightly more than just-system, but close enough to
            // skip the bulk of the cost on the next chat with the same system.
            if (prefixCacheKey is not null && !loadedFromCache)
            {
                try
                {
                    MnnInterop.SaveSession(_model, _prefixCache.PathFor(prefixCacheKey));
                    _ = _prefixCache.EvictIfNeededAsync(ct);
                }
                catch { /* best-effort cache write */ }
            }
        }
        finally
        {
            sinkHandle.Free();
        }
    }

    /// <summary>
    /// Extracts the first system-role message's content from the conversation,
    /// or <c>null</c> if no system message is present. The prefix cache keys
    /// on the system prompt's text.
    /// </summary>
    private static string? ExtractSystemPrompt(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var m in messages)
        {
            if (string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
                return m.Content;
        }
        return null;
    }

    /// <inheritdoc />
    public async Task<bool> SaveSessionAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ThrowIfDisposed();
        // Serialise against ongoing generations on this handle.
        await _generationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return MnnInterop.SaveSession(_model, path);
        }
        finally { _generationLock.Release(); }
    }

    /// <inheritdoc />
    public async Task<bool> LoadSessionAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ThrowIfDisposed();
        await _generationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return MnnInterop.LoadSession(_model, path);
        }
        finally { _generationLock.Release(); }
    }

    /// <summary>
    /// Builds a Qwen ChatML prompt. System / user / assistant turns are each
    /// wrapped in <c>&lt;|im_start|&gt;role\n…\n&lt;|im_end|&gt;\n</c>,
    /// and the final assistant turn is left open for the model to complete.
    /// </summary>
    internal static string BuildQwenChatPrompt(IReadOnlyList<ChatMessage> messages)
    {
        var sb = new StringBuilder(messages.Count * 64);
        foreach (var m in messages)
        {
            var role = string.IsNullOrWhiteSpace(m.Role) ? "user" : m.Role.Trim().ToLowerInvariant();
            sb.Append(ImStart).Append(role).Append('\n');
            sb.Append(m.Content ?? string.Empty);
            sb.Append('\n').Append(ImEnd).Append('\n');
        }
        sb.Append(ImStart).Append("assistant\n");
        return sb.ToString();
    }

    /// <summary>
    /// Drains complete UTF-8 codepoints from the pending byte buffer. Forwards
    /// to <see cref="MnnTokenRouter.TryDrainUtf8"/> — kept here for test
    /// back-compat (<c>QwenTextGeneratorTests</c> calls this name).
    /// </summary>
    internal static bool TryDrainUtf8(List<byte> pending, out string decoded)
        => MnnTokenRouter.TryDrainUtf8(pending, out decoded);

    /// <summary>
    /// Forwards to <see cref="MnnTokenRouter.TryFindStopSequence"/> — kept here
    /// for test back-compat.
    /// </summary>
    internal static bool TryFindStopSequence(StringBuilder sb, string[] stops, out int index)
        => MnnTokenRouter.TryFindStopSequence(sb, stops, out index);

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(QwenTextGenerator));
    }
}
