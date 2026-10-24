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
using CircleAI.Core;
using CircleAI.SelfKnowledge;

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

    /// <summary>What this model is costing in memory, watched from before it loaded.</summary>
    /// <remarks>
    /// See the constructor: the floor is taken before the handle exists, the peak is
    /// sampled as answers are generated, and the result is written down on Dispose.
    /// Null only on the paths that never got that far.
    /// </remarks>
    private readonly MemoryWatch? _watch;

    /// <summary>Roughly how many tokens a conversation is carrying.</summary>
    /// <remarks>
    /// FOR THE MEASUREMENT, NOT FOR THE MODEL. Four characters a token, which is the
    /// usual approximation and is wrong in both directions depending on the language -
    /// and it does not matter here, because the only question being asked of it is
    /// whether one reading was taken at a longer context than another.
    /// </remarks>
    private static int RoughTokens(IReadOnlyList<ChatMessage> messages)
    {
        var chars = 0;
        for (var i = 0; i < messages.Count; i++) chars += messages[i].Content?.Length ?? 0;
        return chars / 4;
    }

    /// <summary>The persona MNN was given as its system prompt at load, or null.</summary>
    private readonly string? _mnnPersona;

    /// <summary>
    /// Tokens MNN's template adds around a system and a user turn plus the generation
    /// prompt - an ESTIMATE, used only so a prompt's reported length includes them.
    /// </summary>
    private const int TemplateOverheadTokens = 14;

    // WHAT MUST NEVER REACH A PERSON. A template marker or a thinking trace in the
    // answer means the prompt shape broke; it is stripped, and logged without text.
    private static readonly string[] LeakMarkers = { "<think>", "</think>", "<|im_start|>", "<|im_end|>", "Thinking Process:" };

    /// <summary>
    /// The content of the single user turn MNN is given: an instruction block when the
    /// caller's system message is not the persona MNN holds, the earlier turns as plain
    /// lines, then the question. Never a chat-template marker.
    /// </summary>
    internal static string MnnContent(IReadOnlyList<ChatMessage> messages, string? loadPersona)
    {
        ArgumentNullException.ThrowIfNull(messages);
        string? system = null;
        var turns = new List<ChatMessage>(messages.Count);
        foreach (var m in messages)
        {
            if (string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase)) system ??= m.Content;
            else turns.Add(m);
        }

        var lastUser = turns.FindLastIndex(m => !string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase));
        var sb = new StringBuilder();

        // A SYSTEM MESSAGE MNN DOES NOT ALREADY HOLD. The service passes the persona
        // at load and sends none per turn; anything else (another head, an old
        // caller) still has its instructions read - as plain words, never as a turn.
        if (!string.IsNullOrWhiteSpace(system) && !string.Equals(system, loadPersona, StringComparison.Ordinal))
            sb.Append(system!.Trim()).Append("\n\n");

        if (lastUser > 0)
        {
            sb.Append("Earlier in this conversation:\n");
            for (var i = 0; i < lastUser; i++)
            {
                var who = string.Equals(turns[i].Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "You" : "User";
                sb.Append(who).Append(": ").Append(Flatten(turns[i].Content)).Append('\n');
            }
            sb.Append('\n');
        }

        if (lastUser >= 0) sb.Append(turns[lastUser].Content ?? string.Empty);
        return sb.ToString();

        static string Flatten(string? text) => (text ?? string.Empty).Replace("\r", " ").Replace('\n', ' ').Trim();
    }

    /// <summary>
    /// Where the exact text handed to the bridge goes each turn, or null - the default - for
    /// nowhere. A test switch, not a log: a host sets it only when asked to show what the
    /// model really receives, because a person's questions are not diagnostics.
    /// </summary>
    public static Action<string>? PromptDump { get; set; }

    /// <summary>A JSON string literal (quotes, backslashes, control characters escaped).</summary>
    internal static string JsonEscape(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    private static string Sha8(string text)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    private static string StripLeaks(string text, ref bool leaked)
    {
        foreach (var marker in LeakMarkers)
        {
            if (text.IndexOf(marker, StringComparison.Ordinal) < 0) continue;
            if (!leaked) Console.WriteLine("CIRCLEAI-LEAK kind=" + marker.Trim('<', '>', '|', ':', '/').Replace(' ', '-'));
            leaked = true;
            text = text.Replace(marker, string.Empty, StringComparison.Ordinal);
        }
        return text;
    }

    private static GenerationOptions CopyWithoutContinuation(GenerationOptions o) => new()
    {
        MaxTokens            = o.MaxTokens,
        Temperature          = o.Temperature,
        TopP                 = o.TopP,
        TopK                 = o.TopK,
        Seed                 = o.Seed,
        StopSequences        = o.StopSequences,
        IncludeReasoning     = o.IncludeReasoning,
        Budget               = o.Budget,
        RetrievalQuery       = o.RetrievalQuery,
        Timing               = o.Timing,
        UsePrefixCache       = false,
        ContinueConversation = false,
    };

    private unsafe int CountTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var buffer = new int[Math.Clamp(text.Length + 16, 64, 32768)];
        fixed (int* p = buffer)
        {
            try { return Math.Max(0, MnnInterop.mnn_llm_tokenize(_model, text, p, buffer.Length)); }
            catch { return -1; }
        }
    }

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
    /// <summary>Whether the memory-mapped weight and KV-cache paths are used.</summary>
    /// <remarks>
    /// A SWITCH BECAUSE A CRASH IS BEING BISECTED, and the honest way to find a
    /// cause is to be able to turn the suspect off. Both mmap paths were wired
    /// recently; since then a typed turn reliably kills the process with
    ///
    ///     SIGSEGV, fault addr 0x0, in MNN::ThreadPool::enqueue
    ///
    /// on the FIRST generation after launch, with well over a gigabyte free.
    /// Turning both off answers "is it the mapping" in one build rather than
    /// three.
    ///
    /// ⚠ TEMPORARILY DEFAULTED OFF while the crash is bisected. Set
    /// CIRCLEAI_MNN_MMAP=1 to turn the mapping back on. If mmap turns out not to
    /// be the cause this default goes back to on, and if it IS the cause the
    /// default stays off until the mapping itself is fixed - a process that dies
    /// mid-answer is worse than a slow first token.
    /// </remarks>
    /// <summary>
    /// Whether MNN may memory-map weights. <b>On by default.</b>
    /// </summary>
    /// <remarks>
    /// A SETTING, NOT JUST AN ENVIRONMENT VARIABLE, AND THAT WAS THE REAL
    /// PROBLEM. This read <c>CIRCLEAI_MNN_MMAP</c> and nothing else - and an
    /// Android app cannot set an environment variable for itself. So on the one
    /// platform this product ships to, the flag was not "off by default": it was
    /// off with no way on, and no way to test either branch on the device where
    /// the fault was found.
    /// <para>
    /// IT WAS OFF, AND THE CONDITION FOR TURNING IT BACK ON HAS BEEN MET. The
    /// earlier note said off "until the mapping itself is fixed", blaming
    /// <c>use_mmap</c> and <c>kvcache_mmap</c> together for a SIGSEGV in
    /// <c>MNN::ThreadPool::enqueue</c>. The cause was later isolated to
    /// <c>kvcache_mmap</c> alone: it built a malformed prefix-cache path
    /// (<c>prefixcache//data/user/0/…</c>), failed to create it, and left a destroyed
    /// mutex behind. Weight mmap was never the fault. <c>kvcache_mmap</c> is now
    /// never enabled at all - see the load path - so this flag no longer controls it,
    /// and the summary line above no longer mentions it.
    /// </para>
    /// <para>
    /// WHAT IT BUYS, measured rather than argued: Qwen2.5-3B, 2.4 GB of weights, loads
    /// and generates on a 3.6 GB P30 Lite at roughly 800 MB resident under exactly the
    /// configuration this enables - weight mmap plus a single thread, prefix cache in
    /// RAM. With it off, the reference phone is held to a 0.8B chat model and a 256M
    /// vision model while a 3B of each is within reach.
    /// </para>
    /// <para>
    /// ⚠ TURNING THIS ON ALONE WOULD HAVE BEEN WORSE THAN LEAVING IT OFF, and that is
    /// worth stating where somebody will find it. <see cref="WeightsExceedEagerFit"/>
    /// decided mapping from weight-versus-8-GB and knew nothing about the device,
    /// while <c>ModelFit</c> marks a model compatible on the ASSUMPTION it will be
    /// paged. A 2.37 GB model on a phone with ~1.19 GB usable was therefore promised
    /// as paged and loaded eagerly - the selector says yes, the loader OOMs. That gap
    /// is closed in the same change as this default; neither is safe without the
    /// other.
    /// </para>
    /// <para>
    /// The environment variable still wins, so <c>CIRCLEAI_MNN_MMAP=0</c> turns it off
    /// without a rebuild if a device disagrees.
    /// </para>
    /// </remarks>
    public static bool AllowMemoryMapping { get; set; } = true;

    /// <summary>Which MNN backend to run on, or null to leave the model's own choice.</summary>
    /// <remarks>
    /// OFF BY DEFAULT AND THAT IS DELIBERATE. libMNN_CL.so and libMNN_Vulkan.so
    /// ship in the APK - 3 MB of GPU backend that nothing has ever asked for - and
    /// every model has run on the CPU. Prefill is one large compute-bound matrix
    /// multiply and is exactly the part a GPU would help; decode is memory-bound
    /// and one token wide, where it would not.
    ///
    /// NO AUTOMATIC FALLBACK, ON PURPOSE. A driver that refuses leaves the model
    /// unable to load, and silently retrying on the CPU would hide exactly the
    /// result this is here to measure. It stays null until a number says
    /// otherwise, and whoever sets it owns the deploy that unsets it.
    /// </remarks>
    public static string? Backend { get; set; }

    /// <summary>Let the KV cache live on disk, which is what the prefix cache needs.</summary>
    /// <remarks>
    /// OFF BY DEFAULT BECAUSE IT USED TO SIGSEGV THIS PHONE. MNN built a malformed
    /// path - "prefixcache/" prepended to an ABSOLUTE session path - failed to
    /// create the directory, and left a destroyed mutex that faulted the threadpool
    /// on the next lock. That was the whole of the "mmap curse", and it is why the
    /// prefix cache has been dead since 2026-09-22.
    ///
    /// WHAT CHANGED: NativeCacheDirectory now puts the process in a directory it can
    /// write, so the mkdir MNN does there can succeed. That is a reason to re-test
    /// it, not a reason to assume it. Whoever turns this on watches for
    /// "Failed to create prefix cache file dir" and for the threadpool fault.
    /// </remarks>
    public static bool AllowKvCacheMmap { get; set; }



    /// <summary>
    /// The environment override, or <c>null</c> when it is not set.
    /// </summary>
    /// <remarks>
    /// Read on every check rather than cached: a test that sets the variable and
    /// a process that inherits it must both be honoured, and caching a static on
    /// first touch makes the result depend on which test ran first.
    /// </remarks>
    private static bool? MmapOverride => Environment.GetEnvironmentVariable("CIRCLEAI_MNN_MMAP") switch
    {
        "1" => true,
        "0" => false,
        _   => null,
    };

    /// <summary>Whether memory mapping is on for this process, right now.</summary>
    internal static bool MmapIsAllowed => MmapOverride ?? AllowMemoryMapping;

    /// <summary>
    /// EXPERIMENT HOOK: force mmap on for EVERY model, not just the giants, so a
    /// model that fits eagerly can be run through the mmap+single-thread path on a
    /// real phone to prove that path is stable. Off by default; a host sets it only
    /// to run that on-device test. Still gated by <see cref="MmapIsAllowed"/>.
    /// </summary>
    public static bool ForceMmap { get; set; }

    /// <summary>
    /// What the SELECTOR decided about mapping this model, given the model's
    /// directory: true for paged, false for eager, null for no opinion.
    /// </summary>
    /// <remarks>
    /// THE LOADER AND THE SELECTOR DECIDED SEPARATELY, FROM DIFFERENT INFORMATION, AND
    /// COULD DISAGREE. This loader has files; ModelFit has a catalogue entry carrying
    /// MinRamGb and a measured resident figure. A model the selector marked compatible
    /// ON THE ASSUMPTION it would be paged, loaded eagerly here, is the OOM this
    /// file's own remarks describe - and the opposite, the P30 mapping a model the fit
    /// rule believed was loading eagerly, is the only reason that phone has a brain.
    /// <para>
    /// Set by a host that has the catalogue. Left null, the loader behaves exactly as
    /// it did: its own file-size reading decides.
    /// </para>
    /// </remarks>
    public static Func<string, bool?>? PromisedMapping { get; set; }

    /// <summary>
    /// Weights bigger than any phone's RAM can hold need mmap; everything else
    /// loads eagerly. 8 GB sits above the RAM of the phones this ships to, so only a
    /// genuinely huge model (a 30B-class MoE) trips it.
    /// </summary>
    internal const long MmapWeightThresholdBytes = 8L * 1000 * 1000 * 1000;

    /// <summary>
    /// Whether a model's on-disk weights are too large to load eagerly, so mmap is
    /// the only way in. PER-MODEL, because mmap'd inference has crashed MNN's
    /// threadpool (a native SIGSEGV in generate) on models that fit in RAM and never
    /// needed mapping — so mmap must engage only where a model genuinely requires it.
    /// Sums the model directory; unknown size falls through to eager, the stable
    /// default.
    /// </summary>
    internal static bool WeightsExceedEagerFit(string modelPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(modelPath);
            if (string.IsNullOrEmpty(dir)) return false;

            // THE DEVICE'S ACTUAL FREE MEMORY, not only a fixed threshold. These two
            // terms answer different questions and both are needed:
            //
            //   > 8 GB          no phone or tablet holds this eagerly, whatever is free
            //   > usable RAM    THIS device cannot hold it right now
            //
            // The second was missing, and its absence is what made turning mmap on
            // unsafe rather than merely unhelpful. ModelFit -- the single owner of
            // "does it fit", which the selectors and the assessor all read -- marks a
            // model compatible ON THE ASSUMPTION it will be paged when it does not fit
            // eagerly. This loader decided by weight-vs-8-GB alone, so a 2.37 GB model
            // on a phone with ~1.19 GB usable was promised as paged and then loaded
            // eagerly: the assessor says yes, the loader OOMs. Qwen2.5-3B is exactly
            // that case, and it is the model the mmap work was proven on.
            //
            // Weights-only here, deliberately: the loader has files, not a catalogue
            // entry, so it cannot see MinRamGb. That makes it slightly more willing to
            // load eagerly than ModelFit is to promise paging, which is the safe
            // direction -- a model whose RAM need is runtime structures rather than
            // weights is not helped by mapping them, and the assessor refuses it
            // anyway.
            var usableBytes = UsableRamBytesOrZero();

            long bytes = 0;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                try { bytes += new FileInfo(f).Length; } catch { }
                if (bytes > MmapWeightThresholdBytes) return true;            // big on any device
                if (usableBytes > 0 && bytes > usableBytes) return true;      // big on THIS device
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// Free RAM this process may commit to weights, or <c>0</c> when it cannot be read.
    /// </summary>
    /// <remarks>
    /// Zero means "unknown", and unknown must fall back to the fixed threshold rather
    /// than to zero-means-everything-is-too-big — reading a failure as "map everything"
    /// would turn one unreadable probe into mmap for every model on every device.
    /// </remarks>
    private static long UsableRamBytesOrZero()
    {
        try
        {
            var probe = DeviceProbe.Snapshot();
            var usable = probe.UsableRamGb;
            return usable > 0 ? (long)(usable * DeviceProbe.BytesPerGb) : 0;
        }
        catch { return 0; }
    }

    /// <param name="modelPath">Absolute path to the model file.</param>
    /// <param name="contextSize">Maximum context window in tokens.</param>
    /// <param name="threads">CPU thread count (<c>null</c> for MNN default).</param>
    /// <param name="templateEngine">Unused for MNN rendering; kept for source compatibility.</param>
    /// <param name="systemPrompt">
    /// The persona, given to MNN as its own system prompt before load. MNN's template is
    /// then the ONE owner of the chat format; see <see cref="MnnContent"/>.
    /// </param>
    /// <param name="samplerJson">MNN sampler config from the model's GenerationProfile, applied before load.</param>
    /// <summary>
    /// A TEST SWITCH: the MNN sampler config to load with instead of the model's own -
    /// {"sampler_type":"greedy"} makes an answer a function of its input alone, which is
    /// how a phone shows whether anything but the input reaches the model. Null in use.
    /// </summary>
    public static string? SamplerJsonForTest { get; set; }

    public QwenTextGenerator(
        string                   modelPath,
        uint                     contextSize,
        int?                     threads,
        IPromptTemplateEngine?   templateEngine,
        string?                  systemPrompt = null,
        string?                  samplerJson  = null)
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
        // SOMEWHERE FOR MNN TO PUT ITS CACHES, BEFORE IT GOES LOOKING FOR THEM.
        //
        // MNN resolves "./mnn_cachefile.bin" and "prefixcache/..." against the
        // process working directory, which on Android is "/" and unwritable - so
        // the OpenCL kernel cache has never loaded and the prefix cache has never
        // been creatable. Beside the model, which is writable by definition: the
        // bundle was downloaded into it.
        //
        // Before create, not before load: MNN reads the OpenCL cache while building
        // the backend, which happens inside create on some paths.
        NativeCacheDirectory.Use(modelPath);

        // AND START WATCHING, BEFORE A SINGLE BYTE OF THE MODEL IS RESIDENT.
        //
        // ModelEntry.MmapResidentGb carries its own instructions - "run the model on
        // the target device, watch resident memory during generation, and record what
        // you saw. Do not compute it." - and not one entry in the catalogue has one,
        // because nothing was ever watching. That empty field is what makes
        // ModelFit.WillMmap false for every model, which is what makes the fit rule
        // charge the whole file, which is what limits a phone with 29 GB of storage to
        // the models that fit in its 3.9 GB of memory.
        //
        // THE FLOOR HAS TO BE TAKEN HERE. One line later the handle exists and the
        // weights are arriving; a floor measured then includes them, and the model
        // measures as free - the most dangerous number this rule could be handed.
        _watch = new MemoryWatch(
            System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(modelPath) ?? modelPath) ?? "");

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

            // PER-MODEL, NOT A GLOBAL SWITCH. mmap'd inference has crashed MNN's
            // threadpool (SIGSEGV in generate) on a model that fits in RAM and never
            // needed mapping. So mmap engages ONLY for weights too large to load
            // eagerly — a 30B-class MoE — where it is the sole option; a model that
            // fits loads eagerly, the stable path. "Everything on" holds: mmap is on
            // and available, engaged where a model needs it, not where it does not.
            //
            // AND THE LOADER AND THE SELECTOR MAY NOT DISAGREE ABOUT IT, which until
            // now they could, because they were deciding separately from different
            // information. This line reads FILE SIZES on disk; ModelFit marks a model
            // compatible from a CATALOGUE ENTRY, which carries MinRamGb and a measured
            // resident figure the loader cannot see. The two have already produced the
            // failure in both directions:
            //
            //   selector says paged, loader loads eagerly  ->  the OOM this file's own
            //     remark describes, Qwen2.5-3B on a phone with ~1.19 GB usable;
            //   selector says eager, loader maps anyway    ->  the P30 today, where a
            //     0.55 GB model against 0.44 GB usable is mapped silently, which is the
            //     only reason that phone has a brain while the fit rule assumes it does
            //     not need one.
            //
            // The second is working in our favour by accident, and an accident that
            // nobody has written down is a thing that gets "fixed" later by somebody
            // tidying up.
            //
            // EITHER PARTY ASKING FOR MAPPING GETS IT. Mapping a model that would have
            // fitted eagerly costs speed; loading eagerly one that needed mapping costs
            // the process. The asymmetry is the whole rule, and the disagreement is
            // said out loud rather than resolved silently.
            var heuristic = WeightsExceedEagerFit(modelPath);
            var promised  = PromisedMapping?.Invoke(modelPath);

            if (promised is { } p && p != heuristic)
                Console.WriteLine(
                    $"CIRCLEAI-MMAP disagreement model={Path.GetFileName(Path.GetDirectoryName(modelPath) ?? modelPath)} " +
                    $"selector={(p ? "paged" : "eager")} loader={(heuristic ? "paged" : "eager")} resolved=paged");

            // AND MAPPING COMES OFF IF IT TRIPPED HERE — BUT ONLY WHERE IT WAS A CHOICE.
            // SelfHeal switches off what broke, and the honest limit of that is right
            // here: when the weights are too big to load eagerly, or the selector only
            // admitted this model BECAUSE it would be paged, withdrawing mapping heals
            // nothing — it trades a SIGSEGV for the OOM this file's own remarks describe.
            // There it is the MODEL that cannot run on this device, not the mapping, and
            // the refusal recorded against it in SelfModel is what takes it out of the
            // selector's hands. A switch that turns a crash into a different crash is
            // worse than no switch, because it also hides which one you are looking at.
            var mappingIsRequired = heuristic || promised == true;
            var experimentHeldOff = SelfHeal.IsOff(SelfHeal.Switch.MemoryMapping);

            var useMmap = MmapIsAllowed
                       && (mappingIsRequired || (ForceMmap && !experimentHeldOff));
            if (useMmap)
            {
                // WEIGHT mmap only — this is what lets a 2.4 GB model fit in ~0.9 GB
                // free. Pairs with single thread; both were proven necessary and
                // safe on a P30.
                mmap.Enable();
                new MnnRuntimeConfig(handle.DangerousGetHandle()).TrySetThreads(1);
            }

            // KV-CACHE mmap: DELIBERATELY OFF. Measured on a P30 loading Qwen2.5-3B
            // on 2026-09-22 — enabling kvcache_mmap made MNN build a malformed prefix
            // cache path ("prefixcache/" prepended to an absolute path →
            // "prefixcache//data/user/0/…"), which it cannot create:
            //     MNNJNI: Failed to create prefix cache file dir: prefixcache
            //     MNNJNI: Failed to memory-map the kvcache!
            //     libc:   FORTIFY: pthread_mutex_lock called on a destroyed mutex
            //   → SIGSEGV in MNN::ThreadPool::enqueue during Session::run.
            // THIS was the real cause of the whole "mmap crash", not the weight mmap
            // or the thread count. The KV cache stays in RAM (small for our context),
            // and the model loads. Re-enable only once the native path bug is fixed
            // and it can be pointed at a directory MNN will actually create.

            // AND IT CAN BE ASKED FOR AGAIN NOW THAT THE DIRECTORY EXISTS.
            // NativeCacheDirectory has put the process somewhere writable, so the
            // mkdir that failed - and left the destroyed mutex behind - has
            // somewhere to land. Off unless somebody turns it on deliberately.
            // THIS is the one healing can genuinely switch off, because it is the one
            // that is a pure choice: off by default, on only because somebody turned it
            // on, and the documented cause of the SIGSEGV above. Nothing is lost by
            // withdrawing it except the speed it was turned on for.
            if (AllowKvCacheMmap && !SelfHeal.IsOff(SelfHeal.Switch.PrefixCache))
            {
                var took = new MnnRuntimeConfig(handle.DangerousGetHandle()).TryEnableKvCacheMmap();
                Console.WriteLine($"CIRCLEAI-KVMMAP asked=true accepted={took}");
            }
        }
        catch { /* older bridge or unmappable store — eager load is still correct */ }

        // WHERE IT RUNS, when somebody has asked for something other than the CPU.
        // Before load, because MNN resolves the backend while loading - the config
        // line the bridge prints afterwards is the proof it took.
        // AND IF IT WILL NOT TAKE, WE STOP ASKING FOR A WHILE. Until now a backend that
        // was refused printed one line and was asked for again on every load for the
        // life of the install. That is the shape of every fault a phone has that an API
        // does not: nothing throws, nothing is reported, the person just gets a slower
        // assistant for ever - and on a Kirin the OpenCL path this repository measured
        // is 22x SLOWER than the CPU it falls back to, so the asking is the whole cost.
        //
        // This is the smallest honest piece of healing in the product, and it is the
        // one Wolverine's own SWITCH_TO_FALLBACK action describes: what tripped goes
        // off, it is written down where the next launch will read it, and the idle
        // review tries it again later in case a driver update fixed it.
        if (!string.IsNullOrWhiteSpace(Backend) && !SelfHeal.IsOff(SelfHeal.Switch.GpuBackend))
        {
            var took = false;
            string? refused = null;
            try { took = new MnnRuntimeConfig(handle.DangerousGetHandle()).TrySetBackend(Backend!); }
            catch (Exception ex) { refused = ex.Message; /* older bridge — CPU still loads */ }

            Console.WriteLine($"CIRCLEAI-BACKEND asked={Backend} accepted={took}");

            if (took)
                // THE BREAKER CLOSING. It was tried again and it worked, so the history
                // goes with it - otherwise the next single refusal, a year later, would
                // start at a week because of one bad afternoon in October.
                SelfHeal.Worked(SelfHeal.Switch.GpuBackend);
            else
                SelfHeal.Trip(SelfHeal.Switch.GpuBackend,
                    $"{Backend} would not take here{(refused is null ? "" : ": " + refused)}");
        }


        // NO THINKING OUT LOUD. The bundles ship enable_thinking:true in their own
        // jinja context, so a reasoning model deliberates in front of the person
        // and — because MNN emits that as prose, not <think> tags — the reasoning
        // router never sees it. On a phone that decodes about seven tokens a
        // second this is not a quality setting: measured on the P30, one factual
        // question spent its entire 160-token budget arguing with the system
        // prompt and never answered. Before load, like every other config read.
        try { new MnnRuntimeConfig(handle.DangerousGetHandle()).TryDisableThinking(); }
        catch { /* bundle without a jinja context — a thinking model still works */ }

        // ONE OWNER OF THE CHAT FORMAT, AND IT IS MNN.
        //
        // This class used to render full ChatML and hand it to
        // mnn_llm_generate_stream_text - and the bridge runs apply_chat_template on
        // whatever it is given (mnnbridge.cpp, generate_stream_text). So the persona,
        // the question and an empty, already-closed assistant turn all became the
        // CONTENT of one user turn, wrapped again in MNN's own template. That is the
        // prompt the recorded failures were answered from: "IT! >", the stray
        // "</think>", the memory heading read back as if it were the question.
        //
        // BUT THE PERSONA RIDES IN EVERY TURN: MNN DOES NOT HOLD IT. It used to be set
        // here as MNN's system_prompt, and the bridge said "accepted". Measured on the
        // P30 on 2026-10-20: "What is your name?" was answered "I am Qwen3.5. What is
        // your name?" and "Are you a person?" with "No, I am Qwen3.5." The bridge renders
        // each turn with apply_chat_template(user_content) - the single-string overload,
        // which builds the template from the user turn alone - so the value was stored and
        // never read, and every answer until then was made without the persona: its name,
        // "one or two short sentences", "ask what they meant", "say so instead of guessing".
        // MnnContent now puts the persona's words ahead of the question in the one user
        // turn. A true system turn needs a bridge export that takes roles - a native rebuild.
        _mnnPersona = null;
        Console.WriteLine($"CIRCLEAI-TEMPLATE owner=mnn persona=" +
                          (string.IsNullOrWhiteSpace(systemPrompt) ? "none" : Sha8(systemPrompt!) + " in-turn") + " thinking=off");
        if (SamplerJsonForTest is { Length: > 0 } forTest) samplerJson = forTest;
        if (!string.IsNullOrWhiteSpace(samplerJson))
        {
            var ok = MnnInteropRt.mnn_llm_set_config(handle.DangerousGetHandle(), samplerJson!) == 0;
            Console.WriteLine($"CIRCLEAI-SAMPLER {samplerJson} accepted={ok}");
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

        // WHAT IT COSTS IS MEASURED WHILE IT WORKS, not when it finishes loading.
        //
        // Weights are resident the moment they load; the conversation's cache grows
        // through generation, and the PEAK is what has to fit. A reading taken at the
        // wrong moment is how a model gets called affordable and then OOMs mid-answer
        // - which is the failure the 15% headroom was invented to paper over, and
        // papering over it is why nobody ever measured the real number.
        //
        // The context is carried into the reading so that two answers of different
        // lengths give a cache SLOPE rather than two constants (ModelCosts.For).
        //
        // ESTIMATED FROM CHARACTERS, NOT TOKENISED, AND THAT IS ENOUGH. CountTokens is
        // a native call and the exact figure is not what this is for: the slope needs
        // to tell a short conversation from a long one, and four characters a token is
        // close enough across the languages this ships in. Tokenising every turn to
        // refine a diagnostic would be paying the model's price for the measurement.
        _watch?.Sample(RoughTokens(messages));

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

        // ONE USER TURN: THE PERSONA'S WORDS, THEN THE QUESTION. The bridge wraps whatever
        // it is given in the model's own chat template, so rendering ChatML here wrapped
        // the template in itself - and MNN holds no system turn of ours (see the
        // constructor). History rides inside the content as plain lines.
        var fullPrompt = MnnContent(messages, _mnnPersona);

        // NO PREFIX CACHE AND NO CONTINUATION FOR CONTENT-ONLY TURNS. Each turn is a
        // fresh templated conversation, so "the KV already holds the earlier turns"
        // is never true here; and a restored prefix on Qwen3.5's linear attention
        // would be full-attention KV without the linear state - a silent wrong state.
        prefixCacheKey = null;
        options = options.ContinueConversation ? CopyWithoutContinuation(options) : options;

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
        var timing = options.Timing;
        long nativeStart = 0;

        var work = Task.Run(async () =>
        {
            // Per-handle serialization (mnnbridge.h:22-24 — concurrent calls
            // on the same handle are undefined behaviour). Await with the
            // caller's cancellation so a queued request can be cancelled
            // before it starts running.
            await _generationLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // HOW LONG THE PROMPT REALLY IS, counted by the model's own tokenizer
                // under the same lock - the persona MNN holds, the content, and the
                // template's own tokens (estimated). Milliseconds, and it is what turns
                // "prefill took 26 s" into a rate somebody can act on.
                if (timing is not null)
                {
                    var persona = CountTokens(_mnnPersona);
                    var content = CountTokens(prompt);
                    timing.PromptTokens = persona < 0 || content < 0 ? -1 : persona + content + TemplateOverheadTokens;
                    timing.KvPath = continuing ? "continued" : "reset";
                }

                // WHAT THE MODEL IS HANDED, VERBATIM, when a host has asked to see it: the
                // bridge wraps exactly this in the model's chat template and nothing else.
                if (PromptDump is { } dump)
                {
                    try { dump($"--- {DateTimeOffset.UtcNow:O} content-tokens={CountTokens(prompt)} max-new={resolvedBudget.MaxTokens}\n{prompt}\n"); }
                    catch { /* a diagnostic never costs the answer */ }
                }

                Interlocked.Exchange(ref nativeStart, System.Diagnostics.Stopwatch.GetTimestamp());
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

        long firstAt = 0;
        var pieces = 0;
        var leaked = false;
        var finish = "eos";
        try
        {
            await foreach (var f in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var fragment = f;
                if (fragment.Kind == ChatFragmentKind.Content && !string.IsNullOrEmpty(fragment.Text))
                {
                    if (firstAt == 0) firstAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    pieces++;
                    var cleaned = StripLeaks(fragment.Text, ref leaked);
                    if (cleaned.Length != fragment.Text.Length) fragment = fragment with { Text = cleaned };
                }
                yield return fragment;
            }

            await work.ConfigureAwait(false);
            if (pieces >= resolvedBudget.MaxTokens - 1) finish = "length";
        }
        finally
        {
            if (timing is not null)
            {
                var started = Interlocked.Read(ref nativeStart);
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (ct.IsCancellationRequested) finish = "cancel";
                if (started > 0 && firstAt > 0)
                {
                    timing.PrefillMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started, firstAt).TotalMilliseconds;
                    timing.DecodeMs  = (long)System.Diagnostics.Stopwatch.GetElapsedTime(firstAt, now).TotalMilliseconds;
                }
                timing.OutputTokens = pieces;
                timing.Finish ??= finish;
            }
        }
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

        // WRITE DOWN WHAT IT COST, BEFORE THE WEIGHTS GO.
        //
        // This is the measurement MmapResidentGb has been documented as needing since
        // the field was written, and which nothing has ever taken. Recorded on the way
        // out, when the peak is known and the model has done whatever it was going to
        // do. A model that loaded and was never asked anything records its load cost,
        // which is still the floor under any answer it would have given.
        //
        // Never fatal: Record swallows its own failures, and a null cost - the floor
        // unreadable, or the peak never above it - is not written at all, because a
        // recorded cost of nothing would tell the fit rule this model is free.
        // THE LONG-TERM MEMORY WHERE THERE IS ONE, THE LOCAL FILE WHERE THERE IS NOT -
        // and never both, because one fact with two owners is this repository's most
        // repeated and most expensive defect. SelfModel is the better home: it
        // supersedes the previous reading rather than appending, it survives a
        // reinstall with the rest of the memory, and it is what the selector reads so
        // that "best" means best MEASURED HERE rather than best ranked at release.
        // ModelCosts is what a desktop, a test, or a head with no memory service gets.
        try
        {
            _watch?.Sample();
            if (_watch?.Cost() is { } cost)
            {
                if (CircleAI.SelfKnowledge.SelfModel.Current is { } self) self.LearnCost(cost);
                else ModelCosts.Record(cost);
            }
        }
        catch { /* a measurement may never be the reason a model fails to close */ }

        // NOT UNDER A RUNNING ANSWER. Freeing the native handle while MNN is still
        // decoding on another thread is a use-after-free in native code; wait for the
        // generation to let go (MNN cannot be interrupted mid-decode), bounded so a
        // stuck one cannot hold the process up for ever.
        var held = false;
        try { held = _generationLock.Wait(TimeSpan.FromSeconds(10)); }
        catch (ObjectDisposedException) { }

        _model.Dispose();
        if (held) { try { _generationLock.Release(); } catch { } }
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
        bool writingCache = false;
        // ASKED OF THE SERVICE, NOT OF THE PATH. PathFor returns the RELATIVE name
        // MNN is handed; what lands on disk is prefixcache/<key>.session_0.k and its
        // per-layer siblings, so File.Exists on that name was never going to be true
        // once the path went relative - a cache that could fill and never be read.
        if (!continuing && prefixCacheKey is not null && _prefixCache.HasEntry(prefixCacheKey))
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

        // REGISTERED BEFORE THE GENERATION IT IS MEANT TO CAPTURE, not after.
        //
        // The native call behind SaveSession is MNN's setPrefixCacheFile(path,
        // write) — it nominates the file the prefix is written to, it does not
        // write one. Called after generating, as it was, it nominated a file for
        // a prefill that had already happened and been discarded, so the first
        // conversation to use a system prompt never left anything behind and the
        // next one missed again. The cache could not fill, so it could not hit.
        //
        // After the reset above, deliberately: the reset clears the KV, and
        // nominating the file first would offer up an empty one.
        if (!continuing && !loadedFromCache && prefixCacheKey is not null)
        {
            try { writingCache = MnnInterop.SaveSession(_model, _prefixCache.PathFor(prefixCacheKey)); }
            catch { /* best-effort cache write */ }
        }

        // WHICH OF THE THREE PATHS THIS TURN TOOK. Prefill was measured at
        // 6 418 ms on a first question and 700-800 ms on later ones in the same
        // conversation, and from outside there was no way to tell a cache that
        // missed from a cache that was never consulted. It was the second: the
        // option gating all of this defaults to false and nothing in the tree
        // ever set it, so every first turn paid a full prefill by default.
        Console.WriteLine(
            $"CIRCLEAI-KV {(continuing ? "continuing" : loadedFromCache ? "prefix-hit" : "prefix-miss")}" +
            $" cache={(prefixCacheKey is null ? "off" : writingCache ? "writing" : "on")}" +
            $" feeding={prompt.Length} chars");

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

            // RT-06: the snapshot itself was written by the native side during
            // the generation above, because the file was nominated before it
            // rather than after. What is left here is housekeeping — keeping the
            // cache directory inside its size budget now that it has grown.
            if (writingCache)
            {
                try { _ = _prefixCache.EvictIfNeededAsync(ct); }
                catch { /* best-effort cache maintenance */ }
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
