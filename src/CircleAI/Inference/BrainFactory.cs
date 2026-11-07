// BrainFactory.cs
//
// ONE PLACE THAT TURNS A MODEL FILE INTO A BRAIN.
//
// Six places built a brain and three of them asked which engine the model needs - through
// AIService.BuildByEngine - while the other three wrote `new QwenTextGenerator` and so handed
// every model to MNN: the in-process session, AddCircleAI's two registrations and the
// inference server. A Bonsai 2 GGUF reaching any of those would have been opened as an MNN
// bundle and failed far from the cause. Every site now asks here, and the engine is decided
// the same way everywhere: the host's override, then the catalogue row, then the file itself.
//
// THE SITES STILL DIFFER IN WHAT THEY PASS, AND THAT IS KEPT. The service's path gives MNN the
// persona and the sampler at load; AddCircleAI's and the session's give it a chat-template
// engine. Those are each site's own settings, carried on the request, not a second rule.

using System;
using CircleAI.Core;
using CircleAI.Core.Models;

namespace CircleAI.Inference;

/// <summary>What to build a brain from.</summary>
/// <param name="ModelPath">The file the loader resolved: a GGUF, or an MNN bundle's config.</param>
public sealed record BrainRequest(string ModelPath)
{
    /// <summary>The catalogue id, when the site knows it.</summary>
    public string? ModelId { get; init; }

    /// <summary>The catalogue row, when the site has it: its engine decides.</summary>
    public ModelEntry? Entry { get; init; }

    /// <summary>An engine the host chose deliberately; it wins over the row and the file.</summary>
    public ModelEngine? Engine { get; init; }

    /// <summary>The context window, in tokens.</summary>
    public int ContextSize { get; init; } = 4096;

    /// <summary>Threads the host asked for; null lets the engine and the device decide.</summary>
    public int? Threads { get; init; }

    /// <summary>
    /// The working memory's bits for the C# engine; null builds it at 4 bits. The balance loop
    /// asks for fewer under pressure (Step 4); MNN's working memory stays as A11 left it.
    /// </summary>
    public KvCompressionMode? KvMode { get; init; }

    /// <summary>What the brain must do - Vision picks MNN's vision runtime.</summary>
    public ChatCapability Capability { get; init; } = ChatCapability.Default;

    /// <summary>MNN's persona, which it reads only at load.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>MNN's sampler, which it reads only at load.</summary>
    public string? SamplerJson { get; init; }

    /// <summary>The chat-template renderer a site gives MNN, when it gives one.</summary>
    public IPromptTemplateEngine? TemplateEngine { get; init; }
}

/// <summary>Builds the brain that can open a model, by its engine.</summary>
public interface IBrainFactory
{
    /// <summary>The generator that opens <paramref name="request"/>'s model.</summary>
    IChatGenerator Build(BrainRequest request);
}

/// <summary>The one <see cref="IBrainFactory"/>: engine-aware, used by every site that builds a brain.</summary>
public sealed class BrainFactory : IBrainFactory
{
    /// <summary>The shared instance.</summary>
    public static BrainFactory Default { get; } = new();

    /// <inheritdoc />
    public IChatGenerator Build(BrainRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelPath);

        // THE ROW'S ENGINE UNLESS THE HOST CHOSE ONE, and the file's when there is no row. The
        // choice is checked as if the row had named it.
        var entry = request.Entry;
        var engine = request.Engine ?? entry?.Engine ?? EngineOf(request.ModelPath);
        if (entry is not null && engine != entry.Engine) entry = entry with { Engine = engine };

        // WHAT WOLVERINE HAS TURNED DOWN ON THIS DEVICE (S6), read where every brain is built: fewer
        // threads, a shorter context, fewer working-memory bits, a thinking budget, scalar kernels.
        // Nothing turned down is the brain exactly as it was built before.
        var tuning = BrainTuning.Current();
        if (!tuning.IsNone)
            CircleAI.Core.Logging.CircleLog.Info("Heal", $"building {request.ModelId ?? "a brain"} with Wolverine's knobs: {tuning.Describe()}");
        var contextSize = tuning.Context(Math.Max(1, request.ContextSize));

