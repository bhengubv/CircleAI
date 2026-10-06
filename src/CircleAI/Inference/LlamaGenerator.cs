#nullable enable

// LlamaGenerator.cs
//
// IChatGenerator backed by llama.cpp through native/llama-bridge — the GGUF
// door, alongside the MNN one.
//
// WHY IT EXISTS. Measured 2026-09-25: Bonsai 2 27B
// (prism-ml/Ternary-Bonsai-2-27B-gguf) has architecture "qwen35", which no MNN
// family covers, and 402 of its tensors carry a custom ternary type MNN has no
// dequant for. Splitting the graph so the unsupported GatedDeltaNet blocks ran
// outside MNN was measured and rejected: 48 of its 64 blocks carry SSM
// tensors, so a split means 49 graph segments and 96 managed/native crossings
// marshalling ~1.97 MB PER TOKEN. (The ToucanTTS precedent that made splitting
// work for TTS had ONE crossing per utterance.) GGUF is where most of the open
// ecosystem ships, so running it beats converting it.
//
// NOT AN MNN REPLACEMENT. MNN stays the default: its bundles are what the
// catalogue ships and what the P30 is proven on. This is for models MNN cannot
// read.
//
// ONE CALL PER HANDLE. llamabridge.h declares concurrent calls on the same
// handle undefined, exactly as mnnbridge does, so every native entry point
// here goes through _gate.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CircleAI.Inference;

/// <summary>
/// On-device chat generator backed by llama.cpp (GGUF models).
/// </summary>
public sealed class LlamaGenerator : IChatGenerator
{
    private const string ImStart = "<|im_start|>";
    private const string ImEnd   = "<|im_end|>";

    private readonly LlamaModelHandle _model;
    private readonly SemaphoreSlim    _gate = new(1, 1);
    private readonly int              _defaultMaxTokens;
    private bool _disposed;

    /// <summary>The GGUF's own <c>general.architecture</c> string.</summary>
    /// <remarks>
    /// Surfaced because it is what separates "this build cannot run it" from
    /// "this model is unsupported". MNN never exposed the distinction, and the
    /// cost was real: a ternary model looked like a conversion problem for far
    /// longer than it should have.
    /// </remarks>
    public string Architecture { get; }

    /// <summary>Context window the model was loaded with.</summary>
    public int ContextSize { get; }

    /// <summary>
    /// Bridge + llama.cpp build string, or null when the native library is
    /// absent. Null means this device has no GGUF backend — a fact to report,
    /// not a crash.
    /// </summary>
    public static string? NativeVersion => LlamaInterop.TryGetVersion();

    /// <summary>True when the native bridge is present and loadable.</summary>
    public static bool IsAvailable => NativeVersion is not null;

