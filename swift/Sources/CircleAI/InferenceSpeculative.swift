// InferenceSpeculative.swift
//
// Port of the managed (non-P/Invoke) logic from
//   src/CircleAI.Inference/MnnInteropRtFeatures.cs
//
//   • RT-05  SpeculativeDecodingPipeline — draft + target speculative decoding,
//            over the injected `IChatGenerator` draft/target seam.
//   • RT-12  MeshOffloadStrategy / MeshPeer / OffloadVerdict — the RAM+latency
//            peer-offload heuristic.
//
// NOT ported (native MNN P/Invoke — no managed logic to lift):
//   • RT-03  MmapWeightLoader        (mnn_llm_set_mmap_mode …)
//   • RT-10  LoRAAdapterManager      (mnn_llm_apply_lora / _train_lora_step …)
//
// Porting notes:
//   • C# `IChatGenerator.StreamAsync(messages, opts, ct)` → Swift
//     `IChatGenerator.stream(messages:options:)` which returns a non-throwing
//     `AsyncStream<String>`; the collect helper simply drains it.
//   • `Action<string> onText` → an `@escaping (String) -> Void` sink.
//   • `CancellationToken` → Swift structured-concurrency cancellation
//     (`Task.isCancelled` / `Task.checkCancellation`).
//   • `new ChatMessage("assistant", text)` (positional) → labelled
//     `ChatMessage(role: "assistant", content: text)`.
//   • The RT-12 strategy is pure; the peer registry is injected as
//     `@escaping () -> [MeshPeer]` (C# `Func<IReadOnlyList<MeshPeer>>`).

import Foundation

// ──────────────────────────────────────────────────────────────────────
// RT-05 speculative decoding — managed implementation
// ──────────────────────────────────────────────────────────────────────

/// (3.3.0) Speculative decoding: a small draft model predicts K tokens; the
/// target model verifies them in one pass and accepts the longest prefix that
/// matches. Falls back gracefully if the drafts diverge early.
///
/// (C# `SpeculativeDecodingPipeline`.) The draft and target generators are
/// injected behind the `IChatGenerator` seam.
public final class SpeculativeDecodingPipeline: @unchecked Sendable {
    private let draft: any IChatGenerator
    private let target: any IChatGenerator
    private let draftLen: Int

    /// - Parameters:
    ///   - draft: the small, fast draft generator.
    ///   - target: the large, authoritative generator that verifies drafts.
    ///   - draftLen: how many tokens the draft proposes per round. 1…64.
    public init(draft: any IChatGenerator, target: any IChatGenerator, draftLen: Int = 8) {
        precondition(draftLen >= 1 && draftLen <= 64, "draftLen must be in 1...64")
        self.draft = draft
        self.target = target
        self.draftLen = draftLen
    }

    /// (3.3.0) Generate a continuation using speculative decoding. Streams
    /// accepted text to `onText`. Returns total chars emitted.
    ///
    /// Verification is word-level (each "word" = a contiguous run of
    /// non-whitespace followed by optional whitespace). Word-level matching is a
    /// closer proxy for token-level alignment than char-LCP would be — most
    /// BPE/WordPiece tokenisers keep word boundaries intact, so two models that
    /// "agree on the word" will typically have produced equivalent tokens. The
    /// accepted prefix is the longest run of words shared between draft and
    /// target outputs; on full divergence we fall back to the first target word.
    @discardableResult
    public func generate(
        messages: [ChatMessage],
        onText: @escaping (String) -> Void,
        maxChars: Int
    ) async throws -> Int {
        precondition(maxChars > 0, "maxChars must be > 0")

        var emitted = 0
        var conversation = messages
        while emitted < maxChars && !Task.isCancelled {
            let draftText = await Self.collect(draft, conversation, draftLen)
            if draftText.isEmpty { break }
            let targetText = await Self.collect(target, conversation, draftLen)
            if targetText.isEmpty { break }

            var accept = Self.longestCommonWordPrefix(draftText, targetText)
            if accept.isEmpty { accept = Self.firstWord(targetText) }
            if accept.isEmpty { break }

            onText(accept)
            emitted += accept.count
            if conversation.isEmpty || conversation[conversation.count - 1].role != "assistant" {
                conversation.append(ChatMessage(role: "assistant", content: accept))
            } else {
                let last = conversation[conversation.count - 1]
                conversation[conversation.count - 1] =
                    ChatMessage(role: "assistant", content: last.content + accept)
            }
        }
        return emitted
    }

    private static func collect(
        _ gen: any IChatGenerator,
        _ messages: [ChatMessage],
        _ maxTokens: Int
    ) async -> String {
        var sb = ""
        let opts = GenerationOptions(maxTokens: maxTokens)
        for await chunk in gen.stream(messages: messages, options: opts) {
            if Task.isCancelled { break }
            sb += chunk
            if sb.count >= maxTokens * 4 { break }  // char-per-token guard
        }
        return sb
    }

