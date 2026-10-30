// ManagedChatGenerator.cs
//
// The C# engine behind IChatGenerator. A conversation becomes a prompt through the model's own
// chat template (JinjaLite) and ids through its tokenizer (ByteLevelBpe); Qwen35Model runs them
// and TokenSampler picks each next token. The reply streams back as fragments - content and
// reasoning split on the model's own <think> and </think> tokens, so thinking never leaks into
// an answer. Every member of the interface is implemented here rather than inherited:
// LlamaGenerator leaked <think> text precisely because it never overrode StreamFragmentsAsync.
//
// THE PERSONA IS A REAL SYSTEM TURN. AIService sends the persona as a system message; every
// system message is merged into the one system turn the template takes, first, which is how
// the model was trained. QwenTextGenerator has to put the persona's words in the user turn
// because the MNN bridge renders from the user turn alone; this engine renders the whole
// conversation itself.
//
// Thinking is off unless the generator is made with it on, as QwenTextGenerator keeps it off:
// on the P30 one factual question once spent its whole budget thinking and never answered.
//
// THE STATE IS KEPT BETWEEN CALLS AND REUSED ONLY WHERE THE TOKENS MATCH. A prompt runs from
// the first token the state does not already hold: after all of it, when the state holds the
// start of the new prompt; else after the mark the state took where the last turn began (a
// recurrent model cannot step back to any other point, and the next turn's prompt matches up
// to that one, because the template writes the history back the same way each time); else from
// the beginning. Checked token by token, the reuse needs neither ContinueConversation nor
// UsePrefixCache to make it safe. One call at a time.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using CircleAI.Core.Logging;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Model;
using CircleAI.Inference.Managed.Runtime;
using CircleAI.Inference.Managed.Sampling;
using CircleAI.Inference.Managed.Text;

namespace CircleAI.Inference.Managed;

/// <summary>How a <see cref="ManagedChatGenerator"/> is set up.</summary>
public sealed record ManagedGeneratorOptions
{
    /// <summary>Tokens one conversation can hold, prompt and reply together; at most the model's own.</summary>
    public int ContextLength { get; init; } = 4096;

    /// <summary>
    /// Threads the products share. 0 picks: 3 on Android - the P30's measured best, its four big
    /// cores with one left for the screen - and every core but one elsewhere.
    /// </summary>
    public int Threads { get; init; }

    /// <summary>Whether the model thinks before it answers (the template's enable_thinking).</summary>
    public bool Thinking { get; init; }

    /// <summary>Reply tokens when a call asks for none.</summary>
    public int MaxNewTokens { get; init; } = 512;
}

/// <summary>Bonsai 2 (qwen35) run by CircleAI's own C# engine, behind <see cref="IChatGenerator"/>.</summary>
public sealed class ManagedChatGenerator : IChatGenerator
{
    private const string Area = "Engine";
    private const ulong SessionMagic = 0x314E_4547_4D49_4143; // "CAIMGEN1", little-endian

    // End-of-generation tokens beside the file's eos, as the fork collects them (llama-vocab.cpp).
    private static readonly string[] EndOfGenerationTexts =
        ["<|im_end|>", "<|endoftext|>", "<|eot_id|>", "<|eom_id|>", "<|end|>", "<end_of_turn>", "<|fim_pad|>", "<|repo_name|>", "<|file_sep|>"];

    private readonly GgufFile _file;
    private readonly WorkerPool _pool;
    private readonly Qwen35Model _model;
    private readonly ByteLevelBpe _tokenizer;
    private readonly JinjaTemplate _template;
    private readonly ManagedGeneratorOptions _options;
    private readonly HashSet<int> _endOfGeneration = [];
    private readonly int _turnStart;
    private readonly int _thinkStart;
    private readonly int _thinkEnd;
    private readonly float[] _logits;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<int> _history = []; // the tokens the state holds, in order
    private Qwen35State _state;
    private int _disposed;

