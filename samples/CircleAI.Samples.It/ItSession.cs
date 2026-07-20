// ItSession.cs
//
// IT!'s shared core — the one piece of C# that both faces of the sample run:
// the desktop console (Program.cs) and the on-phone Android app (MainActivity).
//
// The real-model path deliberately sets NEITHER ModelId NOR ModelPath. That is
// the whole point of CircleAI: StartAsync probes the device, BestFit picks a
// model that actually fits it, BundleModelLoader downloads the bundle, and
// QwenTextGenerator loads it. The consumer states intent; the SDK answers.

using System.Text;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Hosting;
using CircleAI.Hosting.Chat;
using CircleAI.Hosting.Neuron;
using CircleAI.Inference;
using CircleAI.Skills;

namespace CircleAI.Samples.It;

/// <summary>
/// A live IT! conversation: the composed Neuron plus the running chat history.
/// Host-agnostic — drive it from a console loop or an Android activity.
/// </summary>
public sealed class ItSession : IAsyncDisposable
{
    private const string Prompt = "You are IT! - a dry, competent, on-device assistant.";

    private readonly AIService _brain;
    private readonly NeuronNode _it;
    private readonly HeuristicNeuronRouter _concierge = new();
    private readonly List<ChatTurn> _history;

    private readonly ModelRegistryService? _registry;
    private readonly BundleModelLoader? _loader;

    /// <summary>
    /// Drives the brownout path. Wired so the two-slot Neuron is actually
    /// exercisable: without a pressure source the eviction branch is dead code
    /// no matter what the device does.
    /// </summary>
    public ManualMemoryPressureSource Pressure { get; } = new();

    /// <summary>True when this session is running a real on-device model.</summary>
    public bool UsingRealModel { get; }

    /// <summary>
    /// IT!'s tools. Exposed so a host can show <see cref="ItToolBridge.InvocationLog"/>
    /// — the difference between "the model answered" and "the model called a tool".
    /// </summary>
    public ItToolBridge Tools { get; }

    /// <param name="nativeLibDir">
    /// Directory holding the MNN native libraries. On Android pass
    /// <c>ApplicationInfo.NativeLibraryDir</c> so P/Invoke can find
    /// <c>libmnnbridge.so</c>. <c>null</c> on desktop (standard probing).
    /// </param>
    /// <param name="useStubBrain">
    /// <c>true</c> swaps in <see cref="ItGenerator"/> — a canned responder that
    /// needs no model download. Useful for a zero-wait look at the plumbing;
    /// it does NOT represent real UX (no load time, no latency, instant replies).
    /// </param>
    /// <param name="batteryPercent">
    /// Host-supplied battery reading for the <c>get_battery_level</c> tool.
    /// Android passes a real one; leave null on hosts that cannot read it.
    /// </param>
    public ItSession(
        string? nativeLibDir = null,
        bool useStubBrain = false,
        Func<int?>? batteryPercent = null)
    {
        UsingRealModel = !useStubBrain;
        Tools = new ItToolBridge(batteryPercent);

        if (useStubBrain)
        {
            // Placeholder brain — a dummy file satisfies the ModelPath check;
            // the injected generator never reads it.
            var stubPath = Path.Combine(Path.GetTempPath(), "it-placeholder.model");
            if (!File.Exists(stubPath)) File.WriteAllText(stubPath, "placeholder");

            var stubOptions = new AIOptions
            {
                SystemPrompt = Prompt,
                ModelId      = "it-stub",
                ModelPath    = stubPath,
                WarmOnStart  = false,
                ToolBridge   = Tools,
            };

            _brain = new AIService(stubOptions, generatorFactory: _ => new ItGenerator());
        }
        else
        {
            var modelDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CircleAI", "Models");

            _registry = new ModelRegistryService();
            _loader   = new BundleModelLoader(modelDir, _registry);
            var selector = new DeviceAwareModelSelector(_registry);

            var options = new AIOptions
            {
                SystemPrompt          = Prompt,
                NativeLibDir          = nativeLibDir,
                ModelStorageDirectory = modelDir,
                WarmOnStart           = true,   // pay the cold start once, up front
                ToolBridge            = Tools,
                // Self-knowledge from capabilities.json. Without this IT! can
                // describe its TOOLS (a side effect of the tools block) but has
                // no idea it runs offline, remembers across turns, or picks its
                // own model — and, importantly, no idea what it CANNOT do.
                // Opt-in rather than default: skill context spends tokens from a
                // 4096-window on a 0.6B model, so a host should choose it.
                SkillStore            = CapabilityManifestSkillStore.Default,
                // Router set => AIService becomes the two-slot Neuron: warm
                // generalist plus one admission-gated specialist. Left null it
                // is byte-identical to single-slot, which is why the two-slot
                // path was untestable here until now.
                Router                = _concierge,
                // ModelId / ModelPath intentionally unset — the SDK selects.
            };

            _brain = new AIService(
                options, _loader, BuildGenerator, selector, _registry, Pressure);
        }

        _it = new NeuronNode(_brain, id: "it");
        _history = new List<ChatTurn> { new("system", Prompt) };
    }

