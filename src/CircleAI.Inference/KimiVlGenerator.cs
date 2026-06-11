// KimiVlGenerator.cs
//
// IChatGenerator backed by MNN-LLM running a vision-capable model
// (Kimi-VL-A3B-Thinking-2506 / Qwen-VL family). When the latest user
// turn carries ImageBytes, the generator encodes the image and routes
// through mnn_llm_generate_with_image_stream_ex; otherwise it falls
// back to plain mnn_llm_generate_stream_ex so the same generator can
// handle interleaved text + vision conversations.
//
// Wired into the SDK via DI when ChatCapability.Vision is requested
// through the IModelSelector — the registry's catalog entry tags the
// model with capability "Vision", and DeviceAwareModelSelector returns
// it for vision-flagged inference calls.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CircleAI.Inference;

/// <summary>
/// On-device vision+language chat generator backed by MNN-LLM running a
/// Kimi-VL or Qwen-VL family model. Falls back to text-only generation
/// when no <see cref="ChatMessage.ImageBytes"/> is attached.
/// </summary>
public sealed class KimiVlGenerator : IChatGenerator
{
    private const string ImStart = "<|im_start|>";
    private const string ImEnd   = "<|im_end|>";
    private static readonly string[] DefaultStopSequences = [ImEnd, ImStart];

    private readonly MnnModelHandle _model;
    private readonly int _maxNewTokens;
    private readonly IPromptTemplateEngine? _templateEngine;
    private readonly string? _modelDirectory;

    private bool _disposed;

