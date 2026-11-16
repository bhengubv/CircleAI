// ManagedChatGenerator.cs
//
// The C# engine behind IChatGenerator. A conversation becomes a prompt through the model's own
// chat template (JinjaLite) and ids through its tokenizer (ByteLevelBpe); LanguageModel runs them
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
// WHAT NO TURN HAS TIME TO READ IS READ IN THE GAPS (PrimeAsync). The tools block is four minutes of
// reading on the P30 and a turn has two, so a turn that met it never reached its question and never
// got as far as keeping it. It is read on a state of its own, a token at a time, with a turn free to
// take the engine at any token; what was read is written down as it goes, because EMUI ends a busy
// process without notice, and the next start goes on from there.
//
// IT SEES WHEN ITS VISION TOWER IS BESIDE IT. A ChatMessage's ImageBytes become a picture in the
// turn as the model's own template writes one (<|vision_start|><|image_pad|><|vision_end|>), and
// the one <|image_pad|> becomes the picture's tokens from the tower (VisionTower), placed in the
// model by their row and column (LanguageModel.ForwardImageToken) - the sequence the fork's mtmd
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
using CircleAI.Inference.Managed.Store;
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
    /// <remarks>
    /// OFF UNLESS A HOST ASKS (OPEN-GAPS C7), because nowhere it was measured is it faster. S5's run
    /// on the desktop wrote 0.78-0.89 tokens a second drafting against 1.60-2.08 plain, and on the
    /// P30 (S9, 2026-11-02) the check pass alone - three tokens through a product together - took
    /// 97.8 ms against 41.0 ms one by one, so the phone loses before a single draft is rejected.
    /// Lossless either way: a host that measures it faster on its own hardware sets 2.
    /// </remarks>
    public int Drafts { get; init; }

    /// <summary>
    /// Tokens a smaller model of the same vocabulary drafts ahead for a trunk whose layers stream from flash (ModelDrafter):
    /// 0 turns it off. Only while the trunk streams and the drafter fits in RAM beside it.
    /// </summary>
    /// <remarks>
    /// THE ECONOMICS FLIP WHEN THE TRUNK STREAMS. In RAM a check of three tokens costs the P30 more than three plain
    /// tokens (<see cref="Drafts"/>); streamed, every pass reads every layer - 6.5 GB, about 33 s for the 27B - whether
    /// it carries one token or five, and the drafter's tokens cost a fraction of a second each.
    /// <para>OFF: ONE MODEL PER DEVICE (the indexed mind's plan, 2026-11-09). No second model drafts for the trunk, and
    /// none is looked for; every check names the one model that ran.</para>
    /// </remarks>
    public int StreamedDrafts { get; init; }

    /// <summary>
    /// The model that drafts for a streamed trunk: null finds one beside the model - a folder next to its own, a smaller
    /// qwen35 file with the same vocabulary, token for token (the 2B for the 27B); empty for none.
    /// </summary>
    public string? DrafterPath { get; init; }

    /// <summary>
    /// A sibling file that carries this trunk's MTP head (a Bonsai 2 PQ2_0-MTP file beside a PTQ1_0 trunk), so a trunk
    /// whose own file has no head drafts for itself with one. Only read when <see cref="Drafts"/> asks for drafting and
    /// this trunk has no head of its own; null scans the model's own folder for a matching one, empty turns the scan off.
    /// </summary>
    public string? MtpHeadPath { get; init; }

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

    /// <summary>
    /// Where the head's store by script is kept: the output head's rows grouped by writing system, on
    /// flash, so a turn streams in only the scripts its conversation writes in and lets them go when it
    /// ends (<see cref="HeadStore"/>). The device's flash is measured there once, for the channels a turn
    /// streams on. Null keeps it in the platform's cache folder beside the primed starts; empty turns it off.
    /// </summary>
    public string? StoreDirectory { get; init; }
}

/// <summary>Bonsai 2 (qwen35) run by CircleAI's own C# engine, behind <see cref="IChatGenerator"/>.</summary>
public sealed class ManagedChatGenerator : IToolCallingGenerator, ISeeingGenerator, IReadingGenerator
{
    private const string Area = "Engine";
    private const ulong SessionMagic = 0x314E_4547_4D49_4143; // "CAIMGEN1", little-endian

    // The fewest prompt tokens a read is measured from: one or two say more about the clock.
    private const int ReadMeasuredFrom = 4;

    // How often a read in the gaps writes down how far it got, in tokens: about 30 s on the P30, and
    // about 20 MB each time, which is what the recurrent layers hold whatever the length.
    private const int PrimingKeptEvery = 64;

    // How long a read in the gaps waits to see the engine taken by the call that asked for it.
    private static readonly TimeSpan GiveWay = TimeSpan.FromMilliseconds(50);

    // End-of-generation tokens beside the file's eos, eot and eom, as the fork collects them
    // (llama-vocab.cpp): by their text, whatever type the file gives them.
    private static readonly string[] EndOfGenerationTexts =
    [
        "<|eot_id|>", "<|im_end|>", "<|end|>", "<end_of_turn>", "<|endoftext|>", "<|eom_id|>", "<EOT>", "_<EOT>",
        "<|end_of_text|>", "<end_of_utterance>", "<|fim_pad|>", "<|repo_name|>", "<|file_sep|>",
    ];

    private readonly GgufFile _file;
    private readonly WorkerPool _pool;
    private readonly LanguageModel _model;
    private readonly ByteLevelBpe _tokenizer;
    private readonly JinjaTemplate _template;
    private readonly ManagedGeneratorOptions _options;
    private readonly HashSet<int> _endOfGeneration = [];
    private readonly int _turnStart;
    private readonly int[] _userTurn;
    private readonly int[] _turnEnd;
    private readonly int _thinkStart;
    private readonly int _thinkEnd;
    private readonly float[] _logits;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<int> _history = []; // the tokens the state holds, in order
    private readonly ToolCallGrammar _toolCalls = new();
    private readonly ToolCallGrammar.PieceOf _callPiece;
    private readonly string? _primedDirectory;
    private readonly string _modelIdentity;
    private ModelState _state;
    private int _disposed;
    private long _readCost; // what one prompt token last took, in ticks; 0 until one was measured
    private int _wanted;    // calls waiting for the engine, which a read in the gaps stands aside for

    // THE CONVERSATION THE BRAIN IS KEEPING: whose it is, and the tokens of its finished turns as they
    // went in, the empty thought and the notes included. The template would write those turns differently
    // from how they were read, and a prompt written from the words no longer matches what the state holds,
    // so a turn that goes on this conversation is these tokens and then the new turn. Nothing is re-read.
    private string? _talk;
    private string? _talkAsked;     // the question of its last finished turn, as the messages hold it
    private readonly List<int> _talkTokens = [];
    private readonly HashSet<string> _talkTools = new(StringComparer.Ordinal); // the tools its system turn offers

    // Speculative decoding with the file's MTP head (S5): null for a file without one, or when the
    // options ask for plain decoding.
    private readonly Speculator? _speculator;

    // A sibling file whose MTP head this trunk borrowed (AttachMtp): its bytes stay mapped while the head drafts, so it
    // lives as long as the generator. Null when the trunk's own file carries the head, or none was attached.
    private GgufFile? _mtpFile;

    // THE HEAD'S STORE BY SCRIPT, ON FLASH: null until it is written and checked, and where it is off; the
    // readers the device's flash takes at once; and the job that makes them, in the background at load.
    private HeadStore? _headStore;
    private int _channels = 4;
    private int _readBytes = 1024 * 1024;
    private FlashProfile? _flash;   // what the device's flash measured; said beside the index's check, null until measured
    private readonly CancellationTokenSource _storeCancel = new();
    private Task? _storeTask;

    // THE FEED-FORWARD'S STORE, when this load reads its feed-forward from flash: armed warm before a token runs, kept
    // while turns keep coming, and let go once the engine has been idle for the recipe's time (Stopwatch ticks).
    private TileStore? _ffnStore;
    private TileStore? _mixStore;
    private LayerStream? _stream;

    // THE SMALLER MODEL THAT DRAFTS FOR A STREAMED TRUNK (ModelDrafter): found beside the model when it loads, built at the
    // first streaming turn that has room for it, and let go with the layers when the engine is idle.
    private string? _drafterPath;
    private GgufFile? _drafterFile;
    private LanguageModel? _drafterModel;
    private ModelDrafter? _drafter;
    private LayerStream? _drafterStream;
    private HeadStore? _drafterHeadStore;
    private HeadSurface? _drafterHead;   // the turn's surface of the drafter's head
    private Speculator? _drafting;
    private ThoughtIndex? _indexCore;            // step 3, the core index on flash, when one was built for this model
    private TileStore? _indexMix;                // step 2's layers: every layer's attention, tiled
    private TileStore? _indexDown;               // step 2's layers: every layer's ffn_down, tiled, where the layers take their own
    private IndexedFeedForward? _indexServing;   // step 4 in RAM, serving while the layers' stream is open
    private TileStore? _ffnDownStore;            // ffn_down alone, read in place, for a stream step 2 holds no ffn_down for
    private CachedIndex? _indexCoreStore;        // step 2's pieces on flash: each thought's neurons at each layer
    private IndexingQueue? _indexing;            // the index's building, a unit at a time while the phone is idle
    private StrongBox<IndexingStatus>? _everyNeuron;   // no index built: every neuron from the model's own file, as recorded
    private IndexUse? _indexUse;                 // what the person's turns used of the index, kept on flash for the night
    private long _lastCall = Stopwatch.GetTimestamp();   // when a call last wanted the engine (Stopwatch ticks)
    private long _indexNotBefore;                // no unit of indexing before this (Stopwatch ticks): the rest after one
    private long _phoneAsked;                    // when a unit last asked the phone whether it is hot or in use
    private bool _phoneSaysStop;                 // what it said
    private string _phoneSaysWhy = string.Empty; // and why
    private string _stoodAsideWhy = string.Empty;   // why the unit in hand last stood aside
    private bool _stoodAsideForMemory;           // whether that was the phone's memory
    private bool _indexUsedTheModel;             // a unit read words through the model: the state is not a conversation's
    private FfnWarm? _ffnWarm;
    private long _ffnUsed;
    private long _ffnKeepTicks = (long)(180 * (double)Stopwatch.Frequency);
    private System.Threading.Timer? _ffnTimer;

    // STEP 3: WHAT EACH FEED-FORWARD GROUP HAS DONE in full turns, kept on flash, and the share of groups a word computes
    // (100 is all) - set by a small file beside the stores, so a test can change it from outside the process.
    private double[]? _ffnFired;
    private long _ffnFiredTokens;
    private long _ffnControlStamp = -1;
    private const string FfnSurfaceFile = "ffn-surface.txt";
    private const string FfnFidelityFile = "ffn-fidelity.txt";
    private const string IndexFile = "index.txt";

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

    private ManagedChatGenerator(string ggufPath, GgufFile file, WorkerPool pool, LanguageModel model, ByteLevelBpe tokenizer,
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

        var drafts = options.Drafts > 0 ? options.Drafts : DraftsForTest;
        if (model.HasMtp && drafts > 0)
            _speculator = new Speculator(model, new MtpDrafter(model, _state.Capacity), drafts);

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
        _userTurn = [.. tokenizer.Encode("user\n", parseSpecial: false)];
        _turnEnd = tokenizer.TryGetSpecial("<|im_end|>", out var ended) ? [ended, .. tokenizer.Encode("\n", parseSpecial: false)] : [];
        _thinkStart = tokenizer.TryGetSpecial("<think>", out var start) ? start : -1;
        _thinkEnd = tokenizer.TryGetSpecial("</think>", out var end) ? end : -1;

        // Inside a tool call a token counts by the text it writes: a control token writes none, and
        // a thinking token would turn the call into reasoning, so neither can carry a call on.
        _callPiece = token => token == _thinkStart || token == _thinkEnd ? default : _tokenizer.Piece(token, special: false);
    }

    /// <summary>The model's shape.</summary>
    public ModelConfig Config => _model.Config;

    /// <summary>Threads the products share.</summary>
    public int Threads => _pool.Threads;

    /// <summary>Tokens one conversation can hold.</summary>
    public int ContextLength => _state.Capacity;

    /// <summary>Whether the model can see: its vision tower was found and read.</summary>
    public bool CanSee => _towerPath is not null;

    /// <summary>The vision tower the model sees with, or null.</summary>
    public string? VisionTowerPath => _towerPath;

    /// <inheritdoc/>
    public TimeSpan? ReadCost => Volatile.Read(ref _readCost) is > 0 and var ticks ? TimeSpan.FromTicks(ticks) : null;

    /// <inheritdoc/>
    /// <remarks>Counted as a call would run it and changing nothing: what the state holds now, the primed start on disk included.</remarks>
    public int Unread(IReadOnlyList<ChatMessage> messages, IReadOnlyList<CircleAI.Tools.ToolDefinition>? tools) => Unread(messages, tools, null);

