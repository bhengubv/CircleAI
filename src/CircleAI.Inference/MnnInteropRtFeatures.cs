// MnnInteropRtFeatures.cs
//
// (3.3.0) Managed wrappers for the four RT-* features exposed by the
// mnnbridge native library:
//   RT-03  mmap weight loading
//   RT-05  speculative decoding (draft + target verification)
//   RT-10  LoRA adapter apply / unapply
//   RT-12  mesh offload (route inference to a peer when local can't run)

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Inference;

// ──────────────────────────────────────────────────────────────────────
// RT-03 mmap + RT-10 LoRA — P/Invoke surface against mnnbridge 1.3.0+
// ──────────────────────────────────────────────────────────────────────
internal static partial class MnnInteropRt
{
    private const string Lib = "mnnbridge";

    [LibraryImport(Lib)]
    internal static partial int mnn_llm_set_mmap_mode(IntPtr handle, int on);

    [LibraryImport(Lib)]
    internal static partial int mnn_llm_get_mmap_mode(IntPtr handle);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mnn_llm_apply_lora(IntPtr handle, string adapterPath);

    [LibraryImport(Lib)]
    internal static partial int mnn_llm_unapply_lora(IntPtr handle);

    [LibraryImport(Lib)]
    internal static unsafe partial int mnn_llm_get_lora(IntPtr handle, byte* outBuf, int bufSize);

    // RT-10 training (mnnbridge 1.4.0+). Returns 0 on success, MNNBRIDGE_ERR_TRAINING_DISABLED
    // (-12) when the native MNN binary was compiled without MNN_BUILD_TRAIN.
    [LibraryImport(Lib)]
    internal static unsafe partial int mnn_llm_train_lora_step(
        IntPtr handle,
        int*   inputTokens, int inputLen,
        int*   targetTokens, int targetLen,
        float  learningRate,
        int    loraRank,
        float* outLossPtr);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mnn_llm_save_lora(IntPtr handle, string adapterPath);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mnn_llm_set_mmap_tmp_path(IntPtr handle, string path);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mnn_llm_set_config(IntPtr handle, string json);
}

/// <summary>
/// Runtime settings pushed into MNN before the model loads.
/// </summary>
/// <remarks>
/// The knobs that decide how a model behaves live in the BUNDLE's own
/// config.json, downloaded from a third party and never ours. This is the one
/// place a caller can disagree with it.
/// </remarks>
public sealed class MnnRuntimeConfig
{
    private readonly IntPtr _handle;

    /// <summary>Wraps a raw, still-unloaded MNN handle.</summary>
    public MnnRuntimeConfig(IntPtr mnnHandle) => _handle = mnnHandle;

    /// <summary>
    /// Turns a reasoning model's visible deliberation off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE MODEL WAS TALKING TO ITSELF IN FRONT OF THE USER. Asked "What is the
    /// capital of South Africa?" on a P30 Lite, Qwen3.5-0.8B spent 160 tokens and
    /// 14 seconds of decode on "Thinking Process: 1. **Analyze the Request:** ...",
    /// argued with the system prompt, quoted it back, and hit the token cap
    /// without ever answering.
    /// </para>
    /// <para>
    /// The cause is in the bundle: <c>dump_config()</c> on the loaded model shows
    /// <c>"jinja":{"context":{"enable_thinking":true}}</c>, which the taobao-mnn
    /// bundles ship. The chat_template reads that flag, so thinking is on before
    /// any of our code gets a say — and Qwen3's <c>/no_think</c> soft switch in
    /// the system prompt loses to it.
    /// </para>
    /// <para>
    /// It also never reached the generator's reasoning router, because MNN's
    /// export emits the deliberation as ORDINARY PROSE rather than inside
    /// &lt;think&gt; tags. There was no tag to route on.
    /// </para>
    /// <para>
    /// Only the one leaf is sent, and MNN merges it into the existing config —
    /// the chat_template itself lives under the same <c>jinja</c> key and must
    /// survive. Verify with the config line the bridge logs after load; if the
    /// template were being replaced rather than merged, the model would fail to
    /// render a prompt at all, loudly and immediately.
    /// </para>
    /// <para>
    /// Best-effort by design: a bundle with no jinja context, or an older bridge
    /// without the export, must fall through to a thinking model rather than fail
    /// to load one.
    /// </para>
    /// </remarks>
    public bool TryDisableThinking()
    {
        try
        {
            return MnnInteropRt.mnn_llm_set_config(
                _handle, "{\"jinja\":{\"context\":{\"enable_thinking\":false}}}") == 0;
        }
        catch (EntryPointNotFoundException) { return false; }
        catch (DllNotFoundException)        { return false; }
    }
}

