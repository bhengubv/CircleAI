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
// A CALL MAY ASK OTHERWISE (GenerationOptions.Effort, chosen per question by EffortRouter): a
// hard question thinks, a quick one does not, and a thinking budget closes the thought - the
// generator writes the model's own </think> itself - so the answer always comes.
//
// A TOOL CALL IS HELD TO ITS FORM. Once the model writes "<tool_call>" in its answer, each next
// token is drawn only from those that keep the call in the XML form the model's own template
// teaches (ToolCallGrammar) - the model card names malformed and looping calls as a known limit,
// and the logits are ours to mask. The tools themselves reach the model through the same
// template (GenerationOptions.Tools, its tools block), and then a call can only name one of
// them, with its own parameters, every required one given and a fixed set's values only.
//
// THE STATE IS KEPT BETWEEN CALLS AND REUSED ONLY WHERE THE TOKENS MATCH. A prompt runs from
// the first token the state does not already hold: after all of it, when the state holds the
// start of the new prompt; else after the mark the state took where the last turn began (a
// recurrent model cannot step back to any other point, and the next turn's prompt matches up
// to that one, because the template writes the history back the same way each time); else from
// the beginning. Checked token by token, the reuse needs neither ContinueConversation nor
// UsePrefixCache to make it safe. One call at a time.
//
// IT SEES WHEN ITS VISION TOWER IS BESIDE IT. A ChatMessage's ImageBytes become a picture in the
// turn as the model's own template writes one (<|vision_start|><|image_pad|><|vision_end|>), and
// the one <|image_pad|> becomes the picture's tokens from the tower (VisionTower), placed in the
// model by their row and column (Qwen35Model.ForwardImageToken) - the sequence the fork's mtmd
// builds. Each picture token's id in the prompt is negative and made from the picture's hash, so
// the reuse above compares pictures as it compares words, and a picture already in the state is
// not read again. A picture is decoded by the platform (ImageDecoder) and brought within the
// VisionMaxEdge budget only when its tokens must run.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CircleAI.Core.Logging;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Model;
using CircleAI.Inference.Managed.Runtime;
using CircleAI.Inference.Managed.Sampling;
using CircleAI.Inference.Managed.Text;
using CircleAI.Inference.Managed.Vision;
using CircleAI.Vision;

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

    /// <summary>
    /// The catalogue row's generation profile: its samplers - thinking off and on - replace the
    /// card numbers this engine carries (<see cref="SamplingSettings.Direct"/>, <see cref="SamplingSettings.Thinking"/>).
    /// Null keeps those.
    /// </summary>
    public CircleAI.Core.Models.GenerationProfile? Generation { get; init; }

    /// <summary>
    /// Tokens the MTP head drafts ahead per step when the file has one (Bonsai 2's PQ2_0-MTP files):
    /// the trunk checks them all in one pass and keeps what it would have chosen itself, so the reply
    /// is the one plain decoding gives, token for token. 2 is the card's depth; 0 decodes plainly.
    /// </summary>
    public int Drafts { get; init; } = 2;

    /// <summary>Reply tokens when a call asks for none.</summary>
    public int MaxNewTokens { get; init; } = 512;

    /// <summary>
    /// The most tokens a reply may spend thinking before the reasoning is closed and the answer
    /// begins; 0 is no budget. Wolverine tightens it when a reply thought its whole length away
    /// and said nothing (S6).
    /// </summary>
    public int ThinkingBudget { get; init; }

    /// <summary>
    /// How the attention layers hold their keys and values: Off is fp16, as the fork holds them; a
    /// TurboQuant mode holds codes at that many bits a coordinate (S3).
    /// </summary>
    public CircleAI.Core.KvCompressionMode KvMode { get; init; }

    /// <summary>The keys' TurboQuant form in a compressed mode; the values always take the MSE form.</summary>
    public CircleAI.Core.Compression.TurboQuantForm KeyForm { get; init; }

    /// <summary>
    /// Where primed starts are kept: the state after a conversation's system turn, so the next
    /// conversation that opens with the same system turn begins there instead of reading it again
    /// (S3). Null keeps them in the platform's cache folder (the temp path, which on Android is the
    /// app's own cache); empty keeps them in memory only.
    /// </summary>
    public string? PrimedStartDirectory { get; init; }

    /// <summary>
    /// The vision tower - an mmproj GGUF - that lets the model see. Null looks for this model's
    /// tower: a GGUF with "mmproj" in its name, in the model's folder or a folder beside it named
    /// after the model, with the model's general.name and tokens as wide as its embedding (a Q8_0
    /// one first). Empty turns seeing off.
    /// </summary>
    public string? VisionTowerPath { get; init; }

    /// <summary>
    /// How large a picture the tower reads: within the area of a square of this edge, its shape
    /// kept, so at most (edge / 32)^2 tokens. <see cref="ImageBudget.VisionMaxEdge"/> unless given.
    /// </summary>
    public int VisionMaxEdge { get; init; } = ImageBudget.VisionMaxEdge;
}

/// <summary>Bonsai 2 (qwen35) run by CircleAI's own C# engine, behind <see cref="IChatGenerator"/>.</summary>
public sealed class ManagedChatGenerator : IToolCallingGenerator
{
    private const string Area = "Engine";
    private const ulong SessionMagic = 0x314E_4547_4D49_4143; // "CAIMGEN1", little-endian