        if (engine == ModelEngine.Managed)
        {
            // REFUSED HERE WITH THE REASON when it cannot read the pack. The persona needs
            // nothing at load: it arrives with each turn as the system message.
            if (entry is not null && EngineChoice.Run(entry) is null)
                throw new InvalidOperationException(EngineChoice.Explain(entry) + ".");

            // THE THREAD COUNT THIS DEVICE MEASURED FASTEST (Step 4: primed before Ready), unless
            // the host said otherwise - measured once per device and runtime by KernelThreads, with
            // the device's fast cores less one (DeviceContext.BrainThreads) as the fallback - and
            // then Wolverine's knob, when it has turned threads down (S6).
            // Working memory at 4 bits with keys in the MSE form (S3): measured on the 2B against
            // the fork, it kept every one of 289 tokens in a quarter of fp16's memory - fewer bits
            // only when the balance or Wolverine asks for them, and then the leaner of the two.
            // THE ROW'S GENERATION PROFILE samples the answers (S5) - the model card's numbers,
            // data rather than code - and says whether it thinks by default.
            // THE VECTOR KERNEL RUNS ON THE FAST CORES AND NO OTHERS. A little core is about 11 times
            // slower at a product and a product waits for its slowest thread: on the P30 on 2026-11-05
            // KernelThreads measured a cached matrix, chose 7 threads, and a third of a question went
            // on waiting. Four big cores ran the same kernel 1.8 times faster than 7 threads.
            Managed.Kernels.KernelChoice.ScalarOnly = tuning.ScalarKernels;
            var threads = tuning.Threads(request.Threads is > 0
                ? request.Threads.Value
                : Managed.Kernels.Ptq1PairMatrix.Usable
                    ? Math.Max(1, DeviceContext.Read().PerformanceCores)
                    : Managed.Kernels.KernelThreads.Choose(fallback: DeviceContext.Read().BrainThreads)) ?? 1;
            Console.WriteLine($"CIRCLEAI-SLOT built=Managed model={request.ModelId} cap={request.Capability}");
            return Managed.ManagedChatGenerator.Load(request.ModelPath, new Managed.ManagedGeneratorOptions
            {
                ContextLength = contextSize,
                Threads = threads,
                KvMode = Leaner(tuning.WorkingMemory, request.KvMode) ?? KvCompressionMode.TurboQuant4Bit,
                KeyForm = CircleAI.Core.Compression.TurboQuantForm.Mse,
                Generation = entry?.Generation,
                Thinking = entry?.Generation?.EnableThinking ?? false,
                ThinkingBudget = tuning.ThinkingBudget ?? 0,
            });
        }

        if (engine == ModelEngine.LlamaCpp)
        {
            // REFUSED HERE WITH THE REASON, rather than inside MNN with a parse error: stock
            // llama.cpp rejects Bonsai's PTQ1_0, and Explain names whether it was the ABI or
            // the pack.
            if (entry is not null && EngineChoice.Run(entry) is null)
                throw new InvalidOperationException(EngineChoice.Explain(entry) + ".");

            // NO PERSONA AT LOAD: llama.cpp templates the turn, so the persona rides in it.
            Console.WriteLine($"CIRCLEAI-SLOT built=Llama model={request.ModelId} cap={request.Capability}");
            return new LlamaGenerator(request.ModelPath, contextSize: contextSize, threads: tuning.Threads(request.Threads) ?? 0);
        }

        // THE ENGINE-WIDE KNOBS REACH MNN TOO: fewer threads and a shorter context are the same
        // remedy whatever runs the model.
        var mnnThreads = tuning.Threads(request.Threads);

        var vision = request.Capability.HasFlag(ChatCapability.Vision);
        Console.WriteLine($"CIRCLEAI-SLOT built={(vision ? "KimiVl" : "QwenText")} model={request.ModelId} cap={request.Capability}");

        if (vision)
        {
            return new KimiVlGenerator(
                request.ModelPath,
                contextSize: (uint)contextSize,
                threads: mnnThreads,
                templateEngine: request.TemplateEngine);
        }

        return new QwenTextGenerator(
            request.ModelPath,
            contextSize: (uint)contextSize,
            threads: mnnThreads,
            templateEngine: request.TemplateEngine,
            systemPrompt: request.SystemPrompt,
            samplerJson: request.SamplerJson);
    }

    // THE LEANER OF TWO WORKING MEMORIES. Wolverine's knob (S6) and the balance's shape (Step 4)
    // both turn it down, and neither may undo the other; fewer bits is the larger mode.
    private static KvCompressionMode? Leaner(KvCompressionMode? a, KvCompressionMode? b) =>
        a is null ? b : b is null ? a : (KvCompressionMode)Math.Max((int)a.Value, (int)b.Value);

    /// <summary>The engine a model file needs, read from the file when no row says.</summary>
    /// <remarks>
    /// A GGUF the C# engine reads - qwen35 in PTQ1_0 or PQ2_0, Bonsai 2 - is the C# engine's; any
    /// other GGUF is llama.cpp's; anything else is an MNN bundle. The packing is the first ternary
    /// tensor's. A GGUF that cannot be opened is left to llama.cpp, which will say why at load.
    /// </remarks>
    public static ModelEngine EngineOf(string modelPath)
    {
        if (!modelPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) return ModelEngine.Mnn;
        try
        {
            using var file = Managed.Gguf.GgufFile.Open(modelPath);
            var architecture = file.TryGetValue("general.architecture", out var arch) ? arch.AsString() : null;
            string? packing = null;
            foreach (var tensor in file.Tensors)
            {
                if (tensor.Type is not (Managed.Gguf.GgmlType.PTQ1_0 or Managed.Gguf.GgmlType.PQ2_0)) continue;
                packing = tensor.Type.ToString();
                break;
            }
            return Managed.ManagedChatGenerator.Reads(architecture, packing)
                ? ModelEngine.Managed
                : ModelEngine.LlamaCpp;
        }
        catch (Exception)
        {
            return ModelEngine.LlamaCpp;
        }
    }
}