    private ManagedChatGenerator(GgufFile file, WorkerPool pool, Qwen35Model model, ByteLevelBpe tokenizer,
        JinjaTemplate template, ManagedGeneratorOptions options)
    {
        _file = file;
        _pool = pool;
        _model = model;
        _tokenizer = tokenizer;
        _template = template;
        _options = options;
        _logits = new float[model.Config.Vocabulary];
        _state = model.NewState(options.ContextLength);

        if (file.TryGetValue("tokenizer.ggml.eos_token_id", out var eos)) _endOfGeneration.Add(checked((int)eos.AsInt64()));
        foreach (var text in EndOfGenerationTexts)
        {
            if (tokenizer.TryGetSpecial(text, out var id) && tokenizer.Kind(id) == TokenKind.Control) _endOfGeneration.Add(id);
        }
        if (_endOfGeneration.Count == 0) throw new NotSupportedException("The model names no end-of-generation token.");
        _turnStart = tokenizer.TryGetSpecial("<|im_start|>", out var turn) ? turn : -1;
        _thinkStart = tokenizer.TryGetSpecial("<think>", out var start) ? start : -1;
        _thinkEnd = tokenizer.TryGetSpecial("</think>", out var end) ? end : -1;
    }

    /// <summary>The model's shape.</summary>
    public Qwen35Config Config => _model.Config;

    /// <summary>Threads the products share.</summary>
    public int Threads => _pool.Threads;

    /// <summary>Tokens one conversation can hold.</summary>
    public int ContextLength => _state.Capacity;

    internal ByteLevelBpe Tokenizer => _tokenizer;

    /// <summary>Opens <paramref name="ggufPath"/> and makes the model, its tokenizer and its chat template ready.</summary>
    public static ManagedChatGenerator Load(string ggufPath, ManagedGeneratorOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ggufPath);
        options ??= new ManagedGeneratorOptions();
        var started = Stopwatch.GetTimestamp();
        var file = GgufFile.Open(ggufPath);
        try
        {
            var threads = options.Threads > 0 ? options.Threads : OperatingSystem.IsAndroid() ? 3 : Math.Max(1, Environment.ProcessorCount - 1);
            var pool = new WorkerPool(threads);
            try
            {
                var model = Qwen35Model.Load(file, pool);
                var tokenizer = ByteLevelBpe.FromGguf(file);
                var template = JinjaTemplate.Parse(file["tokenizer.chat_template"].AsString());
                var generator = new ManagedChatGenerator(file, pool, model, tokenizer, template, options);
                var thinking = options.Thinking ? "on" : "off";
                CircleLog.Info(Area, Invariant($"managed engine ready: {Path.GetFileName(ggufPath)}, {model.Config.Layers} layers, {threads} threads, context {generator._state.Capacity}, thinking {thinking}, in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms"));
                return generator;
            }
            catch
            {
                pool.Dispose();
                throw;
            }
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<string> GenerateAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null, CancellationToken ct = default) =>
        (await RunAsync(messages, options, null, ct).ConfigureAwait(false)).Content;

