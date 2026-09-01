// VoiceKwsGraph.swift
//
// The keyword-spotting context graph: an Aho-Corasick trie over token ids, so
// several wake phrases can be watched for at once in a single pass over the
// decoder output.
//
// Ported from src/CircleAI.Voice/KwsContextGraph.cs.

import Foundation

/// One node. Reference type on purpose: fail and output links point sideways
/// and upwards through the trie, which a value type cannot express.
public final class KwsContextState: @unchecked Sendable {
    public internal(set) var token: Int = -1
    public internal(set) var tokenScore: Float = 0
    public internal(set) var nodeScore: Float = 0
    public internal(set) var outputScore: Float = 0
    public internal(set) var level: Int = 0
    public internal(set) var acThreshold: Float = 0
    public internal(set) var isEnd: Bool = false
    public internal(set) var phrase: String = ""
    public internal(set) var prefixPhrase: String = ""
    public internal(set) var prefixLength: Int = 0

    var next: [Int: KwsContextState] = [:]
    /// Where to continue when the next token does not extend this node. The
    /// root fails to itself, which is what terminates every walk.
    var fail: KwsContextState!
    /// The longest phrase that ENDS here as a suffix, if any.
    var output: KwsContextState?

    public init() {}
}

public struct KwsKeyword: Sendable, Equatable {
    public let tokens: [Int]
    public let phrase: String
    public let boost: Float
    public let threshold: Float
    public init(tokens: [Int], phrase: String, boost: Float = 0, threshold: Float = 0) {
        self.tokens = tokens
        self.phrase = phrase
        self.boost = boost
        self.threshold = threshold
    }
}

public struct KwsProgress: Sendable, Equatable {
    public let phrase: String
    public let matched: Int
    public let total: Int
    public let meanProbability: Double
    public init(phrase: String, matched: Int, total: Int, meanProbability: Double) {
        self.phrase = phrase
        self.matched = matched
        self.total = total
        self.meanProbability = meanProbability
    }
}

public final class KwsContextGraph: @unchecked Sendable {
    private let rootState = KwsContextState()
    private let contextScore: Float
    private let acThreshold: Float
    private var shadowed: [(phrase: String, shadowedBy: String)] = []

    public var root: KwsContextState { rootState }

    /// Phrases that can NEVER fire because a shorter phrase ends inside them.
    /// Reported rather than silently dropped: somebody configured a wake word
    /// that will not work, and they need to be told which.
    public var shadowedPhrases: [(phrase: String, shadowedBy: String)] { shadowed }

    public init(tokenIds: [[Int]], contextScore: Float, acThreshold: Float,
                scores: [Float]? = nil, phrases: [String]? = nil, acThresholds: [Float]? = nil) {
        self.contextScore = contextScore
        self.acThreshold = acThreshold
        rootState.fail = rootState
        build(tokenIds: tokenIds, scores: scores, phrases: phrases, acThresholds: acThresholds)
    }

    private func build(tokenIds: [[Int]], scores: [Float]?,
                       phrases: [String]?, acThresholds: [Float]?) {
        for i in 0..<tokenIds.count {
            var node = rootState

            // A zero means "not set", so the graph-wide default applies.
            var score = (scores?.isEmpty == false) ? scores![i] : 0
            if score == 0 { score = contextScore }
            var threshold = (acThresholds?.isEmpty == false) ? acThresholds![i] : 0
            if threshold == 0 { threshold = acThreshold }
            let phrase = (phrases?.isEmpty == false) ? phrases![i] : ""
            let length = tokenIds[i].count

            for j in 0..<length {
                let token = tokenIds[i][j]
                let isEnd = j == length - 1

                if let child = node.next[token] {
                    // A SHARED PREFIX keeps the HIGHER boost, so one phrase
                    // cannot quietly weaken another that starts the same way.
                    child.tokenScore = max(score, child.tokenScore)
                    child.nodeScore = node.nodeScore + child.tokenScore
                    child.isEnd = isEnd || child.isEnd
                    child.outputScore = child.isEnd ? child.nodeScore : 0
                    if isEnd {
                        child.phrase = phrase
                        child.acThreshold = threshold
                    }
                    if child.prefixPhrase.isEmpty {
                        child.prefixPhrase = phrase
                        child.prefixLength = length
                    }
                    node = child
                } else {
                    let child = KwsContextState()
                    child.token = token
                    child.tokenScore = score
                    child.nodeScore = node.nodeScore + score
                    child.outputScore = isEnd ? node.nodeScore + score : 0
                    child.level = j + 1
                    child.acThreshold = isEnd ? threshold : 0
                    child.isEnd = isEnd
                    child.phrase = isEnd ? phrase : ""
                    child.prefixPhrase = phrase
                    child.prefixLength = length
                    node.next[token] = child
                    node = child
                }
            }
        }

        // A phrase whose prefix is itself a complete phrase can never fire.
        for i in 0..<tokenIds.count {
            var node = rootState
            let name = (phrases?.isEmpty == false) ? phrases![i] : "#\(i)"
            for j in 0..<tokenIds[i].count {
                guard let child = node.next[tokenIds[i][j]] else { break }
                node = child
                if child.isEnd && j < tokenIds[i].count - 1 {
                    shadowed.append((name, child.phrase))
                    break
                }
            }
        }

        fillFailOutput()
    }

