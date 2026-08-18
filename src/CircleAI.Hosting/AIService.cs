// AIService.cs
//
// Default IAIService implementation. Holds a single QwenTextGenerator for
// the lifetime of the host process so the model isn't reloaded per call.
//
// Threading model:
//   - StartAsync is idempotent and serialised by SemaphoreSlim.
//   - ChatAsync / StreamAsync are safe to call concurrently.
//   - DisposeAsync cancels in-flight stream calls via _shutdownCts.
//
// v2.0 additions:
//   - EnrichSystemPromptAsync — injects device context, RAG snippets, persona
//   - AgenticChatAsync       — loops on tool calls until plain-text response
//   - SubmitFeedbackAsync    — records user feedback signals
//   - Episodic store writes  — exchanges are stored after every ChatAsync

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;
using CircleAI.Inference;
using CircleAI.Memory;
using CircleAI.Skills;
using CircleAI.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Hosting;

/// <summary>
/// Long-lived butler service. Loads a Qwen GGUF model once and serves all
/// downstream callers from that single handle.
/// </summary>
public sealed class AIService : IAIService
{
    private readonly AIOptions _options;
    private readonly IModelLoader? _modelLoader;
    private readonly Func<string, IChatGenerator>? _generatorFactory;
    private readonly IModelSelector? _modelSelector;
    private readonly CircleAI.Core.Models.ModelRegistryService? _modelRegistry;

    /// <summary>
    /// Whether a non-chat modality can be served on this device, and how —
    /// a model, a built-in heuristic, or not at all.
    /// </summary>
    /// <remarks>
    /// Hosts should call this BEFORE offering a capability, so an unavailable
    /// one is declined with a reason instead of failing somewhere inside a
    /// runtime that was never given a model. Every model question routes through
    /// the selector; nothing here decides for itself what is installed.
    /// <para>
    /// Returns <see cref="SelectionQuality.Unavailable"/> when no registry was
    /// supplied — without a catalogue there is nothing to select from, and
    /// claiming otherwise would be a guess.
    /// </para>
    /// </remarks>
    /// <param name="modality">The capability being considered.</param>
    /// <param name="probe">Device snapshot; taken fresh when omitted.</param>
    public ModalityPlan PlanFor(ModelModality modality, DeviceProbe? probe = null)
    {
        if (_modelRegistry is null)
            return new ModalityPlan(SelectionQuality.Unavailable, null,
                "no model registry is wired, so no model of any modality can be selected");

        var device = probe
            ?? (_options.DeviceContext is DefaultDeviceContext d ? d.BuildProbe() : DeviceProbe.Snapshot());

        return new SpeechModelSelector(_modelRegistry).PlanFor(device, modality);
    }
    private readonly ILogger<AIService> _logger;

    // Resolved at StartAsync time so the rest of the lifecycle (download,
    // generator factory, warmup) sees the same model the observer was told
    // about. Cached so IsReady / ChatAsync don't re-select after a restart.
    private string? _resolvedModelId;
    private DeviceTier _resolvedDeviceTier = DeviceTier.Desktop;
    private bool _autoSelected;

    private readonly SemaphoreSlim _startGate = new(1, 1);
    private CancellationTokenSource _shutdownCts = new();

    private IChatGenerator? _generator;
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Whether any turn has asked for tools yet. Once true it stays true.
    /// </summary>
    /// <remarks>
    /// See the note where this is set, in PrepareMessagesAsync. It exists so the
    /// system message stops changing between turns: the tool block is decided per
    /// question but LIVES in the per-conversation prefix, and every flip cost a
    /// full re-prefill of the whole conversation — 36 seconds, measured.
    /// </remarks>
    private bool _toolsLatched;

    /// <summary>
    /// Which tools have entered this conversation's prefix. Grows, never shrinks.
    /// </summary>
    /// <remarks>
    /// Names rather than definitions, because the bridge owns the definitions
    /// and may hand back fresh instances; the name is what identifies a
    /// capability across turns.
    /// </remarks>
    private readonly HashSet<string> _latchedTools = new(StringComparer.Ordinal);

    // Neuron — two-slot residency (opt-in via AIOptions.Router). _generator above
    // is the always-warm generalist floor; _slots owns one evictable specialist.
    private Neuron.ResidentSlotManager? _slots;
    private long _generalistReservedBytes;

    // RT-04 — brownout plumbing
    private readonly IMemoryPressureSource? _pressureSource;
    private IDisposable? _pressureSub;

    // v2.0 — lazy runtime state
    private PersonaState? _personaCache;
    private RagContextBuilder? _ragBuilder;
    private SkillContextBuilder? _skillContextBuilder;

    // Tool call detection tags (Qwen3 native format).
    private const string ToolCallOpen  = "<tool_call>";
    private const string ToolCallClose = "</tool_call>";

    /// <summary>
    /// Constructs the service. Either <paramref name="modelLoader"/> or
    /// <paramref name="generatorFactory"/> must be able to resolve a model.
    /// </summary>
    public AIService(
        AIOptions options,
        IModelLoader? modelLoader = null,
        Func<string, IChatGenerator>? generatorFactory = null,
        ILogger<AIService>? logger = null)
        : this(options, modelLoader, generatorFactory, modelSelector: null, logger) { }

    /// <summary>
    /// Constructs the service with an <see cref="IModelSelector"/> so the SDK
    /// can auto-resolve <see cref="AIOptions.ModelId"/> via <c>BestFit</c>
    /// when the consumer leaves it null. <c>ServiceCollectionExtensions</c>
    /// wires this overload by default.
    /// </summary>
    public AIService(
        AIOptions options,
        IModelLoader? modelLoader,
        Func<string, IChatGenerator>? generatorFactory,
        IModelSelector? modelSelector,
        ILogger<AIService>? logger = null)
        : this(options, modelLoader, generatorFactory, modelSelector,
               modelRegistry: null, logger) { }

    /// <summary>
    /// Constructs the service with an <see cref="IModelSelector"/> AND a
    /// <see cref="CircleAI.Core.Models.ModelRegistryService"/>, enabling
    /// upgrade detection via <see cref="CheckForUpgradesAsync"/>.
    /// <c>ServiceCollectionExtensions</c> wires this overload by default.
    /// </summary>
    public AIService(
        AIOptions                                       options,
        IModelLoader?                                   modelLoader,
        Func<string, IChatGenerator>?                   generatorFactory,
        IModelSelector?                                 modelSelector,
        CircleAI.Core.Models.ModelRegistryService?      modelRegistry,
        ILogger<AIService>?                             logger = null)
        : this(options, modelLoader, generatorFactory, modelSelector,
               modelRegistry, memoryPressureSource: null, logger) { }

