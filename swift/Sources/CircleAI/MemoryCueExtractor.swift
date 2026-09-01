// MemoryCueExtractor.swift
//
// Turning what was said into what is remembered, with NO MODEL.
//
// THE FLOOR THAT ALWAYS WORKS. A model reads a conversation better than a list
// of phrases ever will, and a memory that only fills itself when a model is
// loaded does not fill itself on a phone in aeroplane mode. So this is the
// mechanism, not the degraded mode.
//
// IT KEEPS THE PERSON'S WORDS. Every atom is a sentence somebody actually said,
// lifted whole. Paraphrasing is where extraction starts inventing, and an
// invented memory is worse than an empty one because it comes back with the
// same confidence as a true one.
//
// IT LISTENS TO THE PERSON, NOT TO THE ASSISTANT. What an assistant said it
// would do is a plan; what the person said is the requirement. Extracting from
// both would let the thing that was corrected file its own version of events
// alongside the correction — which is how a memory ends up agreeing with
// whoever spoke last.
//
// IT DOES NOT INVENT A SUBJECT. A wrong subject key is worse than none: it
// makes an atom findable in the wrong situation and invisible in the right one.
//
// Ported from src/CircleAI.Memory/CueExtractor.cs.

import Foundation

public struct CueExtractor: IAtomExtractor {

    public init() {}

    public var name: String { "cues" }

    struct Cue: Sendable {
        let phrase: String
        let kind: AtomKind
        let confidence: Double
        var failed: Bool = false
        var atStart: Bool = false
    }

    /// A sentence this short is a REACTION, not a requirement. "never mind",
    /// "stop it", "I want that" carry a cue and no content, and filing them
    /// fills the memory with things that match everything and mean nothing.
    public static let shortestWorthKeeping = 20

    /// And one this long is a paragraph that happens to contain the word, not a
    /// rule somebody stated. Keeping it would put a page into a recall budget
    /// that holds 600 characters.
    public static let longestWorthKeeping = 240

    /// Ordered by how little they leave open to interpretation. "never" at the
    /// START of a sentence is a rule and nothing else; "use" could be anything,
    /// which is why it scores where it does.
    static let cues: [Cue] = [
        // A rule, stated. The least ambiguous thing a person says — as long as
        // it is the sentence's FIRST word.
        Cue(phrase: "never ", kind: .ruling, confidence: 0.92, atStart: true),
        Cue(phrase: "always ", kind: .ruling, confidence: 0.88, atStart: true),
        Cue(phrase: "do not ", kind: .ruling, confidence: 0.88, atStart: true),
        Cue(phrase: "don\u{27}t ", kind: .ruling, confidence: 0.88, atStart: true),
        Cue(phrase: "must not ", kind: .ruling, confidence: 0.90, atStart: true),
        Cue(phrase: "stop ", kind: .ruling, confidence: 0.82, atStart: true),
        Cue(phrase: "we only ", kind: .ruling, confidence: 0.86),
        Cue(phrase: "we never ", kind: .ruling, confidence: 0.90),
        Cue(phrase: "we always ", kind: .ruling, confidence: 0.88),
        Cue(phrase: "from now on", kind: .ruling, confidence: 0.90),

        // THE SAME RULES WITHOUT THE APOSTROPHE, because that is how people
        // type when they are annoyed — which is exactly when they are stating
        // the rule that was just broken.
        Cue(phrase: "dont ", kind: .ruling, confidence: 0.88, atStart: true),
        Cue(phrase: "wont ", kind: .ruling, confidence: 0.84, atStart: true),
        Cue(phrase: "we dont ", kind: .ruling, confidence: 0.88),
        Cue(phrase: "we wont ", kind: .ruling, confidence: 0.84),

        // A road tried and found CLOSED. Worth as much as one that worked, and
        // it is the thing recall pushes to the top.
        Cue(phrase: "did not work", kind: .decision, confidence: 0.88, failed: true),
        Cue(phrase: "didn\u{27}t work", kind: .decision, confidence: 0.88, failed: true),
        Cue(phrase: "didnt work", kind: .decision, confidence: 0.88, failed: true),
        Cue(phrase: "does not work", kind: .decision, confidence: 0.88, failed: true),
        Cue(phrase: "doesn\u{27}t work", kind: .decision, confidence: 0.88, failed: true),
        Cue(phrase: "doesnt work", kind: .decision, confidence: 0.88, failed: true),
        Cue(phrase: "never worked", kind: .decision, confidence: 0.86, failed: true),
        Cue(phrase: "still broken", kind: .decision, confidence: 0.86, failed: true),
        Cue(phrase: "that broke", kind: .decision, confidence: 0.84, failed: true),
        Cue(phrase: "it failed", kind: .decision, confidence: 0.84, failed: true),

        // Being told AGAIN. The single highest-value thing in a transcript:
        // whatever follows has already cost somebody twice.
        Cue(phrase: "i told you", kind: .ruling, confidence: 0.90),
        Cue(phrase: "i already told", kind: .ruling, confidence: 0.90),
        Cue(phrase: "i said ", kind: .ruling, confidence: 0.84),
        Cue(phrase: "you keep ", kind: .ruling, confidence: 0.86),
        Cue(phrase: "how many times", kind: .ruling, confidence: 0.88),

        // How somebody wants to be worked with.
        Cue(phrase: "i prefer ", kind: .preference, confidence: 0.88),
        Cue(phrase: "i\u{27}d rather ", kind: .preference, confidence: 0.86),
        Cue(phrase: "i would rather ", kind: .preference, confidence: 0.86),
        Cue(phrase: "i hate ", kind: .preference, confidence: 0.84),
        Cue(phrase: "i want ", kind: .preference, confidence: 0.78),
        Cue(phrase: "i like ", kind: .preference, confidence: 0.76),

        // Something settled.
        Cue(phrase: "let\u{27}s use ", kind: .decision, confidence: 0.84),
        Cue(phrase: "lets use ", kind: .decision, confidence: 0.84),
        Cue(phrase: "we\u{27}ll use ", kind: .decision, confidence: 0.84),
        Cue(phrase: "we will use ", kind: .decision, confidence: 0.84),
        Cue(phrase: "we\u{27}re going with", kind: .decision, confidence: 0.86),
        Cue(phrase: "going with ", kind: .decision, confidence: 0.78),
        Cue(phrase: "use ", kind: .decision, confidence: 0.66),
        Cue(phrase: "the answer is", kind: .decision, confidence: 0.72),
    ]