    /// Advance one token. Returns the score contributed, the state landed on,
    /// and the phrase completed if any.
    public func forwardOneStep(_ state: KwsContextState, token: Int)
        -> (score: Float, state: KwsContextState, matched: KwsContextState?) {
        let node: KwsContextState
        let score: Float

        if let direct = state.next[token] {
            node = direct
            score = node.tokenScore
        } else {
            // Fall back along the fail links until a node can take this token,
            // or the root is reached and there is nowhere left to fall.
            var walk = state.fail!
            while walk.next[token] == nil {
                walk = walk.fail
                if walk.token == -1 { break }
            }
            node = walk.next[token] ?? walk
            // The score is the DIFFERENCE, so falling back does not re-award
            // the shared prefix that was already counted.
            score = node.nodeScore - state.nodeScore
        }

        let matched = node.isEnd ? node : node.output
        return (score + node.outputScore, node, matched)
    }

    public func isMatched(_ state: KwsContextState) -> (matched: Bool, state: KwsContextState?) {
        if state.isEnd { return (true, state) }
        if let o = state.output { return (true, o) }
        return (false, nil)
    }

    /// Breadth-first, so a node fail link is set before its children need it.
    private func fillFailOutput() {
        var queue: [KwsContextState] = []
        for child in rootState.next.values {
            child.fail = rootState
            queue.append(child)
        }

        var head = 0
        while head < queue.count {
            let current = queue[head]
            head += 1

            for (token, child) in current.next {
                var fail = current.fail!
                if let direct = fail.next[token] {
                    fail = direct
                } else {
                    fail = fail.fail
                    while fail.next[token] == nil {
                        fail = fail.fail
                        if fail.token == -1 { break }
                    }
                    fail = fail.next[token] ?? fail
                }
                child.fail = fail

                // The OUTPUT link is what makes a shorter phrase finishing
                // INSIDE a longer one not get swallowed by it.
                var output: KwsContextState? = fail
                while let o = output, !o.isEnd {
                    output = o.fail
                    if output?.token == -1 { output = nil; break }
                }
                child.output = output
                child.outputScore += output?.outputScore ?? 0

                queue.append(child)
            }
        }
    }
}

// MARK: - Audio out

public protocol IAudioPlayer: Sendable {
    func play(pcm: Data, sampleRate: Int, channels: Int, bitsPerSample: Int) async throws
    func close() async
}

/// Swallows the audio. Used where a pipeline is being exercised without a
/// speaker - a test, or a build with no audio output wired.
public struct NullAudioPlayer: IAudioPlayer {
    public static let instance = NullAudioPlayer()
    public init() {}
    public func play(pcm: Data, sampleRate: Int, channels: Int, bitsPerSample: Int) async throws {}
    public func close() async {}
}

/// One complete turn: what was heard and what was said back.
public struct VoiceExchange: Sendable, Equatable {
    public let heard: String
    public let replied: String
    public let at: Date
    public init(heard: String, replied: String, at: Date = Date()) {
        self.heard = heard
        self.replied = replied
        self.at = at
    }
}