    /// Split into words preserving trailing whitespace so the rejoin is lossless.
    /// Mirrors the C# `SplitWords`: walk the word body, then the trailing
    /// whitespace run, emitting the combined slice.
    private static func splitWords(_ s: String) -> [String] {
        var words: [String] = []
        let chars = Array(s)
        var i = 0
        let n = chars.count
        while i < n {
            let start = i
            while i < n && !chars[i].isWhitespace { i += 1 }   // word body
            while i < n &&  chars[i].isWhitespace { i += 1 }   // trailing ws
            if i > start { words.append(String(chars[start..<i])) }
        }
        return words
    }

    private static func longestCommonWordPrefix(_ a: String, _ b: String) -> String {
        let wa = splitWords(a)
        let wb = splitWords(b)
        let n = min(wa.count, wb.count)
        var sb = ""
        var i = 0
        while i < n {
            if wa[i] != wb[i] { break }   // ordinal comparison
            sb += wa[i]
            i += 1
        }
        return sb
    }

    private static func firstWord(_ s: String) -> String {
        let words = splitWords(s)
        return words.isEmpty ? "" : words[0]
    }
}

// ──────────────────────────────────────────────────────────────────────
// RT-12 mesh offload — route inference to a peer when local can't run
// ──────────────────────────────────────────────────────────────────────

/// (C# `MeshPeer`.) A candidate peer that could run inference on our behalf.
public struct MeshPeer: Sendable, Equatable {
    public let peerId: String
    public let latencyMs: Double
    public let ramBytes: Int64
    public let loadAvg: Double
    public let supportedModels: [String]

    public init(
        peerId: String,
        latencyMs: Double,
        ramBytes: Int64,
        loadAvg: Double,
        supportedModels: [String]
    ) {
        self.peerId = peerId
        self.latencyMs = latencyMs
        self.ramBytes = ramBytes
        self.loadAvg = loadAvg
        self.supportedModels = supportedModels
    }
}

/// (C# `OffloadVerdict`.) Whether to offload, and to whom, plus the reason.
public struct OffloadVerdict: Sendable, Equatable {
    public let shouldOffload: Bool
    public let targetPeerId: String?
    public let reason: String

    public init(shouldOffload: Bool, targetPeerId: String?, reason: String) {
        self.shouldOffload = shouldOffload
        self.targetPeerId = targetPeerId
        self.reason = reason
    }
}

/// (3.3.0) Mesh-offload strategy: picks a peer when local execution is
/// infeasible (low RAM, slow CPU, model not loaded locally) or when a faster
/// peer is available. Hosts wire the peer registry; the strategy is pure.
///
/// (C# `MeshOffloadStrategy`.) The peer registry is injected as a closure
/// (C# `Func<IReadOnlyList<MeshPeer>>`).
public final class MeshOffloadStrategy: @unchecked Sendable {
    private let peers: @Sendable () -> [MeshPeer]
    private let localRamBytes: Int64
    private let localLoadAvg: Double

    public init(
        peers: @escaping @Sendable () -> [MeshPeer],
        localRamBytes: Int64,
        localLoadAvg: Double
    ) {
        self.peers = peers
        self.localRamBytes = localRamBytes
        self.localLoadAvg = localLoadAvg
    }

    public func decide(
        modelId: String,
        requiredRamBytes: Int64,
        expectedSecondsLocal: Double
    ) throws -> OffloadVerdict {
        if modelId.isBlank { throw ModelRuntimeError.argument("modelId required") }
        if requiredRamBytes <= 0 { throw ModelRuntimeError.argument("requiredRamBytes") }

        // 1) Always offload if local can't fit the model.
        if localRamBytes < requiredRamBytes {
            guard let pick = pickBestPeer(modelId: modelId, requiredRamBytes: requiredRamBytes) else {
                return OffloadVerdict(shouldOffload: false, targetPeerId: nil,
                                      reason: "Local can't fit; no eligible peer")
            }
            return OffloadVerdict(shouldOffload: true, targetPeerId: pick.peerId,
                                  reason: "Local RAM insufficient")
        }

        // 2) Offload if local is overloaded AND a peer can do it noticeably faster.
        if localLoadAvg > 0.85 {
            if let pick = pickBestPeer(modelId: modelId, requiredRamBytes: requiredRamBytes),
               pick.loadAvg < 0.5,
               pick.latencyMs < expectedSecondsLocal * 1000 * 0.7 {
                return OffloadVerdict(shouldOffload: true, targetPeerId: pick.peerId,
                                      reason: "Local overloaded; peer faster")
            }
        }

        return OffloadVerdict(shouldOffload: false, targetPeerId: nil,
                              reason: "Local capacity sufficient")
    }

    /// Eligible peers = those with enough RAM that advertise `modelId`
    /// (case-insensitive), ordered by `latencyMs + loadAvg * 500` ascending;
    /// first wins. Mirrors the C# LINQ `Where(...).OrderBy(...).FirstOrDefault()`.
    private func pickBestPeer(modelId: String, requiredRamBytes: Int64) -> MeshPeer? {
        return peers()
            .filter { p in
                p.ramBytes >= requiredRamBytes
                    && p.supportedModels.contains { $0.caseInsensitiveCompare(modelId) == .orderedSame }
            }
            .min { ($0.latencyMs + $0.loadAvg * 500) < ($1.latencyMs + $1.loadAvg * 500) }
    }
}