    /// <inheritdoc/>
    public int Unread(IReadOnlyList<ChatMessage> messages, IReadOnlyList<CircleAI.Tools.ToolDefinition>? tools, string? conversation)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var offered = tools is { Count: > 0 } ? tools : null;
        var built = BuildPrompt(messages, offered, _options.Thinking, null);
        Volatile.Write(ref _lastCall, Stopwatch.GetTimestamp());
        Interlocked.Increment(ref _wanted);
        try
        {
            _gate.Wait();
        }
        finally
        {
            Interlocked.Decrement(ref _wanted);
        }
        try
        {
            ThrowIfDisposed();
            var prompt = GoingOn(conversation, messages, built, offered, _options.Thinking, null, 0) ?? built.Ids;
            return prompt.Count - Reuse(prompt, PrimedLength(messages, prompt)).Position;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A STATE OF ITS OWN, because a turn's state is replaced by the next turn, and A TOKEN AT A TIME,
    /// because whoever is waiting matters more than this does: a question waits for one token at most.
    /// It reads as a turn would read it, so the state it keeps is the one a turn would have kept.
    /// </remarks>
    public async Task<bool> PrimeAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<CircleAI.Tools.ToolDefinition>? tools, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ThrowIfDisposed();
        if (_primedDirectory is null) return false;

        var prompt = BuildPrompt(messages, tools is { Count: > 0 } ? tools : null, _options.Thinking, null).Ids;
        var length = PrimedLength(messages, prompt);
        if (length == 0) return false;
        var file = PrimedPath(prompt, length);
        if (File.Exists(file)) return true;

        // A TRUNK THAT STREAMS ITS LAYERS DOES NOT READ IN THE GAPS. A token at a time is a pass over every layer - on the P30
        // on 2026-11-09 the 27B read one token of a 609-token tools block in 26 s, five hours for the block - and it read them
        // into a state of its own, a second 151 MB beside the turn's, which left the drafter 177 MB. The turn that needs the
        // block reads it in whole passes and keeps its primed start on flash for every turn after.
        if (Streams())
        {
            CircleLog.Info(Area, Invariant($"the {length}-token system turn is left to the turn that needs it: this trunk streams its layers, and a token at a time is a pass over all of them"));
            return false;
        }

        var started = Stopwatch.GetTimestamp();
        var progress = Path.ChangeExtension(file, ".priming");
        var state = SoFar(progress, prompt, length) ?? _model.NewState(_state.Capacity, _state.KvMode, _state.KeyForm);
        var from = PrimedFrom = state.Position;
        CircleLog.Info(Area, Invariant($"reading a {length}-token system turn in the gaps between turns, from token {from}"));

        while (true)
        {
            while (Volatile.Read(ref _wanted) > 0) await Task.Delay(GiveWay, ct).ConfigureAwait(false);
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (!File.Exists(file))
                {
                    await Task.Run(() =>
                    {
                        EnsureFfnWarm();
                        try
                        {
                            ReadInTheGap(prompt, length, state, progress, ct);
                        }
                        finally
                        {
                            UsedFfn();
                        }
                    }, ct).ConfigureAwait(false);
                    if (state.Position < length) continue;
                    KeepPrimed(prompt, length, state);
                    CircleLog.Info(Area, Invariant($"{length - from} tokens read in the gaps in {Stopwatch.GetElapsedTime(started).TotalSeconds:F0} s"));
                }
                Forget(progress);
                return File.Exists(file);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    // Reads on until the system turn is in, somebody wants the engine, or the generator is going.
    private void ReadInTheGap(List<int> prompt, int length, ModelState state, string progress, CancellationToken ct)
    {
        // The workers stay busy for the reading as they do for a turn (OPEN-GAPS E34).
        using var spinning = _pool.KeepSpinning();
        while (state.Position < length && Volatile.Read(ref _wanted) == 0 && Volatile.Read(ref _disposed) == 0)
        {
            ct.ThrowIfCancellationRequested();
            _model.Forward(prompt[state.Position], state);
            if (state.Position % PrimingKeptEvery != 0 || state.Position == length) continue;
            try
            {
                WritePrimed(progress, prompt, state.Position, state);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                CircleLog.Warn(Area, "how far the reading got could not be kept: " + e.Message);
            }
        }
    }

    // How far an earlier read of this system turn got, when one was written down for this engine.
    private ModelState? SoFar(string progress, List<int> prompt, int length)
    {
        if (!File.Exists(progress)) return null;
        try
        {
            return ReadPrimed(progress, prompt, length);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            CircleLog.Warn(Area, "how far the reading got could not be read, so it starts again: " + e.Message);
            return null;
        }
    }

    private static void Forget(string progress)
    {
        try { File.Delete(progress); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    internal ByteLevelBpe Tokenizer => _tokenizer;

    /// <summary>The token the last read in the gaps began at: past 0 when it went on from an earlier one.</summary>
    internal int PrimedFrom { get; private set; }

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
        architecture is not null && ModelConfig.Architectures.Contains(architecture)
        && ReadPackings.Any(p => string.Equals(quantization, p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The packings <see cref="Reads"/> accepts, as catalogue rows name them: PQ2_0-MTP is the PQ2_0
    /// brain with its multi-token-prediction head, which this engine drafts with.
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
    public static string WhatItReads => string.Join(" or ", ModelConfig.Architectures) + " in " + string.Join(" or ", ReadPackings);

    /// <summary>
    /// Whether a model of <paramref name="architecture"/> packed as <paramref name="quantization"/>, as a catalogue row
    /// names them, is one this engine computes from flash where it can (<see cref="ComputesFromFlashHere"/>): every layer
    /// read from the model's own file as it is needed and repacked as it lands, no copy of them kept in RAM. That is the
    /// packing the vector kernel computes in tiles - PTQ1_0 - and no other; the file is checked again when it is opened
    /// (<see cref="LanguageModel.WouldPair"/>).
    /// </summary>
    /// <remarks>
    /// THE ENGINE'S HALF OF "RAM IS THE TURBO NOT THE ENGINE". Every fit question asks it through
    /// <see cref="CircleAI.Inference.ModelFit.ReadFromFlash"/>, so a brain read this way is not held to its size in RAM
    /// by the selector, the catalogue's assessor or housekeeping - and one that is kept in RAM (PQ2_0, an MTP file,
    /// another engine's) still is.
    /// </remarks>
    public static bool ReadsFromFlash(string? architecture, string? quantization) =>
        Reads(architecture, quantization) && string.Equals(quantization, "PTQ1_0", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this engine computes from flash on this device: where the vector kernel's tiles are usable, which is
    /// what a model's layers are repacked into as they are read. A head tells the device probe with this
    /// (<see cref="CircleAI.Core.DeviceProbe.PlatformFlashReadProbe"/>).
    /// </summary>
    public static bool ComputesFromFlashHere => Kernels.Ptq1PairMatrix.Usable;

    /// <summary>
    /// Where the engine keeps its stores when its options name no folder: the platform's cache folder. The same folder in
    /// every process of an app - which is how a process that does not hold the model reads what the engine records there
    /// (how far its indexing has got: <see cref="IndexingQueue.ReadStatus"/>).
    /// </summary>
    public static string DefaultStoreDirectory => Path.Combine(Path.GetTempPath(), "CircleAI", "store");

    /// <summary>
    /// The seed every turn samples with when its own options give none: null, the default, draws a new one each turn.
    /// A measurement's knob, set by the Android inference process from each load (a developer's check asks for it:
    /// SoakReceiver "engine" --ei seed), so the same question asked twice - drafting on and off - can be held to the same
    /// reply word for word.
    /// </summary>
    public static int? SeedForTest { get; set; }

    /// <summary>
    /// Tokens the MTP head drafts ahead per pass for a brain whose own options ask for none: 0, the default, decodes
    /// plainly - so every brain the product builds is built without drafting (OPEN-GAPS C7; BrainFactory names no
    /// drafts). A measurement's knob, set by the Android inference process from each load: only a developer's check asks
    /// for it (SoakReceiver "engine" --ei drafts), on a phone, to measure whether drafting is faster there.
    /// </summary>
    public static int DraftsForTest { get; set; }

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
            GgufFile? mtpFile = null;
            try
            {
                // A HEAD STORE THAT FITS THIS MODEL, KEPT FROM AN EARLIER LOAD: then the head is read from flash
                // and no copy of it is made in RAM (a paired model's would be ~140 MB for the 2B).
                // AND THE FEED-FORWARD'S, ~255 MB more for the 2B: read from flash, warm only while turns keep coming.
                // SPLIT BEFORE THE LOAD (download, split, store, then index): the stores are written from the file
                // first, so the copy they replace never exists - a model that would not fit in RAM as a copy (the 4B
                // on a P30) loads at all.
                // AND THE MIXING'S: with both, no layer is copied at all - a turn streams them in (LayerStream), all warm
                // where they fit, a layer ahead of the one computing where they do not (the 27B on the P30).
                SplitBeforeLoad(ggufPath, file, options);
                var store = ExistingHeadStore(ggufPath, file, options);
                var ffn = ExistingFfnStore(ggufPath, file, options);
                var mix = ffn is null ? null : ExistingMixStore(ggufPath, file, options);
                var model = LanguageModel.Load(file, pool, headFromStore: store is not null && LanguageModel.WouldPair(file), ffnFromStore: ffn is not null,
                    mixFromStore: mix is not null);
                // A STREAMED TRUNK DRAFTS FOR ITSELF WITH ITS OWN MTP HEAD (line 662): the product's PTQ1_0 file carries no
                // head, but its sibling PQ2_0-MTP file holds one over the SAME trunk (proven bit for bit) - so when a
                // measurement asks for drafts and this file has no head, the head is read from beside it and attached. One
                // trunk computes, its own head drafts; no second model runs.
                var askedDrafts = options.Drafts > 0 ? options.Drafts : DraftsForTest;
                if (askedDrafts > 0 && !model.HasMtp)
                {
                    if (FindMtpHead(ggufPath, file, options) is { } mtpPath)
                    {
                        try
                        {
                            mtpFile = GgufFile.Open(mtpPath);
                            model.AttachMtp(mtpFile);
                            CircleLog.Info(Area, $"the MTP head is read from {Path.GetFileName(mtpPath)} beside this trunk, which carries none of its own");
                        }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException
                                                   or InvalidOperationException or OutOfMemoryException or UnauthorizedAccessException)
                        {
                            CircleLog.Warn(Area, $"no drafting: the MTP head beside this trunk would not attach ({ex.GetType().Name}: {ex.Message})");
                            mtpFile?.Dispose();
                            mtpFile = null;
                        }
                    }
                    else
                    {
                        CircleLog.Info(Area, $"no drafting: {askedDrafts} ahead was asked for, but no MTP head was found for {Path.GetFileName(ggufPath)} in its folder or the ones beside it");
                    }
                }
                var tokenizer = ByteLevelBpe.FromGguf(file);
                var template = JinjaTemplate.Parse(file["tokenizer.chat_template"].AsString());
                var tower = FindTower(ggufPath, options.VisionTowerPath, file, model.Config);
                var towerPath = tower?.Path;
                var generator = new ManagedChatGenerator(ggufPath, file, pool, model, tokenizer, template, options, towerPath, tower?.Config) { _mtpFile = mtpFile };
                var thinking = options.Thinking ? "on" : "off";
                var memory = options.KvMode == CircleAI.Core.KvCompressionMode.Off ? "fp16" : $"{options.KvMode}, keys {options.KeyForm}";
                var drafting = generator._speculator is { } s ? $", the MTP head drafting {s.Drafts} ahead" : model.HasMtp ? ", its MTP head unused" : "";
                var sight = towerPath is null ? "no vision tower" : $"sees with {Path.GetFileName(towerPath)} within {options.VisionMaxEdge}x{options.VisionMaxEdge}";
                CircleLog.Info(Area, Invariant($"managed engine ready: {Path.GetFileName(ggufPath)}, {model.Packing}, {model.Config.Layers} layers, {threads} threads, context {generator._state.Capacity}, working memory {memory}, thinking {thinking}{drafting}, {sight}, in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms"));
                generator.StartStore(ggufPath, store, ffn, mix);
                return generator;
            }
            catch
            {
                mtpFile?.Dispose();
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
    /// ONE MODEL PER DEVICE (the indexed mind's plan, 2026-11-09): a device runs one model, and it drafts for itself with
    /// its own MTP head where its file has one - never a second model. Two models loaded at once heat the phone past its
    /// skin limit, and there is no avoiding that, so no second model is ever chosen to draft. Always null; the parameters
    /// stay so a host can still ask and be told there is none.
    /// </summary>
    internal static string? FindDrafter(string modelPath, GgufFile model, ManagedGeneratorOptions options) => null;

    /// <summary>
    /// The sibling file that carries this trunk's own MTP head, or null for none. A host may name it
    /// (<see cref="ManagedGeneratorOptions.MtpHeadPath"/>); otherwise the trunk's own folder and the model folders beside it
    /// are scanned for a Bonsai 2 PQ2_0-MTP file whose trunk is the same shape (the 9B's PQ2_0-MTP file beside its PTQ1_0
    /// trunk). The model store keeps each model id in its own folder (BundleModelLoader: &lt;root&gt;/&lt;id&gt;/&lt;file&gt;),
    /// so the head sits one folder over, not beside the trunk file. This is NOT a second model - only its blk.{n_layer} head
    /// is read - so one model per device still holds. Returns null when the trunk's file already carries a head, when the scan
    /// is turned off (empty <see cref="ManagedGeneratorOptions.MtpHeadPath"/>), or when nothing nearby matches.
    /// </summary>
    internal static string? FindMtpHead(string modelPath, GgufFile model, ManagedGeneratorOptions options)
    {
        if (ModelConfig.HasMtpHead(model)) return null; // the trunk's own file has one; nothing to borrow
        if (options.MtpHeadPath is { } named)
            return named.Length > 0 && File.Exists(named) && Fits(named) ? named : null;

        var dir = Path.GetDirectoryName(Path.GetFullPath(modelPath));
        if (dir is null || !Directory.Exists(dir)) return null;
        var self = Path.GetFullPath(modelPath);
        foreach (var candidate in NearbyGgufs(dir))
        {
            if (string.Equals(Path.GetFullPath(candidate), self, StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.GetFileName(candidate).IndexOf("MTP", StringComparison.OrdinalIgnoreCase) < 0) continue; // a head file names itself; no need to open every model nearby
            if (Fits(candidate)) return candidate;
        }
        return null;

        // The trunk's own folder first (both files side by side), then each model folder beside it under the store root -
        // one level only, never a deep walk of wherever the model happens to sit.
        static IEnumerable<string> NearbyGgufs(string dir)
        {
            foreach (var f in Listing(dir)) yield return f;
            var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (parent is null || !Directory.Exists(parent)) yield break;
            IEnumerable<string> siblings;
            try { siblings = Directory.EnumerateDirectories(parent); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { yield break; }
            foreach (var sub in siblings)
            {
                if (string.Equals(Path.GetFullPath(sub), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) continue; // scanned already
                foreach (var f in Listing(sub)) yield return f;
            }
        }

        static IEnumerable<string> Listing(string dir)
        {
            try { return Directory.EnumerateFiles(dir, "*.gguf"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { return []; }
        }

        bool Fits(string path)
        {
            try
            {
                using var f = GgufFile.Open(path);
                if (!ModelConfig.HasMtpHead(f)) return false;
                var mine = ModelConfig.FromGguf(model);
                var theirs = ModelConfig.FromGguf(f);
                return theirs.Embedding == mine.Embedding && theirs.Layers == mine.Layers && theirs.Heads == mine.Heads
                    && theirs.KvHeads == mine.KvHeads && theirs.HeadDim == mine.HeadDim && theirs.FeedForward == mine.FeedForward
                    && theirs.Vocabulary == mine.Vocabulary;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException
                                       or UnauthorizedAccessException or KeyNotFoundException)
            {
                return false;
            }
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
    internal static (string Path, VisionConfig Config)? FindTower(string modelPath, string? asked, GgufFile model, ModelConfig config)
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
    private static VisionConfig ReadTower(string towerPath, ModelConfig model, string? modelName)
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
        await EnterAsync(ct).ConfigureAwait(false);
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
        await EnterAsync(ct).ConfigureAwait(false);
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
                var read = ModelState.Read(reader, _model.Config);
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
            // The store's job reads the mapped file: it stops before the file goes.
            _storeCancel.Cancel();
            try
            {
                _storeTask?.Wait(TimeSpan.FromSeconds(30));
            }
            catch (AggregateException)
            {
                // It ended with an error it already logged.
            }
            _ffnTimer?.Dispose();
            LetFfnGo("the generator is going");
            Volatile.Read(ref _indexing)?.Dispose();
            _pool.Dispose();
            _file.Dispose();
            _mtpFile?.Dispose();
            _towerFile?.Dispose();
        }
        finally
        {
            _gate.Release(); // a call still waiting finds the generator disposed
        }
    }

    // ---- one call ----

    private sealed record Outcome(string Content, string? Reasoning, int TokensIn, int TokensOut, FinishReason Finish, TimeSpan Elapsed);

    // Takes the engine for a call, saying so first: a read in the gaps sees that and stands aside.
    private async Task EnterAsync(CancellationToken ct)
    {
        Volatile.Write(ref _lastCall, Stopwatch.GetTimestamp());
        Interlocked.Increment(ref _wanted);
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _wanted);
        }
    }

    private async Task<Outcome> RunAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options, Action<ChatFragment>? emit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messages);
        options ??= new GenerationOptions();
        await EnterAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(() =>
            {
                // THE WORKERS STAY BUSY FOR THE TURN, AND ONLY THE TURN (OPEN-GAPS E34): sleeping between
                // products, they looked half-idle and the P30 moved them to its slow cores.
                using var spinning = _pool.KeepSpinning();
                return Run(messages, options, emit, ct);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _lastCall, Stopwatch.GetTimestamp());
            _gate.Release();
        }
    }

    private Outcome Run(IReadOnlyList<ChatMessage> messages, GenerationOptions options, Action<ChatFragment>? emit, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var sleptBefore = _pool.Sleeps;
        var (runsBefore, waitedBefore) = (_pool.Runs, _pool.Waited);
        var timing = options.Timing;
        var thinking = ThinkingFor(options);
        var tools = options.Tools is { Count: > 0 } offered ? offered : null;
        var built = BuildPrompt(messages, tools, thinking, options.Effort);
        var requested = options.MaxTokens > 0 ? options.MaxTokens : _options.MaxNewTokens;
        var prompt = GoingOn(options.Conversation, messages, built, tools, thinking, options.Effort, requested) ?? built.Ids;
        if (prompt.Count >= _state.Capacity)
            throw new InvalidOperationException($"The prompt is {prompt.Count} tokens; this generator holds {_state.Capacity}.");

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

        // THE EARS HOLD THE BRAIN UNTIL THIS, so nothing reading ahead takes a token's time from it.
        if (!options.JustRead) BrainHold.Release();

        // SOMEBODY IS STILL TALKING (GenerationOptions.JustRead): as far as they have got, no further.
        // What comes after is their turn's end and the reply's opening, which have to follow the last word.
        var readTo = options.JustRead ? EndOfWhatWasSaid(prompt, lastTurn) : prompt.Count;

        // THE FEED-FORWARD IS WARM BEFORE THE FIRST TOKEN RUNS: streamed in from flash when it is cold. A read ahead
        // while someone is still talking warms it, so a spoken turn seldom waits for it.
        EnsureFfnWarm();

        // AND COMPUTES THE SURFACE OF NEURON GROUPS ASKED FOR, recording what fires only while it computes all of them.
        ApplyFfnControl();
        _model.RecordFfn = _model.Paired && _model.FfnGroups is null;

        // THE HEAD STREAMS IN WHILE THE PROMPT IS READ: the groups of the head's store in the scripts the
        // conversation writes in, on the device's channels. Armed before the first logits; let go at the end.
        var headLoading = StartHead(prompt, options, built);
        IHeadRows? surface = null;
        void ArmHead()
        {
            if (headLoading is null || surface is not null) return;
            try
            {
                surface = headLoading.GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                headLoading = null;
                if (!_model.HeadFromStore || Volatile.Read(ref _headStore) is not { } store)
                {
                    CircleLog.Warn(Area, $"the head's surface did not stream in ({ex.GetType().Name}: {ex.Message}); this turn reads the whole head");
                    return;
                }
                // NO COPY TO FALL BACK ON: the whole head from the store, once more, or the turn cannot answer.
                CircleLog.Warn(Area, $"the head's surface did not stream in ({ex.GetType().Name}: {ex.Message}); streaming in all of it");
                surface = _stream is { Slots: > 0 }
                    ? store.Stream(store.Groups, Volatile.Read(ref _channels), Volatile.Read(ref _readBytes))
                    : store.Load(store.Groups, Volatile.Read(ref _channels));
            }
            _model.Head = surface;
            if (surface is HeadSurface held)
                CircleLog.Info(Area, Invariant($"head: {string.Join(", ", held.Groups.Select(g => g.Script))} - {held.Rows:N0} of {_model.Config.Vocabulary:N0} rows, {held.Bytes / 1048576.0:F1} MB ({held.Layout}), streamed in from flash in {held.Elapsed.TotalMilliseconds:F0} ms on {held.Channels} channel(s){(held.Direct ? ", past the cache" : ", dropped from the cache")}"));
            else if (surface is HeadStream read)
                CircleLog.Info(Area, Invariant($"head: {string.Join(", ", read.Groups.Select(g => g.Script))} - {read.Rows:N0} of {_model.Config.Vocabulary:N0} rows ({read.Layout}), read from flash as each pass scores it: {read.SurfaceBytes / 1048576.0:F0} MB a pass through {read.Bytes / 1048576.0:F0} MB of RAM{(read.Direct ? ", past the cache" : ", dropped from the cache")}"));
        }

        Speculator? speculating = null; // the turn's drafting, for what it did when the turn is stopped
        try
        {
            // A STREAMED TRUNK KEEPS NO MARK, NO CONVERSATION COPY AND NO PRIMED START IN RAM. Each is a copy of every recurrent
            // layer - 157 MB for the 27B - and saves re-reading tokens, which a streamed model reads in passes that cost the same
            // 6.5 GB for one token or a hundred and twenty-eight: re-reading a short conversation rides in the pass the question
            // needs anyway. The mark cost a pass too - the prompt was cut there so it could be taken. The primed start stays on
            // flash, read back into the state itself in about a second.
            var leanState = Lean;
            if (leanState) _state.ForgetCopies();

            var path = Resume(prompt, primed);
            if (timing is not null) timing.KvPath = path;
            var reused = _history.Count;
            _model.TakeTimes(); // whatever an abandoned call left counted

            // A WORD CAN STILL CHANGE: the ears heard "Why is this?" on their way to "Why is the sky blue?",
            // and a recurrent state only steps back to a mark. So only this read is lost, not all of them.
            if (options.JustRead && reused < readTo && !leanState) _state.Mark();

            for (var i = reused; i < readTo; i++)
            {
                ct.ThrowIfCancellationRequested();

                // THE EARS ARE ON THE LAST WORDS (BrainHold): they are what a read ahead is waiting for.
                while (options.JustRead && BrainHold.Now)
                {
                    ct.ThrowIfCancellationRequested();
                    Thread.Sleep(2);
                }

                if (i == primed && _state.PrimedPosition != i) Prime(prompt, i, inRam: !leanState);
                if (i == lastTurn && !leanState) _state.Mark();

                // AN ANSWER'S TOKENS GO IN TOGETHER, up to the next place the state is kept: the same
                // sums, each weight read once for all of them. Reading ahead goes a token at a time, so
                // that whatever real comes along need not wait.
                // A MODEL WHOSE LAYERS STREAM FROM FLASH reads its whole prompt in passes as big as it can: every pass reads every
                // layer, so token by token or eight by eight would read the 27B's 6.8 GB that many times.
                // A STREAMED TRUNK THAT DRAFTS WITH ITS MTP HEAD STILL READS ITS PROMPT IN PASSES: the head catches up on each
                // pass from the trunk's own hidden states (Observed), so it never costs a flash pass a token. A head on a trunk
                // kept whole in RAM reads a token at a time as before - there a pass is no cheaper than a token.
                var together = (options.JustRead && !leanState) || built.Slots is not null || (_speculator is not null && !leanState) ? 1
                    : Together(i, readTo, primed, leanState ? -1 : lastTurn, leanState ? StreamedReadTogether : LanguageModel.ReadTogether);
                if (together > 1)
                {
                    var read = new float[]?[together];
                    // THE HEAD WATCHES EVERY TOKEN THE TRUNK RUNS: a buffer a token for the trunk's h_nextn, fed to the head
                    // after the pass so its cache holds the prompt (the batch's counterpart of Speculator.Run).
                    var hidden = _speculator is { NeedsHidden: true } ? new float[together][] : null;
                    if (hidden is not null) for (var k = 0; k < together; k++) hidden[k] = new float[_model.Config.Embedding];
                    if (i + together == prompt.Count)
                    {
                        read[together - 1] = _logits;
                        ArmHead();
                    }
                    var at = _state.Position;
                    _model.ForwardBatch(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(prompt).Slice(i, together), _state, read, hidden);
                    if (hidden is not null) _speculator!.Observed(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(prompt).Slice(i, together), at, hidden);
                    _history.AddRange(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(prompt).Slice(i, together));
                    i += together - 1;
                    continue;
                }

                if (built.Slots is { } slots && slots[i].Image >= 0)
                {
                    // A picture token goes in as the tower made it; an MTP head drafts from text, so
                    // it does not see this position (its drafts there may miss - the reply cannot).
                    var (image, index) = slots[i];
                    var tokens = Seen(built.Images[image]);
                    if (i == prompt.Count - 1) ArmHead();
                    _model.ForwardImageToken(tokens.Token(index), index, tokens.Columns, tokens.Rows, _state, i == prompt.Count - 1 ? _logits : default);
                }
                else if (_speculator is null)
                {
                    if (i == prompt.Count - 1) ArmHead();
                    _model.Forward(prompt[i], _state, i == prompt.Count - 1 ? _logits : default);
                }
                else
                {
                    if (i == prompt.Count - 1) ArmHead();
                    _speculator.Run(prompt[i], _state, i == prompt.Count - 1 ? _logits : default);
                }
                _history.Add(prompt[i]);
            }
            var prefilled = Stopwatch.GetTimestamp();
            var prefillTimes = _model.TakeTimes();
            if (options.JustRead)
            {
                var read = Stopwatch.GetElapsedTime(started, prefilled);
                CircleLog.Info(Area, Invariant($"read ahead to token {readTo}: {Math.Max(0, readTo - reused)} run ({path}) in {read.TotalMilliseconds:F0} ms"));
                return new Outcome(string.Empty, null, prompt.Count, 0, FinishReason.Stop, read);
            }

            if (prefillTimes.Tokens >= ReadMeasuredFrom) Volatile.Write(ref _readCost, (prefillTimes.Total / prefillTimes.Tokens).Ticks);

            // A STREAMED TRUNK'S WORDS ARE DRAFTED BY THE SMALLER MODEL, which first catches up on what the trunk read - from
            // its primed start when the system turn is the same, so a new question costs it the question.
            var speculator = _speculator ?? (maxTokens > 1 && ArmDrafter(prompt) ? _drafting : null);
            speculating = speculator;
            if (speculator is not null && speculator == _drafting)
            {
                var caught = Stopwatch.GetTimestamp();
                var from = _drafter!.Position;
                _drafter.Sync(_history, primed);
                CircleLog.Info(Area, Invariant($"drafting: the drafter caught up to {_history.Count} tokens from {Math.Min(from, _history.Count)} in {Stopwatch.GetElapsedTime(caught).TotalMilliseconds:F0} ms"));
            }

            var sampler = new TokenSampler(Settings(options, thinking), options.Seed ?? SeedForTest);
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

            speculator?.TakeCounts();
            if (maxTokens > 0)
            {
                var (token, end) = Step(_logits);
                while (!end)
                {
                    if (speculator is not null)
                    {
                        (token, end) = speculator.Step(token, _state, _history, Step);
                        continue;
                    }
                    // THE VOICE IS ON THE FIRST WORDS (BrainHold). The workers sleep by themselves.
                    while (BrainHold.Now) Thread.Sleep(2);

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
            CircleLog.Info(Area, Invariant($"where the time went: prompt {prefillTimes}; reply {_model.TakeTimes()}; the {_pool.Threads - 1} workers slept {_pool.Sleeps - sleptBefore} times; {_pool.Runs - runsBefore} pieces of work, {(_pool.Waited - waitedBefore).TotalMilliseconds:F0} ms of them waiting for the slowest"));
            var (drafted, accepted) = speculator?.TakeCounts() ?? (0L, 0L);
            if (drafted > 0 && speculator == _speculator)
                CircleLog.Info(Area, Invariant($"speculative: the trunk kept {accepted} of {drafted} tokens the MTP head drafted ({100.0 * accepted / drafted:F0}%); the head took {_model.TakeMtpTime().TotalMilliseconds:F0} ms"));
            else if (drafted > 0)
            {
                var (_, drafterRead, drafterReading) = _drafterStream?.TakeCounts() ?? default;
                CircleLog.Info(Area, Invariant($"speculative: the trunk kept {accepted} of {drafted} tokens {Path.GetFileName(_drafterPath)} drafted ({100.0 * accepted / drafted:F0}%); {generated.Count} words from {Math.Max(0, generated.Count - 1 - accepted)} passes of the trunk after the prompt's; the drafter took {_drafterModel?.TakeTimes().Total.TotalMilliseconds ?? 0:F0} ms, {drafterRead / 1048576.0:F0} MB of its layers read from flash in {drafterReading.TotalMilliseconds:F0} ms"));
            }
            if (overruled > 0) CircleLog.Info(Area, Invariant($"the tool call's form overruled the model's first choice {overruled} times"));
            if (thinking) CircleLog.Info(Area, Invariant($"thought for {thoughtTokens} tokens{(closedByBudget ? $", closed by its {thinkingBudget}-token budget" : string.Empty)} ({options.Effort?.TemplateName() ?? "the generator's default"})"));
            Keep(options, messages, built);

            return new Outcome(content.ToString(), options.IncludeReasoning && thought.Length > 0 ? thought.ToString().Trim() : null,
                prompt.Count, generated.Count, finish, Stopwatch.GetElapsedTime(started, done));
        }
        catch (OperationCanceledException)
        {
            // Cancellation lands between steps - a checked batch's included (Speculator.Step) - so the state and the
            // history still agree.
            if (timing is not null) timing.Finish ??= "cancel";
            CircleLog.Info(Area, Invariant($"stopped after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms with {_history.Count} tokens in the state, the prompt being {prompt.Count}"));
            if (speculating is not null && speculating == _drafting)
            {
                // A STREAMED TRUNK'S TURN IS MOSTLY STOPPED BY ITS DEADLINE: what the drafts did is said here too.
                var (drafted, accepted) = speculating.TakeCounts();
                var (_, drafterRead, drafterReading) = _drafterStream?.TakeCounts() ?? default;
                if (drafted > 0)
                    CircleLog.Info(Area, Invariant($"speculative, until it stopped: the trunk kept {accepted} of {drafted} tokens {Path.GetFileName(_drafterPath)} drafted ({100.0 * accepted / drafted:F0}%); the drafter took {_drafterModel?.TakeTimes().Total.TotalMilliseconds ?? 0:F0} ms, {drafterRead / 1048576.0:F0} MB of its layers read from flash in {drafterReading.TotalMilliseconds:F0} ms"));
            }
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
        finally
        {
            LetHeadGo(surface, headLoading, options.JustRead);
            LetDrafterHeadGo();
            UsedFfn();
            KeepFired();
        }
    }

    // ---- step 3: what fires, and the surface of neuron groups ----

    private string? FiredPath => StoreDirectoryOf(_options) is { } directory ? Path.Combine(directory, Path.GetFileName(_file.FilePath) + ".ffn-fired") : null;

    // What fired this call, added to what fired before, and kept on flash beside the stores.
    private void KeepFired()
    {
        if (!_model.Paired) return;
        var (activity, tokens) = _model.TakeFfnActivity();
        if (tokens == 0) return;
        _ffnFired ??= new double[activity.Length];
        if (_ffnFired.Length != activity.Length) return;
        for (var i = 0; i < activity.Length; i++) _ffnFired[i] += activity[i];
        _ffnFiredTokens += tokens;
        if (FiredPath is not { } path) return;
        try
        {
            using var stream = new FileStream(path + ".partial", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 12);
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(_ffnFired.Length);
                writer.Write(_ffnFiredTokens);
                foreach (var value in _ffnFired) writer.Write(value);
            }
            stream.Close();
            File.Move(path + ".partial", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept next turn.
        }
    }

    // What fired in earlier loads, read back.
    private void ReadFired()
    {
        if (FiredPath is not { } path || !File.Exists(path)) return;
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            var count = reader.ReadInt32();
            var tokens = reader.ReadInt64();
            var expected = _model.Config.Layers * (_model.Config.FeedForward / _model.FfnGroupSize);
            if (count != expected) return;
            var fired = new double[count];
            for (var i = 0; i < count; i++) fired[i] = reader.ReadDouble();
            _ffnFired = fired;
            _ffnFiredTokens = tokens;
            CircleLog.Info(Area, Invariant($"what the feed-forward's {_model.FfnGroupSize}-neuron groups fired over {tokens:N0} tokens, read back"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            // Recorded again from the next turn.
        }
    }

    // The share of groups asked for in the control file, applied when the file changes. Under the gate.
    private void ApplyFfnControl()
    {
        if (!_model.Paired || StoreDirectoryOf(_options) is not { } directory) return;
        var file = Path.Combine(directory, FfnSurfaceFile);
        long stamp;
        try
        {
            stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file).Ticks : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (stamp == _ffnControlStamp) return;
        _ffnControlStamp = stamp;
        var percent = 100;
        if (stamp != 0 && int.TryParse(File.ReadAllText(file).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var asked)) percent = Math.Clamp(asked, 1, 100);
        if (percent >= 100 || _ffnFired is null)
        {
            _model.FfnGroups = null;
            CircleLog.Info(Area, percent >= 100 ? "feed-forward: every neuron group computed" : $"feed-forward: {percent}% asked for, but nothing has fired yet to choose by; every group computed");
            return;
        }
        var surface = SurfaceOf(percent);
        _model.FfnGroups = surface;
        CircleLog.Info(Area, Invariant($"feed-forward: {percent}% of the neuron groups computed - {surface.Layer(0).Kept} of {surface.Groups} a layer, the ones that fired most over {_ffnFiredTokens:N0} tokens"));
    }

    private FfnSurface SurfaceOf(int percent) =>
        FfnSurface.Strongest(_ffnFired!, percent / 100.0, _model.Config.Layers, _model.FfnGroupSize, _model.Config.FeedForward);

    // THE SURFACES AGAINST EVERY GROUP, on fixed text read token by token (teacher-forced): how often each gives the
    // same top word, how far its distribution moves (KL), and what a word costs. Under the gate.
    private void RunFidelity(int[] percents)
    {
        if (_ffnFired is null)
        {
            CircleLog.Info(Area, "feed-forward fidelity: nothing has fired yet to choose by - ask it something first");
            return;
        }
        using var spinning = _pool.KeepSpinning();
        EnsureFfnWarm();
        HeadSurface? head = null;
        var saved = _model.FfnGroups;
        var recording = _model.RecordFfn;
        try
        {
            if (_model.HeadFromStore && Volatile.Read(ref _headStore) is { } store)
            {
                head = store.Load(store.Surface([Script.Latin]), Volatile.Read(ref _channels));
                _model.Head = head;
            }
            _model.RecordFfn = false;
            var tokens = _tokenizer.Encode(FidelityText, parseSpecial: false);
            var surfaces = percents.Select(SurfaceOf).ToArray();
            var full = _model.NewState(tokens.Count + 1);
            var states = surfaces.Select(_ => _model.NewState(tokens.Count + 1)).ToArray();
            var vocabulary = _model.Config.Vocabulary;
            var p = new float[vocabulary];
            var q = new float[vocabulary];
            var same = new int[surfaces.Length];
            var kl = new double[surfaces.Length];
            var ticks = new long[surfaces.Length];
            long fullTicks = 0;
            var words = tokens.Count - 1;
            for (var i = 0; i < words; i++)
            {
                _model.FfnGroups = null;
                var started = Stopwatch.GetTimestamp();
                _model.Forward(tokens[i], full, p);
                fullTicks += Stopwatch.GetTimestamp() - started;
                var top = ArgMax(p);
                for (var s = 0; s < surfaces.Length; s++)
                {
                    _model.FfnGroups = surfaces[s];
                    started = Stopwatch.GetTimestamp();
                    _model.Forward(tokens[i], states[s], q);
                    ticks[s] += Stopwatch.GetTimestamp() - started;
                    if (ArgMax(q) == top) same[s]++;
                    kl[s] += Divergence(p, q);
                }
            }
            double Ms(long t) => t * 1000.0 / Stopwatch.Frequency / Math.Max(1, words);
            var lines = Enumerable.Range(0, surfaces.Length).Select(s => Invariant(
                $"{percents[s]}% ({surfaces[s].Layer(0).Kept} of {surfaces[s].Groups} groups a layer): same top word {100.0 * same[s] / Math.Max(1, words):F1}%, KL {kl[s] / Math.Max(1, words):F3}, {Ms(ticks[s]):F1} ms a word"));
            CircleLog.Info(Area, Invariant($"feed-forward fidelity over {words} words of fixed text, read token by token, against every group ({Ms(fullTicks):F1} ms a word): {string.Join("; ", lines)}"));
        }
        finally
        {
            _model.FfnGroups = saved;
            _model.RecordFfn = recording;
            _model.Head = null;
            head?.Dispose();
            UsedFfn();
        }
    }

    // ---- the indexed mind: the queue that builds it in the phone's idle time, and the tiers that serve it ----

    // Thought groups a layer, and the share of a layer's neurons in each group's list, unless the control file asks otherwise.
    // The room a pick asks of the flash for an index is summed from these two (ModelFit.IndexToFile), and a test holds
    // the two together.
    internal const int IndexGroups = 8;
    internal const int IndexPercent = 15;

    // After a unit that failed, the queue is left alone this long: a full disk or a damaged file is not hammered on.
    private static readonly TimeSpan IndexRetryAfter = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How far this model's indexing has got (<see cref="IndexingQueue"/>): its units done of how many - or, where an index
    /// would list every neuron, that none is built and every neuron is computed from the model's own file
    /// (<see cref="IndexingStatus.FromItsOwnFile"/>). Null when it has none - its layers all fit in RAM, it does not compute
    /// in the vector kernel's tiles, or stores are off.
    /// </summary>
    public IndexingStatus? Indexing => Volatile.Read(ref _indexing)?.Status ?? Volatile.Read(ref _everyNeuron)?.Value;

    // ---- for the tests of the idle clock's rules: what the timer does, asked for at once, and what it left ----

    /// <summary>The idle clock's work, done now: what its timer does every fifteen seconds.</summary>
    internal void IdleNow() => IdleCheck(null);

    /// <summary>Sets when a call last wanted the engine to <paramref name="ago"/> before now.</summary>
    internal void LastAskedForTest(TimeSpan ago) =>
        Volatile.Write(ref _lastCall, Stopwatch.GetTimestamp() - (long)(ago.TotalSeconds * Stopwatch.Frequency));

    /// <summary>Whether a finished index is in place to serve this engine's turns.</summary>
    internal bool IndexAdopted => _indexCore is not null;

    /// <summary>Why the unit of indexing in hand last stood aside; empty when none has.</summary>
    internal string StoodAsideWhy => _stoodAsideWhy;

    /// <summary>The stores' start-up work - the head's store, the flash measured - which the indexing waits for.</summary>
    internal Task? StoreWork => _storeTask;

    // Where this model's index lives, beside its other stores.
    private string IndexPath(string kind) => Path.Combine(StoreDirectoryOf(_options)!, Path.GetFileName(_file.FilePath) + "." + kind);

    // THE INDEXING, FOUND OR BEGUN (the owner, 2026-11-12: "We queue the indexing for whenever the phone is idle"). A model
    // whose layers are read from flash has its index built a unit at a time while the phone is idle (IndexingQueue): an
    // earlier sitting's record is carried on from, and a finished one serves from the first turn. A model whose layers all
    // fit in RAM is not indexed unless a queue was begun for it, or one is asked for by name (the control file, which gives
    // the groups and the share: <paramref name="asked"/>). The flash it may take is what the phone has free less what is
    // kept for the phone, decided when the work begins and kept with it.
    private void OpenIndex(int? groups = null, int? percent = null, bool asked = false, bool? coherent = null)
    {
        Volatile.Read(ref _indexing)?.Dispose();
        Volatile.Write(ref _indexing, null);
        Volatile.Write(ref _everyNeuron, null);
        _indexCore = null;
        _indexCoreStore = null;
        _indexMix = null;
        _indexDown = null;
        _indexUse = null;
        if (StoreDirectoryOf(_options) is not { } directory) return;
        try
        {
            if (!_model.MixFromStore || !_model.Paired || _mixStore is not { } mix || _ffnStore is not { } ffn)
            {
                IndexingQueue.NotIndexing(_file, directory);
                return;
            }
            var begun = IndexingQueue.Attempt(_file, directory);
            if (groups is null && begun is null && LayerRecipe(mix, ffn).Slots == 0)
            {
                IndexingQueue.NotIndexing(_file, directory);
                CircleLog.Info(Area, "index: every layer fits in RAM here, so none is built");
                return;
            }

            var c = _model.Config;

            // AN INDEX WHOSE CHECK FAILED IS FOLLOWED BY ONE LISTING MORE NEURONS (IndexingRungs; the owner, 2026-11-15: "if
            // it fails then check why it failed. clean up and try again"). Its files went when its check failed; the next
            // share is other work, from the start. Read from the engine's own record or, where the phone cleared that
            // folder, the copy kept in the app's files - so a share already tried is not built again.
            if (groups is null && percent is null && begun is { Status.Failed: true } failed)
            {
                // THE ENGINE'S OWN GROUPS, NOT THE FAILED INDEX'S: one listing every neuron had one group, and the P30's
                // 4B, built again from fifteen in a hundred after it, kept that one (2026-11-15).
                var next = Revision(failed);
                groups = IndexingRungs.GroupsFor(next, IndexGroups);
                percent = next;
            }

            var inGroups = groups ?? begun?.Groups ?? IndexGroups;
            var askedShare = percent ?? begun?.Share ?? IndexPercent;
            var thoughts = coherent ?? begun?.Coherent ?? false;   // one thought a word for every layer, or each layer's own
            var list = percent is { } share
                ? Math.Max(Kernels.Ptq1PairMatrix.TileRows, c.FeedForward * share / 100)
                : begun?.ListNeurons ?? Math.Max(Kernels.Ptq1PairMatrix.TileRows, c.FeedForward * IndexPercent / 100);
            var neurons = ThoughtIndex.ListOf(list, c.FeedForward);

            // AT EVERY NEURON NO INDEX IS BUILT (the owner, 2026-11-15, of a brain that needs every neuron: "Yes, take
            // over"). An index of every neuron is the model's feed-forward copied to flash again - 1.64 times its bytes,
            // each neuron's output weights rounded to fifteen levels - and the model's own file already is every neuron,
            // exact. So wherever the indexing comes to every neuron - the share after one that failed, a record that says
            // so, or an index of every neuron an earlier sitting left, passed or not - nothing is built and nothing of one
            // is kept: every neuron is computed from the model's own file, RAM keeping what layers it can warm and flash
            // giving the rest every word, and there is no check to run, since what is computed is what a check measures
            // an index against. One asked for by name is built as asked, and serves for that sitting only.
            //
            // ONE THAT PASSED GIVES WAY TOO. On the P30 on 2026-11-15 the 9B's index of every neuron, built by the engine
            // before this rule, passed its check and served: copies of its feed-forward on flash, every word read through
            // them - 1.6 times the bytes of the model's own feed-forward - and an index passes giving the model's own top
            // word nine times in ten, where its own file gives it every time.
            if (!asked && neurons >= c.FeedForward)
            {
                ComputeEveryNeuron(directory);
                return;
            }
            var words = _tokenizer.Encode(WatchText, parseSpecial: false);
            var passages = new List<int[]>();
            for (var at = 0; at < words.Count; at += StreamedReadTogether) passages.Add([.. words.Skip(at).Take(StreamedReadTogether)]);
            var readBytes = Volatile.Read(ref _readBytes);
            var withDown = !CachedIndex.LayersTakeTheirOwnDown(neurons, c.FeedForward);
            var complete = IndexingBudget.CompleteCore(c.Layers, inGroups, neurons, c.Embedding, readBytes, withDown);
            var shared = IndexingBudget.SharedLayers(_file, neurons);
            var free = FreeFlash(directory);
            var (coreBudget, sharedBudget) = IndexingBudget.For(free, complete, shared);
            var queue = IndexingQueue.Open(_file, directory, new IndexingPlan(passages, inGroups, list, coreBudget, sharedBudget, Volatile.Read(ref _channels), readBytes, askedShare, thoughts));
            SayTidied(queue);   // what an earlier build left of an index that failed its check
            if (queue.Plan.CoreBudget < CachedIndex.Stride(neurons, c.Embedding, queue.Plan.ReadBytes, withDown))
            {
                queue.Dispose();
                IndexingQueue.NotIndexing(_file, directory);
                CircleLog.Info(Area, Invariant($"index: none is built - {(free is { } f ? Invariant($"{f / 1048576.0:F0} MB of flash is free") : "the flash that is free is not known")}, and not one piece fits beside the {IndexingBudget.Reserve / 1048576.0:F0} MB kept for the phone"));
                return;
            }
            Volatile.Write(ref _indexing, queue);
            var status = queue.Status;
            if (queue.Complete)
            {
                AdoptIndex(queue);
                return;
            }
            CircleLog.Info(Area, Invariant($"index: {status.Done} of {status.Total} units done ({status.Percent}%); {inGroups} {(thoughts ? "coherent thoughts, a word's one thought found at the first layer and kept through every layer" : "thought groups a layer")}, lists of {neurons} of {c.FeedForward} neurons; step 2's pieces may take {queue.Plan.CoreBudget / 1048576.0:F0} MB of the {complete / 1048576.0:F0} MB all of them are, its layers {(queue.Plan.SharedBudget > 0 ? Invariant($"{queue.Plan.SharedBudget / 1048576.0:F0} MB") : Invariant($"none of the {shared / 1048576.0:F0} MB they take - no room, so they are read from the model's own file in place"))}; next, when the phone is idle: {queue.Next}"));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            CircleLog.Warn(Area, $"index: not set up ({ex.GetType().Name}: {ex.Message}); every neuron is computed");
            Volatile.Read(ref _indexing)?.Dispose();
            Volatile.Write(ref _indexing, null);
        }
    }

    // WHAT FOLLOWS AN INDEX WHOSE CHECK FAILED (IndexingRungs), SAID WITH ITS REASON: the next share where one is left; and
    // after one that listed every neuron - by this engine or an earlier one - every neuron again, with no index built
    // (OpenIndex). An index of every neuron differs from every neuron in one thing, its rounded copy of each neuron's
    // output weights, so that copy is what failed it, not the phone; computed from the model's own file, it is not there.
    // Not the first share again, as an earlier rule had it: those failed for too few neurons, and nothing about them has
    // changed - hours of the phone's idle time into the same failures.
    private static int Revision(IndexAttempt failed)
    {
        var measured = Invariant($"{(failed.Status.Agreement ?? 0) / 10.0:F1}%");
        if (IndexingRungs.Next(failed.Share) is { } next)
        {
            CircleLog.Info(Area, Invariant($"index: the last index listed {failed.Share}% of each layer's neurons and its check measured {measured}, under the {IndexingStatus.LeastAgreement / 10.0:F0}% it must reach: too few neurons for this model, so the next lists {next}%"));
            return next;
        }
        CircleLog.Info(Area, Invariant($"index: the last index listed every neuron and its check measured {measured}{(failed.ThisEngine ? string.Empty : ", made by an earlier engine")}: an index of every neuron differs from every neuron only in its copy of each neuron's output weights, rounded to fifteen levels - the fault was that copy, not the phone, so none is built again, and every neuron is computed from the model's own file"));
        return IndexingRungs.Shares[^1];
    }

    // AN INDEX THAT HAS JUST FAILED ITS CHECK: the next is opened now, so the phone's idle time goes on to it.
    private void ReviseAfterItsCheck(IndexingQueue failed)
    {
        var share = failed.Plan.Share > 0 ? failed.Plan.Share : IndexingRungs.ShareOf(failed.Plan.ListNeurons, _model.Config.FeedForward);
        var next = Revision(new IndexAttempt(failed.Plan.Groups, failed.Plan.ListNeurons, share, failed.Status, ThisEngine: true));
        OpenIndex(IndexingRungs.GroupsFor(next, IndexGroups), next);
    }

    // EVERY NEURON FROM THE MODEL'S OWN FILE, recorded where the service reads how a brain's index stands (IndexingStatus
    // .FromItsOwnFile: it serves, and its brain is ready): whatever an index of this model left on flash goes, nothing is
    // built, and every turn computes every neuron - the layers' stream bringing the whole feed-forward.
    private void ComputeEveryNeuron(string directory)
    {
        SayRemoved(IndexingQueue.RemovePieces(directory, _file.FilePath), "what an index of this model left");
        Volatile.Write(ref _everyNeuron, new StrongBox<IndexingStatus>(IndexingQueue.EveryNeuronFromItsOwnFile(_file, directory)));
        CircleLog.Info(Area, Invariant($"index: none is built - an index of this model would list all {_model.Config.FeedForward} neurons of each layer, and the model's own file is every neuron: every neuron is computed from it, exact, nothing copied to flash, RAM keeping what layers it can warm and the rest read from flash every word"));
    }

    // WHAT THE QUEUE REMOVED FROM FLASH THAT NOTHING READS AGAIN, SAID ONCE (the owner, 2026-11-14: "we need to clean up
    // after ourselves or we will kill users phones"): Watch's records once the core index is built from them, and every
    // file of an index whose check failed.
    private static void SayTidied(IndexingQueue queue)
    {
        var (bytes, what) = queue.TakeTidied();
        SayRemoved(bytes, what);
    }

    private static void SayRemoved(long bytes, string what)
    {
        if (bytes <= 0) return;
        var given = bytes >= 1048576 ? Invariant($"{bytes / 1048576.0:F0} MB") : Invariant($"{Math.Max(1, bytes / 1024)} KB");
        CircleLog.Info(Area, $"index: removed {what} - {given} given back to the flash");
    }

    /// <summary>
    /// Removes everything the engine keeps in <paramref name="directory"/> (its own store folder when null) for the
    /// model whose file is named <paramref name="modelFile"/>: the head's store, the feed-forward's, the index, its
    /// record - the record kept where <paramref name="keepRecord"/> says. For a model that has gone from the device.
    /// Returns the bytes given back to the flash.
    /// </summary>
    /// <remarks>
    /// A BRAIN THAT IS REMOVED LEFT ALL OF THIS BEHIND. Housekeeping deletes a superseded brain's folder in the model
    /// store; what the engine made of it sits in the app's cache folder under the file's name, and nothing looked there.
    /// With the upgrade climbing a step at a time, each brain a device grows out of would have left its stores - and,
    /// for one that answered through its index, gigabytes of pieces.
    /// </remarks>
    /// <param name="modelFile">The model's file.</param>
    /// <param name="directory">The engine's store folder; its own when null.</param>
    /// <param name="keepRecord">
    /// Keep the record of its index (IndexingQueue.RecordName), a few hundred bytes that say how its check went: for a
    /// brain whose index failed its check here, so the climb knows that step failed and does not fetch it again
    /// (BrainInWaiting.StepOf).
    /// </param>
    public static long RemoveStoreOf(string modelFile, string? directory = null, bool keepRecord = false)
    {
        if (string.IsNullOrWhiteSpace(modelFile)) return 0;
        long freed = 0;
        try
        {
            var store = directory ?? DefaultStoreDirectory;
            if (!Directory.Exists(store)) return 0;
            var prefix = Path.GetFileName(modelFile) + ".";
            var record = IndexingQueue.RecordName(modelFile);
            foreach (var path in Directory.EnumerateFiles(store, prefix + "*").ToList())
            {
                // The file's own name and a dot, then what the engine keeps it as - never a file that only begins alike.
                var name = Path.GetFileName(path);
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (keepRecord && string.Equals(name, record, StringComparison.Ordinal)) continue;
                try
                {
                    var file = new FileInfo(path);
                    var bytes = file.Length;
                    file.Delete();
                    freed += bytes;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Held open or gone: it is left, and tried again the next time a brain is removed.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The folder could not be listed: nothing is removed.
        }
        return freed;
    }

    // What is free on the flash the stores are on: the phone's own reading where a head installed one (DriveInfo is
    // refused the data partition on Android), the drive's elsewhere, null when neither says.
    private static long? FreeFlash(string directory)
    {
        try
        {
            if (CircleAI.Core.DeviceProbe.PlatformMemoryProbe is { } phone) return phone().StorageFreeBytes;
            if (OperatingSystem.IsAndroid()) return null;
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            return string.IsNullOrWhiteSpace(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    // THE INDEX A FINISHED QUEUE LEFT ON FLASH, taken up to serve: step 3, step 2's pieces and layers, and what the
    // person's turns have used of it.
    private void AdoptIndex(IndexingQueue queue)
    {
        if (!queue.Status.Serves)
        {
            CircleLog.Info(Area, Invariant($"index: complete, and its check measured {(queue.Status.Agreement ?? 0) / 10.0:F1}% against the {IndexingStatus.LeastAgreement / 10.0:F0}% an index must reach: it does not serve, and every neuron is computed"));
            return;
        }
        if (ThoughtIndex.Open(queue.IndexPath) is not { } core || queue.Split() is not { } split)
        {
            CircleLog.Warn(Area, "index: the queue is complete and its files do not open; every neuron is computed");
            return;
        }
        TakeSplit(core, split);
        _indexUse = IndexUse.Open(IndexPath("use"), core.Layers * core.Groups);
        CircleLog.Info(Area, Invariant($"index: complete - {Thoughts(core)} from {core.Records:N0} watched records, lists of {core.ListNeurons} neurons; {SaySplit(core, split)}"));
    }

    // Step 3 and step 2 put in place to serve.
    private void TakeSplit(ThoughtIndex core, SplitByThoughts split)
    {
        _indexCore = core;
        _indexCoreStore = split.Pieces;
        _indexMix = split.Mixing;
        _indexDown = split.Down;
    }

    // Step 2 in a line, for the log: its pieces, and which of the layers' weights it holds tiled - the rest read from step 1.
    private static string SaySplit(ThoughtIndex core, SplitByThoughts split)
    {
        var pieces = split.Pieces;
        var down = !pieces.HoldsAll(core) || !pieces.HoldsDown
            ? split.Down is not null ? "every layer's ffn_down tiled" : "every layer's ffn_down read from step 1, the model's own file, in place"
            : "each piece its own ffn_down";
        var mix = split.Mixing is not null ? "every layer's attention tiled" : "every layer's attention read from step 1 in place";
        return Invariant($"step 2 holds {pieces.Count} of the thoughts' pieces ({(pieces.HoldsAll(core) ? "all of them" : "not all: the rest gathered from step 1")}), {(core.Coherent ? "a thought at a time, its layers in order" : "a layer at a time")}; {mix}; {down}");
    }

    // No index serves: the model computes every neuron. A stream opened for an index is closed with it. Under the gate.
    private void DropIndex(string why)
    {
        if (_stream is not null && (_indexServing is not null || !_stream.BringsFeedForward)) LetFfnGo(why);
        StopServingIndex();
        _indexCore = null;
        _indexCoreStore = null;
        _indexMix = null;
        _indexDown = null;
        _indexUse = null;
    }

    private static CircleAI.Core.PowerState PowerNow()
    {
        try { return CircleAI.Core.DeviceContext.PowerProbe?.Invoke() ?? CircleAI.Core.PowerState.Unknown; }
        catch { return CircleAI.Core.PowerState.Unknown; }
    }

    // Whether the person is using the phone - its screen is on - or null where the device does not say.
    private static bool? InUseNow()
    {
        try { return CircleAI.Core.DeviceContext.InUseProbe?.Invoke(); }
        catch { return null; }
    }

    // WHETHER A UNIT OF INDEXING STOPS WHERE IT IS: a call is waiting for the engine, the generator is going, or the phone -
    // asked once a second at most - is hot or low, has come off its charger, or (unless the work was asked for by name)
    // has been picked up. OFF ITS CHARGER EVEN WORK ASKED FOR BY NAME STANDS ASIDE (the owner, 2026-11-15: "we should be
    // able to tap in and out without anything crashing out"): on the P30 that day a named run read on, flat out, in a hand
    // off its cable, and the phone ended the engine's service two minutes later and kept it from starting again.
    private bool StandAside(bool named)
    {
        if (Volatile.Read(ref _wanted) > 0 || Volatile.Read(ref _disposed) != 0)
        {
            _stoodAsideWhy = "a call wants the engine";
            _stoodAsideForMemory = false;
            return true;
        }
        var now = Stopwatch.GetTimestamp();
        if (_phoneAsked == 0 || Stopwatch.GetElapsedTime(_phoneAsked, now) >= TimeSpan.FromSeconds(1))
        {
            _phoneAsked = now;
            var power = PowerNow();
            var memory = string.Empty;
            _phoneSaysWhy = power.Hot ? "the phone is hot"
                : power.Low ? "the battery is low"
                : IndexingWhen.OffItsCharger(power) ? "the phone is off its charger"
                : !named && InUseNow() == true ? "the phone is in use"
                : ShortOfMemoryNow(out memory) ? memory
                : string.Empty;
            _phoneSaysStop = _phoneSaysWhy.Length > 0;
            _stoodAsideForMemory = memory.Length > 0;
        }
        if (_phoneSaysStop) _stoodAsideWhy = _phoneSaysWhy;
        return _phoneSaysStop;
    }

    // WHETHER THE PHONE IS TOO SHORT OF MEMORY FOR A UNIT (IndexingWhen.ShortOfMemory), by its own reading now, and how
    // it reads. A named run stands aside for it too: no index is worth another app's process.
    private static bool ShortOfMemoryNow(out string why)
    {
        var phone = MemoryNow.Phone();
        long? line = null;
        try { line = CircleAI.Core.DeviceContext.LowMemoryLineProbe?.Invoke(); }
        catch { /* a phone that will not say its line is held to the least there is */ }
        if (!IndexingWhen.ShortOfMemory(phone.Available, line))
        {
            why = string.Empty;
            return false;
        }
        why = Invariant($"the phone is short of memory: {Megabytes(phone.Available)} MB available, and it ends other apps' processes under {Megabytes(line)} MB");
        return true;
    }

    private static string Megabytes(long? bytes) => bytes is { } b ? (b / 1048576.0).ToString("F0", CultureInfo.InvariantCulture) : "an unknown number of";

    // WHAT A UNIT'S WORK LEFT BEHIND IS GIVEN BACK: every generation collected and the heap compacted, the large objects
    // with it. A heap's free pages are still the process's until then, and on a phone they go to swap as they are.
    private static void Collect() => MemoryNow.GiveBack();

    // Layers between two looks at the memory while a unit reads the model.
    private const int TendEvery = 8;

    // What the heap must have grown by, between two looks, for a collection to be worth its while.
    private const long TendWhenGrownBy = 64L << 20;

    // WHILE A UNIT READS THE MODEL ITS MEMORY IS WATCHED, a layer at a time (the model asks before each): every eighth
    // layer what the memory is is said - the heap, the process, the phone - so a unit that costs the phone its swap
    // says where it went, and where the heap has grown since the last look it is collected, so one layer's leavings do
    // not pile on the next's for a pass of sixty-four. Then the unit's own question: whether to stand aside.
    //
    // COLLECTED, NOT COMPACTED. The first version compacted the heap at every look, large objects and all, and on the
    // P30 on 2026-11-13 that made a 750 MB heap - most of it working memory reserved and never written - resident
    // every eighth layer: "the process holds 535 MB" at the first look and 723 MB at the second, with nothing
    // collected ("753 MB ... 753 MB after a collection").
    private Func<bool> Tending(Func<bool>? stop)
    {
        var layer = 0;
        long tended = 0;
        return () =>
        {
            if (layer++ % TendEvery == 0)
            {
                var heap = GC.GetTotalMemory(forceFullCollection: false);
                var collected = heap - tended > TendWhenGrownBy;
                if (collected) GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
                tended = GC.GetTotalMemory(forceFullCollection: false);
                var phone = MemoryNow.Phone();
                CircleLog.Info(Area, Invariant($"index: memory at layer {layer - 1}: the managed heap {heap / 1048576.0:F0} MB{(collected ? Invariant($", {tended / 1048576.0:F0} MB after a collection") : string.Empty)}; the process holds {Environment.WorkingSet / 1048576.0:F0} MB; the phone has {Megabytes(phone.Available)} MB available and {Megabytes(phone.SwapFree)} MB of swap free"));
            }
            return stop?.Invoke() == true;
        };
    }

    // THE QUEUE'S UNITS, WHILE THE PHONE IS IDLE: one - or, at bedtime or when the work was asked for by name, one after
    // another - each recorded on flash as it is done. A unit stands aside the moment a call wants the engine, and runs
    // again from its start the next time the phone is idle. Under the gate.
    private void RunIndexing(IndexingQueue queue, bool named)
    {
        var host = new IndexingHost(this);
        _indexUsedTheModel = false;
        try
        {
            while (!queue.Complete)
            {
                // A UNIT THAT WORKS FROM FLASH ALONE - the core index, a layer's pieces, the cached portion - holds its own
                // buffers (a layer's three tensors and its pieces): the layers' stream gives its RAM back first.
                if (!queue.NextReadsTheModel && (_stream is not null || _ffnWarm is not null)) LetFfnGo("the index's next unit works from flash alone");
                var what = queue.Next;
                var started = Stopwatch.GetTimestamp();
                var done = queue.RunNext(host, () => StandAside(named));
                var worked = Stopwatch.GetElapsedTime(started);
                var status = queue.Status;
                if (!done)
                {
                    CircleLog.Info(Area, Invariant($"index: stood aside after {worked.TotalSeconds:F1} s of: {what} ({_stoodAsideWhy}); {status.Done} of {status.Total} units done ({status.Percent}%)"));
                    if (_stoodAsideForMemory)
                    {
                        // THE PHONE'S MEMORY COMES FIRST: the layers' stream and the unit's buffers are given back now, and
                        // the unit is tried again after a while, when the phone has room.
                        LetFfnGo("the phone is short of memory");
                        Collect();
                        Volatile.Write(ref _indexNotBefore, Stopwatch.GetTimestamp() + (long)(IndexRetryAfter.TotalSeconds * Stopwatch.Frequency));
                    }
                    return;
                }
                CircleLog.Info(Area, Invariant($"index: {what} - {worked.TotalSeconds:F1} s; {status.Done} of {status.Total} units done ({status.Percent}%)"));
                SayTidied(queue);
                Collect();   // what the unit's work left behind does not wait in RAM for the next
                if (queue.Complete)
                {
                    if (_indexCore is null) AdoptIndex(queue);
                    LetFfnGo("the indexing is done");

                    // A CHECK THAT FAILED IS A REASON, NOT AN END: the next index lists more neurons, built in the phone's
                    // idle time like this one (IndexingRungs).
                    if (queue.Status.Failed && ReferenceEquals(Volatile.Read(ref _indexing), queue)) ReviseAfterItsCheck(queue);
                    return;
                }
                if (named) continue;

                // BEDTIME - idle and on charge - goes straight on; otherwise the phone is left alone as long as the unit took.
                var power = PowerNow();
                var pace = IndexingWhen.Pace(Volatile.Read(ref _wanted) > 0, Stopwatch.GetElapsedTime(Volatile.Read(ref _lastCall)), power, InUseNow());
                if (pace == IndexingPace.BackToBack) continue;
                Volatile.Write(ref _indexNotBefore, Stopwatch.GetTimestamp() + (long)(IndexingWhen.Rest(worked, power).TotalSeconds * Stopwatch.Frequency));
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OutOfMemoryException or ArgumentException or InvalidOperationException)
        {
            CircleLog.Warn(Area, Invariant($"index: a unit failed ({ex.GetType().Name}: {ex.Message}); it is tried again after {IndexRetryAfter.TotalMinutes:F0} minutes, and every neuron is computed meanwhile"));
            Volatile.Write(ref _indexNotBefore, Stopwatch.GetTimestamp() + (long)(IndexRetryAfter.TotalSeconds * Stopwatch.Frequency));
        }
        finally
        {
            _model.Watch = null;
            _model.Interrupt = null;
            if (_indexUsedTheModel)
            {
                // What the units read is not a conversation: the next turn reads its own from the start.
                _state.Reset();
                _history.Clear();
                _talk = null;
            }
            UsedFfn();
        }
    }

    // The queue's units that need the model run through the engine that holds it.
    private sealed class IndexingHost(ManagedChatGenerator engine) : IIndexingHost
    {
        public bool Watch(IReadOnlyList<int> words, ThoughtWatch watch, Func<bool>? stop) => engine.WatchPassage(words, watch, stop);

        public int? Check(ThoughtIndex core, SplitByThoughts split, Func<bool>? stop) => engine.CheckIndex(core, split, stop);
    }

    // WATCH READS A PASSAGE: every neuron computed - no index serving, the stream bringing the whole feed-forward - from a
    // fresh conversation, every word's thought and strong neurons recorded at every layer. It looks for a waiting call
    // before each layer. Under the gate.
    private bool WatchPassage(IReadOnlyList<int> words, ThoughtWatch watch, Func<bool>? stop)
    {
        if (_indexCore is not null || _indexServing is not null) DropIndex("Watch reads with every neuron");
        using var spinning = _pool.KeepSpinning();
        EnsureFfnWarm();
        _indexUsedTheModel = true;
        _state.Reset();
        _history.Clear();
        _model.Watch = watch;
        _model.Interrupt = Tending(stop);
        try
        {
            _model.ForwardBatch(words as int[] ?? [.. words], _state, new float[words.Count][]);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _model.Watch = null;
            _model.Interrupt = null;
            _state.Reset();
        }
    }

    // THE CHECK: the held-out words through every neuron, then through the index as a turn will be served it - the layers'
    // stream reopened the way the index leaves it, the tiers under it - every position's logits in one pass each from a
    // fresh state; how often the top word agrees and what each pass took, said in the log. The index is then in place to
    // serve. False when it stood aside for a call: nothing is in place, and the check runs again. Under the gate.
    /// <summary>What is said after an index's check of the flash it was taken on; nothing where the flash was not measured.</summary>
    internal static string CheckedOn(FlashProfile? flash) => flash is null ? string.Empty : $"; on flash that reads {flash.Speed}";

    private int? CheckIndex(ThoughtIndex core, SplitByThoughts split, Func<bool>? stop)
    {
        var tokens = _tokenizer.Encode(FidelityText, parseSpecial: false);
        var n = Math.Min(CheckWords, tokens.Count);
        int[] words = [.. tokens.Take(n)];
        var logits = new float[n][];
        for (var i = 0; i < n; i++) logits[i] = new float[_model.Config.Vocabulary];
        IHeadRows? head = null;
        using var spinning = _pool.KeepSpinning();
        _indexUsedTheModel = true;
        _model.Interrupt = Tending(stop);
        try
        {
            // EVERY NEURON FIRST: no index serving, the stream bringing the whole feed-forward.
            if (_indexCore is not null || _indexServing is not null) DropIndex("the check reads with every neuron first");
            EnsureFfnWarm();
            _stream?.TakeParts();   // what opening the stream read is not the pass's
            if (_model.HeadFromStore && Volatile.Read(ref _headStore) is { } store)
            {
                head = _stream is { Slots: > 0 }
                    ? store.Stream(store.Surface([Script.Latin]), Volatile.Read(ref _channels), Volatile.Read(ref _readBytes))
                    : store.Load(store.Surface([Script.Latin]), Volatile.Read(ref _channels));
                _model.Head = head;
            }
            _state.Reset();
            _history.Clear();
            var started = Stopwatch.GetTimestamp();
            _model.ForwardBatch(words, _state, logits);
            var full = Stopwatch.GetElapsedTime(started);
            var fullRepacked = _stream?.TakeParts().Repacking ?? TimeSpan.Zero;
            var tops = logits.Select(l => ArgMax(l)).ToArray();

            // THEN AS A TURN WILL HAVE IT: the index in place, the layers' stream opened the way it leaves it.
            LetFfnGo("the check reads through the index");
            TakeSplit(core, split);
            EnsureFfnWarm();
            _stream?.TakeParts();   // what opening the stream read is not the pass's
            _state.Reset();
            started = Stopwatch.GetTimestamp();
            _model.ForwardBatch(words, _state, logits);
            var indexed = Stopwatch.GetElapsedTime(started);
            var same = 0;
            for (var i = 0; i < n; i++)
                if (ArgMax(logits[i]) == tops[i]) same++;

            var counts = _indexServing?.TakeCounts() ?? default;
            var repacked = _stream?.TakeParts().Repacking ?? TimeSpan.Zero;
            _indexServing?.TakeUsage();   // the check's words are not the person's

            // PASS OR FAIL AGAINST THE MEASUREMENT (IndexingStatus.LeastAgreement): an index that gives another top word
            // too often serves nobody - every neuron is computed, and the queue keeps the measure with its record.
            var agreement = (int)Math.Round(1000.0 * same / Math.Max(1, n));
            var passed = agreement >= IndexingStatus.LeastAgreement;

            // THE FLASH'S SPEED BESIDE THE RESULT: a device finds its own limit by climbing (ModelChoice.NextStep), and
            // what its flash measured, said with each result, is what the limit is read against.
            var flash = CheckedOn(Volatile.Read(ref _flash));
            CircleLog.Info(Area, Invariant($"index: the check over {n} held-out words - the same top word as every neuron {100.0 * same / Math.Max(1, n):F1}% of the time, and an index must reach {IndexingStatus.LeastAgreement / 10.0:F0}% to serve: {(passed ? "PASS" : "FAIL")}; every neuron's pass {full.TotalSeconds:F0} s ({fullRepacked.TotalSeconds:F0} s of it repacking the model's own file), the index's {indexed.TotalSeconds:F0} s ({repacked.TotalSeconds:F0} s repacking); its pieces {Served(counts)}{flash}"));
            if (passed) _indexUse = IndexUse.Open(IndexPath("use"), core.Layers * core.Groups);
            else DropIndex("the index failed its check");
            return agreement;
        }
        catch (OperationCanceledException)
        {
            DropIndex("the check stood aside");
            return null;
        }
        catch
        {
            DropIndex("the check failed");
            throw;
        }
        finally
        {
            _model.Interrupt = null;
            _model.Head = null;
            (head as IDisposable)?.Dispose();
            _state.Reset();
            _history.Clear();
        }
    }

    // REVISE, AT BEDTIME (the owner, 2026-11-12: "The more you use it the more it learns what the user needs"): the heat of
    // step 2's pieces written again from what the person's turns used most - what the nights before left counting for half
    // - so step 4 warms those thoughts first from the next turn. The pieces stay where they lie. Under the gate.
    private void ReviseIndex(IndexingQueue queue, IndexUse use)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            LetFfnGo("the night revises what step 4 keeps warm");
            var words = use.Since;
            var pieces = queue.Revise(use.Ranking());
            use.Revised(DateTimeOffset.UtcNow);
            _indexCoreStore = pieces;
            CircleLog.Info(Area, Invariant($"index: revised from {words:N0} words of use in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms - step 2's {pieces.Count} pieces carry the day's heat; step 4 warms the thoughts used most first from the next turn"));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OutOfMemoryException or ArgumentException or InvalidOperationException)
        {
            CircleLog.Warn(Area, $"index: the night's revision failed ({ex.GetType().Name}: {ex.Message}); step 2's heat is as it was");
            Volatile.Write(ref _indexNotBefore, Stopwatch.GetTimestamp() + (long)(IndexRetryAfter.TotalSeconds * Stopwatch.Frequency));
        }
    }

    // Held-out words the check reads: every position's logits in RAM at once, the vocabulary wide each.
    private const int CheckWords = 32;

    // WHAT WATCH READS: plain, varied English - questions, tasks, facts, a story - the kind of words a phone's assistant hears
    // and says. Not the check's text (FidelityText), which is held out.
    //
    // SIX TIMES AS MUCH, OF SIXTEEN MORE KINDS (2026-11-15): the first text was some 360 words, so 8 thought groups held
    // about 45 words each to learn a list from, and on the P30 the 9B's index of 8 groups at half its neurons gave the full
    // read's top word a quarter of the time. Requests, talk, science, history, places, food and health, money and work,
    // phones, travel, a story, numbers, sport and culture, how-to, questions, weather and nature, school: 2,139 words.
    private const string WatchText = WatchFirst + WatchKinds;

    private const string WatchKinds =
        " Wake me up at half past five tomorrow, and turn off the alarm on Saturday. Remind me to take my medicine after " +
        "breakfast. Call the clinic and ask if they are open on public holidays. Read the last message from my boss. Reply to " +
        "Lerato and say I will bring the chairs at noon. Turn on Bluetooth and connect to the car. Lower the brightness, the " +
        "screen is too bright at night. How much battery do I have left? You have forty percent, which is enough for about six " +
        "hours. Take a photo. Start recording a voice note. Stop. Delete that. Open the calendar and show me next week. Do I " +
        "have anything on Wednesday? You have a meeting at nine and a dentist appointment at three." +
        " Hello, how are you today? I am well, thank you, and you? I am a bit tired, I did not sleep much last night. I am " +
        "sorry to hear that. Maybe a short walk in the fresh air will help. That is a good idea. Tell me a joke. Why did the " +
        "chicken cross the road? To get to the other side. That is an old one, but it still makes me smile. I feel worried " +
        "about my exams next month. It is normal to feel nervous before exams. Make a plan, study a little every day, and " +
        "rest well the night before. Thank you for listening. You are welcome, I am here whenever you want to talk." +
        " Plants make their food from sunlight, water and carbon dioxide, and they give off oxygen. This is called " +
        "photosynthesis. The human heart beats about seventy times a minute and pumps blood through the whole body. Sound " +
        "travels through the air at about three hundred and forty metres a second, but light is almost a million times " +
        "faster. An atom has a small heavy centre called the nucleus, with electrons moving around it. Ice floats because " +
        "water expands when it freezes. A magnet has a north pole and a south pole, and opposite poles pull towards each " +
        "other. A vaccine teaches the body to recognise a germ, so that it can fight it off quickly later." +
        " Nelson Mandela was released from prison in 1990 after twenty-seven years, and four years later he became the " +
        "first president of a democratic South Africa. The ancient Egyptians built the pyramids at Giza more than four " +
        "thousand years ago. The kingdom of Mapungubwe traded gold and ivory near the Limpopo River long before Europeans " +
        "arrived. The First World War began in 1914 and ended in 1918. People first walked on the Moon in 1969. The printing " +
        "press, built in Germany in the fifteenth century, made books cheaper and helped ideas spread. Many African countries " +
        "won their independence in the 1950s and 1960s, Ghana among the first, in 1957." +
        " Lesotho is a small mountain kingdom completely surrounded by South Africa. The Sahara is the largest hot desert " +
        "in the world, and it covers much of northern Africa. Lake Victoria is shared by Uganda, Kenya and Tanzania. The " +
        "Amazon rainforest grows across Brazil and several of its neighbours. Australia is both a country and a continent. " +
        "The Drakensberg mountains run along the eastern edge of the high plateau. Madagascar, off the coast of Mozambique, " +
        "is home to animals found nowhere else, such as lemurs. The Indian Ocean is warm along the coast of KwaZulu-Natal, " +
        "while the Atlantic near the Cape is cold." +
        " To cook pap, bring salted water to the boil, stir in maize meal slowly so that no lumps form, then cover the pot " +
        "and let it steam on low heat for twenty minutes. A balanced plate has vegetables, some protein such as beans, fish " +
        "or chicken, and a starch. Drink enough clean water every day, and more when it is hot. Wash your hands with soap " +
        "before eating and after using the toilet. If someone has a small burn, cool it under running water for twenty " +
        "minutes, and do not put butter on it. A very young baby with a fever should be seen by a doctor. Bananas and " +
        "oranges make good snacks. Too much salt and sugar is bad for the heart." +
        " My salary is paid on the twenty-fifth of every month. After rent, food and transport I try to save a tenth of what " +
        "is left. How much is fifteen percent of two thousand rand? It is three hundred rand. Send an invoice to the client " +
        "for the work we finished in March, and ask them to pay within thirty days. I have a job interview on Monday morning. " +
        "Prepare by reading about the company, dressing neatly, and arriving ten minutes early. Keep a copy of every receipt " +
        "in case you need to return something. A loan with a high interest rate can cost much more than the amount you " +
        "borrowed. Check your bank statement each month for payments you do not recognise." +
        " Your phone connects to the internet through Wi-Fi or mobile data. Wi-Fi is usually faster at home, and it does " +
        "not use your data bundle. Bluetooth links your phone to earphones, a speaker or a car over a short distance. To " +
        "save battery, lower the screen brightness, close apps you are not using, and turn off location when you do not " +
        "need it. A strong password is long and mixes words, numbers and symbols. Never share the code the bank sends you " +
        "by message, not even with someone who says they work at the bank. Back up your photos so you do not lose them if " +
        "the phone is broken or stolen. An update can fix problems and close security holes." +
        " How do I get to the station from here? Walk straight down this road for two blocks, turn left at the traffic " +
        "lights, and the station is on your right. The taxi to town costs fifteen rand and leaves when it is full. My " +
        "flight to Cape Town leaves at seven, so I must be at the airport by half past five. Do you have a window seat? " +
        "Please keep your bag under the seat in front of you. The bus from Polokwane arrives at the terminal at four in the " +
        "afternoon. How long does it take to drive from Johannesburg to Bloemfontein? About four hours on the N1. Remember " +
        "to carry your identity document when you travel." +
        " There was once an old fisherman who lived alone at the edge of a quiet bay. Each morning before sunrise he rowed " +
        "his small boat out past the rocks and cast his net. Most days he caught only enough for himself, and he shared the " +
        "rest with the children who waited on the sand. One stormy evening a young boy was swept off the rocks by a wave. " +
        "The fisherman heard him shouting, pushed his boat into the dark water and pulled the boy aboard. In the morning the " +
        "whole village came to his door with bread, tea and blankets, and from that day he was never alone again." +
        " There are seven days in a week, twelve months in a year, and three hundred and sixty-five days in a year, except " +
        "a leap year, which has one more. What is one hundred minus thirty-seven? It is sixty-three. Half of ninety is " +
        "forty-five. A kilometre is one thousand metres, and a kilogram is one thousand grams. If a loaf costs eighteen " +
        "rand, three loaves cost fifty-four rand. Christmas is on the twenty-fifth of December. What day comes after " +
        "Thursday? Friday. Count from one to ten: one, two, three, four, five, six, seven, eight, nine, ten. Seventy-two " +
        "divided by eight is nine." +
        " South Africa won the Rugby World Cup in 1995, 2007, 2019 and 2023. In football a match has two halves of " +
        "forty-five minutes, and each team has eleven players on the field. Bafana Bafana is the name of the national " +
        "football team. Marathon runners cover just over forty-two kilometres. Miriam Makeba was a famous singer who sang " +
        "about freedom and was known around the world. Many people enjoy reading, singing in a choir, or dancing at parties " +
        "and festivals. Heritage Day is on the twenty-fourth of September, when people wear traditional clothes and share " +
        "food from their cultures." +
        " How do I change a flat tyre? Park on flat ground, put on the handbrake, loosen the nuts a little, lift the car " +
        "with the jack, take off the wheel, put on the spare, tighten the nuts and lower the car. To plant a tree, dig a " +
        "hole twice as wide as its roots, set the tree in it, fill it with soil and water it well. To clean a burnt pot, soak " +
        "it in hot soapy water first. How do I take a screenshot? Press the power button and the volume down button " +
        "together. To make a budget, write down what you earn, list what you spend, and look for things you can cut." +
        " What is the biggest animal on Earth? The blue whale is the biggest animal that has ever lived. Why do we have " +
        "seasons? Because the Earth is tilted, each half of it leans towards the sun for part of the year. How many legs " +
        "does a spider have? Eight. What language is spoken in Brazil? Portuguese. Who painted the Mona Lisa? Leonardo da " +
        "Vinci. What is the freezing point of water in Fahrenheit? Thirty-two degrees. Where do penguins live? Mostly in the " +
        "southern half of the world, and some live on the coast near Cape Town. What is the opposite of ancient? Modern." +
        " The weather service warns of strong winds along the coast tonight, with waves up to four metres. In winter the " +
        "highveld has cold, dry mornings and frost, but the days are often sunny. Summer brings thunderstorms late in the " +
        "day, with lightning and heavy rain that can flood low roads. Never cross a flooded river or bridge. A rainbow " +
        "appears when the sun shines through drops of rain. Lions live in groups called prides, and elephants can live for " +
        "seventy years. The baobab tree stores water in its thick trunk to survive long dry seasons." +
        " A noun is the name of a person, place or thing, and a verb is a doing word. Please help me with my homework: what " +
        "is the plural of child? The plural of child is children. How do you greet someone in isiZulu? You say sawubona to " +
        "one person and sanibonani to a group. Spell the word necessary. N, E, C, E, S, S, A, R, Y. My teacher asked us to " +
        "write an essay about someone we admire. Start with an introduction, give three reasons in the body, and end with a " +
        "conclusion. Reading every day makes your vocabulary bigger and your writing better.";

    private const string WatchFirst =
        "What time is it in Johannesburg right now? Set a timer for ten minutes and remind me to call my sister at six. " +
        "The weather today is sunny with a light wind from the south, and tomorrow it may rain in the afternoon. " +
        "Water boils at one hundred degrees Celsius at sea level, and freezes at zero. The sun is a star, and the Earth " +
        "goes around it once a year, while the Moon goes around the Earth about once a month. " +
        "How do I make a cup of tea? Boil the water, put a tea bag in a cup, pour the water over it, wait three minutes, " +
        "then add milk or sugar if you like. Can you read me my messages? You have two new messages: one from Thandi " +
        "asking about lunch on Friday, and one from the bank about your card. " +
        "Africa is the second largest continent, and the Nile is one of the longest rivers in the world. Mount Kilimanjaro " +
        "in Tanzania is the highest mountain in Africa. Cape Town sits below Table Mountain, near the meeting of two oceans. " +
        "Please add eggs, bread, rice and tomatoes to my shopping list. What is twelve times eight? Twelve times eight is " +
        "ninety-six. How far is it from Durban to Pretoria? It is about six hundred kilometres by road. " +
        "Once upon a time a small girl lived in a village by a river. Every morning she walked to school with her brother, " +
        "and every evening they helped their grandmother cook supper over the fire. One day they found a lost goat near " +
        "the water, and they carried it home to the farmer, who thanked them with a basket of oranges. " +
        "Turn on the torch. Turn up the volume. Play some music. Call my mother. Send a message to John saying I am " +
        "running ten minutes late. What is the capital of Kenya? The capital of Kenya is Nairobi. Who wrote Things Fall " +
        "Apart? Chinua Achebe wrote it. Why is the sky blue? Sunlight is scattered by the air, and blue light is scattered " +
        "more than red, so the sky looks blue. Thank you, that was helpful. Good night.";

    private static int ArgMax(ReadOnlySpan<float> values)
    {
        var best = 0;
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] > values[best]) best = i;
        }
        return best;
    }

    // KL(P || Q) over the tokens both can say (a token outside the head's surface is -inf in both).
    private static double Divergence(ReadOnlySpan<float> p, ReadOnlySpan<float> q)
    {
        double maxP = double.NegativeInfinity, maxQ = double.NegativeInfinity;
        for (var i = 0; i < p.Length; i++)
        {
            if (p[i] > maxP) maxP = p[i];
            if (q[i] > maxQ) maxQ = q[i];
        }
        double sumP = 0, sumQ = 0;
        for (var i = 0; i < p.Length; i++)
        {
            if (!float.IsNegativeInfinity(p[i])) sumP += Math.Exp(p[i] - maxP);
            if (!float.IsNegativeInfinity(q[i])) sumQ += Math.Exp(q[i] - maxQ);
        }
        double logZp = maxP + Math.Log(sumP), logZq = maxQ + Math.Log(sumQ), kl = 0;
        for (var i = 0; i < p.Length; i++)
        {
            if (float.IsNegativeInfinity(p[i])) continue;
            var lp = p[i] - logZp;
            var lq = float.IsNegativeInfinity(q[i]) ? -1e9 : q[i] - logZq;
            kl += Math.Exp(lp) * (lp - lq);
        }
        return kl;
    }

    // Ordinary English the fidelity run reads: a day of errands, the kind of words a person says to it.
    private const string FidelityText =
        "Morning light came through the kitchen window while the kettle began to hum. Sipho checked the weather on his phone: " +
        "light rain in the afternoon, clearing by evening. He packed an umbrella, a sandwich and the library book he had promised " +
        "to return. On the bus he read about how bees find their way home, using the position of the sun and the smell of their hive. " +
        "At work the printer jammed twice before lunch, so he opened the tray, removed a crumpled page and showed a colleague how to " +
        "reset it. In the evening he called his mother, who asked whether he was eating properly and reminded him that his cousin's " +
        "wedding was in three weeks. Before bed he set an alarm for six, plugged in his phone and wrote a short list for tomorrow: " +
        "buy milk, pay the electricity bill, and fix the dripping tap in the bathroom.";

    // ---- the head's store by script ----

    // THE TURN IS OVER: RAM lets the head go. Its surface is freed; one still streaming in is freed when it
    // lands; and a turn that read the whole head as it lies lets the OS take those pages back too.
    private void LetHeadGo(IHeadRows? surface, Task<IHeadRows>? loading, bool justRead)
    {
        _model.Head = null;
        if (surface is not null)
        {
            if (surface is HeadStream read)
            {
                var (bytes, reading) = read.TakeCounts();
                CircleLog.Info(Area, Invariant($"head: {bytes / 1048576.0:F0} MB read from flash in {reading.TotalMilliseconds:F0} ms this turn ({(reading.TotalSeconds > 0 ? bytes / 1048576.0 / reading.TotalSeconds : 0):F0} MB/s)"));
            }
            (surface as IDisposable)?.Dispose();
            return;
        }
        if (loading is not null)
        {
            _ = loading.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion) (t.Result as IDisposable)?.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return;
        }
        if (!justRead && Volatile.Read(ref _headStore) is not null) HeadStore.ReleaseMapped(_file);
    }

    // The surface a turn streams in, started now. A model that keeps no copy of its head (HeadFromStore) streams
    // one for every turn that wants logits, all of the head's groups when the conversation writes in that many
    // scripts. Otherwise null - the turn reads the whole head as it lies - for no store yet, a read ahead (no
    // logits), pictures, MTP drafting, or so many scripts that nearly all of the head would come in anyway.
    // A TRUNK THAT STREAMS ITS LAYERS STREAMS ITS HEAD TOO: read from flash as each pass scores it, two windows in RAM
    // where the surface would hold 198 MB of the 27B's - the room the drafter needs beside it on the P30.
    private Task<IHeadRows>? StartHead(List<int> prompt, GenerationOptions options, PromptIds built)
    {
        var store = Volatile.Read(ref _headStore);
        if (store is null || options.JustRead) return null;
        var served = _model.HeadFromStore;
        if (!served && (_speculator is not null || built.Slots is not null)) return null;
        var scripts = ScriptsOf(prompt);
        var groups = store.Surface(scripts);
        if (!served && groups.Sum(g => (long)g.Rows) >= store.Rows * 9L / 10)
        {
            CircleLog.Info(Area, $"head: the whole head as it lies this turn - the conversation writes in {string.Join(", ", scripts)}");
            return null;
        }
        if (_stream is { Slots: > 0 })
        {
            // A STREAMED TRUNK STREAMS ITS HEAD - two windows in RAM, the surface re-read from flash each pass. A plain turn
            // reads it once a token, but an MTP head drafting reads it on EVERY draft step and verify, so streaming it is
            // gigabytes of flash a turn (5.4 GB measured for 24 tokens at 2 drafts on the P30). HOLD IT IN RAM for the turn
            // when there is room: loaded once, every draft reads it from RAM. Without room it streams as before.
            if (HoldStreamedHeadInRam(_speculator is not null, MemoryNow.AvailableBytes(), DrafterReserve, SurfaceBytes(store, groups)))
            {
                CircleLog.Info(Area, Invariant($"head: held in RAM for the turn ({SurfaceBytes(store, groups) / 1048576.0:F0} MB), not re-streamed from flash each draft step - the MTP head reads it many times a turn"));
                return LoadHead(store, groups);
            }
            try
            {
                return Task.FromResult<IHeadRows>(store.Stream(groups, Volatile.Read(ref _channels), Volatile.Read(ref _readBytes)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
            {
                return Task.FromException<IHeadRows>(ex);
            }
        }
        return LoadHead(store, groups);
    }

    // A surface streamed in on the device's channels, off the turn's thread.
    private Task<IHeadRows> LoadHead(HeadStore store, IReadOnlyList<HeadStore.Group> groups)
    {
        var channels = Volatile.Read(ref _channels);
        return Task.Factory.StartNew<IHeadRows>(() => store.Load(groups, channels), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    // The writing systems a conversation's tokens are in: the scripts of the head's surface, the trunk's and the drafter's.
    private HashSet<Script> ScriptsOf(List<int> prompt)
    {
        var scripts = new HashSet<Script>();
        foreach (var token in prompt)
        {
            if ((uint)token < (uint)_tokenizer.Count) scripts.Add(ScriptOf(token));
        }
        return scripts;
    }

    // A token's writing system, as the head's store groups it.
    private Script ScriptOf(int token) => ScriptOf(_tokenizer, token);

    private static Script ScriptOf(ByteLevelBpe tokenizer, int token) =>
        TokenScripts.Of(tokenizer.Piece(token, special: true), tokenizer.Kind(token) is TokenKind.Control or TokenKind.UserDefined or TokenKind.Unknown);

    // THE STORES WRITTEN STRAIGHT FROM THE FILE BEFORE THE MODEL IS BUILT, for a model the vector kernel repacks: its head
    // by script and its feed-forward, each checked against the file, so the load that follows keeps neither as a copy.
    // Once per model; a failure leaves the load as it was, with its copies.
    private static void SplitBeforeLoad(string ggufPath, GgufFile file, ManagedGeneratorOptions options)
    {
        if (!LanguageModel.WouldPair(file) || StoreDirectoryOf(options) is not { } directory) return;
        if (ModelConfig.HasMtpHead(file) && options.Drafts > 0) return; // the MTP head drafts from the whole table
        try
        {
            var headPath = StorePath(directory, ggufPath);
            if (HeadStore.Unreadable(file) is null && HeadStore.Open(file, headPath, HeadLayout.Paired) is null)
            {
                var tokenizer = ByteLevelBpe.FromGguf(file);
                var started = Stopwatch.GetTimestamp();
                var head = HeadStore.Write(file, headPath, token => ScriptOf(tokenizer, token), layout: HeadLayout.Paired);
                var written = Stopwatch.GetElapsedTime(started);
                var wrong = head.Verify(file);
                CircleLog.Info(Area, Invariant($"split before the load: the head's store written (Paired), {head.Bytes / 1048576.0:F0} MB in {written.TotalMilliseconds:F0} ms; checked in {(Stopwatch.GetElapsedTime(started) - written).TotalMilliseconds:F0} ms: {wrong} of {head.Rows:N0} rows differ from the model"));
                if (wrong != 0) File.Delete(headPath);
            }
            // NO STORE OF THE LAYERS: ONE COPY OF THE WEIGHTS. They are read from the model's own file and repacked as they
            // land (TileStore.InPlace). A written store of the 27B was 6.5 GB beside its 5.9 GB file on a P30 with 5.2 GB
            // left, and writing it took ten minutes at the eMMC's 12 MB/s.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            CircleLog.Warn(Area, $"the split before the load failed ({ex.GetType().Name}: {ex.Message}); the model loads with its copies");
        }
    }

    // Where the head's store is kept: the option's folder, the platform's cache folder beside the primed starts
    // when none is given, and null when the option turns it off.
    private static string? StoreDirectoryOf(ManagedGeneratorOptions options) => options.StoreDirectory switch
    {
        null => DefaultStoreDirectory,
        "" => null,
        var given => given,
    };

    private static string StorePath(string directory, string ggufPath) => Path.Combine(directory, Path.GetFileName(ggufPath) + ".head");

    // The head store an earlier load left that fits this model as it will run here - its shape the kernel's - so
    // this load can keep no copy of the head; null when there is none, or it is off or another shape's.
    private static HeadStore? ExistingHeadStore(string ggufPath, GgufFile file, ManagedGeneratorOptions options)
    {
        if (StoreDirectoryOf(options) is not { } directory || HeadStore.Unreadable(file) is not null) return null;
        if (ModelConfig.HasMtpHead(file) && options.Drafts > 0) return null; // the MTP head drafts from the whole table
        var layout = LanguageModel.WouldPair(file) ? HeadLayout.Paired : HeadLayout.Rows;
        return HeadStore.Open(file, StorePath(directory, ggufPath), layout);
    }

    private static string FfnPath(string directory, string ggufPath) => Path.Combine(directory, Path.GetFileName(ggufPath) + ".ffn");

    // The feed-forward read in place from the model's own file, so this load keeps no copy of it in RAM and flash keeps no
    // second one; null when stores are off or the model does not compute in the vector kernel's tiles here.
    private static TileStore? ExistingFfnStore(string ggufPath, GgufFile file, ManagedGeneratorOptions options) =>
        InPlace(file, options, TileSet.FeedForward);

    // The mixing, read in place the same way: with the feed-forward's, every layer is read from flash, none copied into RAM.
    private static TileStore? ExistingMixStore(string ggufPath, GgufFile file, ManagedGeneratorOptions options) =>
        InPlace(file, options, TileSet.Mixing);

    private static TileStore? InPlace(GgufFile file, ManagedGeneratorOptions options, TileSet set) =>
        StoreDirectoryOf(options) is not null && LanguageModel.WouldPair(file) && TileStore.Unreadable(file, set) is null
            ? TileStore.OverModel(file, set)
            : null;

    // Makes the stores ready in the background, once per model: the head's and the feed-forward's written and checked
    // against the model if no earlier load left them, the device's flash measured if it never was, then turns may use
    // them. Every reason one is not used is said.
    private void StartStore(string ggufPath, HeadStore? existing, TileStore? existingFfn, TileStore? existingMix)
    {
        // A model that keeps no copy of its feed-forward has its store from the first turn, and lets the warm one go
        // when it has been idle long enough. The idle clock also does a fidelity run when one is asked for.
        if (existingFfn is not null) _ffnStore = existingFfn;
        if (existingMix is not null && _model.MixFromStore) _mixStore = existingMix;
        OpenIndex();
        if (StoreDirectoryOf(_options) is not null)
        {
            ReadFired();
            _ffnTimer = new System.Threading.Timer(IdleCheck, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        }
        if (StoreDirectoryOf(_options) is not { } directory)
        {
            CircleLog.Info(Area, "no head store: turned off (StoreDirectory is empty); every turn reads the whole head");
            return;
        }
        if (HeadStore.Unreadable(_file) is { } why)
        {
            CircleLog.Info(Area, $"no head store: {why}; every turn reads the whole head");
            return;
        }
        if (_speculator is not null)
            CircleLog.Info(Area, _model.HeadFromStore
                ? "the MTP head drafts from the head's store too (the turn's script surface), held in RAM for the turn where there is room"
                : "the head is kept whole in RAM, so the MTP head's drafts read all of it");

        // A model that keeps no copy of its head has its store from the first turn: nothing else can score it.
        if (existing is not null) Volatile.Write(ref _headStore, existing);
        var ct = _storeCancel.Token;
        _storeTask = Task.Factory.StartNew(() => PrepareStore(ggufPath, directory, existing, existingFfn, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void PrepareStore(string ggufPath, string directory, HeadStore? existing, TileStore? existingFfn, CancellationToken ct)
    {
        FlashProfile? profile = null;
        try
        {
            var path = StorePath(directory, ggufPath);
            var layout = _model.Paired ? HeadLayout.Paired : HeadLayout.Rows;
            var started = Stopwatch.GetTimestamp();
            var store = existing ?? HeadStore.Open(_file, path, layout);
            if (existing is not null)
            {
                CircleLog.Info(Area, Invariant($"the head is read from its store on flash ({layout}): {Scripts(store!)}; {store!.Bytes / 1048576.0:F0} MB, no copy of it in RAM"));
            }
            else if (store is null)
            {
                store = HeadStore.Write(_file, path, ScriptOf, layout: layout, ct: ct);
                var written = Stopwatch.GetElapsedTime(started);
                var wrong = store.Verify(_file);
                var checkedIn = Stopwatch.GetElapsedTime(started) - written;
                CircleLog.Info(Area, Invariant($"the head's store by script written ({layout}): {Scripts(store)}; {store.Bytes / 1048576.0:F0} MB in {written.TotalMilliseconds:F0} ms; checked in {checkedIn.TotalMilliseconds:F0} ms: {wrong} of {store.Rows:N0} rows differ from the model"));
                if (wrong != 0)
                {
                    File.Delete(path);
                    CircleLog.Warn(Area, "the head's store was wrong and is gone; every turn reads the whole head");
                    return;
                }
                if (_model.Paired) CircleLog.Info(Area, "turns read their head from the store now; the head's copy in RAM goes at the next load");
            }
            else
            {
                HeadStore.ReleaseMapped(_file);
                CircleLog.Info(Area, Invariant($"the head's store by script found ({layout}): {Scripts(store)}; {store.Bytes / 1048576.0:F0} MB"));
            }

            // THE DEVICE'S FLASH, measured once on the store, for the channels a turn streams on. A store that
            // cannot be measured still serves, on the default channels.
            ct.ThrowIfCancellationRequested();
            try
            {
                profile = FlashProfile.Remembered(directory);
                if (profile is null)
                {
                    var measuring = Stopwatch.GetTimestamp();
                    profile = FlashProfile.Measure(path, directory, ct: ct);
                    CircleLog.Info(Area, Invariant($"flash measured in {Stopwatch.GetElapsedTime(measuring).TotalMilliseconds:F0} ms: {profile}"));
                }
                else
                {
                    CircleLog.Info(Area, $"flash as measured before: {profile}");
                }
                Volatile.Write(ref _channels, profile.Channels);
                Volatile.Write(ref _readBytes, profile.ChunkBytes);
                Volatile.Write(ref _flash, profile);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                CircleLog.Warn(Area, $"the flash could not be measured ({ex.GetType().Name}: {ex.Message}); turns stream on {Volatile.Read(ref _channels)} channels");
            }
            if (existing is null) Volatile.Write(ref _headStore, store);
        }
        catch (OperationCanceledException)
        {
            return; // the generator is going
        }
        catch (Exception ex)
        {
            CircleLog.Warn(Area, $"the head's store could not be made ({ex.GetType().Name}: {ex.Message}); every turn reads the whole head");
        }

        if (_model.Paired) PrepareFfn(ggufPath, directory, existingFfn, profile, ct);

        static string Scripts(HeadStore store) =>
            string.Join(", ", store.Groups.Select(g => g.Script.ToString() + " " + g.Rows.ToString("N0", CultureInfo.InvariantCulture)));
    }

    // THE FEED-FORWARD'S STORE, for a model that computes in the vector kernel's tiles: found, and its recipe set - how
    // long a warm one stays after a turn - or written and checked for the next load, whose feed-forward is then read
    // from flash and kept in RAM only while it is warm.
    private void PrepareFfn(string ggufPath, string directory, TileStore? existing, FlashProfile? profile, CancellationToken ct)
    {
        try
        {
            if (existing is not null)
            {
                // THE RECIPE: where the flash brings the feed-forward back in a fraction of a second it streams in every
                // turn; where it takes longer, a warm one stays while turns keep coming.
                var rate = profile?.Scattered.Where(p => p.Bytes == profile.ChunkBytes && p.Channels == profile.Channels).Select(p => p.MegabytesPerSecond).FirstOrDefault() ?? 0;
                var bringBack = rate > 0 ? TimeSpan.FromSeconds(existing.DataBytes / (rate * 1024 * 1024)) : TimeSpan.FromSeconds(10);
                var keep = bringBack < FfnStreamEveryTurnBelow ? TimeSpan.Zero : FfnKeptWarm;
                Volatile.Write(ref _ffnKeepTicks, (long)(keep.TotalSeconds * Stopwatch.Frequency));
                CircleLog.Info(Area, Invariant($"the feed-forward is read from {(existing.InPlace ? "the model's own file" : "its store on flash")}: {existing.DataBytes / 1048576.0:F0} MB as tiles, no copy of it in RAM; bringing it in takes about {bringBack.TotalMilliseconds:F0} ms here, so {(keep == TimeSpan.Zero ? "it streams in every turn" : $"a warm one stays {keep.TotalSeconds:F0} s after a turn")}"));
                return;
            }
            if (FfnStore.Unreadable(_file) is { } why)
            {
                CircleLog.Info(Area, $"no feed-forward store: {why}; the feed-forward stays in RAM");
                return;
            }
            var path = FfnPath(directory, ggufPath);
            var started = Stopwatch.GetTimestamp();
            var store = FfnStore.Open(_file, path);
            if (store is null)
            {
                store = FfnStore.Write(_file, path, ct);
                var written = Stopwatch.GetElapsedTime(started);
                var wrong = store.Verify(_file);
                CircleLog.Info(Area, Invariant($"the feed-forward's store written: {store.Entries.Count} tensors, {store.DataBytes / 1048576.0:F0} MB in {written.TotalMilliseconds:F0} ms; checked in {(Stopwatch.GetElapsedTime(started) - written).TotalMilliseconds:F0} ms: {wrong} of {store.Entries.Count} differ from the model"));
                if (wrong != 0)
                {
                    File.Delete(path);
                    CircleLog.Warn(Area, "the feed-forward's store was wrong and is gone; the feed-forward stays in RAM");
                    return;
                }
            }
            CircleLog.Info(Area, "the feed-forward's store is ready; from the next load the feed-forward is read from flash, its copy in RAM gone");
        }
        catch (OperationCanceledException)
        {
            // The generator is going.
        }
        catch (Exception ex)
        {
            CircleLog.Warn(Area, $"the feed-forward's store could not be made ({ex.GetType().Name}: {ex.Message}); the feed-forward stays in RAM");
        }
    }

    // Where the flash brings the feed-forward back faster than this, it streams in every turn; else a warm one stays this long.
    private static readonly TimeSpan FfnStreamEveryTurnBelow = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan FfnKeptWarm = TimeSpan.FromMinutes(3);

    // THE FEED-FORWARD IS ARMED BEFORE A TOKEN RUNS, streamed in from flash when it is cold - or, for a model that reads
    // every layer from flash, the layers' stream is opened. Under the gate.
    private void EnsureFfnWarm()
    {
        if (_model.MixFromStore)
        {
            EnsureLayers();
            return;
        }
        if (!_model.FfnFromStore || _ffnWarm is not null) return;
        var store = _ffnStore ?? throw new InvalidOperationException("The feed-forward is read from its store, and this load found none.");
        var warm = store.Load(Volatile.Read(ref _channels), Volatile.Read(ref _readBytes));
        _model.ArmFfn(warm);
        _ffnWarm = warm;
        CircleLog.Info(Area, Invariant($"feed-forward: {warm.Bytes / 1048576.0:F0} MB streamed in from flash in {warm.Elapsed.TotalMilliseconds:F0} ms on {warm.Channels} channel(s){(warm.Direct ? ", past the cache" : ", dropped from the cache")} - it was cold"));
    }

    // THE LAYERS' STREAM FOR A MODEL THAT READS EVERY LAYER FROM FLASH, opened when a turn needs it: every layer warm where
    // they all fit beside what the phone keeps for itself; else as many as fit beside a ring of two slots, the rest read a
    // layer ahead of the one computing. Under the gate.
    private void EnsureLayers()
    {
        if (_stream is null) OpenLayers();
    }

    private void OpenLayers()
    {
        var mix = _mixStore ?? throw new InvalidOperationException("Every layer is read from flash, and this load found no mixing store.");
        TileStore? ffn = _ffnStore ?? throw new InvalidOperationException("Every layer is read from flash, and this load found no feed-forward store.");

        // AN INDEX SERVES THE KNOWLEDGE LAYERS - STEP 4 REACHING STEP 2, AND ONLY STEP 2 REACHING STEP 1 (the owner,
        // 2026-11-15). With every thought's piece in step 2, a word's gate, up and down come as its pieces and the stream
        // brings the mixing alone - unless the layers' own ffn_down is the fewer bytes (LayersTakeTheirOwnDown), when the
        // stream brings it whole and the pieces give their gate and up alone. With only some pieces - a phone short of
        // flash - the stream still brings ffn_down whole, and a word whose thought has no piece has its gate and up
        // gathered from the model's own file. Step 2's layers come tiled from its own stores, so no pass repacks the
        // model's file; where step 2 had no room for them, they are read from step 1 in place.
        if (_indexCore is { } indexed)
        {
            mix = _indexMix ?? mix;
            ffn = _indexCoreStore is { } pieces && pieces.HoldsAll(indexed) && pieces.HoldsDown && !TakesItsOwnDown(indexed)
                ? null
                : _indexDown ?? (_ffnDownStore ??= TileStore.OverModel(_file, TileSet.FeedForward, SplitByThoughts.DownKinds));
        }
        var (warm, slots, available) = LayerRecipe(mix, ffn);
        var channels = Volatile.Read(ref _channels);
        var stream = new LayerStream(mix, ffn, channels, Volatile.Read(ref _readBytes), warm, slots);
        _model.Stream = stream;
        _stream = stream;
        CircleLog.Info(Area, Invariant($"layers: {stream.Describe()}{(stream.BringsFeedForward ? "" : ", the mixing alone")}, read from flash on {channels} channel(s){(stream.Direct ? " past the cache" : ", dropped from the cache")}; {(available is { } a ? $"{a / 1048576.0:F0} MB was available" : "the RAM available is not known")}"));
        if (_indexCore is not { } core) return;
        try
        {
            ServeIndex(core, available, stream.Bytes, withDown: !stream.BringsFeedForward);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // THE INDEX'S FILES ARE NOT THERE - a cache folder cleared under the engine. The turn is not failed for it: the
            // index is put down, the stream opened again with the whole feed-forward, and every neuron is computed. The
            // next load finds the queue's files gone and builds them again.
            CircleLog.Warn(Area, $"index: it cannot serve ({ex.GetType().Name}: {ex.Message}); every neuron is computed until the next load");
            StopServingIndex();
            _model.Stream = null;
            _stream = null;
            stream.Dispose();
            _indexCore = null;
            _indexCoreStore = null;
            _indexMix = null;
            _indexDown = null;
            _indexUse = null;
            OpenLayers();
        }
    }

    // THE INDEX SERVING THE TURNS (the indexed mind): step 4 read into RAM - whole thoughts, the ones the person's days
    // used most first, as many as the RAM left beside the layers' stream allows over what the phone keeps for itself, with
    // the slots step 2 reads ahead into - and the model's knowledge layers taking a word's pieces from it. Under the gate.
    private void ServeIndex(ThoughtIndex core, long? available, long streamBytes, bool withDown = true)
    {
        // RAM THAT IS REALLY SPARE (IndexingWhen.WarmRoom): what is available over the reserve and the stream, and on a
        // phone that swaps no more than half the swap free above what is kept free.
        var phone = MemoryNow.Phone();
        var budget = IndexingWhen.WarmRoom(available, phone.SwapFree, phone.SwapTotal, IndexReserve, streamBytes);

        // STEP 2'S READ-AHEAD FROM THE SAME ROOM: two slots at least - one the layer computes from, one coming in - and up
        // to four, from half of it; step 4 takes the rest.
        var held = IndexedFeedForward.HeldBytes(core, _indexCoreStore, withDown);
        var ahead = core.Coherent ? (int)Math.Clamp(budget / 2 / Math.Max(1, held), 0, IndexedFeedForward.MostAhead) : 0;
        if (ahead < 2) ahead = 0;
        var serving = new IndexedFeedForward(core, _indexCoreStore, _file, budget - ahead * held, Volatile.Read(ref _channels), Volatile.Read(ref _readBytes), withDown, ahead);
        _indexServing = serving;
        _model.Index = serving;
        var reading = serving.AheadSlots > 0
            ? Invariant($"step 2 reads a pass's pieces ahead into {serving.AheadSlots} slots ({serving.AheadBytesHeld / 1048576.0:F0} MB) once its words' thoughts are found at the first layer")
            : "step 2 reads a piece when a layer asks for it";
        CircleLog.Info(Area, Invariant($"index: {Thoughts(core)}, lists of {core.ListNeurons} of {core.FeedForward} neurons; step 4 holds {serving.WarmPieces} pieces of whole thoughts in RAM ({serving.WarmBytes / 1048576.0:F0} MB), read in {serving.WarmElapsed.TotalMilliseconds:F0} ms; {reading}; {_indexCoreStore?.Count ?? 0} pieces in step 2, any other gathered by step 2 from step 1, the model's own file{(withDown ? string.Empty : "; each piece's gate and up alone, the layers bringing their own ffn_down")}; the layers' attention {(_indexMix is not null ? "tiled in step 2" : "read from step 1 in place")}"));
    }

    // WHETHER AN INDEX'S LAYERS TAKE THEIR OWN FFN_DOWN, WHOLE, OVER ITS PIECES' ROUNDED COPY (CachedIndex.LayersTakeTheirOwnDown).
    internal static bool TakesItsOwnDown(ThoughtIndex core) => CachedIndex.LayersTakeTheirOwnDown(core);

    // The index stops serving: the model computes every neuron again, and RAM holds none of the warm index. What the turns
    // used of it goes to flash first, for the night.
    private void StopServingIndex()
    {
        if (_indexServing is { } serving && _indexUse is { } use) use.Add(serving.TakeUsage());
        _model.Index = null;
        _indexServing?.Dispose();
        _indexServing = null;
    }

    // Kept for the phone when the warm index fills the RAM the layers' stream leaves.
    private const long IndexReserve = 384L << 20;

    // THE DRAFTER, ARMED WHEN A STREAMED TRUNK IS ABOUT TO DECODE. By then the trunk's ring, state, head windows and buffers
    // are in RAM, so what the phone has left is what it has: asked when the layers opened, the P30 said 597 MB before the
    // ring and the state had filled, and the turn ran down to 3 MB of free swap. Built once and kept while the layers are;
    // its head - the surface of the scripts the trunk's head scores, from its own store - for the turn only. False when
    // this turn decodes plainly. Under the gate.
    private bool ArmDrafter(List<int> prompt)
    {
        if (_drafterPath is not { } path || _stream is not { Slots: > 0 } || _options.StreamedDrafts <= 0) return false;
        try
        {
            var scripts = ScriptsOf(prompt);
            var built = _drafting is null;
            if (built && !BuildDrafter(path, scripts)) return false;
            var store = _drafterHeadStore!;
            var groups = store.Surface(scripts);
            var head = SurfaceBytes(store, groups);
            if (!built && MemoryNow.AvailableBytes() is { } available && available < head + DrafterReserve)
            {
                CircleLog.Info(Area, Invariant($"no drafting this turn: {available / 1048576.0:F0} MB available, the drafter's head wants {head / 1048576.0:F0} MB beside the {DrafterReserve / 1048576.0:F0} MB kept for the phone; the drafter lets go"));
                LetDrafterGo();
                return false;
            }
            _drafterHead = store.Load(groups, Volatile.Read(ref _channels));
            _drafterModel!.Head = _drafterHead;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException
                                   or InvalidOperationException or OutOfMemoryException or KeyNotFoundException or UnauthorizedAccessException)
        {
            CircleLog.Warn(Area, $"no drafting: the drafter would not build ({ex.GetType().Name}: {ex.Message})");
            LetDrafterGo();
            _drafterPath = null;
            return false;
        }
    }

    // THE DRAFTER BY THE DEVICE'S RECIPE: its layers from its own stores, as many warm as the RAM left beside the trunk's
    // turn allows once its head's surface, its state and a ring of two slots are counted, and what the phone keeps for itself;
    // the rest read from flash for every token it runs - the 2B's 345 MB of layers, about a second a drafted token on the
    // P30 with none warm. Without room for that much this turn decodes plainly, and the next asks again.
    private bool BuildDrafter(string path, IReadOnlySet<Script> scripts)
    {
        _drafterFile ??= GgufFile.Open(path);
        SplitBeforeLoad(path, _drafterFile, _options);
        var ffn = ExistingFfnStore(path, _drafterFile, _options);
        var mix = ffn is null ? null : ExistingMixStore(path, _drafterFile, _options);
        var headStore = StoreDirectoryOf(_options) is { } directory ? HeadStore.Open(_drafterFile, StorePath(directory, path), HeadLayout.Paired) : null;
        if (ffn is null || mix is null || headStore is null)
        {
            CircleLog.Info(Area, $"no drafting: {Path.GetFileName(path)} has no {(headStore is null ? "head" : "layer")} store here");
            LetDrafterGo();
            _drafterPath = null;
            return false;
        }

        var c = ModelConfig.FromGguf(_drafterFile);
        var head = SurfaceBytes(headStore, headStore.Surface(scripts));
        var state = DrafterStateBytes(c);
        var layers = mix.DataBytes + ffn.DataBytes;
        var slot = Enumerable.Range(0, mix.Layers).Max(il => mix.LayerRange(il).Length + ffn.LayerRange(il).Length);
        var available = MemoryNow.AvailableBytes();
        var spare = available is { } a ? a - DrafterReserve - head - state : long.MaxValue;
        long warm;
        int slots;
        if (spare >= layers)
        {
            (warm, slots) = (long.MaxValue, 0);
        }
        else if (spare >= 2 * slot)
        {
            (warm, slots) = (spare - 2 * slot, 2);
        }
        else
        {
            CircleLog.Info(Area, Invariant($"no drafting this turn: {available / 1048576.0:F0} MB available, the drafter wants {(DrafterReserve + head + state + 2 * slot) / 1048576.0:F0} MB at the least - its head {head / 1048576.0:F0}, its state {state / 1048576.0:F0}, two slots of {slot / 1048576.0:F0}, and {DrafterReserve / 1048576.0:F0} kept for the phone"));
            return false;
        }

        var started = Stopwatch.GetTimestamp();
        var model = LanguageModel.Load(_drafterFile, _pool, headFromStore: true, ffnFromStore: true, mixFromStore: true);
        var stream = new LayerStream(mix, ffn, Volatile.Read(ref _channels), Volatile.Read(ref _readBytes), warm, slots);
        model.Stream = stream;
        _drafterModel = model;
        _drafterStream = stream;
        _drafterHeadStore = headStore;
        _drafter = new ModelDrafter(model, Math.Min(_state.Capacity, DrafterCapacity), _options.KvMode, _options.KeyForm);
        _drafting = new Speculator(_model, _drafter, _options.StreamedDrafts);
        CircleLog.Info(Area, Invariant($"drafting: {Path.GetFileName(path)} drafts {_options.StreamedDrafts} ahead for the streamed trunk, {stream.Describe()}, its head a surface of {head / 1048576.0:F0} MB, its state {state / 1048576.0:F0} MB; {(available is { } room ? $"{room / 1048576.0:F0} MB was available" : "the RAM available is not known")}; built in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms"));
        return true;
    }

    // What a surface of a head's store holds in RAM once it is read in.
    private static long SurfaceBytes(HeadStore store, IReadOnlyList<HeadStore.Group> groups) =>
        groups.SelectMany(g => Enumerable.Range(g.FirstChunk, g.Chunks)).Sum(c => (long)store.Chunks[c].Bytes);

    // WHETHER A STREAMED HEAD IS HELD IN RAM FOR THE TURN instead of re-streamed from flash on every pass. A plain turn
    // reads the head once a token; an MTP head drafting reads it on every draft step and verify, so a streamed head is
    // gigabytes of flash a turn (5.4 GB measured for 24 tokens at 2 drafts on the P30). Holding its surface pays only
    // when a drafter is running AND it fits beside what the phone is owed; without a drafter, without room, or when
    // memory cannot be read, it streams as before. The served rows are identical either way (HeadStoreTests), so no
    // output test guards this decision - the policy lives here, named and testable, so it cannot silently revert to
    // per-step flash reads while every reply still matches to the bit.
    internal static bool HoldStreamedHeadInRam(bool drafting, long? availableBytes, long reserveBytes, long surfaceBytes) =>
        drafting && availableBytes is { } available && available - reserveBytes >= surfaceBytes;

    // The drafter's recurrent layers three times over - its state, the checkpoint its drafts start from, its primed start.
    // Its keys and values are a few KB a position, and a conversation's are counted in the phone's share.
    private static long DrafterStateBytes(ModelConfig c)
    {
        long recurrent = 0;
        for (var il = 0; il < c.Layers; il++)
        {
            if (c.IsRecurrent(il)) recurrent += ((long)c.SsmHeads * c.SsmState * c.SsmState + (long)(c.ConvKernel - 1) * c.ConvChannels) * sizeof(float);
        }
        return 3 * recurrent;
    }

    // The drafter's head goes at the end of the turn: RAM holds none of its surface.
    private void LetDrafterHeadGo()
    {
        if (_drafterModel is { } model) model.Head = null;
        _drafterHead?.Dispose();
        _drafterHead = null;
    }

    // The drafter goes: its head, its stream, its model, its file. Under the gate.
    private void LetDrafterGo()
    {
        var held = _drafterModel is not null;
        LetDrafterHeadGo();
        _drafting = null;
        _drafter = null;
        if (_drafterModel is { } model) model.Stream = null;
        _drafterModel = null;
        _drafterHeadStore = null;
        _drafterStream?.Dispose();
        _drafterStream = null;
        _drafterFile?.Dispose();
        _drafterFile = null;
        if (held) GC.Collect(); // its state and buffers are managed arrays: RAM holds none of them once they are collected
    }

    // The most positions the drafter's state holds, and what is kept beside it for the phone - counted against what is
    // available once the trunk's turn holds all it holds.
    private const int DrafterCapacity = 8192;
    private const long DrafterReserve = 384L << 20;

    // Kept for the phone itself when every layer stays warm. Streaming, a turn holds more than its layers - the 27B's head
    // surface (206 MB of Latin rows), its DeltaNet state (151 MB, 48 recurrent layers), a prompt's buffers - and the P30
    // lost twenty processes to the low-memory killer the one time the 27B overran it.
    private const long AllWarmReserve = 128L << 20;

    // HOW MANY LAYERS STAY WARM: every one when they fit with room to spare (the 2B on the P30: 345 MB); else none, every
    // layer streamed through a ring of two slots (the 27B: 6.2 GB of layers a pass). Measured on the P30 on 2026-11-09:
    // three layers held warm beside the 27B's ring, head surface and state bought it 3/64 of a pass and cost 308 MB, and
    // took the phone to 3 MB of free swap - so a streaming turn holds no warm layer until a device measures that it pays.
    private static (long Warm, int Slots, long? Available) LayerRecipe(TileStore mix, TileStore? ffn)
    {
        var available = MemoryNow.AvailableBytes();
        var total = mix.DataBytes + (ffn?.DataBytes ?? 0);
        if (available is not { } room || total + AllWarmReserve <= room) return (long.MaxValue, 0, available);
        return (0, 2, available);
    }

    // Megabytes a second, of bytes that took the time given; none where no time was taken.
    private static double PerSecond(long bytes, TimeSpan time) => time.TotalSeconds > 0 ? bytes / 1048576.0 / time.TotalSeconds : 0;

    // What an index's groups are, said: coherent thoughts, each a word's from the first layer to the last, or each layer's own.
    private static string Thoughts(ThoughtIndex core) => core.Coherent
        ? Invariant($"{core.Groups} coherent thoughts, a word's one thought found at the first layer and kept through every layer")
        : Invariant($"{core.Groups} thought groups a layer");

    // What served the index's pieces, in the owner's steps: step 4 in RAM, step 2 read ahead and read when asked, step 1
    // through step 2, and the words that took their piece where the word before left it.
    internal static string Served(IndexCounts counts) => Invariant(
        $"{counts.Warm} from step 4 in RAM, {counts.Ahead} read ahead by step 2 (the layers waited {counts.Waited.TotalMilliseconds:F0} ms on it), {counts.Asked} read by step 2 when a layer asked{(counts.FromModel > 0 ? Invariant($" - {counts.FromModel} of them gathered from step 1, the model's own file, step 2 holding no piece for them") : string.Empty)}{(counts.Again > 0 ? Invariant($", {counts.Again} where the word before left them") : string.Empty)}; {counts.Bytes / 1048576.0:F0} MB read for them");

    // A call has used the feed-forward: the idle clock starts again, and where it streams every turn it goes now. Under the gate.
    private void UsedFfn()
    {
        if (_ffnWarm is null && _stream is null) return;
        Volatile.Write(ref _ffnUsed, Stopwatch.GetTimestamp());
        if (_stream is { Slots: > 0 } streaming)
        {
            // WHAT THE LOADS' TIME WENT TO, flash or the CPU: a layer read in place is read and then repacked into tiles.
            var (waited, read, reading) = streaming.TakeCounts();
            var (fromFlash, flashing, repacking) = streaming.TakeParts();
            var parts = repacking > TimeSpan.Zero
                ? Invariant($": {fromFlash / 1048576.0:F0} MB of the model's own file read in {flashing.TotalMilliseconds:F0} ms ({PerSecond(fromFlash, flashing):F0} MB/s), repacked into tiles in {repacking.TotalMilliseconds:F0} ms")
                : string.Empty;
            CircleLog.Info(Area, Invariant($"layers: {read / 1048576.0:F0} MB read from flash in {reading.TotalMilliseconds:F0} ms ({PerSecond(read, reading):F0} MB/s){parts}; the pass waited {waited.TotalMilliseconds:F0} ms for them"));

            // AND THE STREAM'S FIRST LOAD, ALONE - read before any pass computed beside it: what reading and repacking take
            // with the CPU to themselves, against what the pass's loads took beside the products.
            if (streaming.TakeFirstLoad() is { } first)
            {
                var alone = first.Repacking > TimeSpan.Zero
                    ? Invariant($": {first.FromFlash / 1048576.0:F0} MB of the model's own file read in {first.Reading.TotalMilliseconds:F0} ms ({PerSecond(first.FromFlash, first.Reading):F0} MB/s), repacked into tiles in {first.Repacking.TotalMilliseconds:F0} ms ({PerSecond(first.Bytes, first.Repacking):F0} MB/s)")
                    : string.Empty;
                CircleLog.Info(Area, Invariant($"layers: the first alone, before a pass computed beside it: {first.Bytes / 1048576.0:F0} MB of tiles in {first.Whole.TotalMilliseconds:F0} ms ({PerSecond(first.Bytes, first.Whole):F0} MB/s){alone}"));
            }
        }
        if (_indexServing is { } serving)
        {
            var counts = serving.TakeCounts();
            if (counts.Warm + counts.Ahead + counts.Asked + counts.Again > 0)
                CircleLog.Info(Area, Invariant($"index: pieces {Served(counts)}"));
            _indexUse?.Add(serving.TakeUsage());   // what the person's turns used, kept for the night's revision
        }
        if (Volatile.Read(ref _ffnKeepTicks) == 0) LetFfnGo("it streams in every turn here");
    }

    // The warm feed-forward goes: the layers let go of it, then its memory - or the layers' stream closes. Under the gate.
    private void LetFfnGo(string why)
    {
        if (_stream is { } stream)
        {
            var drafter = _drafterStream?.Bytes ?? 0;
            LetDrafterGo();
            StopServingIndex();
            _model.Stream = null;
            _stream = null;
            var bytes = stream.Bytes;
            stream.Dispose();
            CircleLog.Info(Area, Invariant($"layers let go ({why}): RAM holds none of their {bytes / 1048576.0:F0} MB{(drafter > 0 ? Invariant($", nor the drafter's {drafter / 1048576.0:F0} MB") : "")}"));
        }
        if (_ffnWarm is not { } warm) return;
        _model.ReleaseFfn();
        _ffnWarm = null;
        warm.Dispose();
        CircleLog.Info(Area, Invariant($"feed-forward let go ({why}): RAM holds none of its {warm.Bytes / 1048576.0:F0} MB"));
    }

    // The idle clock: an engine idle for the recipe's time lets its warm feed-forward go; a fidelity run asked for in the
    // control file is done; and the index is worked on - a unit of its queue, or the night's revision - when the phone is
    // idle (IndexingWhen). Never while a call runs.
    private void IdleCheck(object? state)
    {
        var request = StoreDirectoryOf(_options) is { } directory ? Path.Combine(directory, FfnFidelityFile) : null;
        var fidelity = request is not null && File.Exists(request);
        var indexRequest = StoreDirectoryOf(_options) is { } folder ? Path.Combine(folder, IndexFile) : null;
        var index = indexRequest is not null && File.Exists(indexRequest);
        var idle = (_ffnWarm is not null || _stream is not null) && Stopwatch.GetTimestamp() - Volatile.Read(ref _ffnUsed) >= Volatile.Read(ref _ffnKeepTicks);

        // THE INDEX'S WORK WAITS FOR AN IDLE PHONE: nobody asking and not for a little while, nothing saying the phone is in use, the phone
        // neither hot nor low - and the stores' own start-up work done, and the rest after the last unit over.
        var pace = IndexingPace.Wait;
        if (Volatile.Read(ref _indexing) is not null && _storeTask is not { IsCompleted: false } && Stopwatch.GetTimestamp() >= Volatile.Read(ref _indexNotBefore))
            pace = IndexingWhen.Pace(Volatile.Read(ref _wanted) > 0, Stopwatch.GetElapsedTime(Volatile.Read(ref _lastCall)), PowerNow(), InUseNow());
        if (pace != IndexingPace.Wait && ShortOfMemoryNow(out _)) pace = IndexingPace.Wait;   // no unit begins on a phone short of memory
        if (!fidelity && !idle && !index && pace == IndexingPace.Wait) return;
        if (!_gate.Wait(0)) return;
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (index)
            {
                // ASKED FOR BY NAME: the queue is run to its end now, with the screen on or off - a call, heat, a low
                // battery or the phone coming off its charger still stops it, and the idle clock carries the queue on
                // from there. "groups,percent" asks for that index; an empty file for the one in hand.
                var parts = File.ReadAllText(indexRequest!).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                File.Delete(indexRequest!);
                int? groups = parts.Length > 0 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) ? Math.Clamp(g, 1, 64) : null;
                int? percent = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? Math.Clamp(p, 1, 100) : null;
                // "groups,percent,coherent": a word's one thought for every layer (ThoughtIndex.Coherent); without the
                // third field, each layer groups its words on its own - what is named is what is built.
                var coherent = parts.Length > 2 && string.Equals(parts[2], "coherent", StringComparison.OrdinalIgnoreCase);
                var inHand = Volatile.Read(ref _indexing);
                var ff = _model.Config.FeedForward;
                var another = inHand is null
                    || (groups is { } wanted && wanted != inHand.Plan.Groups)
                    || (percent is { } share && ThoughtIndex.ListOf(Math.Max(Kernels.Ptq1PairMatrix.TileRows, ff * share / 100), ff) != ThoughtIndex.ListOf(inHand.Plan.ListNeurons, ff))
                    || coherent != inHand.Plan.Coherent;
                if (another)
                {
                    // A SHARE ASKED FOR BY NAME IS BUILT AS ASKED, every neuron too; with none named, the climb's rule
                    // stands - a brain that computes every neuron from its own file builds nothing.
                    DropIndex("another index was asked for");
                    OpenIndex(groups ?? inHand?.Plan.Groups ?? IndexGroups, percent, asked: percent is not null, coherent: coherent);
                }
                if (Volatile.Read(ref _indexing) is { Complete: false } named) RunIndexing(named, named: true);
                else CircleLog.Info(Area, Volatile.Read(ref _indexing) is null ? "index: asked for, and none is built for this model here" : "index: asked for, and it is complete already");
                return;
            }
            if (fidelity)
            {
                var percents = File.ReadAllText(request!).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 1, 99) : 0)
                    .Where(v => v > 0).Distinct().ToArray();
                File.Delete(request!);
                if (percents.Length > 0) RunFidelity(percents);
                return;
            }
            if (pace != IndexingPace.Wait && Volatile.Read(ref _indexing) is { } queue)
            {
                if (!queue.Complete)
                {
                    RunIndexing(queue, named: false);
                    return;
                }
                if (_indexUse is { } use && IndexingWhen.ReviseDue(use.Since, use.LastRevised, DateTimeOffset.UtcNow, pace))
                {
                    ReviseIndex(queue, use);
                    return;
                }
            }
            var since = Stopwatch.GetTimestamp() - Volatile.Read(ref _ffnUsed);
            if (since >= Volatile.Read(ref _ffnKeepTicks)) LetFfnGo(Invariant($"idle {since / (double)Stopwatch.Frequency:F0} s"));
        }
        catch (Exception ex)
        {
            // A timer's callback must never throw: that would end the process.
            CircleLog.Warn(Area, $"the idle clock's work failed ({ex.GetType().Name}: {ex.Message})");
        }
        finally
        {
            _gate.Release();
        }
    }

    // The turn is written: what the state holds is the conversation now, and this is where it goes on from.
    private void Keep(GenerationOptions options, IReadOnlyList<ChatMessage> messages, PromptIds built)
    {
        if (options.Conversation is null || built.Slots is not null) return;
        if (_stream is { Slots: > 0 })
        {
            // A streamed trunk keeps no copy of the conversation (see the prompt's read): the next turn reads it again in
            // the pass its question needs.
            _talk = null;
            return;
        }
        _talk = options.Conversation;
        _talkAsked = options.RetrievalQuery ?? messages.LastOrDefault(m => IsPerson(m))?.Content;
        _talkTokens.Clear();
        _talkTokens.AddRange(_history);
        _talkTools.Clear();
        foreach (var tool in options.Tools ?? []) _talkTools.Add(tool.Name);
        _state.MarkTalk();
    }

    private static bool IsPerson(ChatMessage message) => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase);

    // The prompt of a turn going on the conversation the brain is keeping: what it holds, the last turn's
    // end, and what has been said since as the template writes it. Null when the turn is to be read from
    // its messages: a conversation it is not keeping, a state that has since gone to somebody else, no room
    // left, or a tool its system turn does not offer: the tools are written there, and that was read long ago.
    private List<int>? GoingOn(string? conversation, IReadOnlyList<ChatMessage> messages, PromptIds built,
        IReadOnlyList<CircleAI.Tools.ToolDefinition>? tools, bool thinking, ReasoningEffort? effort, int room)
    {
        if (conversation is null || conversation != _talk || built.Slots is not null || _turnEnd.Length == 0) return null;
        if (_state.TalkPosition != _talkTokens.Count) return null;
        if (tools is not null && !tools.All(t => _talkTools.Contains(t.Name))) return null;

        // WHAT WAS SAID SINCE THE BRAIN LAST SPOKE, WHICH IS MORE THAN THE QUESTION WHEN A TABLE ANSWERED ONE
        // IN BETWEEN: "What's the capital of Australia?" never reaches it, and "How many people live there?"
        // has to. The messages run on from the last question it saw; everything after that exchange is new.
        var fresh = new List<ChatMessage>(messages.Where(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase)));
        var first = fresh.Count;
        var seen = -1;
        for (var i = messages.Count - 2; i >= 0 && seen < 0; i--)
            if (IsPerson(messages[i]) && string.Equals(messages[i].Content, _talkAsked, StringComparison.Ordinal)) seen = i;
        var from = seen >= 0 ? seen + 2 : messages.Count - 1;
        for (var i = Math.Max(from, 0); i < messages.Count; i++)
            if (!string.Equals(messages[i].Role, "system", StringComparison.OrdinalIgnoreCase)) fresh.Add(messages[i]);
        if (fresh.Count == first) return null;

        var whole = BuildPrompt(fresh, tools, thinking, effort).Ids;

        // After the system turn, where the first of them begins.
        var start = whole.IndexOf(_turnStart);
        if (start >= 0 && first > 0) start = whole.IndexOf(_turnStart, start + 1);
        if (start < 0) return null;

        var count = _talkTokens.Count + _turnEnd.Length + whole.Count - start;
        if (count + room >= _state.Capacity) return null;

        var prompt = new List<int>(count);
        prompt.AddRange(_talkTokens);
        prompt.AddRange(_turnEnd);
        prompt.AddRange(whole.Skip(start));
        return prompt;
    }

    // How many tokens from i can be read together: to the end, or the next place the state is kept.
    // The most prompt tokens a streamed model reads in one pass: a pass reads every layer from flash, and each token's
    // buffers for the 27B are about 400 KB.
    private const int StreamedReadTogether = 128;

    private static int Together(int i, int readTo, int primed, int lastTurn, int most)
    {
        var end = Math.Min(readTo, i + most);
        if (i < primed) end = Math.Min(end, primed);
        if (i < lastTurn) end = Math.Min(end, lastTurn);
        return end - i;
    }

    // Where the last thing said ends: before its turn's end and the line after it.
    private int EndOfWhatWasSaid(List<int> prompt, int lastTurn)
    {
        var end = lastTurn < 0 ? prompt.Count : lastTurn;
        while (end > 0 && (_endOfGeneration.Contains(prompt[end - 1]) || prompt[end - 1] == _userTurn[^1])) end--;
        return end;
    }

    // Brings the state to the longest start of the prompt it can reuse, and says which it did:
    // all it holds; the mark where the last turn began; the primed start where the system turn
    // ended, held in memory or kept on disk; or nothing.
    private string Resume(List<int> prompt, int primed)
    {
        Debug.Assert(_history.Count == _state.Position);
        var (path, position) = Reuse(prompt, primed);
        switch (path)
        {
            case "continued":
                return path;
            case "prefix-hit":
                _state.Rewind();
                _history.RemoveRange(position, _history.Count - position);
                return path;
            case "talk":
                _state.RewindToTalk();
                _history.RemoveRange(position, _history.Count - position);
                return path;
            case "primed":
                _state.RewindToPrimed();
                _history.RemoveRange(position, _history.Count - position);
                return path;
            case "primed-disk" when LoadPrimed(prompt, primed):
                return path;
        }

        _state.Reset();
        _history.Clear();
        return "reset";
    }

    // The longest start of the prompt the state can go on from and how, changing nothing: what
    // Resume does, and what Unread counts from.
    private (string Path, int Position) Reuse(List<int> prompt, int primed)
    {
        var held = _history.Count;
        if (held > 0 && held < prompt.Count && Holds(prompt, held)) return ("continued", held);

        // THE LATER OF THE TWO: a mark can be left over from before the conversation's last turn ended.
        var marked = _state.MarkedPosition;
        var talk = _state.TalkPosition;
        if (talk > marked && talk < prompt.Count && Holds(prompt, talk)) return ("talk", talk);
        if (marked > 0 && marked < prompt.Count && Holds(prompt, marked)) return ("prefix-hit", marked);
        if (talk > 0 && talk < prompt.Count && Holds(prompt, talk)) return ("talk", talk);

        if (primed > 0)
        {
            if (_state.PrimedPosition == primed && _history.Count >= primed && Holds(prompt, primed)) return ("primed", primed);
            if (_primedDirectory is not null && File.Exists(PrimedPath(prompt, primed))) return ("primed-disk", primed);
        }
        return ("reset", 0);
    }

    // Where the system turn ends - the start of the turn after it - when the conversation opens
    // with one; 0 when it does not. The service keeps the system turn to the persona alone, so it
    // is the same tokens every time.
    private int PrimedLength(IReadOnlyList<ChatMessage> messages, List<int> prompt)
    {
        if (_turnStart < 0 || !messages.Any(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(m.Content)))
            return 0;
        var first = prompt.IndexOf(_turnStart);
        var next = first < 0 ? -1 : prompt.IndexOf(_turnStart, first + 1);
        if (next < 0) return 0;

        // AND THE NEXT TURN'S OWN OPENING, which is the same tokens every time: whoever speaks after
        // the system turn is the person. Three tokens are a third of a second on a P30, and a
        // spoken question has nothing to spare.
        var opening = next + 1 + _userTurn.Length;
        return prompt.Count > opening && prompt.Skip(next + 1).Take(_userTurn.Length).SequenceEqual(_userTurn) ? opening : next;
    }

    // The system turn is in: this point is kept on disk for the next process, and remembered in RAM - unless the trunk
    // streams its layers, whose copy would be 157 MB of the 27B: it reads the start back from flash instead.
    private void Prime(List<int> prompt, int length, bool inRam)
    {
        if (inRam) _state.MarkPrimed();
        KeepPrimed(prompt, length, _state);
    }

    // Whether this turn's trunk streams its layers through a ring: its state keeps no copy of itself (see Run).
    private bool Lean => _stream is { Slots: > 0 };

    // Whether the trunk reads its layers from flash through a ring: the open stream's, or the one a turn would open now.
    private bool Streams() => _stream is { } open
        ? open.Slots > 0
        : _model.MixFromStore && _mixStore is { } mix && _ffnStore is { } ffn && LayerRecipe(mix, ffn).Slots > 0;

    // A system turn's state, kept on disk under its own name unless one is there already.
    private void KeepPrimed(List<int> prompt, int length, ModelState state)
    {
        if (_primedDirectory is null) return;
        try
        {
            var file = PrimedPath(prompt, length);
            if (File.Exists(file)) return;
            WritePrimed(file, prompt, length, state);
            CircleLog.Info(Area, Invariant($"primed start kept: a {length}-token system turn, {new FileInfo(file).Length / 1024} KB"));
            Prune();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            CircleLog.Warn(Area, "a primed start could not be kept: " + e.Message);
        }
    }

    // The first tokens of a prompt and the state after them, written aside and moved into place, so
    // a reader never finds half of one.
    private void WritePrimed(string file, List<int> prompt, int length, ModelState state)
    {
        Directory.CreateDirectory(_primedDirectory!);
        var writing = file + ".writing";
        using (var stream = new FileStream(writing, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            writer.Write(PrimedMagic);
            writer.Write(EngineNumerics);
            writer.Write(_modelIdentity);
            writer.Write(length);
            for (var i = 0; i < length; i++) writer.Write(prompt[i]);
            state.Write(writer, _model.Config);
            writer.Flush();
            FlashFile.Settle(stream, 0, stream.Length); // on flash, and only there: the 27B's start is 151 MB of pages
        }
        File.Move(writing, file, overwrite: true);
    }

    // The state WritePrimed kept, when it was kept for this model, engine and working memory and its
    // tokens, at most `most` of them, are how this prompt starts; null when it is anything else. Read
    // into `into` when it is given and holds the same capacity and working memory.
    private ModelState? ReadPrimed(string file, List<int> prompt, int most, ModelState? into = null)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        if (reader.ReadUInt64() != PrimedMagic || reader.ReadInt32() != EngineNumerics || reader.ReadString() != _modelIdentity)
            return null;
        var count = reader.ReadInt32();
        if (count <= 0 || count > most) return null;
        for (var i = 0; i < count; i++)
        {
            if (reader.ReadInt32() != prompt[i]) return null;
        }
        var state = ModelState.Read(reader, _model.Config, into);
        return state.Position == count && state.Capacity == _state.Capacity && state.KvMode == _state.KvMode && state.KeyForm == _state.KeyForm ? state : null;
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
            // A STREAMED TRUNK READS ITS START INTO THE STATE IT HAS: a second state while the first is let go would be 151 MB
            // more of the 27B for a moment, and the start is not kept in RAM after - the next is read from flash again.
            var lean = Lean;
            if (ReadPrimed(file, prompt, length, lean ? _state : null) is not { } state || state.Position != length) return false;

            _state = state;
            _history.Clear();
            for (var i = 0; i < length; i++) _history.Add(prompt[i]);
            _speculator?.Forget();
            if (!lean) _state.MarkPrimed();
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow); // in use: the last to be pruned
            if (lean) DropCached(file);
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            CircleLog.Warn(Area, "a primed start could not be read and is primed again: " + e.Message);
            try { File.Delete(file); } catch (IOException) { }
            return false;
        }
    }

    // A file read once into the state leaves the OS's cache: RAM is not where it is kept.
    private static void DropCached(string file)
    {
        try
        {
            using var read = FlashFile.Open(file, direct: false);
            read.Drop(0, read.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            // The cache lets it go in its own time.
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

    // A few primed starts are kept; the least recently used go first. Reads left unfinished are counted
    // apart: on the P30 on 2026-11-05 the one about to finish took a place, the start of the shape the
    // brain had just left went, and growing back to that shape read its 606 tokens all over again.
    private void Prune()
    {
        var folder = new DirectoryInfo(_primedDirectory!);
        foreach (var kind in new[] { "*.primed", "*.priming" })
        {
            foreach (var file in folder.GetFiles(kind).OrderByDescending(f => f.LastWriteTimeUtc).Skip(PrimedStartsKept))
            {
                try { file.Delete(); } catch (IOException) { }
            }
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
    // A TRUNK THAT STREAMS ITS LAYERS NEVER THINKS BEFORE IT ANSWERS: every thought token is a pass over all of them -
    // about 30 s for the 27B on the P30 - so a thought of a thousand tokens is hours of silence before the first word.
    // On the P30 on 2026-11-09 the effort router judged a pirate question hard ("medium, up to 1024 thinking tokens"),
    // the 27B spent its four tokens and 153 s thinking, and no word came within the deadline.
    private bool ThinkingFor(GenerationOptions options)
    {
        var asked = options.Effort is { } effort ? effort != ReasoningEffort.Off && _thinkStart >= 0 : _options.Thinking;
        if (!asked || !Streams()) return asked;
        CircleLog.Info(Area, $"no thinking this turn ({options.Effort?.TemplateName() ?? "the generator's default"} was asked): this trunk streams its layers, and every thought token is a pass over all of them");
        return false;
    }

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