/// <summary>(3.3.0) RT-03 mmap weight loading control.</summary>
public sealed class MmapWeightLoader
{
    private readonly IntPtr _handle;
    public MmapWeightLoader(IntPtr mnnHandle) => _handle = mnnHandle;

    /// <summary>Where MNN may write its mmap scratch file.</summary>
    /// <remarks>
    /// Set before <see cref="Enable"/>. MNN reads tmp_path first and only then
    /// honours use_mmap (llm.cpp:177-183), so enabling without a writable
    /// scratch directory silently does nothing at all — which is exactly what
    /// happened here for as long as mmap has existed in this bridge.
    /// </remarks>
    public void UseScratch(string dir)
    {
        var r = MnnInteropRt.mnn_llm_set_mmap_tmp_path(_handle, dir);
        if (r != 0) throw new InvalidOperationException($"mnn_llm_set_mmap_tmp_path failed: {r}");
    }

    public void Enable() { var r = MnnInteropRt.mnn_llm_set_mmap_mode(_handle, 1); if (r != 0) throw new InvalidOperationException($"mnn_llm_set_mmap_mode failed: {r}"); }
    public void Disable() { var r = MnnInteropRt.mnn_llm_set_mmap_mode(_handle, 0); if (r != 0) throw new InvalidOperationException($"mnn_llm_set_mmap_mode failed: {r}"); }
    public bool IsEnabled => MnnInteropRt.mnn_llm_get_mmap_mode(_handle) == 1;
}

/// <summary>(3.3.0) RT-10 LoRA adapter manager — apply / read / unapply on a loaded model.</summary>
public sealed class LoRAAdapterManager
{
    private readonly IntPtr _handle;
    public LoRAAdapterManager(IntPtr mnnHandle) => _handle = mnnHandle;

    public void Apply(string adapterPath)
    {
        if (string.IsNullOrWhiteSpace(adapterPath)) throw new ArgumentException("adapterPath required");
        if (!File.Exists(adapterPath) && !Directory.Exists(adapterPath))
            throw new FileNotFoundException("LoRA adapter not found", adapterPath);
        var r = MnnInteropRt.mnn_llm_apply_lora(_handle, adapterPath);
        if (r != 0) throw new InvalidOperationException($"mnn_llm_apply_lora failed: {r}");
    }

    public void Unapply()
    {
        var r = MnnInteropRt.mnn_llm_unapply_lora(_handle);
        if (r != 0) throw new InvalidOperationException($"mnn_llm_unapply_lora failed: {r}");
    }

    public unsafe string? CurrentAdapter()
    {
        Span<byte> buf = stackalloc byte[4096];
        fixed (byte* p = buf)
        {
            var r = MnnInteropRt.mnn_llm_get_lora(_handle, p, buf.Length);
            if (r < 0) return null;
            if (r == 0) return null;
            return Encoding.UTF8.GetString(buf[..r]);
        }
    }