    // End-of-generation tokens beside the file's eos, eot and eom, as the fork collects them
    // (llama-vocab.cpp): by their text, whatever type the file gives them.
    private static readonly string[] EndOfGenerationTexts =
    [
        "<|eot_id|>", "<|im_end|>", "<|end|>", "<end_of_turn>", "<|endoftext|>", "<|eom_id|>", "<EOT>", "_<EOT>",
        "<|end_of_text|>", "<end_of_utterance>", "<|fim_pad|>", "<|repo_name|>", "<|file_sep|>",
    ];

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
    private readonly ToolCallGrammar _toolCalls = new();
    private readonly ToolCallGrammar.PieceOf _callPiece;
    private readonly string? _primedDirectory;
    private readonly string _modelIdentity;
    private Qwen35State _state;
    private int _disposed;

    // Speculative decoding with the file's MTP head (S5): null for a file without one, or when the
    // options ask for plain decoding.
    private readonly Speculator? _speculator;

    // Seeing: the tower beside the model (its shape read at load, its weights on the first picture),
    // the template's picture token, and the last few pictures' tokens by their hash.
    private readonly string? _towerPath;
    private readonly VisionConfig? _towerConfig;
    private readonly int _imagePad;
    private GgufFile? _towerFile;
    private VisionTower? _tower;
    private readonly List<(string Key, ImageEmbeddings Tokens)> _seen = [];
    private const int PicturesKept = 2;

    // A primed start on disk: what it is, then the state. Bumped with the engine's arithmetic, so a
    // start primed by older numbers is never read as this engine's.
    private const ulong PrimedMagic = 0x3144_454D_4952_5043; // "CPRIMED1", little-endian
    private const int EngineNumerics = 1;
    private const int PrimedStartsKept = 4;

    private ManagedChatGenerator(string ggufPath, GgufFile file, WorkerPool pool, Qwen35Model model, ByteLevelBpe tokenizer,
        JinjaTemplate template, ManagedGeneratorOptions options, string? towerPath, VisionConfig? towerConfig)
    {
        _towerPath = towerPath;
        _towerConfig = towerConfig;
        _imagePad = tokenizer.TryGetSpecial("<|image_pad|>", out var pad) ? pad : -1;
        if (towerPath is not null && _imagePad < 0) throw new NotSupportedException("The model's vocabulary has no <|image_pad|>, so a picture has nowhere to go.");
        if (options.VisionMaxEdge <= 0) throw new ArgumentOutOfRangeException(nameof(options), options.VisionMaxEdge, "VisionMaxEdge must be positive.");
        _file = file;
        _pool = pool;
        _model = model;
        _tokenizer = tokenizer;
        _template = template;
        _options = options;
        _primedDirectory = options.PrimedStartDirectory switch
        {
            null => Path.Combine(Path.GetTempPath(), "CircleAI", "primed"),
            "" => null,
            var directory => directory,
        };
        _modelIdentity = Identity(ggufPath, file.Length);
        _logits = new float[model.Config.Vocabulary];
        _state = model.NewState(options.ContextLength, options.KvMode, options.KeyForm);

        if (model.HasMtp && options.Drafts > 0)
            _speculator = new Speculator(model, new MtpDrafter(model, _state.Capacity), options.Drafts);

        foreach (var key in new[] { "tokenizer.ggml.eos_token_id", "tokenizer.ggml.eot_token_id", "tokenizer.ggml.eom_token_id" })
        {
            if (file.TryGetValue(key, out var id)) _endOfGeneration.Add(checked((int)id.AsInt64()));
        }
        foreach (var text in EndOfGenerationTexts)
        {
            if (tokenizer.TryGetSpecial(text, out var id)) _endOfGeneration.Add(id);
        }
        if (_endOfGeneration.Count == 0) throw new NotSupportedException("The model names no end-of-generation token.");
        _turnStart = tokenizer.TryGetSpecial("<|im_start|>", out var turn) ? turn : -1;
        _thinkStart = tokenizer.TryGetSpecial("<think>", out var start) ? start : -1;
        _thinkEnd = tokenizer.TryGetSpecial("</think>", out var end) ? end : -1;

        // Inside a tool call a token counts by the text it writes: a control token writes none, and
        // a thinking token would turn the call into reasoning, so neither can carry a call on.
        _callPiece = token => token == _thinkStart || token == _thinkEnd ? default : _tokenizer.Piece(token, special: false);
    }

    /// <summary>The model's shape.</summary>
    public Qwen35Config Config => _model.Config;

    /// <summary>Threads the products share.</summary>
    public int Threads => _pool.Threads;

    /// <summary>Tokens one conversation can hold.</summary>
    public int ContextLength => _state.Capacity;

    /// <summary>Whether the model can see: its vision tower was found and read.</summary>
    public bool CanSee => _towerPath is not null;

    /// <summary>The vision tower the model sees with, or null.</summary>
    public string? VisionTowerPath => _towerPath;

    internal ByteLevelBpe Tokenizer => _tokenizer;

    /// <summary>How long the last picture read took: decoded, fitted and through the tower.</summary>
    internal TimeSpan LastPicture { get; private set; }

    /// <summary>How long the tower alone took on the last picture.</summary>
    internal TimeSpan LastTower => _tower?.LastEncode ?? TimeSpan.Zero;

    // The most pixels a picture is brought within: the area of a square of VisionMaxEdge.
    private int MaxPixels => (int)Math.Min(int.MaxValue, (long)_options.VisionMaxEdge * _options.VisionMaxEdge);

