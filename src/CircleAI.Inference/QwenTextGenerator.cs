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

    /// <summary>
    /// The exact text the KV cache currently holds, or null when it holds
    /// nothing trustworthy.
    /// </summary>
    /// <remarks>
    /// Written and read only under <see cref="_generationLock"/>, which already
    /// serialises every call on this handle, so it needs no synchronisation of
    /// its own. Null is the safe value: it forces a full prefill, which is
    /// always correct and merely slower.
    /// </remarks>
    private string? _kvText;

    /// <summary>How much of <see cref="_kvText"/> was prompt; the rest is answer.</summary>
    private int _kvPromptLength;

    /// <summary>
    /// Whether this platform's native bridge can stream text, or has to be
    /// driven the old way.
    /// </summary>
    /// <remarks>
    /// Static, because the answer is a property of the loaded binary and not of
    /// any one model — probing again for a second model on the same host would
    /// just throw a second time and reach the same conclusion.
    /// </remarks>
    private static volatile bool _streamsText = true;

    /// <summary>
    /// Where the new prompt can be picked up from, given what the cache holds,
    /// or 0 when it cannot be reused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// EXACT ON THE PROMPT, WHITESPACE-TOLERANT ON THE ANSWER — and the split
    /// matters, because the two halves come from different places and only one
    /// of them is under anyone's control.
    /// </para>
    /// <para>
    /// The prompt is rendered by the template on both calls and must match byte
    /// for byte; any difference there is a real change of instructions.
    /// </para>
    /// <para>
    /// The answer is not. The cache holds what the MODEL emitted; the new prompt
    /// holds what the CALLER stored and the template then re-rendered. Measured
    /// on a P30 Lite, those differed by one character — the model began " The
    /// capital of France is Paris." with a leading space, and the history kept it
    /// without. Divergence at index 407 of 439, on the first character of the
    /// reply, on every single turn. An exact comparison can therefore never
    /// succeed for any model that opens with whitespace, which is most of them.
    /// </para>
    /// <para>
    /// Tolerating whitespace here is safe in a way that tolerating anything else
    /// would not be: the differing tokens are the assistant's own spacing in its
    /// own prior turn, and the words either side are identical. If a single word
    /// differs, this returns 0 and the whole conversation is prefilled again.
    /// </para>
    /// </remarks>
    private static int ResumePoint(string kvText, int kvPromptLength, string fullPrompt)
    {
        if (kvPromptLength <= 0 || kvPromptLength > kvText.Length) return 0;
        if (fullPrompt.Length <= kvPromptLength) return 0;

        // The instructions and every settled turn: exact.
        if (string.CompareOrdinal(kvText, 0, fullPrompt, 0, kvPromptLength) != 0) return 0;

        var cachedAnswer = kvText.AsSpan(kvPromptLength);

        // Walk the new prompt from the same point, matching the cached answer's
        // non-whitespace characters in order. Whitespace on either side is
        // skipped rather than required to line up.
        int i = kvPromptLength, a = 0;
        while (a < cachedAnswer.Length)
        {
            if (char.IsWhiteSpace(cachedAnswer[a])) { a++; continue; }
            while (i < fullPrompt.Length && char.IsWhiteSpace(fullPrompt[i])) i++;
            if (i >= fullPrompt.Length || fullPrompt[i] != cachedAnswer[a]) return 0;
            i++; a++;
        }

        // Everything the cache holds is accounted for, and there is genuinely new
        // text after it. Anything less means nothing to gain.
        return i < fullPrompt.Length ? i : 0;
    }

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

        // The FIRST P/Invoke is where a missing native runtime surfaces. Left
        // bare it throws "DllNotFoundException: mnnbridge", which says nothing
        // about the real cause — on Android that is a build-time packaging
        // omission, and diagnosing it once cost an APK teardown.
        MnnModelHandle handle;
        try
        {
            handle = MnnInterop.mnn_llm_create(modelPath);
        }
        catch (DllNotFoundException ex)
        {
            throw MnnNativeDiagnostics.Explain(ex, modelPath);
        }
        catch (EntryPointNotFoundException ex)
        {
            // Library loaded but is the wrong build / an older ABI.
            throw MnnNativeDiagnostics.Explain(ex, modelPath);
        }

        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new InvalidOperationException(
                $"MNN failed to create model from '{modelPath}'. " +
                "Verify the file is a valid GGUF or MNN model and that " +
                "libmnnbridge is on the native library search path.");
        }

        // MEMORY-MAP THE WEIGHTS instead of reading them into the heap.
        //
        // RT-03 built this and nothing ever called it, so every model was loaded
        // eagerly and held in full — 975 MB resident for a 1.5B on a phone with
        // 3.7 GB, most of which is weights the kernel could have paged from the
        // file on demand and dropped again under pressure.
        //
        // Before load, because MNN reads the flag while loading. Best-effort: an
        // older bridge without the export, or a filesystem that cannot map, must
        // degrade to the eager path rather than fail the model.
        try
        {
            var mmap = new MmapWeightLoader(handle.DangerousGetHandle());
            // Scratch beside the model. It has to be writable by this process,
            // and the model directory already is — the bundle was downloaded
            // into it. MNN reads tmp_path before it honours use_mmap, so the
            // order here is not cosmetic.
            var scratch = Path.Combine(Path.GetDirectoryName(modelPath) ?? ".", "mmap");
            Directory.CreateDirectory(scratch);
            mmap.UseScratch(scratch);
            mmap.Enable();
        }
        catch { /* older bridge or unmappable store — eager load is still correct */ }

        // NO THINKING OUT LOUD. The bundles ship enable_thinking:true in their own
        // jinja context, so a reasoning model deliberates in front of the person
        // and — because MNN emits that as prose, not <think> tags — the reasoning
        // router never sees it. On a phone that decodes about seven tokens a
        // second this is not a quality setting: measured on the P30, one factual
        // question spent its entire 160-token budget arguing with the system
        // prompt and never answered. Before load, like every other config read.
        try { new MnnRuntimeConfig(handle.DangerousGetHandle()).TryDisableThinking(); }
        catch { /* bundle without a jinja context — a thinking model still works */ }

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
        var fullPrompt = (_templateEngine is not null && !string.IsNullOrEmpty(_modelDirectory))
            ? _templateEngine.Render(_modelDirectory!, messages, addGenerationPrompt: true)
            : BuildQwenChatPrompt(messages);

        // CONTINUE INSTEAD OF RE-READING, when this is provably the same
        // conversation carrying on. _kvText is the exact text the KV cache
        // currently represents — the last prompt plus the answer that was
        // generated from it. If the new prompt starts with that verbatim, then
        // everything before the new turn is already prefilled and only the tail
        // needs feeding.
        //
        // A STRING COMPARISON, DELIBERATELY, rather than tracking message counts
        // or hashing the list. It is exact, it is template-agnostic, and it fails
        // closed: an edited earlier turn, a changed system prompt or a fresh chat
        // simply stops matching and falls through to the full prefill below.
        var prompt      = fullPrompt;
        var continuing  = false;
        if (options.ContinueConversation && _kvText is { Length: > 0 })
        {
            var resume = ResumePoint(_kvText, _kvPromptLength, fullPrompt);
            if (resume > 0)
            {
                prompt     = fullPrompt[resume..];
                continuing = true;
            }
        }

        // Whether reuse fired is visible without logging anything a person said:
        // the bridge already reports the token count it was handed, and a turn
        // that reused the cache feeds only its own question — tens of tokens
        // instead of the whole transcript.
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
                var answer = RunGeneration(prompt, resolvedBudget.MaxTokens, stopSequences,
                                           includeReasoning, prefixCacheKey, channel.Writer,
                                           continuing, ct);

                // What the KV cache now represents: everything that was fed to
                // it, plus everything it produced. Recorded only when the caller
                // asked to continue — otherwise the next call must not think it
                // can skip a prefill that the reset is about to discard.
                _kvText = options.ContinueConversation ? fullPrompt + answer : null;
                _kvPromptLength = options.ContinueConversation ? fullPrompt.Length : 0;

                channel.Writer.TryComplete();
            }
            catch (OperationCanceledException oce)
            {
                // A cancelled or failed generation leaves the KV cache in a state
                // no string describes — it holds a partial answer that no caller
                // ever saw. Forget it, so the next call prefills in full rather
                // than continuing from something that is not there.
                _kvText = null;
                channel.Writer.TryComplete(oce);
            }
            catch (Exception ex)
            {
                _kvText = null;
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

    /// <returns>The answer text, so the caller can record what the KV now holds.</returns>
    private unsafe string RunGeneration(
        string prompt,
        int maxTokens,
        string[] stopSequences,
        bool includeReasoning,
        string? prefixCacheKey,
        ChannelWriter<ChatFragment> writer,
        bool continuing,
        CancellationToken ct)
    {
        // RT-06: prefix-cache check BEFORE reset. If we have a cached session
        // for this (modelId, systemPrompt) pair, load it — the system prefill
        // is already baked in. Otherwise fall through to the legacy reset path.
        //
        // Skipped entirely when continuing: the live KV cache already holds this
        // conversation, which is strictly more than any snapshot of the system
        // prompt alone, and loading one would throw the conversation away.
        bool loadedFromCache = false;
        if (!continuing && prefixCacheKey is not null && File.Exists(_prefixCache.PathFor(prefixCacheKey)))
        {
            loadedFromCache = MnnInterop.LoadSession(_model, _prefixCache.PathFor(prefixCacheKey));
            if (loadedFromCache) _prefixCache.Touch(prefixCacheKey);
        }

        if (!loadedFromCache && !continuing)
        {
            // Stateless generation: clear KV cache + sliding-window history before every call.
            // The OpenAI-compatible /v1/chat/completions contract is multi-turn-via-replay
            // (clients send the full message history), so server-side memory between calls
            // would replay the prior request's tokens — which we observed on the shared handle.
            //
            // NOT WHEN CONTINUING. The retained state that is wrong for a server
            // sharing one handle between clients is exactly what a phone holding
            // one conversation wants: the caller has proven this prompt continues
            // the last one, so the earlier turns are already in the cache and the
            // only new text is the tail being fed now.
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

            // THE STREAMING ENTRY POINT, not the one named for it. MNN samples
            // using the model's config.json defaults — no per-call sampling knobs.
            //
            // mnn_llm_generate_stream_ex hands over the whole answer once it is
            // finished, so a person waits in silence for the entire generation
            // and then receives it at once. This one delivers text as MNN decodes
            // it, which is what every layer above has been built to consume.
            //
            // FALL BACK RATHER THAN CRASH ON AN OLDER BRIDGE. This library ships
            // a native binary per platform — Android arm64 and armv7, Windows,
            // Linux x64 and arm64, macOS Intel and Apple silicon, iOS — and they
            // are built on different machines at different times. A managed
            // release that adds an export will always reach some host whose .so
            // predates it, and a missing entry point is a hard crash on the
            // person's FIRST question.
            //
            // Slow is a bad day. Crashing is a broken product. The old export is
            // still there and still correct; it just cannot stream, so an
            // unrefreshed platform waits for the whole answer instead of failing.
            int rc;
            if (_streamsText)
            {
                try
                {
                    rc = MnnInterop.mnn_llm_generate_stream_text(
                        _model, prompt, maxTokens, &MnnTokenRouter.OnTextNative,
                        GCHandle.ToIntPtr(sinkHandle));
                }
                catch (EntryPointNotFoundException)
                {
                    // Decided once per process, not once per turn: the probe
                    // costs a thrown exception and the answer never changes for
                    // a given binary.
                    _streamsText = false;
                    rc = MnnInterop.mnn_llm_generate_stream_ex(
                        _model, prompt, maxTokens, &MnnTokenRouter.OnTokenNative,
                        GCHandle.ToIntPtr(sinkHandle));
                }
            }
            else
            {
                rc = MnnInterop.mnn_llm_generate_stream_ex(
                    _model, prompt, maxTokens, &MnnTokenRouter.OnTokenNative,
                    GCHandle.ToIntPtr(sinkHandle));
            }

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

            // What the model actually said, so the caller can record what the KV
            // cache now contains. Taken from the sink rather than re-derived,
            // because the sink is what the stop-sequence trimming ran against —
            // anything else would describe text the cache does not hold.
            return sink.Emitted.ToString();
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
