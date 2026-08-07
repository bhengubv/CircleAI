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
using System.Text.Json;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Documents;
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
    /// <remarks>
    /// THE BREVITY LINE IS NOT STYLE, IT IS LATENCY. This is answered out loud
    /// on a phone that decodes about seven tokens a second, so every sentence
    /// the model adds is several more seconds of talking — and the whole answer
    /// then sits in the history and gets prefilled again on the next question.
    /// Measured on a P30 Lite: one unprompted answer ran to 1 915 characters,
    /// 64 seconds of decode, and pushed the following turn's prompt to 586
    /// tokens and its first token out to 41 seconds. One long answer cost two
    /// turns.
    ///
    /// Asked for rather than truncated. A cap alone cuts the model off
    /// mid-sentence, which sounds broken; told to be brief it finishes properly
    /// inside the budget, and the cap stays as a backstop.
    /// </remarks>
    private const string Prompt =
        "You are IT! - a dry, competent, on-device assistant. " +
        "Answer in one or two short sentences. Do not list options or explain " +
        "your reasoning unless you are asked to.";

    private readonly AIService _brain;
    private readonly NeuronNode _it;
    private readonly HeuristicNeuronRouter _concierge = new();
    private readonly List<ChatTurn> _history;

    private readonly ModelRegistryService? _registry;
    private readonly BundleModelLoader? _loader;

    /// <summary>
    /// Selector for the non-chat modalities. Asked BEFORE attempting vision or
    /// voice, so an uncatalogued capability is declined with a reason instead of
    /// failing somewhere inside a runtime that never had a model.
    /// Null in stub mode, where there is no registry to select from.
    /// </summary>
    private readonly ISpeechModelSelector? _speech;

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
    /// <param name="pinModelId">
    /// Forces one specific model instead of letting the device decide.
    /// </param>
    /// <remarks>
    /// FOR MEASURING, NOT FOR SHIPPING. Normally leaving this null is the entire
    /// point — StartAsync probes the phone and BestFit picks. But you cannot ask
    /// "how far up the ladder can this phone go" while the phone is the one
    /// choosing the rung: the selector reads battery, and the same handset picked
    /// a 0.6B model at 73% charge and a 1.5B at 100%, which makes every timing
    /// incomparable with every other. Pinning removes that variable so the numbers
    /// mean something.
    /// </remarks>
    public ItSession(
        string? nativeLibDir = null,
        bool useStubBrain = false,
        Func<int?>? batteryPercent = null,
        string? pinModelId = null,
        bool lean = false)
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
            _speech   = new SpeechModelSelector(_registry);
            var selector = new DeviceAwareModelSelector(_registry);

            var options = new AIOptions
            {
                SystemPrompt          = Prompt,
                NativeLibDir          = nativeLibDir,
                ModelStorageDirectory = modelDir,
                WarmOnStart           = true,   // pay the cold start once, up front
                // LEAN ALSO DROPS THE TOOLS, to price them. The base system
                // prompt is one line — thirteen tokens — yet the prompt reaching
                // the model measured 449. Almost all of it is injected, and the
                // tool-definitions block is the largest injector: every schema,
                // on every turn, including "I am feeling lonely today."
                //
                // On this phone prefill costs ~70 ms per prompt token, so every
                // 14 tokens of preamble is a second the person waits.
                ToolBridge            = lean ? null : Tools,
                // Self-knowledge from capabilities.json. Without this IT! can
                // describe its TOOLS (a side effect of the tools block) but has
                // no idea it runs offline, remembers across turns, or picks its
                // own model — and, importantly, no idea what it CANNOT do.
                // Opt-in rather than default: skill context spends tokens from a
                // 4096-window on a 0.6B model, so a host should choose it.
                // LEAN DROPS THE SELF-KNOWLEDGE, to price it. capabilities.json is
                // 23 KB and the skill context is rebuilt into the system prompt on
                // every turn — which on a phone is paid for in prefill, before the
                // person hears a single word. Worth knowing what it costs before
                // deciding it is worth it.
                SkillStore            = lean ? null : CapabilityManifestSkillStore.Default,
                // Router set => AIService becomes the two-slot Neuron: warm
                // generalist plus one admission-gated specialist. Left null it
                // is byte-identical to single-slot, which is why the two-slot
                // path was untestable here until now.
                Router                = _concierge,
                // Normally unset — the SDK selects. Set only when a caller is
                // measuring one specific rung of the ladder.
                ModelId               = pinModelId,
                // ONE PERSON, ONE CONVERSATION — so keep the KV cache between
                // turns. The default resets it before every call, which is right
                // for a server whose clients replay their history and ruinous
                // here: measured on the P30, first token took 32.8 s on question
                // one and 47.1 s by question five, because the whole transcript
                // was re-read from the top each time.
                DefaultGenerationOptions = new GenerationOptions
                {
                    ContinueConversation = true,
                    // A BACKSTOP, not the plan — the brevity instruction in the
                    // system prompt is what should keep answers short. 512 (the
                    // default) is 73 seconds of speech at this phone's decode
                    // rate; 160 is about four sentences, which is longer than
                    // any answer here should need and short enough that a model
                    // that ignores the instruction cannot hold the floor.
                    MaxTokens = 160,
                },
            };

            _brain = new AIService(
                options, _loader, BuildGenerator, selector, _registry, Pressure);
        }

        _it = new NeuronNode(_brain, id: "it");
        _history = new List<ChatTurn> { new("system", Prompt) };
    }

    // Mirrors what AddCircleAI wires: the template engine reads each model's own
    // chat_template from its tokenizer config, so nothing hardcodes ChatML.
    //
    // MODALITY-AWARE. This used to build QwenTextGenerator unconditionally, which
    // meant KimiVlGenerator — a complete vision runtime that routes through
    // mnn_llm_generate_with_image_stream_ex — was never constructed by anything.
    // The image bytes were set on the turn and then silently ignored by a
    // text-only generator, so vision read as "not implemented" when in fact it
    // was written and merely unreachable.
    private static IChatGenerator BuildGenerator(string modelPath)
        => IsVisionModel(modelPath)
            ? new KimiVlGenerator(
                modelPath,
                contextSize: 4096,
                threads: null,
                templateEngine: new PromptTemplateEngine())
            : new QwenTextGenerator(
                modelPath,
                contextSize: 4096,
                threads: null,
                templateEngine: new PromptTemplateEngine());

    /// <summary>
    /// Whether the resolved model is a vision-language model, and therefore
    /// needs the VL generator rather than the text one.
    /// </summary>
    /// <remarks>
    /// Keyed on the registry's <see cref="ModelModality"/> — the selector's own
    /// classification — so the answer comes from the catalogue rather than from
    /// guessing at a filename. Falls back to a name check only when the path
    /// cannot be matched to an entry (a side-loaded model), because getting this
    /// wrong in the safe direction costs a text-only answer, while getting it
    /// wrong the other way loads a VL graph for every ordinary chat.
    /// </remarks>
    private static bool IsVisionModel(string modelPath)
    {
        try
        {
            using var registry = new ModelRegistryService();
            foreach (var entry in registry.AllModels)
            {
                if (entry.Modality != ModelModality.Vision) continue;
                if (modelPath.Contains(entry.Name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // Registry unavailable — fall through to the name heuristic.
        }

        return modelPath.Contains("-VL", StringComparison.OrdinalIgnoreCase)
            || modelPath.Contains("Kimi-VL", StringComparison.OrdinalIgnoreCase)
            || modelPath.Contains("VLM", StringComparison.OrdinalIgnoreCase);
    }

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
    /// <param name="onThinking">
    /// When supplied, the model's REASONING is streamed here as it decodes,
    /// separately from the answer — so you can watch IT! think.
    /// <para>
    /// This switches the turn onto <c>AIService.StreamFragmentsAsync</c>.
    /// <c>NeuronNode</c>/<c>IChatRuntime</c> is deliberately a string-only
    /// contract, and the plain <c>StreamAsync</c> path FILTERS
    /// <c>&lt;think&gt;</c> out — so reasoning can only come from the brain's
    /// fragment stream. Only the Qwen3 ladder emits it; other models tag
    /// everything Content and this simply stays quiet.
    /// </para>
    /// </param>
    public async Task<string> RunTurnStreamingAsync(
        string input, Action<string> emitLine, Action<string> onChunk,
        Action<string>? onThinking = null)
    {
        var d = _concierge.Route(new RouteContext(input));
        var organ = d.Organ == Organ.Specialist ? $"{d.Organ} ({d.Capability})" : $"{d.Organ}";
        emitLine($"   -> concierge routes to: {organ}   [{d.Reason}]");

        _history.Add(new ChatTurn("user", input));

        var sb = new StringBuilder();

        if (onThinking is null)
        {
            onChunk("IT! > ");
            await foreach (var chunk in _it.StreamAsync(_history))
            {
                sb.Append(chunk);
                onChunk(chunk);
            }
        }
        else
        {
            var msgs = _history.Select(t => new ChatMessage(t.Role, t.Content)).ToList();
            var inThinking = false;
            var startedAnswer = false;

            await foreach (var f in _brain.StreamFragmentsAsync(msgs))
            {
                if (f.Kind == ChatFragmentKind.Reasoning)
                {
                    if (!inThinking) { onThinking("\n[thinking] "); inThinking = true; }
                    onThinking(f.Text);
                }
                else
                {
                    if (inThinking) { onThinking("\n"); inThinking = false; }
                    if (!startedAnswer) { onChunk("IT! > "); startedAnswer = true; }
                    sb.Append(f.Text);
                    onChunk(f.Text);
                }
            }
            if (!startedAnswer) onChunk("IT! > ");
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

    /// <summary>
    /// Ask about an IMAGE. Routes to the vision organ and sends the bytes with
    /// the turn (ChatMessage.ImageBytes), which KimiVlGenerator consumes.
    /// <para>
    /// This path existed in the engine but nothing ever fed it: no host set
    /// ImageBytes, so RouteContext.HasImage was always false and the vision
    /// generator was unreachable. Cataloguing a vision model is still required
    /// for BestFit(Vision) to resolve — until then this surfaces the real
    /// "no vision model" error instead of silently answering as if blind.
    /// </para>
    /// </summary>
    public async Task<string> RunImageTurnAsync(
        string question, byte[] imageBytes, Action<string> emitLine, Action<string> onChunk)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);

        // ASK THE SELECTOR FIRST. Whether B! can see is a selection decision, not
        // something to discover by catching an exception thrown deep inside a
        // generator that was handed an image it has no encoder for. The plan also
        // distinguishes "no vision model shipped" from "this phone is too small
        // for the one we ship" — different sentences to the user, different fixes.
        var plan = _speech?.PlanFor(DeviceProbe.Snapshot(), ModelModality.Vision)
                   ?? new ModalityPlan(SelectionQuality.Unavailable, null,
                          "stub mode has no model registry to select a vision model from");
        if (!plan.IsAvailable)
        {
            var msg = $"IT! > (I can't see images: {plan.Reason})\n";
            onChunk(msg);
            _history.Add(new ChatTurn("user", question + " [image]"));
            _history.Add(new ChatTurn("assistant", msg));
            return msg;
        }

        var d = _concierge.Route(new RouteContext(question, HasImage: true));
        var organ = d.Organ == Organ.Specialist ? $"{d.Organ} ({d.Capability})" : $"{d.Organ}";
        emitLine($"   -> concierge routes to: {organ}   [{d.Reason}]");
        emitLine($"   -> vision: {plan.Reason}");

        var msgs = _history.Select(t => new ChatMessage(t.Role, t.Content)).ToList();
        msgs.Add(new ChatMessage("user", question) { ImageBytes = imageBytes });

        var sb = new StringBuilder();
        onChunk("IT! > ");
        await foreach (var chunk in _brain.StreamAsync(msgs))
        {
            sb.Append(chunk);
            onChunk(chunk);
        }
        onChunk("\n");

        _history.Add(new ChatTurn("user", question + " [image]"));
        _history.Add(new ChatTurn("assistant", sb.ToString()));
        return sb.ToString();
    }

    /// <summary>
    /// Slice 1a: renders a SAMPLE CV to PDF through the offline document engine.
    /// NO model — this proves the RENDERER runs on the device (pure-managed
    /// PDFsharp + embedded DejaVu on ARM64 EMUI) and emits a valid PDF, before
    /// the Neuron fills real content in a later slice. Bytes + suggested filename
    /// come back; the host decides where they land.
    /// </summary>
    public static Task<DocumentResult> GenerateSampleCvAsync(CancellationToken ct = default)
    {
        // Static + model-free on purpose: the CV render is independent of the
        // brain, so a host can prove the document engine on device without
        // waiting for the (slow, ~433 MB) model to load.
        var engine = new PdfSharpDocumentEngine();
        return engine.RenderAsync(new DocumentRequest(DocumentKind.Cv, SampleCv()), ct).AsTask();
    }

    /// <summary>A realistic SA entry-level CV, used to prove the render path end to end.</summary>
    private static CvDocument SampleCv() => new(
        FullName: "Thabo Mokoena",
        Headline: "Junior Software Developer",
        Contact: new CvContact(
            Email: "thabo.mokoena@example.co.za",
            Phone: "+27 82 555 0142",
            Location: "Soweto, Johannesburg",
            Links: new[] { "github.com/thabomokoena" }),
        Summary: "Motivated developer with a National Diploma in IT and hands-on experience "
               + "building offline-first Android apps in C#. Keen to grow in a team that ships.",
        Experience: new[]
        {
            new CvExperience("IT Support Intern", "Gauteng Community Hub", "Johannesburg", "Feb 2023", null,
                new[]
                {
                    "Resolved 40+ hardware and network tickets a week, cutting turnaround from 3 days to 1.",
                    "Built a small C# tool to automate monthly asset reports, saving ~6 hours a month.",
                }),
            new CvExperience("Retail Assistant", "Shoprite", "Soweto", "Jun 2021", "Jan 2023",
                new[] { "Handled point-of-sale and daily cash-ups with zero shortfalls over 18 months." }),
        },
        Education: new[]
        {
            new CvEducation("National Diploma: Information Technology", "University of Johannesburg",
                "Johannesburg", "2020", "2022", "Distinction in Software Development."),
            new CvEducation("National Senior Certificate", "Morris Isaacson High School", "Soweto", null, "2019"),
        },
        Skills: new[] { "C#", ".NET / MAUI", "Android", "SQL", "Git", "Problem solving" },
        Certifications: new[] { new CvCertification("Microsoft Certified: Azure Fundamentals", "Microsoft", "2023") },
        Languages: new[] { "English", "isiZulu", "Sesotho" });

    // ── Slice 1b: model-TAILORED CV ────────────────────────────────────────────

    /// <summary>
    /// Slice 1b: the MODEL fills a CV, tailored to a target role, and the offline
    /// engine renders it to PDF. This is the CONTENT half that Slice 1a's static
    /// render (<see cref="GenerateSampleCvAsync"/>) proved the plumbing for.
    /// <para>
    /// The on-device brain (<c>_brain</c>, the same <see cref="AIService"/> that
    /// serves chat) is asked to emit JSON matching the <see cref="CvDocument"/>
    /// schema — summary and experience bullets aligned to <paramref name="targetRole"/>
    /// (role keywords, action-led, quantified where the source supports it).
    /// <see cref="GenerationOptions.IncludeReasoning"/> is OFF so a Qwen3-class
    /// model's &lt;think&gt; block never contaminates the JSON. The JSON is
    /// deserialised into a <see cref="CvDocument"/> and rendered through the SAME
    /// <see cref="PdfSharpDocumentEngine"/> the sample path uses.
    /// </para>
    /// <para>
    /// GUARANTEE: a real PDF ALWAYS comes back. A 0.6B model can return imperfect
    /// JSON; when parsing fails — or the brain itself errors, or there is no model
    /// on this device at all — a DETERMINISTIC fallback builds a CvDocument straight
    /// from <paramref name="rawProfile"/> so the user still gets a document made of
    /// their own words rather than nothing.
    /// </para>
    /// </summary>
    /// <param name="rawProfile">
    /// The candidate's unstructured profile — pasted experience, an old CV's text,
    /// a few lines about themselves. The model structures and tailors it; on the
    /// fallback path it becomes the summary verbatim so nothing is lost.
    /// </param>
    /// <param name="targetRole">The role to tailor toward, e.g. "Data Analyst".</param>
    /// <param name="fullName">
    /// Optional identity from the HOST (the person's own name). Preferred over
    /// anything the model emits — identity is not a thing to let a model guess.
    /// Falls back to a best-effort read of the profile's first line, then "Candidate".
    /// </param>
    /// <param name="contact">
    /// Optional contact block from the HOST. Preferred over the model's, for the
    /// same reason — a CV must not carry a hallucinated phone number or email.
    /// </param>
    public async Task<DocumentResult> GenerateTailoredCvAsync(
        string rawProfile,
        string targetRole,
        string? fullName = null,
        CvContact? contact = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRole);

        CvDocument cv;
        try
        {
            var messages = new List<ChatMessage>
            {
                new("system", CvTailoringSystemPrompt),
                new("user", BuildCvTailoringPrompt(rawProfile, targetRole)),
            };

            // IncludeReasoning=false → JSON-strict: the model still THINKS, but the
            // reasoning is dropped so only the final answer (the JSON) reaches us.
            // Low temperature keeps it on the schema instead of embellishing.
            var options = new GenerationOptions
            {
                IncludeReasoning = false,
                Temperature      = 0.2f,
                TopP             = 0.9f,
                MaxTokens        = 1024,
            };

            var reply = await _brain.ChatAsync(messages, options, ct).ConfigureAwait(false);
            cv = ParseTailoredCv(reply, targetRole, fullName, contact)
                 ?? BuildFallbackCv(rawProfile, targetRole, fullName, contact);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The brain itself failed (no model resolved for this device, a native
            // OOM, a load error). A CV vertical that yields NOTHING on a bad turn is
            // worse than one that yields a plain CV from the user's own words — so
            // fall back rather than throw. Cancellation still propagates.
            cv = BuildFallbackCv(rawProfile, targetRole, fullName, contact);
        }

        // Render through the SAME offline engine Slice 1a proved on the device.
        var engine = new PdfSharpDocumentEngine();
        try
        {
            return await engine
                .RenderAsync(new DocumentRequest(DocumentKind.Cv, cv), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Belt and braces on the ALWAYS-a-PDF promise: if some model-supplied
            // value slipped past sanitisation and the layout engine rejected it,
            // re-render the deterministic fallback, whose fields are fully
            // controlled and known to render (it is what Slice 1a exercises).
            var safe = BuildFallbackCv(rawProfile, targetRole, fullName, contact);
            return await engine
                .RenderAsync(new DocumentRequest(DocumentKind.Cv, safe), ct)
                .ConfigureAwait(false);
        }
    }

    // The JSON contract handed to the model. Field names match CvDocument's
    // properties (matched case-insensitively on the way back in), so the reply
    // deserialises straight into the record. Deliberately terse — every token
    // spent here is a token off a 0.6B model's 4096 window.
    private const string CvTailoringSystemPrompt =
        "You are a professional CV writer. Output ONE JSON object and nothing else — no prose, " +
        "no markdown, no code fences. Use exactly these fields:\n" +
        "{\n" +
        "  \"fullName\": string,\n" +
        "  \"headline\": string,\n" +
        "  \"contact\": { \"email\": string, \"phone\": string, \"location\": string, \"links\": [string] },\n" +
        "  \"summary\": string,\n" +
        "  \"experience\": [ { \"title\": string, \"organisation\": string, \"location\": string, \"startDate\": string, \"endDate\": string, \"highlights\": [string] } ],\n" +
        "  \"education\": [ { \"qualification\": string, \"institution\": string, \"location\": string, \"startDate\": string, \"endDate\": string, \"notes\": string } ],\n" +
        "  \"skills\": [string],\n" +
        "  \"certifications\": [ { \"name\": string, \"issuer\": string, \"year\": string } ],\n" +
        "  \"languages\": [string]\n" +
        "}\n" +
        "Tailor \"summary\" and every \"highlights\" bullet to the TARGET ROLE: mirror the role's " +
        "keywords, start each bullet with an action verb, and quantify results where the source " +
        "supports it. Do NOT invent facts absent from the source. A null \"endDate\" means \"Present\".";

    private static string BuildCvTailoringPrompt(string rawProfile, string targetRole) =>
        $"TARGET ROLE:\n{targetRole.Trim()}\n\nSOURCE PROFILE:\n{rawProfile.Trim()}\n\n" +
        "Return the tailored CV as a single JSON object now.";

    private static readonly JsonSerializerOptions CvJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
    };

    /// <summary>
    /// Deserialises the model's reply into a render-ready <see cref="CvDocument"/>,
    /// or returns <c>null</c> when the reply can't be trusted — the signal for the
    /// deterministic fallback to take over. NEVER throws on bad model output:
    /// imperfect JSON from a small model is expected, not exceptional.
    /// </summary>
    private static CvDocument? ParseTailoredCv(
        string? modelReply, string targetRole, string? fullName, CvContact? contact)
    {
        var json = ExtractJsonObject(modelReply);
        if (json is null) return null;

        CvDocument? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<CvDocument>(json, CvJsonOptions);
        }
        catch (JsonException)
        {
            return null;   // unparseable — fall back to a deterministic CV
        }
        if (parsed is null) return null;

        // The model owns the words; it does NOT own the render invariants. Host
        // identity wins over model identity, and every collection is sanitised so a
        // ragged item — a null title, a missing highlight — cannot reach (and NRE)
        // the template, which only guards the top-level lists, not their contents.
        var name       = FirstNonBlank(fullName, parsed.FullName);
        var experience = SanitiseExperience(parsed.Experience);
        var education  = SanitiseEducation(parsed.Education);
        var skills     = SanitiseStrings(parsed.Skills);
        var languages  = SanitiseStrings(parsed.Languages);
        var summary    = string.IsNullOrWhiteSpace(parsed.Summary) ? null : parsed.Summary!.Trim();

        // Nothing usable came back (e.g. the model emitted "{}"): defer to the
        // deterministic fallback, which at least preserves the raw profile text.
        if (name is null && summary is null &&
            experience.Count == 0 && education.Count == 0 && skills.Count == 0)
            return null;

        return new CvDocument(
            FullName:       name ?? "Candidate",
            Headline:       FirstNonBlank(parsed.Headline, targetRole) ?? targetRole.Trim(),
            Contact:        contact ?? parsed.Contact ?? new CvContact(),
            Summary:        summary,
            Experience:     experience,
            Education:      education,
            Skills:         skills,
            Certifications: SanitiseCertifications(parsed.Certifications),
            Languages:      languages.Count > 0 ? languages : null);
    }

    /// <summary>
    /// The DETERMINISTIC fallback — no model. Guarantees a real PDF from the user's
    /// own words when the brain returns unparseable JSON, errors, or is absent.
    /// Career-ops still shows at the layout level: the target role becomes the
    /// headline and the raw profile becomes the summary, so nothing typed is lost.
    /// Built on <see cref="CvDocument.Minimal"/> — the schema's own fallback ctor.
    /// </summary>
    private static CvDocument BuildFallbackCv(
        string rawProfile, string targetRole, string? fullName, CvContact? contact)
    {
        var name    = FirstNonBlank(fullName, GuessNameFromProfile(rawProfile)) ?? "Candidate";
        var summary = rawProfile.Trim();
        return CvDocument.Minimal(name, targetRole.Trim(), contact ?? new CvContact())
            with { Summary = summary.Length == 0 ? null : summary };
    }

    /// <summary>
    /// Pulls the outermost <c>{ … }</c> span from a reply that may wrap the JSON in
    /// code fences or a stray sentence. A heuristic, not a validator — the
    /// deserialise step decides whether the span is real JSON.
    /// </summary>
    private static string? ExtractJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        var end   = text.LastIndexOf('}');
        return start >= 0 && end > start ? text.Substring(start, end - start + 1) : null;
    }

    /// <summary>
    /// Best-effort name read for the fallback: a pasted profile often opens with the
    /// person's name. Accepts a short, digit-free first line as the name; otherwise
    /// returns <c>null</c> and the caller defaults to "Candidate". Never fabricates —
    /// it only lifts text the user already wrote.
    /// </summary>
    private static string? GuessNameFromProfile(string rawProfile)
    {
        foreach (var raw in rawProfile.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var looksLikeName =
                line.Length <= 40 &&
                words.Length is >= 1 and <= 4 &&
                line.All(c => char.IsLetter(c) || c is ' ' or '-' or '\'' or '.');
            return looksLikeName ? line : null;   // first non-empty line decides
        }
        return null;
    }

    // ── model-output sanitisers — coalesce nulls / drop unrenderable items ──────

    /// <summary>First non-blank candidate, trimmed; <c>null</c> when all are blank.</summary>
    private static string? FirstNonBlank(params string?[] candidates)
    {
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c)) return c.Trim();
        return null;
    }

    private static IReadOnlyList<CvExperience> SanitiseExperience(IReadOnlyList<CvExperience>? items)
    {
        if (items is not { Count: > 0 }) return Array.Empty<CvExperience>();
        var list = new List<CvExperience>(items.Count);
        foreach (var e in items)
        {
            // A role with no title can't render meaningfully — drop it rather than
            // emit a headless bullet block.
            if (e is null || string.IsNullOrWhiteSpace(e.Title)) continue;
            list.Add(new CvExperience(
                Title:        e.Title.Trim(),
                Organisation: e.Organisation?.Trim() ?? string.Empty,
                Location:     e.Location,
                StartDate:    e.StartDate?.Trim() ?? string.Empty,
                EndDate:      e.EndDate,
                Highlights:   SanitiseStrings(e.Highlights)));
        }
        return list;
    }

    private static IReadOnlyList<CvEducation> SanitiseEducation(IReadOnlyList<CvEducation>? items)
    {
        if (items is not { Count: > 0 }) return Array.Empty<CvEducation>();
        var list = new List<CvEducation>(items.Count);
        foreach (var ed in items)
        {
            if (ed is null || string.IsNullOrWhiteSpace(ed.Qualification)) continue;
            list.Add(new CvEducation(
                Qualification: ed.Qualification.Trim(),
                Institution:   ed.Institution?.Trim() ?? string.Empty,
                Location:      ed.Location,
                StartDate:     ed.StartDate,
                EndDate:       ed.EndDate,
                Notes:         ed.Notes));
        }
        return list;
    }

    private static IReadOnlyList<CvCertification>? SanitiseCertifications(IReadOnlyList<CvCertification>? items)
    {
        if (items is not { Count: > 0 }) return null;
        var list = new List<CvCertification>(items.Count);
        foreach (var c in items)
            if (c is not null && !string.IsNullOrWhiteSpace(c.Name))
                list.Add(new CvCertification(c.Name.Trim(), c.Issuer, c.Year));
        return list.Count == 0 ? null : list;
    }

    private static IReadOnlyList<string> SanitiseStrings(IReadOnlyList<string>? items)
    {
        if (items is not { Count: > 0 }) return Array.Empty<string>();
        var list = new List<string>(items.Count);
        foreach (var s in items)
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
        return list;
    }

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