    /// <summary>(Phase D1) Run one gradient-descent step on the LoRA adapter weights.
    /// Returns the scalar loss for the batch. Throws if the native MNN binary
    /// was compiled without training support.</summary>
    public unsafe float TrainStep(
        ReadOnlySpan<int> inputTokens,
        ReadOnlySpan<int> targetTokens,
        float learningRate = 1e-4f,
        int   loraRank     = 8)
    {
        if (inputTokens.IsEmpty)  throw new ArgumentException("inputTokens required",  nameof(inputTokens));
        if (targetTokens.IsEmpty) throw new ArgumentException("targetTokens required", nameof(targetTokens));
        if (learningRate <= 0)    throw new ArgumentOutOfRangeException(nameof(learningRate));
        if (loraRank <= 0)        throw new ArgumentOutOfRangeException(nameof(loraRank));

        float loss = 0;
        int rc;
        fixed (int*   inp = inputTokens)
        fixed (int*   tgt = targetTokens)
        {
            rc = MnnInteropRt.mnn_llm_train_lora_step(_handle, inp, inputTokens.Length, tgt, targetTokens.Length, learningRate, loraRank, &loss);
        }
        if (rc == -12)
            throw new NotSupportedException(
                "mnnbridge native library was compiled without MNN_BUILD_TRAIN. " +
                "Rebuild MNN with -DMNN_BUILD_TRAIN=ON to enable on-device LoRA fine-tuning.");
        if (rc != 0)
            throw new InvalidOperationException($"mnn_llm_train_lora_step failed: {rc}");
        return loss;
    }

    /// <summary>(Phase D1) Persist the current LoRA adapter weights to <paramref name="adapterPath"/>
    /// so a future <see cref="Apply"/> call can reload them.</summary>
    public void SaveAdapter(string adapterPath)
    {
        if (string.IsNullOrWhiteSpace(adapterPath)) throw new ArgumentException("adapterPath required");
        var dir = Path.GetDirectoryName(adapterPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var rc = MnnInteropRt.mnn_llm_save_lora(_handle, adapterPath);
        if (rc != 0) throw new InvalidOperationException($"mnn_llm_save_lora failed: {rc}");
    }
}

// ──────────────────────────────────────────────────────────────────────
// RT-05 speculative decoding — managed implementation
// ──────────────────────────────────────────────────────────────────────

/// <summary>(3.3.0) Speculative decoding: a small draft model predicts K tokens;
/// the target model verifies them in one pass and accepts the longest prefix
/// that matches. Falls back gracefully if the drafts diverge early.</summary>
public sealed class SpeculativeDecodingPipeline
{
    private readonly IChatGenerator _draft;
    private readonly IChatGenerator _target;
    private readonly int _draftLen;

    public SpeculativeDecodingPipeline(IChatGenerator draft, IChatGenerator target, int draftLen = 8)
    {
        _draft  = draft  ?? throw new ArgumentNullException(nameof(draft));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        if (draftLen < 1 || draftLen > 64) throw new ArgumentOutOfRangeException(nameof(draftLen));
        _draftLen = draftLen;
    }

    /// <summary>(3.3.0) Generate a continuation using speculative decoding.
    /// Streams accepted text to <paramref name="onText"/>. Returns total chars emitted.
    /// <para>
    /// Verification is word-level (each "word" = a contiguous run of non-whitespace
    /// followed by optional whitespace). Word-level matching is a closer proxy for
    /// token-level alignment than char-LCP would be — most BPE/WordPiece tokenisers
    /// keep word boundaries intact, so two models that "agree on the word" will
    /// typically have produced equivalent tokens. The accepted prefix is the longest
    /// run of words shared between draft and target outputs; on full divergence we
    /// fall back to the first target word.
    /// </para></summary>
    public async Task<int> GenerateAsync(IReadOnlyList<ChatMessage> messages, Action<string> onText, int maxChars, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(onText);
        if (maxChars <= 0) throw new ArgumentOutOfRangeException(nameof(maxChars));

        var emitted = 0;
        var conversation = messages.ToList();
        while (emitted < maxChars && !ct.IsCancellationRequested)
        {
            var draftText  = await CollectAsync(_draft,  conversation, _draftLen, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(draftText)) break;
            var targetText = await CollectAsync(_target, conversation, _draftLen, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(targetText)) break;

            var accept = LongestCommonWordPrefix(draftText, targetText);
            if (accept.Length == 0) accept = FirstWord(targetText);
            if (accept.Length == 0) break;

            onText(accept);
            emitted += accept.Length;
            if (conversation.Count == 0 || conversation[^1].Role != "assistant")
                conversation.Add(new ChatMessage("assistant", accept));
            else
                conversation[^1] = new ChatMessage("assistant", conversation[^1].Content + accept);
        }
        return emitted;
    }