    /// <summary>
    /// Whether this engine reads a model of <paramref name="architecture"/> packed as
    /// <paramref name="quantization"/>, as a catalogue row names them: qwen35 in PTQ1_0 or PQ2_0
    /// (Bonsai 2). The file itself is checked again when it is opened.
    /// </summary>
    /// <remarks>
    /// THE ONE GATE FOR A ROW ON THIS ENGINE. EngineChoice, the device assessor and the brain factory
    /// all ask it, so a row this engine cannot open yet - a packing it has no kernels for, an MTP
    /// file, a drafter, a vision tower - is never selected, fetched or built.
    /// </remarks>
    public static bool Reads(string? architecture, string? quantization) =>
        string.Equals(architecture, "qwen35", StringComparison.OrdinalIgnoreCase)
        && ReadPackings.Any(p => string.Equals(quantization, p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The packings <see cref="Reads"/> accepts for qwen35, as catalogue rows name them: PQ2_0-MTP is
    /// the PQ2_0 brain with its multi-token-prediction head, which this engine drafts with.
    /// </summary>
    public static IReadOnlyList<string> ReadPackings { get; } = ["PTQ1_0", "PQ2_0", "PQ2_0-MTP"];

    /// <summary>
    /// What this engine delivers of a model's capabilities today: answers, a thinking mode, a long
    /// context and tool calls - held to the template's XML form, which ToolCallReader reads (S8). Not
    /// images: a brain sees only when its tower sits beside it (S8), which the setup plan fetches
    /// behind it only where both fit (S7), so a row cannot promise sight.
    /// </summary>
    public static ChatCapability Delivers =>
        ChatCapability.Default | ChatCapability.Tools | ChatCapability.Reasoning | ChatCapability.LongContext;

    /// <summary>What this engine reads, said the way a refusal says it.</summary>
    public static string WhatItReads => "qwen35 in " + string.Join(" or ", ReadPackings);

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
                var tower = FindTower(ggufPath, options.VisionTowerPath, file, model.Config);
                var towerPath = tower?.Path;
                var generator = new ManagedChatGenerator(ggufPath, file, pool, model, tokenizer, template, options, towerPath, tower?.Config);
                var thinking = options.Thinking ? "on" : "off";
                var memory = options.KvMode == CircleAI.Core.KvCompressionMode.Off ? "fp16" : $"{options.KvMode}, keys {options.KeyForm}";
                var drafting = generator._speculator is { } s ? $", the MTP head drafting {s.Drafts} ahead" : model.HasMtp ? ", its MTP head unused" : "";
                var sight = towerPath is null ? "no vision tower" : $"sees with {Path.GetFileName(towerPath)} within {options.VisionMaxEdge}x{options.VisionMaxEdge}";
                CircleLog.Info(Area, Invariant($"managed engine ready: {Path.GetFileName(ggufPath)}, {model.Packing}, {model.Config.Layers} layers, {threads} threads, context {generator._state.Capacity}, working memory {memory}, thinking {thinking}{drafting}, {sight}, in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms"));
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

    /// <summary>
    /// The vision tower to see with, and its shape: <paramref name="asked"/> when given (empty for
    /// none; one that does not fit the model is refused), else the first tower found that is this
    /// model's - a GGUF with "mmproj" in its name, in the model's folder or in a folder beside it
    /// named after the model (where the catalogue keeps a tower as an entry of its own), with the
    /// model's general.name and tokens as wide as the model's embedding; a Q8_0 one first, the
    /// model's own folder first. A candidate that is not this model's is passed over. Null when
    /// there is none.
    /// </summary>
    internal static (string Path, VisionConfig Config)? FindTower(string modelPath, string? asked, GgufFile model, Qwen35Config config)
    {
        var name = model.TryGetValue("general.name", out var value) && value.Type == GgufType.String ? value.AsString() : null;
        if (asked is not null)
        {
            if (asked.Length == 0) return null;
            if (!File.Exists(asked)) throw new FileNotFoundException("No vision tower where one was named.", asked);
            var path = Path.GetFullPath(asked);
            return (path, ReadTower(path, config, name));
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(modelPath));
        if (folder is null || !Directory.Exists(folder)) return null;
        // Beside it: the folders the catalogue names after this model (Ternary-Bonsai-2-2B-Vision
        // beside Ternary-Bonsai-2-2B), and only those - a model kept in a crowded folder costs no scan.
        var parent = Path.GetDirectoryName(folder);
        IEnumerable<string> beside = parent is null || string.IsNullOrEmpty(name) ? [] : Folders(parent).Where(d =>
            !string.Equals(d, folder, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(d).StartsWith(name, StringComparison.OrdinalIgnoreCase));
        var candidates = new[] { folder }.Concat(beside)
            .SelectMany((dir, rank) => Towers(dir).Select(file => (File: file, Rank: rank)))
            .OrderBy(c => Path.GetFileName(c.File).Contains("Q8_0", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(c => c.Rank == 0 ? 0 : 1)
            .ThenBy(c => c.File, StringComparer.Ordinal);
        foreach (var (file, _) in candidates)
        {
            try
            {
                return (file, ReadTower(file, config, name));
            }
            catch (Exception e) when (e is NotSupportedException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                CircleLog.Info(Area, $"{Path.GetFileName(file)} is not {name ?? "this model"}'s vision tower: {e.Message}");
            }
        }
        return null;

        static IEnumerable<string> Towers(string dir)
        {
            try
            {
                return Directory.EnumerateFiles(dir, "*.gguf").Where(f => Path.GetFileName(f).Contains("mmproj", StringComparison.OrdinalIgnoreCase)).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }

        static IEnumerable<string> Folders(string dir)
        {
            try
            {
                return Directory.EnumerateDirectories(dir).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    // A tower's shape, read and checked against the model now - one for another model fails here,
    // not on the first picture; its weights are bound on the first picture.
    private static VisionConfig ReadTower(string towerPath, Qwen35Config model, string? modelName)
    {
        using var tower = GgufFile.Open(towerPath);
        var config = VisionConfig.FromGguf(tower);
        if (config.Projection != model.Embedding)
            throw new InvalidDataException($"{Path.GetFileName(towerPath)} makes tokens {config.Projection} wide; the model reads {model.Embedding}.");
        if (modelName is not null && tower.TryGetValue("general.name", out var name) && name.Type == GgufType.String
            && !string.Equals(name.AsString(), modelName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{Path.GetFileName(towerPath)} is {name.AsString()}'s tower, not {modelName}'s.");
        return config;
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
            _speculator?.Forget(); // the draft source's carry belongs to the state that was replaced
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
            _towerFile?.Dispose();
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
        var thinking = ThinkingFor(options);
        var tools = options.Tools is { Count: > 0 } offered ? offered : null;
        var built = BuildPrompt(messages, tools, thinking, options.Effort);
        var prompt = built.Ids;
        if (prompt.Count >= _state.Capacity)
            throw new InvalidOperationException($"The prompt is {prompt.Count} tokens; this generator holds {_state.Capacity}.");
        var requested = options.MaxTokens > 0 ? options.MaxTokens : _options.MaxNewTokens;

        // THE THOUGHT HAS ITS OWN ALLOWANCE, ON TOP OF THE ANSWER'S. A budget counted inside
        // MaxTokens let a long thought eat the answer's share and end the turn mid-thought.
        // ONE BUDGET, TWO OWNERS: the call's is the effort router's, per question; the generator's
        // is Wolverine's knob (S6), which caps the call's once it has tightened it - a reply that
        // thought its whole length away - and is the budget when a call names none.
        var thinkingBudget = thinking && _thinkEnd >= 0 ? Tighter(options.ThinkingBudget, _options.ThinkingBudget) : 0;
        var maxTokens = Math.Min(PowerBudgetPolicy.Resolve(options.Budget, requested).MaxTokens + thinkingBudget, _state.Capacity - prompt.Count);
        var grammar = GrammarFor(tools);
        var lastTurn = _turnStart < 0 ? -1 : prompt.LastIndexOf(_turnStart);
        var primed = PrimedLength(messages, prompt);
        if (timing is not null) timing.PromptTokens = prompt.Count;

        try
        {
            var path = Resume(prompt, primed);
            if (timing is not null) timing.KvPath = path;
            var reused = _history.Count;
            _model.TakeTimes(); // whatever an abandoned call left counted
            for (var i = reused; i < prompt.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (i == primed && _state.PrimedPosition != i) Prime(prompt, i);
                if (i == lastTurn) _state.Mark();
                if (built.Slots is { } slots && slots[i].Image >= 0)
                {
                    // A picture token goes in as the tower made it; an MTP head drafts from text, so
                    // it does not see this position (its drafts there may miss - the reply cannot).
                    var (image, index) = slots[i];
                    var tokens = Seen(built.Images[image]);
                    _model.ForwardImageToken(tokens.Token(index), index, tokens.Columns, tokens.Rows, _state, i == prompt.Count - 1 ? _logits : default);
                }
                else if (_speculator is null)
                {
                    _model.Forward(prompt[i], _state, i == prompt.Count - 1 ? _logits : default);
                }
                else
                {
                    _speculator.Run(prompt[i], _state, i == prompt.Count - 1 ? _logits : default);
                }
                _history.Add(prompt[i]);
            }
            var prefilled = Stopwatch.GetTimestamp();
            var prefillTimes = _model.TakeTimes();

            var sampler = new TokenSampler(Settings(options, thinking), options.Seed);
            var generated = new List<int>();
            var reasoning = OpensInThought(prompt, lastTurn);
            var thoughtTokens = 0;
            var closedByBudget = false;
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

            // A tool call the model opens in its answer is held to the template's XML form until it
            // closes: from the token after "<tool_call>", only tokens that keep it well formed are drawn.
            ToolCallGrammar.State? call = null;
            var overruled = 0;

            // ONE TOKEN OF THE REPLY from one position's logits: the tool call's form, the sampler, an
            // end of generation (not added), the text, a stop sequence, the length. True when the reply
            // ends with it - then it is not fed back. Plain and speculative decoding both draw every
            // token through here, in order, so a speculative reply is the plain reply token for token.
            (int Token, bool End) Step(float[] logits)
            {
                ct.ThrowIfCancellationRequested();
                int token;
                if (reasoning && thinkingBudget > 0 && thoughtTokens >= thinkingBudget)
                {
                    // THE BUDGET IS SPENT: the thought is closed for the model, with its own token,
                    // and it answers from what it has thought so far.
                    token = _thinkEnd;
                    closedByBudget = true;
                }
                else
                {
                    // THIS POSITION'S LOGITS, the parameter: under speculative decoding they are the
                    // verify pass's for this position, not the generator's own buffer.
                    if (call is { } held)
                    {
                        var masked = grammar.Mask(logits, held, _callPiece, _endOfGeneration);
                        if (masked == ToolCallGrammar.Masked.Overruled) overruled++;
                        if (masked == ToolCallGrammar.Masked.Nothing)
                        {
                            CircleLog.Warn(Area, "no token can carry the tool call on; it is no longer held to its form");
                            call = null;
                        }
                    }
                    token = sampler.Sample(logits, generated);
                }
                if (firstAt == 0) firstAt = Stopwatch.GetTimestamp();
                if (_endOfGeneration.Contains(token))
                {
                    finish = FinishReason.Stop;
                    return (token, true);
                }
                generated.Add(token);
                if (call is { } open) call = grammar.TryAdvance(open, _callPiece(token), out var next) ? next : null;

                if (token == _thinkStart || token == _thinkEnd)
                {
                    Text(Decode(decoder, default, flush: true));
                    reasoning = token == _thinkStart;
                }
                else
                {
                    if (reasoning) thoughtTokens++;
                    Text(Decode(decoder, _tokenizer.Piece(token, special: false)));
                    if (call is null && !reasoning && EndsWith(content, ToolCallGrammar.Opening)) call = grammar.Start;
                    if (!reasoning && Release(final: false))
                    {
                        finish = FinishReason.Stop;
                        stopped = true;
                        return (token, true);
                    }
                }
                return (token, generated.Count >= maxTokens);
            }

            _speculator?.TakeCounts();
            if (maxTokens > 0)
            {
                var (token, end) = Step(_logits);
                while (!end)
                {
                    if (_speculator is not null)
                    {
                        (token, end) = _speculator.Step(token, _state, _history, Step);
                        continue;
                    }
                    _model.Forward(token, _state, _logits);
                    _history.Add(token);
                    (token, end) = Step(_logits);
                }
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
            var pictures = built.Images.Length == 0 ? "" : Invariant($" ({built.Images.Length} pictures, {built.Images.Sum(p => p.Count)} of them picture tokens)");
            CircleLog.Info(Area, Invariant($"{prompt.Count} prompt tokens{pictures}, {prompt.Count - reused} run ({path}) in {prefillMs:F0} ms; {generated.Count} tokens in {decodeSeconds * 1000:F0} ms ({rate:F2} tok/s); {finish}"));
            CircleLog.Info(Area, Invariant($"where the time went: prompt {prefillTimes}; reply {_model.TakeTimes()}"));
            var (drafted, accepted) = _speculator?.TakeCounts() ?? (0L, 0L);
            if (drafted > 0)
                CircleLog.Info(Area, Invariant($"speculative: the trunk kept {accepted} of {drafted} tokens the MTP head drafted ({100.0 * accepted / drafted:F0}%); the head took {_model.TakeMtpTime().TotalMilliseconds:F0} ms"));
            if (overruled > 0) CircleLog.Info(Area, Invariant($"the tool call's form overruled the model's first choice {overruled} times"));
            if (thinking) CircleLog.Info(Area, Invariant($"thought for {thoughtTokens} tokens{(closedByBudget ? $", closed by its {thinkingBudget}-token budget" : string.Empty)} ({options.Effort?.TemplateName() ?? "the generator's default"})"));

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

    // Brings the state to the longest start of the prompt it can reuse, and says which it did:
    // all it holds; the mark where the last turn began; the primed start where the system turn
    // ended, held in memory or kept on disk; or nothing.
    private string Resume(List<int> prompt, int primed)
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

        if (primed > 0)
        {
            if (_state.PrimedPosition == primed && _history.Count >= primed && Holds(prompt, primed))
            {
                _state.RewindToPrimed();
                _history.RemoveRange(primed, _history.Count - primed);
                return "primed";
            }
            if (LoadPrimed(prompt, primed)) return "primed-disk";
        }

        _state.Reset();
        _history.Clear();
        return "reset";
    }

    // Where the system turn ends - the start of the turn after it - when the conversation opens
    // with one; 0 when it does not. The service keeps the system turn to the persona alone, so it
    // is the same tokens every time.
    private int PrimedLength(IReadOnlyList<ChatMessage> messages, List<int> prompt)
    {
        if (_turnStart < 0 || !messages.Any(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(m.Content)))
            return 0;
        var first = prompt.IndexOf(_turnStart);
        return first < 0 ? 0 : Math.Max(0, prompt.IndexOf(_turnStart, first + 1));
    }

    // The system turn is in: this point is remembered, and kept on disk for the next process.
    private void Prime(List<int> prompt, int length)
    {
        _state.MarkPrimed();
        if (_primedDirectory is null) return;
        try
        {
            var file = PrimedPath(prompt, length);
            if (File.Exists(file)) return;
            Directory.CreateDirectory(_primedDirectory);
            var writing = file + ".writing";
            using (var stream = new FileStream(writing, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(PrimedMagic);
                writer.Write(EngineNumerics);
                writer.Write(_modelIdentity);
                writer.Write(length);
                for (var i = 0; i < length; i++) writer.Write(prompt[i]);
                _state.Write(writer, _model.Config);
            }
            File.Move(writing, file, overwrite: true);
            CircleLog.Info(Area, Invariant($"primed start kept: a {length}-token system turn, {new FileInfo(file).Length / 1024} KB"));
            Prune();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            CircleLog.Warn(Area, "a primed start could not be kept: " + e.Message);
        }
    }

    // The state at the end of this prompt's system turn, from disk, when one was kept for this
    // model, engine, working memory and system turn; the conversation goes on from there.
    private bool LoadPrimed(List<int> prompt, int length)
    {
        if (_primedDirectory is null) return false;
        var file = PrimedPath(prompt, length);
        if (!File.Exists(file)) return false;
        try
        {
            Qwen35State state;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (reader.ReadUInt64() != PrimedMagic || reader.ReadInt32() != EngineNumerics
                    || reader.ReadString() != _modelIdentity || reader.ReadInt32() != length)
                    return false;
                for (var i = 0; i < length; i++)
                {
                    if (reader.ReadInt32() != prompt[i]) return false;
                }
                state = Qwen35State.Read(reader, _model.Config);
            }
            if (state.Position != length || state.Capacity != _state.Capacity || state.KvMode != _state.KvMode || state.KeyForm != _state.KeyForm)
                return false;

            _state = state;
            _history.Clear();
            for (var i = 0; i < length; i++) _history.Add(prompt[i]);
            _speculator?.Forget();
            _state.MarkPrimed();
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow); // in use: the last to be pruned
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            CircleLog.Warn(Area, "a primed start could not be read and is primed again: " + e.Message);
            try { File.Delete(file); } catch (IOException) { }
            return false;
        }
    }

    // A primed start's file, named for everything that decides the state it holds: the engine's
    // arithmetic, the model, the working memory, the capacity, and the tokens of the system turn.
    private string PrimedPath(List<int> prompt, int length)
    {
        var key = new StringBuilder().Append(EngineNumerics).Append('|').Append(_modelIdentity).Append('|')
            .Append((int)_state.KvMode).Append('|').Append((int)_state.KeyForm).Append('|').Append(_state.Capacity).Append('|');
        for (var i = 0; i < length; i++) key.Append(prompt[i]).Append(',');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString())));
        return Path.Combine(_primedDirectory!, hash[..32].ToLowerInvariant() + ".primed");
    }

    // A few primed starts are kept; the least recently used go first.
    private void Prune()
    {
        var stale = new DirectoryInfo(_primedDirectory!).GetFiles("*.primed")
            .OrderByDescending(f => f.LastWriteTimeUtc).Skip(PrimedStartsKept);
        foreach (var file in stale)
        {
            try { file.Delete(); } catch (IOException) { }
        }
    }

    // The model, without reading all of it: its length and the hash of its first megabyte - the
    // header and the metadata, which name its architecture, its packing and its vocabulary.
    private static string Identity(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        var head = new byte[(int)Math.Min(stream.Length, 1 << 20)];
        stream.ReadExactly(head);
        return string.Create(CultureInfo.InvariantCulture, $"{length}:{Convert.ToHexString(SHA256.HashData(head))[..32]}");
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
    internal string RenderPrompt(IReadOnlyList<ChatMessage> messages) => RenderPrompt(messages, null, _options.Thinking, null);

    /// <summary>
    /// The conversation as the model's own template writes it, offering <paramref name="tools"/>
    /// through the template's own tools block, thinking or not as <paramref name="thinking"/> says.
    /// </summary>
    /// <remarks>
    /// A TEMPLATE THAT TAKES <c>reasoning_effort</c> is given the effort's name whenever the model
    /// thinks; one that does not (Qwen3.5's) never reads it, and the thinking budget is what
    /// holds the length there.
    /// </remarks>
    internal string RenderPrompt(IReadOnlyList<ChatMessage> messages, IReadOnlyList<CircleAI.Tools.ToolDefinition>? tools,
        bool thinking, ReasoningEffort? effort)
    {
        var system = string.Join("\n\n", messages
            .Where(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(m.Content))
            .Select(m => m.Content));
        var turns = new List<object?>();
        if (system.Length > 0) turns.Add(Turn("system", system));
        foreach (var m in messages)
        {
            var isSystem = string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase);
            if (m.ImageBytes is { Length: > 0 })
            {
                if (isSystem) throw new NotSupportedException("A system message cannot carry a picture: the model's own template refuses one.");
                if (_towerPath is null)
                    throw new NotSupportedException($"{Path.GetFileName(_file.FilePath)} has no vision tower beside it (a *mmproj*.gguf in its folder), so it cannot see; images go to a vision brain.");
                turns.Add(new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["role"] = m.Role.ToLowerInvariant(), ["content"] = Picture(m.Content) });
                continue;
            }
            if (isSystem) continue;
            turns.Add(Turn(m.Role.ToLowerInvariant(), m.Content ?? ""));
        }
        var variables = new Dictionary<string, object?>
        {
            ["messages"] = turns,
            ["tools"] = tools is { Count: > 0 } ? TemplateTools(tools) : null,
            ["add_generation_prompt"] = true,
            ["enable_thinking"] = thinking,
        };
        if (thinking && effort is { } level && level != ReasoningEffort.Off) variables["reasoning_effort"] = level.TemplateName();
        return _template.Render(variables);

        static OrderedDictionary<string, object?> Turn(string role, string content) =>
            new(StringComparer.Ordinal) { ["role"] = role, ["content"] = content };

        // The picture, then the words, as the template's content items: it writes the picture as
        // <|vision_start|><|image_pad|><|vision_end|>.
        static List<object?> Picture(string? text)
        {
            var items = new List<object?> { new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "image" } };
            if (!string.IsNullOrEmpty(text))
                items.Add(new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "text", ["text"] = text });
            return items;
        }
    }

    /// <summary>A picture in a prompt: its bytes, their SHA-256, and the grid of tokens the tower makes of it.</summary>
    internal sealed record PromptImage(byte[] Bytes, string Hash, int Columns, int Rows)
    {
        public int Count => Columns * Rows;
    }

    /// <summary>
    /// A prompt's ids - each picture's tokens in place of the <|image_pad|> the template wrote for
    /// it - its pictures, and per position the picture and the token there (-1 for a word).
    /// </summary>
    internal sealed record PromptIds(List<int> Ids, PromptImage[] Images, (int Image, int Index)[]? Slots);

    /// <summary>
    /// The conversation as ids for the model: rendered and tokenized, each picture's one
    /// <|image_pad|> replaced by as many ids as the picture has tokens. Those ids are negative -
    /// never a word's - and made from the picture's hash and the token's place, so the state's
    /// reuse, which compares ids, compares pictures too.
    /// </summary>
    internal PromptIds BuildPrompt(IReadOnlyList<ChatMessage> messages) => BuildPrompt(messages, null, _options.Thinking, null);

    /// <summary>
    /// The conversation as ids for the model, with <paramref name="tools"/> offered, thinking as
    /// <paramref name="thinking"/> says and <paramref name="effort"/> named to a template that reads
    /// it - rendered as <see cref="RenderPrompt(IReadOnlyList{ChatMessage}, IReadOnlyList{CircleAI.Tools.ToolDefinition}?, bool, ReasoningEffort?)"/>
    /// renders it, its pictures expanded as above.
    /// </summary>
    internal PromptIds BuildPrompt(IReadOnlyList<ChatMessage> messages, IReadOnlyList<CircleAI.Tools.ToolDefinition>? tools,
        bool thinking, ReasoningEffort? effort)
    {
        var ids = _tokenizer.Encode(RenderPrompt(messages, tools, thinking, effort), parseSpecial: true);
        var pictures = messages.Where(m => m.ImageBytes is { Length: > 0 }).Select(m => Plan(m.ImageBytes!)).ToArray();
        if (pictures.Length == 0) return new PromptIds(ids, [], null);

        var expanded = new List<int>(ids.Count + pictures.Sum(p => p.Count));
        var slots = new List<(int, int)>(expanded.Capacity);
        var next = 0;
        foreach (var id in ids)
        {
            if (id != _imagePad)
            {
                expanded.Add(id);
                slots.Add((-1, -1));
                continue;
            }
            if (next == pictures.Length) throw new InvalidDataException("The prompt holds more pictures than the conversation gave.");
            var picture = pictures[next];
            var key = ulong.Parse(picture.Hash.AsSpan(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            for (var j = 0; j < picture.Count; j++)
            {
                expanded.Add(SlotId(key, j));
                slots.Add((next, j));
            }
            next++;
        }
        if (next != pictures.Length) throw new InvalidDataException($"The prompt holds {next} pictures; the conversation gave {pictures.Length}.");
        return new PromptIds(expanded, pictures, slots.ToArray());
    }

    // A picture's grid, from its header when the header gives the size the picture decodes to
    // (ImageBudget reads it without decoding a pixel), else from decoding it.
    private PromptImage Plan(byte[] bytes)
    {
        var config = _towerConfig!;
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var (width, height) = HeaderGivesTheFrame(bytes) && ImageBudget.Measure(bytes) is { Width: > 0, Height: > 0 } size
            ? size
            : Size(ImageDecoder.Decode(bytes));
        var (w, h) = config.Fit(width, height, MaxPixels);
        return new PromptImage(bytes, hash, w / config.TokenSide, h / config.TokenSide);

        static (int, int) Size(RasterImage image) => (image.Width, image.Height);
    }

    // Whether a picture's header size is the size its first frame decodes to. Not for a GIF, whose
    // header gives the screen its frames are drawn on, nor an extended WebP (VP8X), whose header
    // gives the canvas of an animation: Windows decodes the first frame alone, which can be smaller,
    // and a grid planned from the canvas would not be the grid the tower makes.
    internal static bool HeaderGivesTheFrame(ReadOnlySpan<byte> bytes)
    {
        var gif = bytes.Length >= 3 && bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F';
        var extendedWebP = bytes.Length >= 16 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
            && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P'
            && bytes[12] == 'V' && bytes[13] == 'P' && bytes[14] == '8' && bytes[15] == 'X';
        return !gif && !extendedWebP;
    }

    // A picture token's id: negative, from the picture's hash and the token's index, mixed so that
    // two pictures' tokens differ wherever the pictures do.
    private static int SlotId(ulong picture, int index)
    {
        var h = picture + (ulong)index * 0x9E37_79B9_7F4A_7C15UL;
        h ^= h >> 33;
        h *= 0xFF51_AFD7_ED55_8CCDUL;
        h ^= h >> 33;
        h *= 0xC4CE_B9FE_1A85_EC53UL;
        h ^= h >> 33;
        return -1 - (int)(h & 0x3FFF_FFFF);
    }

    // A picture's tokens: from the last few seen, else decoded by the platform, brought within the
    // budget and run through the tower now.
    private ImageEmbeddings Seen(PromptImage picture)
    {
        var key = picture.Hash + ":" + MaxPixels.ToString(CultureInfo.InvariantCulture);
        foreach (var (k, tokens) in _seen)
        {
            if (k == key) return tokens;
        }
        var tower = Tower();
        var started = Stopwatch.GetTimestamp();
        var raster = ImageDecoder.Decode(picture.Bytes);
        var seen = tower.Encode(raster, MaxPixels);
        if (seen.Columns != picture.Columns || seen.Rows != picture.Rows)
            throw new InvalidDataException($"The picture decoded to {raster.Width}x{raster.Height}, not the size its header gave: {seen.Columns}x{seen.Rows} tokens where {picture.Columns}x{picture.Rows} were planned.");
        LastPicture = Stopwatch.GetElapsedTime(started);
        CircleLog.Info(Area, Invariant($"a {raster.Width}x{raster.Height} picture seen as {seen.Columns}x{seen.Rows} tokens in {LastPicture.TotalMilliseconds:F0} ms (the tower {tower.LastEncode.TotalMilliseconds:F0} ms)"));
        _seen.Add((key, seen));
        if (_seen.Count > PicturesKept) _seen.RemoveAt(0);
        return seen;
    }

    // The tower's weights, bound on the first picture: until then only its shape is read.
    private VisionTower Tower()
    {
        if (_tower is not null) return _tower;
        var started = Stopwatch.GetTimestamp();
        var file = GgufFile.Open(_towerPath!);
        try
        {
            _tower = VisionTower.Load(file, _pool);
            _towerFile = file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
        CircleLog.Info(Area, Invariant($"vision tower ready: {Path.GetFileName(_towerPath)}, {_tower.Config.Layers} layers of {_tower.Config.Embedding}, in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms"));
        return _tower;
    }

    // The tools as the template's tools variable holds them: the function schemas every
    // template is trained on, from the one owner of that mapping (ToolSchema).
    private static JsonElement TemplateTools(IReadOnlyList<CircleAI.Tools.ToolDefinition> tools)
    {
        using var document = JsonDocument.Parse(CircleAI.Tools.ToolSchema.Json(tools));
        return document.RootElement.Clone();
    }

    // Whether this call thinks: as its effort says, or the generator's own default when it says none.
    private bool ThinkingFor(GenerationOptions options) =>
        options.Effort is { } effort ? effort != ReasoningEffort.Off && _thinkStart >= 0 : _options.Thinking;

    // The tighter of two token budgets where both are set, else whichever is; 0 is none.
    private static int Tighter(int a, int b) =>
        a > 0 && b > 0 ? Math.Min(a, b) : Math.Max(Math.Max(a, 0), Math.Max(b, 0));

    // The form a call is held to on this call: the offered tools' own signatures, or any
    // identifier when no tools came with it. A tool whose name - or a required parameter's -
    // the form cannot write is left out, said once: the model could never call it anyway.
    private ToolCallGrammar GrammarFor(IReadOnlyList<CircleAI.Tools.ToolDefinition>? tools)
    {
        if (tools is null) return _toolCalls;
        var functions = new List<ToolCallGrammar.Function>(tools.Count);
        foreach (var tool in tools)
        {
            if (tool is null) continue;
            IReadOnlyList<string> required = tool.RequiredParameters ?? Array.Empty<string>();
            var parameters = new List<ToolCallGrammar.Parameter>(tool.Parameters?.Count ?? 0);
            var callable = ToolCallGrammar.CanCarry(tool.Name);
            foreach (var (name, parameter) in tool.Parameters ?? new Dictionary<string, CircleAI.Tools.ToolParameter>())
            {
                var isRequired = required.Contains(name, StringComparer.Ordinal);
                if (!ToolCallGrammar.CanCarry(name))
                {
                    if (isRequired) callable = false;
                    continue;
                }
                parameters.Add(new ToolCallGrammar.Parameter(name, isRequired, parameter?.Enum));
            }
            if (!callable)
            {
                CircleLog.Warn(Area, $"the tool \"{tool.Name}\" cannot be called in the template's form; its call is not held to it");
                continue;
            }
            functions.Add(new ToolCallGrammar.Function(tool.Name, parameters));
        }
        return functions.Count == 0 ? _toolCalls : new ToolCallGrammar(functions);
    }

    // The model card's numbers for this mode - the catalogue row's profile when it has one (S5) - at
    // the caller's temperature. A caller can tighten top-k and top-p but not loosen them past the
    // card: GenerationOptions' own defaults (40 and 0.9) are generic, the card's are what this model
    // is tuned for. Temperature 0 is plain greedy.
    private SamplingSettings Settings(GenerationOptions options, bool thinking)
    {
        if (options.Temperature <= 0)
        {
            if (thinking) CircleLog.Warn(Area, "greedy decoding with thinking on can loop (the model card advises against it)");
            return SamplingSettings.Greedy;
        }
        var card = Card(_options.Generation, thinking);
        return card with
        {
            Temperature = options.Temperature,
            TopK = options.TopK > 0 ? Math.Min(options.TopK, card.TopK) : card.TopK,
            TopP = options.TopP > 0 ? Math.Min(options.TopP, card.TopP) : card.TopP,
        };
    }

    /// <summary>
    /// The sampling a generation profile asks for with thinking on or off: the profile's sampler for
    /// that mode, read as this engine samples - penalties over the last 64 generated tokens, as the
    /// card says - or the engine's own card numbers when the profile has none.
    /// </summary>
    internal static SamplingSettings Card(CircleAI.Core.Models.GenerationProfile? profile, bool thinking)
    {
        var sampler = thinking ? profile?.ThinkingSampler ?? profile?.Sampler : profile?.Sampler;
        if (sampler is null) return thinking ? SamplingSettings.Thinking : SamplingSettings.Direct;
        const int window = 64;
        return sampler.SamplerType switch
        {
            "greedy" => SamplingSettings.Greedy,
            // MNN's "penalty": the repetition penalty, then the highest logit.
            "penalty" => SamplingSettings.Greedy with { RepeatPenalty = (float)sampler.Penalty, PenaltyWindow = window },
            _ => new SamplingSettings((float)sampler.Temperature, sampler.TopK, (float)sampler.TopP, (float)sampler.MinP,
                (float)sampler.PresencePenalty, 0f, (float)sampler.Penalty, window),
        };
    }

    private static bool EndsWith(StringBuilder text, string tail)
    {
        if (text.Length < tail.Length) return false;
        for (var i = 0; i < tail.Length; i++)
        {
            if (text[text.Length - tail.Length + i] != tail[i]) return false;
        }
        return true;
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