    /// <inheritdoc/>
    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var fragment in StreamFragmentsAsync(messages, options, ct).ConfigureAwait(false))
        {
            if (fragment.Kind == ChatFragmentKind.Content) yield return fragment.Text;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatFragment> StreamFragmentsAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<ChatFragment>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = RunAsync(messages, options, fragment => channel.Writer.TryWrite(fragment), stop.Token);
        _ = run.ContinueWith(t => channel.Writer.TryComplete(t.Exception?.InnerException),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        var read = false;
        try
        {
            await foreach (var fragment in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return fragment;
            read = true;
        }
        finally
        {
            // The caller stopped reading early, or reading failed: stop the run and let it give
            // the engine back before this returns.
            if (!read)
            {
                stop.Cancel();
                try { await run.ConfigureAwait(false); }
                catch { /* the reader's own exception goes on; an abandoned run's is not wanted */ }
            }
        }
        await run.ConfigureAwait(false); // the run's own end: finished, cancelled or failed
    }

    /// <inheritdoc/>
    public async Task<ChatResponse> GenerateResponseAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null, CancellationToken ct = default)
    {
        var outcome = await RunAsync(messages, options, null, ct).ConfigureAwait(false);
        return new ChatResponse(outcome.Content, outcome.TokensIn, outcome.TokensOut, outcome.Elapsed, outcome.Finish, outcome.Reasoning);
    }

    /// <summary>
    /// Writes the conversation to <paramref name="path"/> as far as its next turn can use it: up to
    /// where the last turn began (the template writes a reply back into the history differently
    /// from how it was generated), or all of it when no turn began.
    /// </summary>
    public async Task<bool> SaveSessionAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await Task.Run(() =>
            {
                var atMark = _state.MarkedPosition > 0;
                var count = atMark ? _state.MarkedPosition : _history.Count;
                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
                writer.Write(SessionMagic);
                writer.Write(_file.Length);
                writer.Write(_model.Config.Vocabulary);
                writer.Write(count);
                for (var i = 0; i < count; i++) writer.Write(_history[i]);
                _state.Write(writer, _model.Config, atMark);
            }, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Restores a conversation <see cref="SaveSessionAsync"/> wrote for this model. False when
    /// there is no such file; a file written for another model is refused.
    /// </summary>
    public async Task<bool> LoadSessionAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) return false;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var (history, state) = await Task.Run(() =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
                if (reader.ReadUInt64() != SessionMagic) throw new InvalidDataException($"{path} is not a managed-engine session.");
                if (reader.ReadInt64() != _file.Length || reader.ReadInt32() != _model.Config.Vocabulary)
                    throw new InvalidDataException($"{path} was written for another model.");
                var count = reader.ReadInt32();
                if (count < 0) throw new InvalidDataException($"{path} is damaged.");
                var tokens = new List<int>(count);
                for (var i = 0; i < count; i++) tokens.Add(reader.ReadInt32());
                var read = Qwen35State.Read(reader, _model.Config);
                if (read.Position != count) throw new InvalidDataException($"{path} holds {count} tokens but a state of {read.Position}.");
                return (tokens, read);
            }, ct).ConfigureAwait(false);

            _state = state;
            _history.Clear();
            _history.AddRange(history);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Releases the threads and the mapped model file, once any call running has finished.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _gate.Wait();
        try
        {
            _pool.Dispose();
            _file.Dispose();
        }
        finally
        {
            _gate.Release(); // a call still waiting finds the generator disposed
        }
    }

    // ---- one call ----

    private sealed record Outcome(string Content, string? Reasoning, int TokensIn, int TokensOut, FinishReason Finish, TimeSpan Elapsed);

    private async Task<Outcome> RunAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options, Action<ChatFragment>? emit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messages);
        options ??= new GenerationOptions();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(() => Run(messages, options, emit, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Outcome Run(IReadOnlyList<ChatMessage> messages, GenerationOptions options, Action<ChatFragment>? emit, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var timing = options.Timing;
        var prompt = _tokenizer.Encode(RenderPrompt(messages), parseSpecial: true);
        if (prompt.Count >= _state.Capacity)
            throw new InvalidOperationException($"The prompt is {prompt.Count} tokens; this generator holds {_state.Capacity}.");
        var requested = options.MaxTokens > 0 ? options.MaxTokens : _options.MaxNewTokens;
        var maxTokens = Math.Min(PowerBudgetPolicy.Resolve(options.Budget, requested).MaxTokens, _state.Capacity - prompt.Count);
        var lastTurn = _turnStart < 0 ? -1 : prompt.LastIndexOf(_turnStart);
        if (timing is not null) timing.PromptTokens = prompt.Count;

        try
        {
            var path = Resume(prompt);
            if (timing is not null) timing.KvPath = path;
            var reused = _history.Count;
            for (var i = reused; i < prompt.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (i == lastTurn) _state.Mark();
                _model.Forward(prompt[i], _state, i == prompt.Count - 1 ? _logits : default);
                _history.Add(prompt[i]);
            }
            var prefilled = Stopwatch.GetTimestamp();

            var sampler = new TokenSampler(Settings(options), options.Seed);
            var generated = new List<int>();
            var reasoning = OpensInThought(prompt, lastTurn);
            var content = new StringBuilder();
            var thought = new StringBuilder();
            var stops = options.StopSequences?.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal).ToArray() ?? [];
            var holdBack = stops.Length == 0 ? 0 : stops.Max(s => s.Length) - 1;
            var emitted = 0;
            var decoder = Encoding.UTF8.GetDecoder();
            var finish = FinishReason.Length;
            var stopped = false;
            var firstAt = 0L;

            void Text(string text)
            {
                if (reasoning)
                {
                    if (text.Length == 0) return;
                    thought.Append(text);
                    if (options.IncludeReasoning) emit?.Invoke(new ChatFragment(ChatFragmentKind.Reasoning, text));
                    return;
                }
                if (content.Length == 0) text = text.TrimStart(); // the blank line after </think>
                content.Append(text);
            }

            // Sends the content on as far as no stop sequence could still be forming in it; true
            // when one has formed, the content then ending where it begins. (One cannot begin in
            // what was already sent: the last holdBack characters are always kept back.)
            bool Release(bool final)
            {
                var cut = -1;
                if (stops.Length > 0)
                {
                    var unsent = content.ToString(emitted, content.Length - emitted);
                    foreach (var stop in stops)
                    {
                        var at = unsent.IndexOf(stop, StringComparison.Ordinal);
                        if (at >= 0 && (cut < 0 || at < cut)) cut = at;
                    }
                    if (cut >= 0) content.Length = emitted + cut;
                }
                var upTo = cut >= 0 || final ? content.Length : content.Length - holdBack;
                if (upTo > emitted)
                {
                    emit?.Invoke(new ChatFragment(ChatFragmentKind.Content, content.ToString(emitted, upTo - emitted)));
                    emitted = upTo;
                }
                return cut >= 0;
            }

            while (generated.Count < maxTokens)
            {
                ct.ThrowIfCancellationRequested();
                var token = sampler.Sample(_logits, generated);
                if (firstAt == 0) firstAt = Stopwatch.GetTimestamp();
                if (_endOfGeneration.Contains(token))
                {
                    finish = FinishReason.Stop;
                    break;
                }
                generated.Add(token);

                if (token == _thinkStart || token == _thinkEnd)
                {
                    Text(Decode(decoder, default, flush: true));
                    reasoning = token == _thinkStart;
                }
                else
                {
                    Text(Decode(decoder, _tokenizer.Piece(token, special: false)));
                    if (!reasoning && Release(final: false))
                    {
                        finish = FinishReason.Stop;
                        stopped = true;
                        break;
                    }
                }

                if (generated.Count >= maxTokens) break;
                _model.Forward(token, _state, _logits);
                _history.Add(token);
            }
            if (!stopped)
            {
                Text(Decode(decoder, default, flush: true));
                if (Release(final: true)) finish = FinishReason.Stop;
            }

            var done = Stopwatch.GetTimestamp();
            if (firstAt == 0) firstAt = done;
            if (timing is not null)
            {
                timing.PrefillMs = (long)Stopwatch.GetElapsedTime(started, firstAt).TotalMilliseconds;
                timing.DecodeMs = (long)Stopwatch.GetElapsedTime(firstAt, done).TotalMilliseconds;
                timing.OutputTokens = generated.Count;
                timing.Finish ??= finish == FinishReason.Length ? "length" : "eos";
            }
            var prefillMs = Stopwatch.GetElapsedTime(started, prefilled).TotalMilliseconds;
            var decodeSeconds = Stopwatch.GetElapsedTime(prefilled, done).TotalSeconds;
            var rate = decodeSeconds > 0 ? generated.Count / decodeSeconds : 0;
            CircleLog.Info(Area, Invariant($"{prompt.Count} prompt tokens, {prompt.Count - reused} run ({path}) in {prefillMs:F0} ms; {generated.Count} tokens in {decodeSeconds * 1000:F0} ms ({rate:F2} tok/s); {finish}"));

            return new Outcome(content.ToString(), options.IncludeReasoning && thought.Length > 0 ? thought.ToString().Trim() : null,
                prompt.Count, generated.Count, finish, Stopwatch.GetElapsedTime(started, done));
        }
        catch (OperationCanceledException)
        {
            // Cancellation lands between steps, so the state and the history still agree.
            if (timing is not null) timing.Finish ??= "cancel";
            throw;
        }
        catch
        {
            // A failure inside a step can leave the layers out of step with each other: start clean.
            _state.Reset();
            _history.Clear();
            if (timing is not null) timing.Finish ??= "error";
            throw;
        }
    }

    // Brings the state to the longest start of the prompt it can reuse, and says which it did.
    private string Resume(List<int> prompt)
    {
        Debug.Assert(_history.Count == _state.Position);
        var held = _history.Count;
        if (held > 0 && held < prompt.Count && Holds(prompt, held)) return "continued";

        var marked = _state.MarkedPosition;
        if (marked > 0 && marked < prompt.Count && Holds(prompt, marked))
        {
            _state.Rewind();
            _history.RemoveRange(marked, _history.Count - marked);
            return "prefix-hit";
        }

        _state.Reset();
        _history.Clear();
        return "reset";
    }

    private bool Holds(List<int> prompt, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (prompt[i] != _history[i]) return false;
        }
        return true;
    }

    // Whether the prompt leaves the model inside a <think> block: the template opens one for
    // the reply when thinking is on.
    private bool OpensInThought(List<int> prompt, int lastTurn)
    {
        if (_thinkStart < 0) return false;
        for (var i = prompt.Count - 1; i > lastTurn; i--)
        {
            if (prompt[i] == _thinkEnd) return false;
            if (prompt[i] == _thinkStart) return true;
        }
        return false;
    }

    /// <summary>
    /// The conversation as the model's own template writes it: one system turn, first, holding
    /// every system message's text; the other messages in order.
    /// </summary>
    internal string RenderPrompt(IReadOnlyList<ChatMessage> messages)
    {
        var system = string.Join("\n\n", messages
            .Where(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(m.Content))
            .Select(m => m.Content));
        var turns = new List<object?>();
        if (system.Length > 0) turns.Add(Turn("system", system));
        foreach (var m in messages)
        {
            if (string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase)) continue;
            if (m.ImageBytes is { Length: > 0 }) throw new NotSupportedException("This engine has no vision tower yet; images go to a vision brain.");
            turns.Add(Turn(m.Role.ToLowerInvariant(), m.Content ?? ""));
        }
        return _template.Render(new Dictionary<string, object?>
        {
            ["messages"] = turns,
            ["tools"] = null,
            ["add_generation_prompt"] = true,
            ["enable_thinking"] = _options.Thinking,
        });

        static OrderedDictionary<string, object?> Turn(string role, string content) =>
            new(StringComparer.Ordinal) { ["role"] = role, ["content"] = content };
    }

    // The model card's numbers for this mode, at the caller's temperature. A caller can tighten
    // top-k and top-p but not loosen them past the card: GenerationOptions' own defaults (40 and
    // 0.9) are generic, the card's are what this model is tuned for. Temperature 0 is plain greedy.
    private SamplingSettings Settings(GenerationOptions options)
    {
        if (options.Temperature <= 0)
        {
            if (_options.Thinking) CircleLog.Warn(Area, "greedy decoding with thinking on can loop (the model card advises against it)");
            return SamplingSettings.Greedy;
        }
        var card = _options.Thinking ? SamplingSettings.Thinking : SamplingSettings.Direct;
        return card with
        {
            Temperature = options.Temperature,
            TopK = options.TopK > 0 ? Math.Min(options.TopK, card.TopK) : card.TopK,
            TopP = options.TopP > 0 ? Math.Min(options.TopP, card.TopP) : card.TopP,
        };
    }

    // The text a token's bytes complete; a character split across tokens waits in the decoder.
    private static string Decode(Decoder decoder, ReadOnlySpan<byte> bytes, bool flush = false)
    {
        var count = decoder.GetCharCount(bytes, flush);
        Span<char> chars = count <= 256 ? stackalloc char[count] : new char[count];
        var written = decoder.GetChars(bytes, chars, flush);
        return written == 0 ? "" : new string(chars[..written]);
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