    private static async Task<string> CollectAsync(IChatGenerator gen, IReadOnlyList<ChatMessage> messages, int maxTokens, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var opts = new GenerationOptions { MaxTokens = maxTokens };
        await foreach (var chunk in gen.StreamAsync(messages, opts, ct).ConfigureAwait(false))
        {
            sb.Append(chunk);
            if (sb.Length >= maxTokens * 4) break;  // char-per-token guard
        }
        return sb.ToString();
    }

    /// <summary>Split into words preserving trailing whitespace so the rejoin is lossless.</summary>
    private static IReadOnlyList<string> SplitWords(string s)
    {
        var words = new List<string>();
        var i = 0;
        while (i < s.Length)
        {
            var start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;          // word body
            while (i < s.Length &&  char.IsWhiteSpace(s[i])) i++;          // trailing ws
            if (i > start) words.Add(s[start..i]);
        }
        return words;
    }

    private static string LongestCommonWordPrefix(string a, string b)
    {
        var wa = SplitWords(a);
        var wb = SplitWords(b);
        var n  = Math.Min(wa.Count, wb.Count);
        var sb = new StringBuilder();
        for (var i = 0; i < n; i++)
        {
            if (!string.Equals(wa[i], wb[i], StringComparison.Ordinal)) break;
            sb.Append(wa[i]);
        }
        return sb.ToString();
    }

    private static string FirstWord(string s)
    {
        var words = SplitWords(s);
        return words.Count == 0 ? "" : words[0];
    }
}

// ──────────────────────────────────────────────────────────────────────
// RT-12 mesh offload — route inference to a peer when local can't run
// ──────────────────────────────────────────────────────────────────────

public sealed record MeshPeer(string PeerId, double LatencyMs, long RamBytes, double LoadAvg, IReadOnlyList<string> SupportedModels);

public sealed record OffloadVerdict(bool ShouldOffload, string? TargetPeerId, string Reason);

/// <summary>(3.3.0) Mesh-offload strategy: picks a peer when local execution is
/// infeasible (low RAM, slow CPU, model not loaded locally) or when a faster
/// peer is available. Hosts wire the peer registry; the strategy is pure.</summary>
public sealed class MeshOffloadStrategy
{
    private readonly Func<IReadOnlyList<MeshPeer>> _peers;
    private readonly long _localRamBytes;
    private readonly double _localLoadAvg;

    public MeshOffloadStrategy(Func<IReadOnlyList<MeshPeer>> peers, long localRamBytes, double localLoadAvg)
    {
        _peers         = peers ?? throw new ArgumentNullException(nameof(peers));
        _localRamBytes = localRamBytes;
        _localLoadAvg  = localLoadAvg;
    }

    public OffloadVerdict Decide(string modelId, long requiredRamBytes, double expectedSecondsLocal)
    {
        if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentException("modelId required");
        if (requiredRamBytes <= 0)                throw new ArgumentOutOfRangeException(nameof(requiredRamBytes));

        // 1) Always offload if local can't fit the model.
        if (_localRamBytes < requiredRamBytes)
        {
            var pick = PickBestPeer(modelId, requiredRamBytes);
            return pick is null
                ? new OffloadVerdict(false, null, "Local can't fit; no eligible peer")
                : new OffloadVerdict(true, pick.PeerId, "Local RAM insufficient");
        }

        // 2) Offload if local is overloaded AND a peer can do it noticeably faster.
        if (_localLoadAvg > 0.85)
        {
            var pick = PickBestPeer(modelId, requiredRamBytes);
            if (pick is not null && pick.LoadAvg < 0.5 && pick.LatencyMs < expectedSecondsLocal * 1000 * 0.7)
                return new OffloadVerdict(true, pick.PeerId, "Local overloaded; peer faster");
        }

        return new OffloadVerdict(false, null, "Local capacity sufficient");
    }

    private MeshPeer? PickBestPeer(string modelId, long requiredRamBytes)
    {
        return _peers()
            .Where(p => p.RamBytes >= requiredRamBytes
                     && p.SupportedModels.Any(m => string.Equals(m, modelId, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.LatencyMs + p.LoadAvg * 500)
            .FirstOrDefault();
    }
}