    /// <summary>
    /// (RT-04) Construct with a memory-pressure source. When the source
    /// raises <see cref="MemoryPressureLevel.Critical"/> the service hot-swaps
    /// the running generator to the next entry in the fallback chain.
    /// </summary>
    public AIService(
        AIOptions                                       options,
        IModelLoader?                                   modelLoader,
        Func<string, IChatGenerator>?                   generatorFactory,
        IModelSelector?                                 modelSelector,
        CircleAI.Core.Models.ModelRegistryService?      modelRegistry,
        IMemoryPressureSource?                          memoryPressureSource,
        ILogger<AIService>?                             logger = null)
    {
        _options          = options ?? throw new ArgumentNullException(nameof(options));
        _modelLoader      = modelLoader;
        _generatorFactory = generatorFactory;
        _modelSelector    = modelSelector;
        _modelRegistry    = modelRegistry;
        _pressureSource   = memoryPressureSource;
        _logger           = logger ?? NullLogger<AIService>.Instance;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CircleAI.Core.Models.UpgradeInfo>> CheckForUpgradesAsync(
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_modelRegistry is null) return Array.Empty<CircleAI.Core.Models.UpgradeInfo>();

        // Resolved, not ModelStorageDirectory directly. This method used to read
        // ONLY that property while the loader defaulted to ModelStorageDir, so a
        // host that set the other one had upgrade detection silently scan the
        // wrong directory — or, when it was null, return "no upgrades" forever
        // while models sat happily downloaded somewhere else.
        var storageDir = _options.ResolvedModelStorageDirectory;

        // Nothing downloaded yet is not an error, and the registry should not be
        // asked to walk a directory that does not exist.
        if (string.IsNullOrWhiteSpace(storageDir) || !System.IO.Directory.Exists(storageDir))
            return Array.Empty<CircleAI.Core.Models.UpgradeInfo>();

        try
        {
            return await _modelRegistry
                .CheckForUpgradesAsync(storageDir, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Butler upgrade check failed; treating as no upgrades.");
            return Array.Empty<CircleAI.Core.Models.UpgradeInfo>();
        }
    }

    /// <inheritdoc />
    public bool IsReady => _started && _generator is not null && !_disposed;

    /// <summary>
    /// The model id the generalist floor resolved at <see cref="StartAsync"/> —
    /// surfaced by <see cref="Neuron.NeuronNode.EngineLabel"/>. <c>null</c> until
    /// the service has started (or when a raw <see cref="AIOptions.ModelPath"/>
    /// with no id was pinned).
    /// </summary>
    public string? ResolvedModelId => _resolvedModelId;

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_started) return;

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_started) return;

            // Apply host-injected native library directory (e.g. Android nativeLibraryDir)
            // before any P/Invoke triggers the DLL resolver callback.
            if (!string.IsNullOrWhiteSpace(_options.NativeLibDir))
                NativeLibraryResolver.OverrideDirectory = _options.NativeLibDir;
            NativeLibraryResolver.EnsureRegistered();

            var modelPath = await ResolveModelPathAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Butler loading model from {ModelPath}", modelPath);

            // Device-tier defaults: when ContextSize is left null, derive it
            // from the resolved device tier (set in ResolveModelPathAsync
            // when the selector ran; otherwise Desktop default).
            var contextSize = _options.ContextSize
                ?? DeviceTierDefaults.ContextWindow(_resolvedDeviceTier);

            var generator = _generatorFactory is not null
                ? _generatorFactory(modelPath)
                : new QwenTextGenerator(
                    modelPath,
                    contextSize: (uint)Math.Max(1, contextSize),
                    threads: _options.ThreadCount);

            if (generator is null)
                throw new InvalidOperationException("Generator factory returned null.");

            _generator = generator;
            _generalistReservedBytes = EstimateModelBytes(modelPath);
            if (_options.Router is not null)
                _slots ??= new Neuron.ResidentSlotManager(_generalistReservedBytes, ProbeDevice);

            if (_options.WarmOnStart)
            {
                try { await WarmUpAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Butler warm-up failed; continuing anyway.");
                }
            }

            _started = true;
            _logger.LogInformation("Butler service ready.");

            // RT-04 — subscribe to platform pressure source. Critical → brownout.
            if (_pressureSource is not null && _pressureSub is null)
            {
                _pressureSub = _pressureSource.Subscribe(async (_, next) =>
                {
                    if (next == MemoryPressureLevel.Critical)
                    {
                        // Evict the specialist first — the generalist is the floor.
                        if (_slots is not null)
                            await _slots.EvictSpecialistAsync().ConfigureAwait(false);
                        await BrownoutAsync(BrownoutReason.MemoryPressure, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                });
            }

            await FireObserverAsync(o => o.OnStartedAsync(ct), ct).ConfigureAwait(false);

            // Opt-in upgrade check. Hosts that want this off explicitly disable
            // CheckForUpgradesOnStart so the cold-start path stays fast.
            if (_options.CheckForUpgradesOnStart)
            {
                var upgrades = await CheckForUpgradesAsync(ct).ConfigureAwait(false);
                foreach (var u in upgrades)
                {
                    await FireObserverAsync(o => o.OnUpgradeAvailableAsync(u, ct), ct)
                        .ConfigureAwait(false);
                }
            }
        }
        finally { _startGate.Release(); }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_disposed) return;

        // Persist persona before teardown.
        await TrySavePersonaAsync().ConfigureAwait(false);

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_shutdownCts.IsCancellationRequested)
            {
                try { _shutdownCts.Cancel(); } catch { /* already disposed */ }
            }

            if (_slots is not null)
                await _slots.EvictSpecialistAsync().ConfigureAwait(false);

            if (_generator is IAsyncDisposable adisp)
                await adisp.DisposeAsync().ConfigureAwait(false);
            else
                _generator?.Dispose();

            _generator = null;
            _started = false;
            _personaCache = null;

            _logger.LogInformation("Butler service stopped.");

            await FireObserverAsync(o => o.OnStoppedAsync(CancellationToken.None),
                CancellationToken.None).ConfigureAwait(false);

            if (!_disposed)
            {
                var old = _shutdownCts;
                _shutdownCts = new CancellationTokenSource();
                try { old.Dispose(); } catch { /* already cancelled/disposed */ }
            }
        }
        finally { _startGate.Release(); }
    }

    // ------------------------------------------------------------------
    // RT-04 — Brownout: hot-swap to next-smaller fallback under pressure
    // ------------------------------------------------------------------

    /// <summary>
    /// Hot-swap the running generator to the next entry in its fallback
    /// chain. Drains the current generation gracefully (via shutdownCts
    /// cancellation), disposes the generator, resolves + loads the
    /// fallback, fires <see cref="IAIObserver.OnBrownoutAsync"/>.
    /// No-op when not started, when no fallback exists, or when no
    /// selector/registry is wired. Safe to call concurrently.
    /// </summary>
    public async Task<bool> BrownoutAsync(
        BrownoutReason    reason,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (!_started || _generator is null) return false;
        if (_modelSelector is null || string.IsNullOrWhiteSpace(_resolvedModelId))
        {
            _logger.LogDebug("Brownout requested ({Reason}) but no selector or no resolved model — skipped.", reason);
            return false;
        }

        var from = _resolvedModelId!;
        var chain = _modelSelector.ChainFor(from);
        var idx = -1;
        for (var i = 0; i < chain.Count; i++)
            if (string.Equals(chain[i], from, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
        if (idx < 0 || idx + 1 >= chain.Count)
        {
            _logger.LogDebug("Brownout requested ({Reason}) but no fallback available from '{From}'.", reason, from);
            return false;
        }
        var to = chain[idx + 1];

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (string.Equals(_resolvedModelId, to, StringComparison.OrdinalIgnoreCase)) return false;

            _logger.LogInformation("Brownout ({Reason}): swapping {From} -> {To}", reason, from, to);

            // Cancel in-flight generations so they drain.
            try { _shutdownCts.Cancel(); } catch { /* already disposed */ }

            if (_generator is IAsyncDisposable adisp) await adisp.DisposeAsync().ConfigureAwait(false);
            else _generator?.Dispose();
            _generator = null;

            var old = _shutdownCts;
            _shutdownCts = new CancellationTokenSource();
            try { old.Dispose(); } catch { }

            _resolvedModelId = to;
            _generator = await BuildGeneratorAsync(to, ct).ConfigureAwait(false);
        }
        finally { _startGate.Release(); }

        await FireObserverAsync(o => o.OnBrownoutAsync(from, to, reason, ct), ct)
            .ConfigureAwait(false);
        return true;
    }

    // ------------------------------------------------------------------
    // Single-turn inference
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public Task<string> AskAsync(string question, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(question);
        // Pass only the user message — PrepareMessagesAsync will inject the
        // enriched system prompt (persona + device context + RAG).
        var messages = new List<ChatMessage>
        {
            new("user", question),
        };
        return ChatAsync(messages, _options.DefaultGenerationOptions, ct);
    }

    /// <inheritdoc />
    public async Task<string> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        await EnsureStartedAsync(ct).ConfigureAwait(false);

        // Determine the user query (last user message) for routing + RAG lookup.
        var userQuery = messages.LastOrDefault(m =>
            string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content
            ?? string.Empty;
        var hasImage = messages.Any(m => m.ImageBytes is not null);

        // Neuron: generalist by default; a specialist may answer when a router is
        // configured. Byte-identical to the single-slot path when Router is null.
        var generator = await SelectSlotAsync(userQuery, hasImage, ct).ConfigureAwait(false);

        var prepared = await PrepareMessagesAsync(messages, userQuery, ct)
            .ConfigureAwait(false);
        var effectiveOptions = options ?? _options.DefaultGenerationOptions;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);

        var correlationId = Guid.NewGuid();
        var sw = Stopwatch.StartNew();
        var response = await generator
            .GenerateAsync(prepared, effectiveOptions, linked.Token)
            .ConfigureAwait(false);
        sw.Stop();

        // Store exchange in episodic memory (fire-and-forget with error isolation).
        _ = TryStoreEpisodeAsync(userQuery, response, ct);

        await FireObserverAsync(o => o.OnChatCompletedAsync(
            new AIChatEvent(correlationId, prepared, response, sw.Elapsed, DateTimeOffset.UtcNow),
            ct), ct).ConfigureAwait(false);

        return response;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        await EnsureStartedAsync(ct).ConfigureAwait(false);

        var userQuery = messages.LastOrDefault(m =>
            string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content
            ?? string.Empty;
        var hasImage = messages.Any(m => m.ImageBytes is not null);

        // Neuron slot selection (generalist unless a router routes to a specialist).
        var generator = await SelectSlotAsync(userQuery, hasImage, ct).ConfigureAwait(false);

        var prepared = await PrepareMessagesAsync(messages, userQuery, ct)
            .ConfigureAwait(false);
        var effectiveOptions = options ?? _options.DefaultGenerationOptions;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);

        var correlationId = Guid.NewGuid();
        var sw = Stopwatch.StartNew();
        var tokenCount = 0;
        var firstToken = true;
        var sb = new StringBuilder();

        await foreach (var piece in generator.StreamAsync(prepared, effectiveOptions, linked.Token)
            .ConfigureAwait(false))
        {
            if (firstToken)
            {
                firstToken = false;
                await FireObserverAsync(o => o.OnStreamStartedAsync(
                    new AIStreamEvent(correlationId, prepared, sw.Elapsed, 0, DateTimeOffset.UtcNow),
                    ct), ct).ConfigureAwait(false);
            }

            sb.Append(piece);
            tokenCount++;
            yield return piece;
        }

        sw.Stop();

        // Store the full streamed response episodically.
        _ = TryStoreEpisodeAsync(userQuery, sb.ToString(), ct);

        await FireObserverAsync(o => o.OnStreamCompletedAsync(
            new AIStreamEvent(correlationId, prepared, sw.Elapsed, tokenCount, DateTimeOffset.UtcNow),
            ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Streaming variant that preserves the REASONING/CONTENT tag instead of
    /// discarding the model's thinking.
    /// <para>
    /// <see cref="StreamAsync"/> calls <c>IChatGenerator.StreamAsync</c>, which
    /// filters <c>&lt;think&gt;…&lt;/think&gt;</c> out for back-compat — so a host
    /// wired to it can never show reasoning, no matter what the model emits.
    /// This calls <c>StreamFragmentsAsync</c> instead and passes the tag through,
    /// letting a UI render thinking separately from the answer.
    /// </para>
    /// <para>
    /// Only reasoning-capable models (the Qwen3 ladder) emit Reasoning
    /// fragments; for others the default generator implementation tags
    /// everything Content, so this degrades to <see cref="StreamAsync"/>'s
    /// behaviour rather than breaking.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<ChatFragment> StreamFragmentsAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        await EnsureStartedAsync(ct).ConfigureAwait(false);

        var userQuery = messages.LastOrDefault(m =>
            string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content
            ?? string.Empty;
        var hasImage = messages.Any(m => m.ImageBytes is not null);

        var generator = await SelectSlotAsync(userQuery, hasImage, ct).ConfigureAwait(false);
        var prepared = await PrepareMessagesAsync(messages, userQuery, ct).ConfigureAwait(false);
        var effectiveOptions = options ?? _options.DefaultGenerationOptions;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);

        var correlationId = Guid.NewGuid();
        var sw = Stopwatch.StartNew();
        var tokenCount = 0;
        var firstToken = true;
        var answer = new StringBuilder();

        await foreach (var fragment in generator
            .StreamFragmentsAsync(prepared, effectiveOptions, linked.Token)
            .ConfigureAwait(false))
        {
            if (firstToken)
            {
                firstToken = false;
                await FireObserverAsync(o => o.OnStreamStartedAsync(
                    new AIStreamEvent(correlationId, prepared, sw.Elapsed, 0, DateTimeOffset.UtcNow),
                    ct), ct).ConfigureAwait(false);
            }

            // Only the ANSWER is remembered — storing the thinking trace as the
            // episode would poison recall with the model's scratchpad.
            if (fragment.Kind == ChatFragmentKind.Content)
                answer.Append(fragment.Text);

            tokenCount++;
            yield return fragment;
        }

        sw.Stop();
        _ = TryStoreEpisodeAsync(userQuery, answer.ToString(), ct);

        await FireObserverAsync(o => o.OnStreamCompletedAsync(
            new AIStreamEvent(correlationId, prepared, sw.Elapsed, tokenCount, DateTimeOffset.UtcNow),
            ct), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ToolResult> InvokeToolAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ThrowIfDisposed();

        if (_options.ToolBridge is null)
        {
            var failResult = new ToolResult
            {
                ToolName = invocation.ToolName,
                Success = false,
                Error = "No tool bridge configured.",
            };

            await FireObserverAsync(o => o.OnToolInvokedAsync(
                new AIToolEvent(Guid.NewGuid(), invocation, failResult,
                    TimeSpan.Zero, DateTimeOffset.UtcNow),
                ct), ct).ConfigureAwait(false);

            return failResult;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);

        var correlationId = Guid.NewGuid();
        var sw = Stopwatch.StartNew();
        var result = await _options.ToolBridge.InvokeAsync(invocation, linked.Token).ConfigureAwait(false);
        sw.Stop();

        await FireObserverAsync(o => o.OnToolInvokedAsync(
            new AIToolEvent(correlationId, invocation, result, sw.Elapsed, DateTimeOffset.UtcNow),
            ct), ct).ConfigureAwait(false);

        return result;
    }

    // ------------------------------------------------------------------
    // v2.0 — Agentic loop
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<string> AgenticChatAsync(
        string prompt,
        GenerationOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(prompt);
        await EnsureStartedAsync(ct).ConfigureAwait(false);

        // Neuron slot selection for the whole agentic run (prompt has no image).
        var generator = await SelectSlotAsync(prompt, hasImage: false, ct).ConfigureAwait(false);

        // Pinned value wins; otherwise derive from the device tier resolved
        // at StartAsync (defaults to Desktop when no selector ran).
        var maxIter = Math.Max(
            1,
            _options.AgenticMaxIterations
                ?? DeviceTierDefaults.AgenticMaxIterations(_resolvedDeviceTier));
        var effectiveOptions = options ?? _options.DefaultGenerationOptions;

        // Build conversation history with just the user turn.
        // PrepareMessagesAsync injects the enriched system prompt on every
        // iteration so the model always has fresh persona/device/RAG context.
        var history = new List<ChatMessage>
        {
            new("user", prompt),
        };

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);

        string lastResponse = string.Empty;
        for (int iteration = 0; iteration < maxIter; iteration++)
        {
            // forceTools: this IS the tool loop. Whatever the question looks
            // like, a caller who invoked the agentic path has already decided.
            var prepared = await PrepareMessagesAsync(history, prompt, linked.Token, forceTools: true)
                .ConfigureAwait(false);

            var sw = Stopwatch.StartNew();
            var response = await generator
                .GenerateAsync(prepared, effectiveOptions, linked.Token)
                .ConfigureAwait(false);
            sw.Stop();

            lastResponse = response;

            // Append the assistant turn to history for the next iteration.
            history.Add(new ChatMessage("assistant", response));

            await FireObserverAsync(o => o.OnChatCompletedAsync(
                new AIChatEvent(Guid.NewGuid(), prepared, response, sw.Elapsed, DateTimeOffset.UtcNow),
                ct), ct).ConfigureAwait(false);

            // Try to extract a tool call from the response.
            var invocation = ParseToolCall(response);
            if (invocation is null) break; // No tool call — we're done.

            if (_options.ToolBridge is null)
            {
                // No bridge — append an error result and re-prompt so the model
                // can respond without the tool (graceful degradation).
                history.Add(new ChatMessage("tool",
                    $"{{\"tool\": \"{invocation.ToolName}\", \"error\": \"No tool bridge configured.\"}}"));
                continue;
            }

            // Execute the tool and append the result.
            var toolResult = await InvokeToolAsync(invocation, linked.Token).ConfigureAwait(false);
            var toolContent = toolResult.Success
                ? $"{{\"tool\": \"{toolResult.ToolName}\", \"result\": {JsonSerializer.Serialize(toolResult.Result)}}}"
                : $"{{\"tool\": \"{toolResult.ToolName}\", \"error\": {JsonSerializer.Serialize(toolResult.Error)}}}";

            history.Add(new ChatMessage("tool", toolContent));
            // Loop back to re-prompt with tool result in history.
        }

        // Store the entire agentic exchange as a single episode.
        _ = TryStoreEpisodeAsync(prompt, lastResponse, ct);

        return lastResponse;
    }

    // ------------------------------------------------------------------
    // v2.0 — Feedback
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public async Task SubmitFeedbackAsync(FeedbackSignal signal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ThrowIfDisposed();

        if (_options.FeedbackStore is null) return;

        try
        {
            await _options.FeedbackStore.AddAsync(signal, ct).ConfigureAwait(false);

            // Update in-memory persona from the signal.
            var persona = await EnsurePersonaAsync(ct).ConfigureAwait(false);
            if (signal.Polarity == FeedbackPolarity.Positive)
                persona.PositiveSignals++;
            else if (signal.Polarity == FeedbackPolarity.Negative)
                persona.NegativeSignals++;
            persona.TotalInteractions++;

            // Gap 6 — run FeedbackAnalyser and apply persona adaptations.
            var recentSignals = await _options.FeedbackStore
                .GetRecentAsync(20, ct)
                .ConfigureAwait(false);

            var adaptation = new FeedbackAnalyser().Analyse(recentSignals);

            // Verbosity: float delta maps to string state machine.
            if (adaptation.VerbosityDelta < 0f)
                persona.Verbosity = persona.Verbosity switch
                {
                    "detailed" => "balanced",
                    _          => "brief",
                };
            else if (adaptation.VerbosityDelta > 0f)
                persona.Verbosity = persona.Verbosity switch
                {
                    "brief" => "balanced",
                    _       => "detailed",
                };

            // Formality: same pattern (analyser returns 0 currently; wired for future).
            if (adaptation.FormalityDelta < 0f)
                persona.Formality = persona.Formality switch
                {
                    "formal"  => "neutral",
                    _         => "casual",
                };
            else if (adaptation.FormalityDelta > 0f)
                persona.Formality = persona.Formality switch
                {
                    "casual" => "neutral",
                    _        => "formal",
                };

            // Accumulate preferred topic weights.
            foreach (var topic in adaptation.PreferredTopics)
            {
                persona.TopicWeights.TryGetValue(topic, out var existing);
                persona.TopicWeights[topic] = existing + 1f;
            }

            await TrySavePersonaAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store feedback signal; non-fatal.");
        }
    }

    // ------------------------------------------------------------------
    // DisposeAsync
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { _shutdownCts.Cancel(); } catch { /* already disposed */ }
        try { _pressureSub?.Dispose(); } catch { /* swallow */ } finally { _pressureSub = null; }

        // Persist persona before teardown.
        await TrySavePersonaAsync().ConfigureAwait(false);

        try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { /* swallow */ }

        if (_slots is not null)
            try { await _slots.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
        _slots = null;

        if (_generator is IAsyncDisposable adisp)
            try { await adisp.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
        else
            try { _generator?.Dispose(); } catch { /* swallow */ }
        _generator = null;

        _shutdownCts.Dispose();
        _startGate.Dispose();
    }

    // ------------------------------------------------------------------
    // Private — startup helpers
    // ------------------------------------------------------------------

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        if (_started) return;
        await StartAsync(ct).ConfigureAwait(false);
    }

    private async Task<string> ResolveModelPathAsync(CancellationToken ct)
    {
        // 1. Explicit path wins — used by tests and devs pinning a local file.
        if (!string.IsNullOrWhiteSpace(_options.ModelPath))
        {
            if (!System.IO.File.Exists(_options.ModelPath))
                throw new System.IO.FileNotFoundException(
                    "Configured AIOptions.ModelPath does not exist.",
                    _options.ModelPath);
            _resolvedModelId = _options.ModelId; // may be null; downstream tolerates
            return _options.ModelPath!;
        }

        if (_modelLoader is null)
            throw new InvalidOperationException(
                "AIService needs either AIOptions.ModelPath or an IModelLoader.");

        // 2. Resolve ModelId — pinned by consumer, or auto-selected from the
        //    live device when null. The directive's "infer from device, don't
        //    ask the consumer" principle: consumer states intent (capabilities
        //    via AIOptions, currently fixed to Default), SDK answers the model.
        var modelId      = _options.ModelId;
        var autoSelected = false;

        if (string.IsNullOrWhiteSpace(modelId))
        {
            if (_modelSelector is null)
                throw new InvalidOperationException(
                    "AIOptions.ModelId is null and no IModelSelector is registered. " +
                    "Either pin ModelId / ModelPath, or call AddCircleAI which wires " +
                    "DeviceAwareModelSelector by default.");

            var deviceCtx = _options.DeviceContext ?? DefaultDeviceContext.Instance;
            var probe     = deviceCtx is DefaultDeviceContext ddc
                ? ddc.BuildProbe()
                : DeviceProbe.Snapshot(); // generic context — probe from runtime
            // ASK THE MODALITY SELECTOR FIRST for anything that is not plain
            // text. BestFit only knows the chat catalogue, so a Vision request
            // it cannot satisfy comes back as a generic "no model satisfies
            // required capabilities" — true, but it does not distinguish "we
            // ship no VLM" from "this device is too small for the one we ship",
            // which are different problems with different fixes. PlanFor draws
            // that line and its Reason is written to be shown to a person.
            if (_options.RequiredCapabilities.HasFlag(ChatCapability.Vision))
            {
                var visionPlan = PlanFor(ModelModality.Vision, probe);
                if (!visionPlan.IsAvailable)
                    throw new InvalidOperationException(
                        $"Vision was requested but cannot be served: {visionPlan.Reason}. " +
                        "Catalogue a vision model, or drop ChatCapability.Vision from " +
                        "AIOptions.RequiredCapabilities.");
            }

            var selection = _modelSelector.BestFit(probe, _options.RequiredCapabilities);

            modelId             = selection.ModelId;
            _resolvedDeviceTier = selection.Tier;
            autoSelected        = true;
        }

        _resolvedModelId = modelId;
        _autoSelected    = autoSelected;
        await FireObserverAsync(
            o => o.OnModelFetchingAsync(modelId!, autoSelected, ct), ct)
            .ConfigureAwait(false);

        // 3. Already on disk? Use it.
        //
        // Ask the loader whether the model is COMPLETE — do not just test that
        // GetModelPath's file exists. For an MNN bundle that path is config.json:
        // 403 bytes, downloaded first, and present long before the 450 MB weight
        // file beside it. A download interrupted after those first few KB left a
        // directory that passed this gate forever, so the download was skipped on
        // every subsequent launch and MNN failed with "load failed" every time.
        // The model could never repair itself, and on the P30 Lite it never did:
        // chat was dead on that phone from the first interrupted fetch onward.
        //
        // ModelExists checks the weight file, which is the thing that actually
        // has to be there.
        var existing = _modelLoader.GetModelPath(modelId!);
        if (!string.IsNullOrEmpty(existing) &&
            System.IO.File.Exists(existing) &&
            _modelLoader.ModelExists(modelId!))
            return existing;

        // 4. Fetch via the loader.
        ct.ThrowIfCancellationRequested();
        _logger.LogInformation("Butler downloading model {ModelId}", modelId);
        var downloaded = await _modelLoader.DownloadModelAsync(modelId!).ConfigureAwait(false);
        if (string.IsNullOrEmpty(downloaded) || !System.IO.File.Exists(downloaded))
            throw new InvalidOperationException(
                $"Model loader returned an invalid path for '{modelId}'.");
        return downloaded;
    }

    /// <inheritdoc/>
    public async Task PrewarmAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (!_started) { await StartAsync(ct).ConfigureAwait(false); return; }
        await WarmUpAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // RT-02 — Session persistence (generalist floor only)
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<bool> SaveSessionAsync(string path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(path)) return false;
        var generator = _generator;                 // always-warm generalist floor
        if (generator is null) return false;
        try { return await generator.SaveSessionAsync(path, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogDebug(ex, "Session save failed; non-fatal."); return false; }
    }

    /// <inheritdoc />
    public async Task<bool> LoadSessionAsync(string path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(path)) return false;
        await EnsureStartedAsync(ct).ConfigureAwait(false);
        var generator = _generator;
        if (generator is null) return false;
        try { return await generator.LoadSessionAsync(path, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogDebug(ex, "Session load failed; non-fatal."); return false; }
    }

    // ------------------------------------------------------------------
    // Neuron — two-slot residency helpers
    // ------------------------------------------------------------------

    private DeviceProbe ProbeDevice()
    {
        var deviceCtx = _options.DeviceContext ?? DefaultDeviceContext.Instance;
        return deviceCtx is DefaultDeviceContext ddc ? ddc.BuildProbe() : DeviceProbe.Snapshot();
    }

    private static long EstimateModelBytes(string modelPath)
    {
        try { return new System.IO.FileInfo(modelPath).Length; }
        catch { return 0L; }
    }

    /// <summary>
    /// Build a fresh generator for a specific model id via the loader + factory.
    /// Shared by the RT-04 brownout swap and the Neuron specialist slot.
    /// </summary>
    private async Task<IChatGenerator> BuildGeneratorAsync(string modelId, CancellationToken ct)
    {
        if (_modelLoader is null)
            throw new InvalidOperationException(
                $"Building model '{modelId}' by id requires an IModelLoader.");

        // Completeness, not mere presence — see ResolveModelPathAsync. Testing
        // only that GetModelPath's file exists accepts a bundle whose config.json
        // arrived and whose weights did not.
        var existing = _modelLoader.GetModelPath(modelId);
        var modelPath = !string.IsNullOrEmpty(existing) &&
                        System.IO.File.Exists(existing) &&
                        _modelLoader.ModelExists(modelId)
            ? existing
            : await _modelLoader.DownloadModelAsync(modelId).ConfigureAwait(false);
        if (string.IsNullOrEmpty(modelPath) || !System.IO.File.Exists(modelPath))
            throw new InvalidOperationException($"Model '{modelId}' resolution failed.");

        var contextSize = _options.ContextSize
            ?? DeviceTierDefaults.ContextWindow(_resolvedDeviceTier);

        return _generatorFactory is not null
            ? _generatorFactory(modelPath)
            : new QwenTextGenerator(
                modelPath,
                contextSize: (uint)Math.Max(1, contextSize),
                threads: _options.ThreadCount);
    }

    /// <summary>
    /// Neuron slot selection. With no router configured this returns the
    /// generalist (identical to the single-slot path). With a router: it
    /// classifies the turn and, on a specialist decision, best-fits + hot-loads
    /// (admission-gated) a specialist into the second slot and answers from it.
    /// Any miss — no selector/loader, a best-fit that resolves to the generalist
    /// itself, gate denial, or a build failure — degrades to the generalist and
    /// never throws.
    /// </summary>
    private async Task<IChatGenerator> SelectSlotAsync(
        string userQuery, bool hasImage, CancellationToken ct)
    {
        var generalist = _generator
            ?? throw new InvalidOperationException("Butler is not ready.");

        var router = _options.Router;
        if (router is null) return generalist;

        Neuron.RouteDecision decision;
        try { decision = router.Route(new Neuron.RouteContext(userQuery ?? string.Empty, hasImage)); }
        catch { return generalist; }   // a router fault must never break generation

        if (decision.Organ != Neuron.Organ.Specialist) return generalist;

        // Specialists need a selector (to best-fit the capability) and a loader
        // (to fetch/build the bundle). Absent either, the generalist answers.
        if (_modelSelector is null || _modelLoader is null) return generalist;

        try
        {
            var selection = _modelSelector.BestFit(ProbeDevice(), decision.Capability);

            // Best-fit resolved to the generalist itself — no second slot needed.
            if (string.Equals(selection.ModelId, _resolvedModelId, StringComparison.OrdinalIgnoreCase))
                return generalist;

            _slots ??= new Neuron.ResidentSlotManager(_generalistReservedBytes, ProbeDevice);
            var admission = await _slots.EnsureSpecialistAsync(selection, BuildGeneratorAsync, ct)
                .ConfigureAwait(false);

            // Denied / failed → generalist; admitted / already-resident → specialist.
            return admission.Generator ?? generalist;
        }
        catch (OperationCanceledException) { throw; }
        catch { return generalist; }
    }

    /// <summary>
    /// Exercises the native path once so the first real call does not.
    /// </summary>
    /// <remarks>
    /// WARMS THE PROMPT A REAL TURN WILL SEND, not the bare one. The point is
    /// to leave a prefix-cache snapshot under the key the first question will
    /// look up; warming a system prompt nothing ever sends writes a snapshot
    /// nothing ever finds.
    /// <para>
    /// THIS WAS TRIED BEFORE AND RECORDED AS WORSE, so here is why that verdict
    /// no longer holds. The measurement was:
    /// </para>
    /// <code>
    ///                    warm-up                    first question
    ///   bare      prompt= 66 tok, prefill  2 455 ms   prompt=102 tok, 3 329 ms
    ///   enriched  prompt=495 tok, prefill 13 239 ms   prompt=102 tok, 3 658 ms
    ///   model load 16.4 s -> 22.9 s
    /// </code>
    /// <para>
    /// Two things have changed since. The 495 tokens were mostly the TOOL BLOCK,
    /// which at the time was assembled for any turn; it is now latched off until
    /// something asks for a tool, and the phone's own log confirms a real voice
    /// turn carries none of it — <c>enrichment=131 tools=0</c>, about 35 tokens
    /// rather than 400. And the first turn was "unchanged" because the mechanism
    /// that would have carried the warm-up forward was switched off:
    /// <c>UsePrefixCache</c> defaults to false and nothing in the tree set it,
    /// so there was no cache to hit and nothing to be gained by matching a key.
    /// </para>
    /// <para>
    /// Both are now true, so the old note's own closing sentence applies —
    /// making the first turn fast needs the prefix cache to survive across
    /// process starts. That is this, plus <c>UsePrefixCache</c> at the caller.
    /// </para>
    /// <para>
    /// AN EMPTY QUERY, DELIBERATELY. Enrichment adds retrieved memories and
    /// skill descriptions only when there is a question to retrieve against, so
    /// an empty one yields exactly the stable part — persona and device context
    /// — which is the part every turn shares and the only part worth caching.
    /// </para>
    /// </remarks>
    private async Task WarmUpAsync(CancellationToken ct)
    {
        var generator = _generator;
        if (generator is null) return;

        var seed = new[]
        {
            new ChatMessage("system", _options.SystemPrompt),
            new ChatMessage("user", "."),
        };

        // Built the same way a real turn's messages are, so the system message —
        // and therefore the prefix-cache key derived from it — is the same one
        // the first question will ask for.
        var warmMessages = await PrepareMessagesAsync(seed, string.Empty, ct).ConfigureAwait(false);

        var warmOptions = new GenerationOptions
        {
            MaxTokens = 1,
            Temperature = 0f,
            // The whole reason this runs before anybody asks anything: the
            // prefill of the shared system block is written to disk here, where
            // no one is waiting for it, instead of in front of the first
            // question, where they are.
            UsePrefixCache = true,
        };

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);
        _ = await generator.GenerateAsync(warmMessages, warmOptions, linked.Token).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Private — v2.0 context enrichment
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds the enriched message list:
    ///   1. Augmented system prompt (persona hints + device context + RAG snippets)
    ///   2. Original conversation messages
    /// </summary>
    // internal, not private: AssemblyInfo already grants CircleAI.Tests access
    // (same reason ParseToolCall is internal). Prompt composition decides what
    // the model can actually know, so it is worth asserting directly rather
    // than inferring from a generated reply.
    /// <param name="forceTools">
    /// Always include the tool block, whatever the question looks like.
    /// </param>
    /// <remarks>
    /// Set by the agentic path, which exists FOR tool use — a caller that has
    /// explicitly asked to run the tool loop has already answered the question
    /// the cue test is guessing at, and letting the guess overrule them would
    /// disarm the loop on any turn whose wording happened not to match.
    /// </remarks>
    internal async Task<List<ChatMessage>> PrepareMessagesAsync(
        IReadOnlyList<ChatMessage> messages,
        string userQuery,
        CancellationToken ct,
        bool forceTools = false)
    {
        // Enrichment is computed ONCE and used differently depending on whether
        // the caller owns the system turn.
        var enrichment = await BuildEnrichmentAsync(userQuery, ct).ConfigureAwait(false);

        // Tool descriptions are a CAPABILITY CONTRACT. If a ToolBridge is
        // registered and the model is never told the tools exist, tool calling
        // silently no-ops and looks like a model failure — so this is appended
        // regardless of who owns the system turn.
        //
        // BUT NOT ON EVERY TURN, because the contract is not free. Measured on a
        // P30 Lite with Qwen2.5-1.5B: the block took the prompt from 33 tokens to
        // 449, and prefill on that phone costs ~70 ms per prompt token — so
        // first-token time went from 4.5 s to 27.5 s. Twenty-three seconds of
        // JSON schemas, prefilled again on every question, to answer "I am
        // feeling lonely today."
        //
        // Sent only when the turn might actually reach for a tool. Getting this
        // wrong in one direction costs a tool call the model could have made;
        // getting it wrong in the other costs every user twenty-three seconds of
        // silence on every question. The asymmetry is not close.
        // ONCE ON, IT STAYS ON — and that is a latency fix, not a capability one.
        //
        // The gate above is per-QUESTION and the block it controls sits in the
        // per-CONVERSATION system message. So the system message flipped between
        // turns, and because it is the first thing in the prompt, every flip
        // invalidated the KV prefix for the WHOLE conversation.
        //
        // Caught on the P30 by the bench, four questions in:
        //
        //     "What is 17 times 4?"                -> prompt=30 tok,   prefill  1 028 ms
        //     "How many days are in a leap year?"  -> prompt=1081 tok, prefill 36 161 ms
        //
        // Nothing was slow. "how many" is a tool cue and "17 times 4" is not, so
        // the block arrived, the system message changed, and the entire
        // conversation was prefilled again from the top. Thirty-six seconds
        // before the first word.
        //
        // The note below rightly says stable things belong in the system message.
        // The tool block was the one thing in there that was not stable.
        //
        // Latching makes it stable again. The 449-token cost measured earlier is
        // only paid REPEATEDLY when the cache misses; held constant, it is
        // prefilled once for the conversation and every later turn reuses it. So
        // the first tool-ish question pays for the block once, and after that it
        // is free — instead of every question after it paying a full re-prefill.
        //
        // Still off until something actually asks for it, so the FIRST turn of a
        // conversation stays cheap. That first impression is the one that matters.
        // ONLY THE TOOLS THE QUESTION CAN USE, AND THE SET ONLY GROWS.
        //
        // Sending the whole catalogue on any cue was measured at 1 235
        // characters — about 300 tokens, ten seconds of prefill — including
        // tools with no bearing on the question. "How many days in a leap year"
        // was carrying a battery reader and a product price lookup.
        //
        // The set ACCUMULATES rather than being recomputed per turn, for the
        // same reason the block latches at all: it lives in the system message,
        // which is the conversation's prefix, and a prefix that changes costs a
        // full re-prefill. A set that shrank when the subject changed would
        // invalidate the cache on every topic switch — the exact bug latching
        // was introduced to fix. Growing only when a genuinely new capability is
        // needed costs one re-prefill per new capability, and nothing after.
        var all = _toolsLatched || forceTools || NeedsTools(userQuery)
            ? await AvailableToolsAsync(ct).ConfigureAwait(false)
            : System.Array.Empty<ToolDefinition>();

        if (all.Count > 0)
        {
            var wanted = forceTools ? all : RelevantTools(all, userQuery);
            foreach (var t in wanted) _latchedTools.Add(t.Name);
        }

        if (_latchedTools.Count > 0) _toolsLatched = true;

        var toolBlock = _latchedTools.Count == 0
            ? string.Empty
            : ToolPromptRenderer.Render(
                all.Where(t => t is not null && _latchedTools.Contains(t.Name)).ToList());

        var hasSystem = messages.Any(m =>
            string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase));

        // GROUNDING BELONGS IN THE SYSTEM MESSAGE — where it started, and where
        // it has to be for a KV cache to survive between turns.
        //
        // It was moved onto the newest user turn on the theory that grounding is
        // volatile and volatile things belong last. That was wrong twice over.
        // The grounding here is a fixed list of skill names, identical on every
        // turn; and attaching it to whichever turn is newest makes it MOVE, so
        // rendering turn two silently rewrote turn one and broke the very prefix
        // the change was meant to preserve. Verified by logging the prompt the
        // model actually received rather than reasoning about it.
        //
        // In the system message it sits in front of the whole conversation and
        // never shifts, so the prefix stays byte-identical and only genuinely
        // new text needs prefilling.
        //
        // What must stay stable to keep that true: see the note on rounding the
        // clock in BuildEnrichmentAsync. Anything that changes here costs one
        // full re-prefill of the conversation, so change it rarely and on
        // purpose.
        var extra = (hasSystem && _options.SystemPromptEnrichment != SystemPromptEnrichment.Always)
            ? toolBlock
            : Combine(enrichment, toolBlock);

        // WHAT THE PROMPT IS ACTUALLY MADE OF. Added after a warm-up experiment
        // measured 495 prompt tokens where a real turn sends 102, with no
        // explanation that survived reading the code — enrichment defaults to
        // Always for both paths, and neither was asking for tools. Guessing at
        // prompt size from source has now been wrong twice; this prints it.
        // Console, not the logger: hosts wire a logger provider or they do not,
        // and this sample does not — the LogInformation version of this line
        // printed nowhere and cost a build to discover.
        Console.WriteLine(
            $"CIRCLEAI-PROMPT caller={_options.SystemPrompt?.Length ?? 0} " +
            $"enrichment={enrichment?.Length ?? 0} tools={toolBlock.Length} " +
            $"query={userQuery?.Length ?? 0} hasSystem={hasSystem}");

        var prepared = new List<ChatMessage>(messages.Count + 1);

        if (hasSystem)
        {
            var pending = extra;
            foreach (var m in messages)
            {
                if (pending.Length > 0 &&
                    string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
                {
                    prepared.Add(new ChatMessage("system", Combine(m.Content, pending)));
                    pending = string.Empty;
                }
                else
                {
                    prepared.Add(m);
                }
            }
        }
        else
        {
            var combined = Combine(_options.SystemPrompt, extra);
            if (!string.IsNullOrWhiteSpace(combined))
                prepared.Add(new ChatMessage("system", combined));
            prepared.AddRange(messages);
        }

        // THE KEY THE PREFIX CACHE WILL ACTUALLY USE. Two turns that agree on
        // this share a cached prefill; two that differ by one character do not,
        // and pay the difference in seconds. Printed so a miss can be explained
        // instead of guessed at — a warm-up and a first question that disagree
        // here is precisely the bug that made warming the model pointless.
        Console.WriteLine(
            "CIRCLEAI-SYSTEM " + Fingerprint(
                prepared.FirstOrDefault(m =>
                    string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))?.Content));

        return prepared;
    }

    /// <summary>
    /// What the host's bridge offers. Empty when there is no bridge, so callers
    /// can ask unconditionally.
    /// </summary>
    private async Task<IReadOnlyList<ToolDefinition>> AvailableToolsAsync(CancellationToken ct)
    {
        if (_options.ToolBridge is null) return System.Array.Empty<ToolDefinition>();

        try
        {
            return await _options.ToolBridge.GetAvailableToolsAsync(ct).ConfigureAwait(false)
                   ?? System.Array.Empty<ToolDefinition>();
        }
        catch
        {
            // A failing bridge must degrade to plain chat, never break the turn.
            return System.Array.Empty<ToolDefinition>();
        }
    }

    /// <summary>
    /// Renders the registered tools into a system-prompt block. Returns empty
    /// when no bridge is configured, so callers append unconditionally.
    /// </summary>
    private async Task<string> BuildToolBlockAsync(CancellationToken ct)
    {
        var tools = await AvailableToolsAsync(ct).ConfigureAwait(false);
        return ToolPromptRenderer.Render(tools);
    }

    /// <summary>
    /// Whether this turn is worth spending the tool block on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A DELIBERATELY BLUNT TEST, and blunt in the generous direction. It asks
    /// only whether the question reaches outward — at the world, at the device,
    /// at something the model cannot know from its weights. Anything that does
    /// gets the tools; conversation, feelings, general knowledge and small talk
    /// do not.
    /// </para>
    /// <para>
    /// The costs are wildly asymmetric. A false negative loses one tool call on
    /// one turn. A false positive costs EVERY user twenty-three seconds of
    /// silence on EVERY question, because the block is re-prefilled each time —
    /// measured at 33 tokens without it against 449 with, on a phone where
    /// prefill runs ~70 ms per token. So the bar for including it is real
    /// evidence in the question, not the mere possibility of usefulness.
    /// </para>
    /// <para>
    /// Cheap and deterministic on purpose, matching the concierge router: asking
    /// a model whether the turn needs tools would cost a generation to save a
    /// prefill.
    /// </para>
    /// </remarks>
    private static bool NeedsTools(string? userQuery)
    {
        if (string.IsNullOrWhiteSpace(userQuery)) return false;

        var q = userQuery.ToLowerInvariant();
        foreach (var cue in ToolCues)
            if (q.Contains(cue.Phrase, StringComparison.Ordinal)) return true;

        return false;
    }

    /// <summary>
    /// The tools this question could actually use.
    /// </summary>
    /// <remarks>
    /// Empty means send no tool block at all, and that is the point: a cue can
    /// fire for a capability nothing registered provides. "How many days in a
    /// leap year" matches an arithmetic cue, and with no calculator registered
    /// the honest answer is that no tool helps — so the model gets none, and
    /// the turn keeps its 30-token prompt instead of paying for 300.
    /// <para>
    /// A cue with no narrowing words (see <c>Any</c>) matches everything,
    /// because "check the thing for me" genuinely does not say which tool.
    /// </para>
    /// </remarks>
    private static List<ToolDefinition> RelevantTools(
        IReadOnlyList<ToolDefinition> tools, string? userQuery)
    {
        var picked = new List<ToolDefinition>();
        if (tools.Count == 0 || string.IsNullOrWhiteSpace(userQuery)) return picked;

        var q = userQuery.ToLowerInvariant();

        foreach (var cue in ToolCues)
        {
            if (!q.Contains(cue.Phrase, StringComparison.Ordinal)) continue;

            foreach (var tool in tools)
            {
                if (tool is null || picked.Contains(tool)) continue;

                // A cue that names no serving words wants everything.
                if (cue.Serves.Length == 0) { picked.Add(tool); continue; }

                var describes = (tool.Name + " " + tool.Description).ToLowerInvariant();
                if (cue.Serves.Any(w => describes.Contains(w, StringComparison.Ordinal)))
                    picked.Add(tool);
            }
        }

        return picked;
    }

    /// <summary>A phrase that means a tool is wanted, and what could serve it.</summary>
    /// <param name="Phrase">What a person says.</param>
    /// <param name="Serves">
    /// Words that identify a tool able to answer it, matched against the tool's
    /// own name and description.
    /// </param>
    /// <remarks>
    /// THE CUE KNOWS THE INTENT; THIS TELLS IT WHICH TOOL. Without the second
    /// half every cue sent the WHOLE catalogue — measured at 1 235 characters,
    /// ~300 tokens, ~10 seconds of prefill on a P30 — including tools that
    /// could not possibly help. "How many days in a leap year" was shipping the
    /// battery reader and a product price lookup.
    /// <para>
    /// Matching on the tool's own words rather than on hard-coded tool names,
    /// because the tools come from whatever bridge a host registered and this
    /// file must not know their names.
    /// </para>
    /// </remarks>
    private readonly record struct ToolCue(string Phrase, string[] Serves);

    /// <summary>Words that mean the answer is not in the model's weights.</summary>
    /// <remarks>
    /// Reaching OUTWARD is the common thread — at live facts, at the device's
    /// own state, at arithmetic the model should not be trusted to do in its
    /// head. Not a list of tool names: a person asks "what's the weather", never
    /// "call get_weather".
    /// </remarks>
    private static readonly ToolCue[] ToolCues =
    {
        // Live facts the weights cannot hold. Served by anything that can reach
        // the network or look something up.
        new("weather",       Web), new("temperature", Web), new("forecast", Web),
        new("news",          Web), new("today's",     Web), new("right now", Web),
        new("current",       Web), new("latest",      Web), new("search",    Web),
        new("look up",       Web), new("google",      Web), new("exchange rate", Web),
        new("price",         Lookup),

        // The device itself.
        new("battery",  Device), new("signal",    Device), new("storage", Device),
        new("wifi",     Device), new("wi-fi",     Device), new("bluetooth", Device),
        new("volume",   Device),

        // ARITHMETIC, WHICH NOTHING REGISTERED CAN ACTUALLY DO. These cues fire
        // on "how many" and "calculate" and there is no calculator tool — so
        // before this they bought a full catalogue of tools that cannot add up,
        // at ten seconds a turn. Now they match nothing, and matching nothing
        // sends nothing. If a calculator is ever registered, its description
        // will contain these words and it will start being offered with no
        // change here.
        new("calculate",   Maths), new("convert",  Maths),
        new("how much is", Maths), new("how many", Maths),

        // Explicit instruction to go and do something. Deliberately broad: the
        // person has asked for an action without saying which, so this is the
        // one case where offering everything is right.
        new("check ",  Any), new("find out", Any),
        new("tell me the time", Any), new("what time", Any),
    };

    private static readonly string[] Web    = { "search", "web", "internet", "news", "weather", "online" };
    private static readonly string[] Lookup = { "price", "lookup", "look up", "product", "sku", "catalog" };
    private static readonly string[] Device = { "device", "battery", "charge", "signal", "storage", "wifi", "volume", "phone" };
    private static readonly string[] Maths  = { "calculat", "math", "arithmetic", "convert", "sum", "multipl" };

    /// <summary>Matches every tool — used when the intent does not narrow it.</summary>
    private static readonly string[] Any = System.Array.Empty<string>();

    /// <summary>Joins two prompt fragments, tolerating either being empty.</summary>
    private static string Combine(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))  return second ?? string.Empty;
        if (string.IsNullOrWhiteSpace(second)) return first;
        return first + "\n\n" + second;
    }

    /// <summary>
    /// The enrichment ONLY — persona, affect, device context, RAG recall and
    /// skill context — without <see cref="AIOptions.SystemPrompt"/> in front.
    /// <para>
    /// Split out from the full prompt so it can be appended to a system turn the
    /// CALLER owns. Previously enrichment and base prompt were one string, so
    /// the only options were "use ours" or "use theirs" — and a host that set
    /// its own system prompt silently lost RAG recall along with it.
    /// </para>
    /// </summary>
    /// <summary>
    /// Reports an enrichment section that failed, without failing the turn.
    /// </summary>
    /// <remarks>
    /// Console rather than a logger, for the reason given at the CIRCLEAI-PROMPT
    /// line: hosts wire a logger provider or they do not, and this sample does
    /// not — on Android this reaches logcat under the DOTNET tag, which is
    /// verified, and a LogInformation call reaches nowhere at all.
    /// </remarks>
    private static void EnrichmentFailed(string section, Exception ex)
        => Console.WriteLine($"CIRCLEAI-ENRICH {section} failed: {ex.GetType().Name}: {ex.Message}");

    /// <summary>
    /// A short, stable fingerprint of a system message.
    /// </summary>
    /// <remarks>
    /// THE PREFIX CACHE IS KEYED ON THIS TEXT, so "did the system message
    /// change" is the question that decides whether a turn costs 700 ms or six
    /// seconds — and character counts cannot answer it, because two different
    /// prompts of the same length look identical in a log. A hash can, and
    /// unlike the prompt itself it discloses nothing about the person using it.
    /// </remarks>
    private static string Fingerprint(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "empty";
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }

    private async Task<string> BuildEnrichmentAsync(
        string userQuery, CancellationToken ct)
    {
        var sb = new StringBuilder();

        // 1. Persona hints.
        try
        {
            var persona = await EnsurePersonaAsync(ct).ConfigureAwait(false);
            var hint = persona.ToSystemPromptHint();
            if (!string.IsNullOrWhiteSpace(hint))
            {
                sb.AppendLine();
                sb.Append(hint);
            }
        }
        // NON-FATAL IS NOT THE SAME AS NOT WORTH KNOWING. Every section of the
        // enrichment swallowed its own failure, and because the enrichment goes
        // into the system message, a section that quietly failed changed the
        // prompt — and with it the prefix-cache key and the conversation's whole
        // KV prefix. Observed on the P30: the warm-up assembled
        // `enrichment=0` where the very next real turn assembled
        // `enrichment=131`, so the snapshot was written under a key nothing
        // would ever look up. Nothing threw, nothing logged, and the prompt was
        // simply different in a way that cost a full re-prefill.
        catch (Exception ex) { EnrichmentFailed("persona", ex); }

        // 1b. Affect state.
        if (_options.AffectStore is not null)
        {
            try
            {
                var affect = await _options.AffectStore.LoadAsync(_options.PersonaUserId, ct).ConfigureAwait(false);
                var hint = affect.ToSystemPromptHint();
                if (!string.IsNullOrWhiteSpace(hint))
                {
                    sb.AppendLine();
                    sb.Append(hint);
                }
            }
            catch (Exception ex) { EnrichmentFailed("affect", ex); }
        }

        // 2. Device context.
        var ctx = _options.DeviceContext;
        if (ctx is not null && ctx is not NullDeviceContext)
        {
            var ctxLines = new List<string>();
            // ROUNDED TO THE HOUR, deliberately. At minute precision this line
            // changed every sixty seconds, and because it sits in the system
            // message that made the whole conversation's KV cache unreusable —
            // every turn re-prefilled the transcript from the top to keep a
            // timestamp fresh that nothing was reading.
            //
            // An hour is enough for "is it morning", which is all this is for. A
            // turn that genuinely needs the time says so ("what time is it"),
            // and that hits the tool cue and gets the real clock from a tool.
            if (ctx.LocalTime.HasValue)
            {
                var hour = new DateTimeOffset(
                    ctx.LocalTime.Value.Year, ctx.LocalTime.Value.Month, ctx.LocalTime.Value.Day,
                    ctx.LocalTime.Value.Hour, 0, 0, ctx.LocalTime.Value.Offset);
                ctxLines.Add($"Local time: around {hour:yyyy-MM-dd HH:00} ({ctx.TimeZoneId ?? "UTC"})");
            }
            if (!string.IsNullOrWhiteSpace(ctx.LocationHint))
                ctxLines.Add($"Location: {ctx.LocationHint}");
            // BANDED, FOR THE SAME REASON THE CLOCK IS ROUNDED — and it is the
            // same bug, three lines further down, left behind when the clock was
            // fixed. At whole-percent precision this line changed every few
            // minutes of use, and sitting in the system message that made the
            // whole conversation's prefix unreusable: a turn could pay a full
            // re-prefill because the battery had dropped from 68% to 67%.
            //
            // Nothing reads the exact figure. It exists so the model can decline
            // to start something expensive on a dying phone, and a band answers
            // that. A question about the actual level is a tool call, the same
            // as a question about the actual time.
            if (ctx.BatteryLevel.HasValue)
            {
                var pct = (int)(ctx.BatteryLevel.Value * 100);
                var band = pct switch
                {
                    <= 10 => "critical",
                    <= 25 => "low",
                    <= 60 => "moderate",
                    <= 90 => "good",
                    _     => "full",
                };
                var charging = ctx.IsCharging == true ? " (charging)" : string.Empty;
                ctxLines.Add($"Battery: {band}{charging}");
            }
            if (!string.IsNullOrWhiteSpace(ctx.NetworkType))
                ctxLines.Add($"Network: {ctx.NetworkType}");
            if (!string.IsNullOrWhiteSpace(ctx.ActiveAppId))
                ctxLines.Add($"Active app: {ctx.ActiveAppId}");

            if (ctxLines.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("[Device context]");
                foreach (var line in ctxLines)
                    sb.AppendLine(line);
            }
        }

        // 3. RAG context (relevant past exchanges).
        if (_options.EpisodicMemory is not null && _options.RagTopK > 0 &&
            !string.IsNullOrWhiteSpace(userQuery))
        {
            try
            {
                var builder = EnsureRagBuilder();
                var ragBlock = await builder.BuildContextAsync(userQuery, ct)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(ragBlock))
                {
                    sb.AppendLine();
                    sb.Append(ragBlock);
                }
            }
            catch (Exception ex) { EnrichmentFailed("rag", ex); }
        }

        // 4. Skill context (relevant capability definitions).
        if (_options.SkillStore is not null && !string.IsNullOrWhiteSpace(userQuery))
        {
            try
            {
                var skillBuilder = EnsureSkillContextBuilder();
                var skillBlock = await skillBuilder.BuildContextAsync(userQuery, ct)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(skillBlock))
                {
                    sb.AppendLine();
                    sb.Append(skillBlock);
                }
            }
            catch (Exception ex) { EnrichmentFailed("skills", ex); }
        }

        // Each section prefixes a newline, so an empty builder would otherwise
        // hand back leading blank lines.
        return sb.ToString().Trim();
    }

    /// <summary>
    /// <see cref="AIOptions.SystemPrompt"/> followed by everything
    /// <see cref="BuildEnrichmentAsync"/> produces. Used when the caller has NOT
    /// supplied a system turn of their own.
    /// </summary>
    private async Task<string> BuildEnrichedSystemPromptAsync(
        string userQuery, CancellationToken ct)
        => Combine(_options.SystemPrompt,
                   await BuildEnrichmentAsync(userQuery, ct).ConfigureAwait(false));

    private SkillContextBuilder EnsureSkillContextBuilder()
    {
        if (_skillContextBuilder is not null) return _skillContextBuilder;
        _skillContextBuilder = new SkillContextBuilder(_options.SkillStore!, _options.SkillTopK);
        return _skillContextBuilder;
    }

    private RagContextBuilder EnsureRagBuilder()
    {
        if (_ragBuilder is not null) return _ragBuilder;
        _ragBuilder = _options.RagBuilder
            ?? new RagContextBuilder(
                _options.EpisodicMemory!,
                embedder: null,          // recency-only until embedder is wired up
                topK: _options.RagTopK);
        return _ragBuilder;
    }

    // ------------------------------------------------------------------
    // Private — persona helpers
    // ------------------------------------------------------------------

    private async Task<PersonaState> EnsurePersonaAsync(CancellationToken ct)
    {
        if (_personaCache is not null) return _personaCache;
        if (_options.PersonaStore is null)
        {
            _personaCache = new PersonaState { UserId = _options.PersonaUserId };
            return _personaCache;
        }

        _personaCache = await _options.PersonaStore
            .LoadAsync(_options.PersonaUserId, ct)
            .ConfigureAwait(false);
        return _personaCache;
    }

    private async Task TrySavePersonaAsync()
    {
        if (_personaCache is null || _options.PersonaStore is null) return;
        try
        {
            await _options.PersonaStore
                .SaveAsync(_personaCache, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist persona state; non-fatal.");
        }
    }

    // ------------------------------------------------------------------
    // Private — episodic memory
    // ------------------------------------------------------------------

    private async Task TryStoreEpisodeAsync(
        string userText, string assistantText, CancellationToken ct)
    {
        if (_options.EpisodicMemory is null) return;
        if (string.IsNullOrWhiteSpace(userText)) return;

        try
        {
            var entry = new EpisodicMemoryEntry
            {
                UserText      = userText,
                AssistantText = assistantText,
                AppContext     = _options.DeviceContext?.ActiveAppId,
                // Embedding is left null here; a background service can
                // back-fill embeddings when the embedding model is available.
                Embedding = null,
            };
            await _options.EpisodicMemory.AddAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Episodic store write failed; non-fatal.");
        }
    }

    // ------------------------------------------------------------------
    // Private — tool call parsing
    // ------------------------------------------------------------------

    /// <summary>
    /// Attempts to parse a tool call from Qwen3's native
    /// <c>&lt;tool_call&gt;...&lt;/tool_call&gt;</c> format.
    /// Returns <c>null</c> when no tool call is present.
    /// </summary>
    internal static ToolInvocation? ParseToolCall(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return null;

        var start = response.IndexOf(ToolCallOpen, StringComparison.Ordinal);
        if (start < 0) return null;

        var contentStart = start + ToolCallOpen.Length;
        var end = response.IndexOf(ToolCallClose, contentStart, StringComparison.Ordinal);
        if (end < 0) return null;

        var json = response[contentStart..end].Trim();
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Support both {"name":...} and {"tool_name":...} spellings.
            var toolName = root.TryGetProperty("name", out var nameProp)
                ? nameProp.GetString()
                : root.TryGetProperty("tool_name", out var tnProp)
                    ? tnProp.GetString()
                    : null;

            if (string.IsNullOrWhiteSpace(toolName)) return null;

            var args = new Dictionary<string, object?>();
            if (root.TryGetProperty("arguments", out var argsProp) &&
                argsProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in argsProp.EnumerateObject())
                    args[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString()
                        : (object?)prop.Value.GetRawText();
            }

            return new ToolInvocation { ToolName = toolName!, Arguments = args };
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Private — observer
    // ------------------------------------------------------------------

    private async ValueTask FireObserverAsync(
        Func<IAIObserver, ValueTask> action, CancellationToken ct)
    {
        if (_options.Observer is null) return;
        try { await action(_options.Observer).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* respect cancellation silently */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IAIObserver threw; observer errors are non-fatal.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AIService));
    }
}