    public func extract(_ episode: EpisodicMemoryEntry, subject: String? = nil) -> [AtomCandidate] {
        var found: [AtomCandidate] = []
        var seen = Set<String>()

        // The person's turn only. See the header.
        for sentence in Self.sentences(episode.userText) {
            let length = sentence.count
            if length < Self.shortestWorthKeeping || length > Self.longestWorthKeeping { continue }

            let lowered = sentence.lowercased()

            // THE MOST SPECIFIC CUE WINS. "i told you" and "you keep" often sit
            // in one sentence, and filing it twice makes one complaint look
            // like a pattern.
            let matched = Self.cues
                .filter { cue in
                    let at = Self.position(lowered, cue.phrase)
                    return at >= 0 && (!cue.atStart || at == 0)
                }
                .sorted { a, b in
                    a.confidence != b.confidence
                        ? a.confidence > b.confidence
                        : a.phrase.count > b.phrase.count
                }
                .first

            guard let cue = matched else { continue }
            guard seen.insert(Self.normalise(sentence)).inserted else { continue }

            let outcome: DecisionOutcome? = cue.failed
                ? .failed
                : (cue.kind == .decision ? .resolved : nil)

            found.append(AtomCandidate(
                atom: MemoryAtom(
                    kind: cue.kind,
                    text: sentence,
                    subject: subject ?? episode.appContext,
                    sourceEpisode: episode.id,
                    recordedAtUtc: episode.recordedAt,
                    outcome: outcome),
                confidence: cue.confidence,
                cue: cue.phrase.trimmingCharacters(in: .whitespaces),
                quote: sentence))
        }

        return found
    }

    // MARK: - Text

    /// Where the phrase starts, or -1.
    ///
    /// It must not be INSIDE a word: "use " matching in "abuse the" would file
    /// a decision nobody made. Only a boundary counts.
    static func position(_ haystack: String, _ needle: String) -> Int {
        let hay = Array(haystack)
        let need = Array(needle)
        guard !need.isEmpty, hay.count >= need.count else { return -1 }

        for start in 0...(hay.count - need.count) {
            if Array(hay[start..<(start + need.count)]) != need { continue }
            if start == 0 || !(hay[start - 1].isLetter || hay[start - 1].isNumber) { return start }
        }
        return -1
    }

    /// A full stop only ends a sentence when whitespace or the end follows —
    /// otherwise every version number and every file name splits a rule in half.
    static func sentences(_ text: String) -> [String] {
        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return [] }

        let trimSet = CharacterSet(charactersIn: " \t-*>.,")
        let chars = Array(text)
        var out: [String] = []
        var start = 0

        for i in 0..<chars.count {
            let c = chars[i]
            let ends = c == "\n" || c == "\r" || c == "?" || c == "!" ||
                (c == "." && (i + 1 >= chars.count || chars[i + 1].isWhitespace))
            if !ends { continue }

            let sentence = String(chars[start..<i]).trimmingCharacters(in: trimSet)
            if !sentence.isEmpty { out.append(sentence) }
            start = i + 1
        }

        let last = String(chars[start...]).trimmingCharacters(in: trimSet)
        if !last.isEmpty { out.append(last) }
        return out
    }

    /// The key a store uses to answer "do I already know this".
    ///
    /// Case, spacing and trailing punctuation are all noise: the same sentence
    /// typed twice with different punctuation is one memory, and filing it
    /// twice is how a memory starts repeating itself.
    public static func normalise(_ text: String) -> String {
        text.lowercased()
            .split(whereSeparator: { $0.isWhitespace })
            .joined(separator: " ")
            .trimmingCharacters(in: CharacterSet(charactersIn: ".,!?;: "))
    }
}