    /// <summary>Loads a GGUF model.</summary>
    /// <param name="ggufPath">Absolute path to the .gguf file.</param>
    /// <param name="contextSize">Context window; 0 takes the model's own.</param>
    /// <param name="threads">CPU threads; 0 lets the bridge choose.</param>
    /// <param name="maxNewTokens">Default cap when a call supplies none.</param>
    public LlamaGenerator(string ggufPath, int contextSize = 0, int threads = 0, int maxNewTokens = 512)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ggufPath);
        if (!System.IO.File.Exists(ggufPath))
            throw new System.IO.FileNotFoundException("GGUF model not found.", ggufPath);

        _defaultMaxTokens = maxNewTokens > 0 ? maxNewTokens : 512;

        _model = LlamaInterop.Create(ggufPath, contextSize, threads);
        if (_model.IsInvalid)
        {
            _model.Dispose();
            throw new InvalidOperationException(
                $"llama-bridge could not open '{ggufPath}'. Verify the file is a GGUF and that " +
                "libllamabridge is on the native library search path.");
        }

        var rc = LlamaInterop.Load(_model);
        if (rc != 0)
        {
            _model.Dispose();
            throw new InvalidOperationException(
                $"llama-bridge load failed with code {rc} for '{ggufPath}'. " +
                (rc == -2 ? "The model file could not be read — a custom quantisation type " +
                            "(for example a ternary fork's) needs the matching llama.cpp build."
                          : string.Empty));
        }

        ContextSize  = Math.Max(0, LlamaInterop.GetContextSize(_model));
        Architecture = ReadArch();
    }

    private string ReadArch()
    {
        Span<byte> buf = stackalloc byte[128];
        var n = LlamaInterop.GetArch(_model, buf, buf.Length);
        return n <= 0 ? string.Empty : Encoding.UTF8.GetString(buf[..n]);
    }

    /// <inheritdoc/>
    public async Task<string> GenerateAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions? options = null,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await foreach (var piece in StreamAsync(messages, options, ct).ConfigureAwait(false))
            sb.Append(piece);
        return sb.ToString();
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var prompt = BuildPrompt(messages);
        var max    = options?.MaxTokens is > 0 ? options.MaxTokens : _defaultMaxTokens;

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        // The native call blocks for the whole generation, so it runs off the
        // caller's thread and feeds the channel as pieces arrive.
        _ = Task.Run(() =>
        {
            // The state the unmanaged callback needs, reached through a pinned
            // GCHandle. It cannot be a captured lambda: this assembly disables
            // runtime marshalling, so the callback has to be a plain function
            // pointer with no managed closure behind it.
            var state  = new StreamState(channel.Writer, ct);
            var gch    = GCHandle.Alloc(state);
            var handle = _model.DangerousGetHandle();

            try
            {
                var rc = InvokeNative(handle, prompt, max, gch);

                if (rc < 0)
                    channel.Writer.TryComplete(
                        new InvalidOperationException($"llama-bridge generation failed with code {rc}."));
                else
                    channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
            finally
            {
                gch.Free();
                _gate.Release();
            }
        }, CancellationToken.None);

        await foreach (var piece in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return piece;
    }

    /// <summary>
    /// The native call, isolated so pointer work is the ONLY unsafe code here.
    /// <c>StreamAsync</c> must stay an ordinary async iterator — C# forbids
    /// <c>await</c> inside an unsafe context (CS4004), so marking the whole
    /// class unsafe does not work.
    /// </summary>
    private static unsafe int InvokeNative(IntPtr handle, string prompt, int maxTokens, GCHandle state)
    {
        // NUL-terminated: the bridge takes a C string.
        var bytes = Encoding.UTF8.GetBytes(prompt + "\0");
        fixed (byte* p = bytes)
        {
            return LlamaInterop.GenerateStreamText(
                handle, p, maxTokens, &OnPiece, (void*)GCHandle.ToIntPtr(state));
        }
    }

    /// <summary>What the unmanaged callback needs, reached via a GCHandle.</summary>
    private sealed record StreamState(ChannelWriter<string> Writer, CancellationToken Ct);

    /// <summary>
    /// The streaming callback, as an UNMANAGED function pointer — not a
    /// delegate. This assembly disables runtime marshalling (CA1420 is an
    /// error here), because a marshalled delegate corrupted the stack and
    /// showed up as a 0xC0000005 on the first streamed token.
    /// </summary>
    /// <remarks>
    /// Must never throw: it is called from native code, and an exception
    /// crossing that boundary terminates the process. Returning non-zero is
    /// how cancellation stops generation cleanly.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int OnPiece(byte* text, int len, void* user)
    {
        try
        {
            if (user == null) return 1;
            var state = GCHandle.FromIntPtr((IntPtr)user).Target as StreamState;
            if (state is null) return 1;
            if (state.Ct.IsCancellationRequested) return 1;   // non-zero stops generation

            if (text != null && len > 0)
            {
                var s = Encoding.UTF8.GetString(text, len);
                if (!string.IsNullOrEmpty(s)) state.Writer.TryWrite(s);
            }
            return 0;
        }
        catch
        {
            // Never let an exception reach native code.
            return 1;
        }
    }

    /// <summary>
    /// ChatML, the format the Qwen-family GGUFs this targets are trained on.
    /// A model whose GGUF carries its own chat template should use that — a
    /// follow-up, once there is a second family to test against. Guessing a
    /// template now would be inventing a requirement.
    /// </summary>
    private static string BuildPrompt(IReadOnlyList<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            var role = string.IsNullOrWhiteSpace(m.Role) ? "user" : m.Role.ToLowerInvariant();
            sb.Append(ImStart).Append(role).Append('\n')
              .Append(m.Content ?? string.Empty).Append(ImEnd).Append('\n');
        }
        sb.Append(ImStart).Append("assistant\n");
        return sb.ToString();
    }

    /// <summary>Clears the KV cache so the next prompt starts clean.</summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _gate.Wait();
        try { LlamaInterop.Reset(_model); }
        finally { _gate.Release(); }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _model.Dispose();
        _gate.Dispose();
    }
}