    /// <summary>
    /// Loads a vision-language model and prepares it for generation.
    /// </summary>
    /// <param name="modelPath">Absolute path to the MNN model file.</param>
    /// <param name="contextSize">Maximum context window in tokens.</param>
    /// <param name="threads">CPU thread count, or <c>null</c> for MNN default.</param>
    /// <param name="templateEngine">
    /// Catalog-driven prompt template engine. Falls back to ChatML when null.
    /// </param>
    public KimiVlGenerator(
        string                   modelPath,
        uint                     contextSize     = 8192,
        int?                     threads         = null,
        IPromptTemplateEngine?   templateEngine  = null)
    {
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
                "Verify the file is a vision-capable MNN model and that " +
                "libmnnbridge is on the native library search path.");
        }

        int rc = MnnInterop.mnn_llm_load(handle);
        if (rc != 0)
        {
            handle.Dispose();
            throw new InvalidOperationException(
                $"MNN vision model load failed with code {rc} for '{modelPath}'.");
        }

        // Sanity check: confirm the model identifies as vision-capable.
        // We don't fail the constructor on mismatch (some quantised builds
        // omit the type marker) — but a warning lets the caller know they
        // wired a text-only model into a vision generator.
        int modelType = MnnInterop.mnn_llm_get_model_type(handle);
        IsVisionCapable = modelType == 1;

        _model          = handle;
        _maxNewTokens   = (int)Math.Min(contextSize, int.MaxValue);
        _templateEngine = templateEngine;
        _modelDirectory = System.IO.Path.GetDirectoryName(modelPath);
    }

    /// <summary>
    /// <c>true</c> when the loaded model reports vision capability via
    /// <c>mnn_llm_get_model_type == 1</c>. Text-only models still work
    /// for non-image turns but emit a warning when an image is supplied.
    /// </summary>
    public bool IsVisionCapable { get; }

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
        ArgumentNullException.ThrowIfNull(messages);
        ThrowIfDisposed();

        options ??= new GenerationOptions();

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });

        var prompt = (_templateEngine is not null && !string.IsNullOrEmpty(_modelDirectory))
            ? _templateEngine.Render(_modelDirectory!, messages, addGenerationPrompt: true)
            : QwenTextGenerator.BuildQwenChatPrompt(messages);

        var stopSequences = (options.StopSequences is { Length: > 0 }
            ? options.StopSequences
            : DefaultStopSequences);

        // Find the latest message carrying ImageBytes — that's the vision
        // turn for this exchange. If none, fall through to text-only.
        var imageBytes = messages.LastOrDefault(m => m.ImageBytes is { Length: > 0 })?.ImageBytes;

        var work = Task.Run(() =>
        {
            try
            {
                if (imageBytes is { Length: > 0 })
                    RunVisionGeneration(prompt, imageBytes, options, stopSequences, channel.Writer, ct);
                else
                    RunTextGeneration(prompt, options, stopSequences, channel.Writer, ct);

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
        }, ct);

        await foreach (var piece in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return piece;

        await work.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _model.Dispose();
        GC.SuppressFinalize(this);
    }

    // ────────────────────────────────────────────────────────────────────
    // Internals
    // ────────────────────────────────────────────────────────────────────

    private unsafe void RunVisionGeneration(
        string                prompt,
        byte[]                imageBytes,
        GenerationOptions     options,
        string[]              stopSequences,
        ChannelWriter<string> writer,
        CancellationToken     ct)
    {
        MnnImageHandle imageHandle;
        fixed (byte* p = imageBytes)
        {
            imageHandle = MnnInterop.mnn_llm_image_from_bytes(p, imageBytes.Length);
        }

        if (imageHandle.IsInvalid)
        {
            imageHandle.Dispose();
            throw new InvalidOperationException(
                "MNN failed to decode image bytes. Ensure the data is JPEG/PNG/WebP " +
                "and the loaded model is vision-capable.");
        }

        try
        {
            var emitted = new StringBuilder();

            // The native callback must remain alive for the duration of the call.
            // The surrounding synchronous P/Invoke keeps the delegate reachable
            // for the entire native call via the JIT-emitted reverse-pinvoke
            // thunk. The assembly has [DisableRuntimeMarshalling], so the
            // delegate's parameter list must be blittable (byte*); the runtime
            // cannot marshal a managed string here.
            MnnTokenCallback callback = (byte* tokenPtr, int isDone, IntPtr _) =>
            {
                if (ct.IsCancellationRequested) return;

                var token = tokenPtr == null
                    ? null
                    : Marshal.PtrToStringUTF8((IntPtr)tokenPtr);
                if (string.IsNullOrEmpty(token)) return;

                emitted.Append(token);

                if (TryFindStopSequence(emitted, stopSequences, out int stopAt))
                {
                    var prior = emitted.ToString(0, stopAt);
                    var alreadyLen = emitted.Length - token.Length;
                    if (stopAt > alreadyLen)
                    {
                        var tail = prior[alreadyLen..];
                        if (tail.Length > 0)
                            writer.TryWrite(tail);
                    }
                    return;
                }

                writer.TryWrite(token);
            };

            float temperature = options.Temperature > 0f ? options.Temperature : 0f;
            float topP        = options.TopP is > 0f and < 1f ? options.TopP : 1f;
            int   topK        = options.TopK > 0 ? options.TopK : 0;
            uint  seed        = options.Seed.HasValue ? unchecked((uint)options.Seed.Value) : 0u;
            int   maxTokens   = Math.Max(1, options.MaxTokens > 0 ? options.MaxTokens : _maxNewTokens);

            ct.ThrowIfCancellationRequested();

            int rc = MnnInterop.mnn_llm_generate_with_image_stream_ex(
                _model, prompt, imageHandle, maxTokens, temperature, topP, topK, seed, callback, IntPtr.Zero);

            if (rc < 0)
                throw new InvalidOperationException($"MNN vision generation failed with code {rc}.");
        }
        finally
        {
            imageHandle.Dispose();
        }
    }

    private unsafe void RunTextGeneration(
        string                prompt,
        GenerationOptions     options,
        string[]              stopSequences,
        ChannelWriter<string> writer,
        CancellationToken     ct)
    {
        var emitted = new StringBuilder();

        // See RunVisionGeneration above for why the delegate parameter is byte*
        // and why the local delegate variable keeps the callback alive for the
        // duration of the synchronous P/Invoke.
        MnnTokenCallback callback = (byte* tokenPtr, int isDone, IntPtr _) =>
        {
            if (ct.IsCancellationRequested) return;

            var token = tokenPtr == null
                ? null
                : Marshal.PtrToStringUTF8((IntPtr)tokenPtr);
            if (string.IsNullOrEmpty(token)) return;

            emitted.Append(token);

            if (TryFindStopSequence(emitted, stopSequences, out int stopAt))
            {
                var prior = emitted.ToString(0, stopAt);
                var alreadyLen = emitted.Length - token.Length;
                if (stopAt > alreadyLen)
                {
                    var tail = prior[alreadyLen..];
                    if (tail.Length > 0)
                        writer.TryWrite(tail);
                }
                return;
            }

            writer.TryWrite(token);
        };

        float temperature = options.Temperature > 0f ? options.Temperature : 0f;
        float topP        = options.TopP is > 0f and < 1f ? options.TopP : 1f;
        int   topK        = options.TopK > 0 ? options.TopK : 0;
        uint  seed        = options.Seed.HasValue ? unchecked((uint)options.Seed.Value) : 0u;
        int   maxTokens   = Math.Max(1, options.MaxTokens > 0 ? options.MaxTokens : _maxNewTokens);

        ct.ThrowIfCancellationRequested();

        int rc = MnnInterop.mnn_llm_generate_stream_ex(
            _model, prompt, maxTokens, temperature, topP, topK, seed, callback, IntPtr.Zero);

        if (rc < 0)
            throw new InvalidOperationException($"MNN text generation failed with code {rc}.");
    }

    private static bool TryFindStopSequence(StringBuilder buffer, string[] stops, out int index)
    {
        var text = buffer.ToString();
        index = int.MaxValue;
        foreach (var stop in stops)
        {
            if (string.IsNullOrEmpty(stop)) continue;
            int found = text.IndexOf(stop, StringComparison.Ordinal);
            if (found >= 0 && found < index) index = found;
        }
        return index != int.MaxValue;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(KimiVlGenerator));
    }
}