    // Mirrors what AddCircleAI wires: the template engine reads each model's own
    // chat_template from its tokenizer config, so nothing hardcodes ChatML.
    private static IChatGenerator BuildGenerator(string modelPath)
        => new QwenTextGenerator(
            modelPath,
            contextSize: 4096,
            threads: null,
            templateEngine: new PromptTemplateEngine());

    /// <summary>
    /// Resolves + downloads (first run) + loads + warms the model. On a cold
    /// device this does real work — several hundred MB and a native model load.
    /// Call it off the UI thread.
    /// </summary>
    public Task StartAsync() => _brain.StartAsync();

    /// <summary>One-line status, e.g. "ready - Qwen3-0.6B-MNN (CircleAI)".</summary>
    public string StatusLine => $"{_it.StatusMessage} - {_it.EngineLabel}";

    /// <summary>The scripted conversation both hosts can run to demo IT! end to end.</summary>
    public static IReadOnlyList<string> DemoTurns { get; } = new[]
    {
        "hi",
        "my name is Thabo",
        "what is my name?",
        "solve x^2 = 49 step by step",
        "thanks, bye",
    };

    /// <summary>
    /// Route one turn through the concierge, stream IT!'s reply, and emit the
    /// result as plain lines.
    /// </summary>
    public async Task RunTurnAsync(string input, Action<string> emit)
    {
        var d = _concierge.Route(new RouteContext(input));
        var organ = d.Organ == Organ.Specialist ? $"{d.Organ} ({d.Capability})" : $"{d.Organ}";
        emit($"   -> concierge routes to: {organ}   [{d.Reason}]");

        _history.Add(new ChatTurn("user", input));

        var sb = new StringBuilder();
        await foreach (var chunk in _it.StreamAsync(_history))
            sb.Append(chunk);

        emit($"IT! > {sb}");
        _history.Add(new ChatTurn("assistant", sb.ToString()));
    }

    /// <summary>
    /// Interactive variant: emits the routing decision as a line, then pushes
    /// IT!'s reply through <paramref name="onChunk"/> piece by piece as the model
    /// streams it — so a UI renders the answer arriving live.
    /// </summary>
    public async Task<string> RunTurnStreamingAsync(
        string input, Action<string> emitLine, Action<string> onChunk)
    {
        var d = _concierge.Route(new RouteContext(input));
        var organ = d.Organ == Organ.Specialist ? $"{d.Organ} ({d.Capability})" : $"{d.Organ}";
        emitLine($"   -> concierge routes to: {organ}   [{d.Reason}]");

        _history.Add(new ChatTurn("user", input));

        onChunk("IT! > ");
        var sb = new StringBuilder();
        await foreach (var chunk in _it.StreamAsync(_history))
        {
            sb.Append(chunk);
            onChunk(chunk);
        }
        onChunk("\n");

        _history.Add(new ChatTurn("assistant", sb.ToString()));
        return sb.ToString();
    }

    /// <summary>
    /// Runs one turn through the AGENTIC path: the model may emit a
    /// <c>&lt;tool_call&gt;</c>, IT! executes it, feeds the result back, and the
    /// model answers from that.
    /// <para>
    /// Returns which tools actually ran, not just the text. That distinction is
    /// the whole test — a plausible-sounding answer with an EMPTY tool list means
    /// the model invented the number rather than calling anything, which is
    /// exactly the failure mode a fake generator cannot reveal.
    /// </para>
    /// </summary>
    public async Task<ItToolTurn> RunToolTurnAsync(string input)
    {
        var before = Tools.InvocationLog.Count;
        var answer = await _brain.AgenticChatAsync(input);
        var called = Tools.InvocationLog.Skip(before).ToList();
        return new ItToolTurn(answer, called);
    }

    /// <param name="Answer">IT!'s final text.</param>
    /// <param name="ToolsCalled">
    /// Tools genuinely invoked during the turn. Empty means the model never
    /// emitted a parseable tool call.
    /// </param>
    public readonly record struct ItToolTurn(string Answer, IReadOnlyList<string> ToolsCalled);

    /// <summary>
    /// Fires a Critical memory-pressure signal. The Neuron should evict the
    /// specialist slot first and keep the generalist serving — so a turn taken
    /// straight after this must still answer, not throw.
    /// </summary>
    public ValueTask SignalCriticalMemoryAsync()
        => Pressure.Raise(MemoryPressureLevel.Critical);

    /// <summary>Returns pressure to normal so the specialist may be admitted again.</summary>
    public ValueTask ClearMemoryPressureAsync()
        => Pressure.Raise(MemoryPressureLevel.Normal);

    /// <summary>Prompts that should provoke a tool call, with unguessable answers.</summary>
    public static IReadOnlyList<string> ToolProbes { get; } = new[]
    {
        "what is the battery level?",
        "what does SKU-1001 cost?",
    };

    public async ValueTask DisposeAsync()
    {
        await _brain.DisposeAsync();
        _loader?.Dispose();
        _registry?.Dispose();
    }
}
