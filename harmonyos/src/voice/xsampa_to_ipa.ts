// xsampa_to_ipa.ts
//
// Port of src/CircleAI.Voice/XsampaToIpa.cs and SentencePieceUnigram.cs.
//
// Parity is asserted against fixtures/voice_xsampa_to_ipa.json and
// fixtures/voice_sentencepiece_unigram.json, which the C# reference generates.
// If this file and those disagree, one of them is wrong and the test names the
// case.

/**
 * Every phone in the NCHLT Afrikaans dictionary, mapped to IPA.
 *
 * Derived from the corpus, not from memory: exactly the distinct phones in
 * nchlt_afr.dict, with every IPA character checked against the target voice's
 * own token table before the table was written.
 */
const XSAMPA_TO_IPA: Readonly<Record<string, string>> = Object.freeze({
  // Vowels
  a: 'a', 'A:': 'ɑː', 'A:r': 'ɑːr',
  E: 'ɛ', O: 'ɔ', '@': 'ə',
  i: 'i', u: 'u', y: 'y',
  '9': 'œ', '2:': 'øː', '{': 'æ',

  // Diphthongs — NCHLT gives one token, the voice wants both elements.
  '9y': 'œy', '@i': 'əi', '@u': 'əu',
  'i@': 'iə', 'u@': 'uə',

  // Consonants
  b: 'b', d: 'd', f: 'f',
  // U+0261 LATIN SMALL LETTER SCRIPT G — the IPA letter, NOT ASCII 'g'. The
  // voice's vocabulary carries ɡ; a plain 'g' would miss and be dropped.
  g: 'ɡ',
  j: 'j', k: 'k', l: 'l',
  m: 'm', n: 'n', N: 'ŋ',
  p: 'p', r: 'r', s: 's',
  S: 'ʃ', t: 't', v: 'v',
  w: 'w', x: 'x', z: 'z',
  Z: 'ʒ',

  // APPROXIMATION, DELIBERATE AND THE ONLY ONE. X-SAMPA h\ is ɦ, the voiced
  // glottal fricative Afrikaans uses in "hond". This voice's vocabulary has no
  // ɦ, only h. Voicing is lost; place and manner are right, so the word stays
  // recognisable.
  'h\\': 'h',
});

/** Result of a conversion: the IPA symbols, and the phones that had no mapping. */
export interface XsampaConversion {
  readonly ipa: string[];
  /**
   * Empty is the good case. An unmapped phone produces NO SOUND and the audio
   * is merely shorter — every acoustic measure still passes. A caller that
   * cannot see the misses cannot refuse.
   */
  readonly unmapped: string[];
}

/**
 * Convert X-SAMPA phone tokens to a flat IPA symbol list.
 *
 * LONGEST MATCH ON WHOLE TOKENS. Several entries are multi-character (A:r, @i,
 * 9y) and NCHLT emits them as single tokens; matching on the token — never
 * character by character — is what keeps A:r from becoming A + : + r.
 */
export function xsampaToIpa(xsampa: readonly string[]): XsampaConversion {
  const ipa: string[] = [];
  const unmapped: string[] = [];

  for (const phone of xsampa) {
    if (phone.trim() === '') continue;

    const mapped = Object.prototype.hasOwnProperty.call(XSAMPA_TO_IPA, phone)
      ? XSAMPA_TO_IPA[phone]
      : undefined;
    if (mapped !== undefined) {
      // Spread by CODE POINT, not by charCodeAt: the voice tokenises ɑ, ː and r
      // separately, and splitting a surrogate pair would produce lone halves.
      for (const ch of mapped) ipa.push(ch);
      continue;
    }

    if (!unmapped.includes(phone)) unmapped.push(phone);
  }

  return { ipa, unmapped };
}

/** True when every phone in `xsampa` has a mapping. */
export function xsampaCanSayAll(xsampa: readonly string[]): boolean {
  return xsampa
    .filter((p) => p.trim() !== '')
    .every((p) => Object.prototype.hasOwnProperty.call(XSAMPA_TO_IPA, p));
}

/** The X-SAMPA phones this table knows — for tests and diagnostics. */
export function xsampaKnownPhones(): string[] {
  return Object.keys(XSAMPA_TO_IPA);
}

// ---------------------------------------------------------------------------
// SentencePiece unigram
// ---------------------------------------------------------------------------

/**
 * Cost charged for falling back to raw bytes.
 *
 * Any finite penalty works, because fallback only ever competes with "no path
 * at all". It must be worse than a real piece so the lattice never prefers it
 * where a piece exists, and finite so a path always exists.
 */
const FALLBACK_PENALTY = 10.0;

/** SentencePiece unigram tokeniser — Viterbi over the piece lattice. */
export class SentencePieceUnigram {
  private readonly ids: Record<string, number>;
  private readonly scores: Record<string, number>;
  private readonly maxPieceLength: number;

  constructor(ids: Record<string, number>, scores: Record<string, number>) {
    this.ids = ids;
    this.scores = scores;
    this.maxPieceLength = Object.keys(ids).reduce(
      (max, k) => Math.max(max, [...k].length),
      1,
    );
  }

  get count(): number {
    return Object.keys(this.ids).length;
  }

  /**
   * Encode text to token ids.
   *
   * VITERBI, NOT GREEDY LONGEST-MATCH. Unigram scores are not monotone in piece
   * length — a long piece can score worse than the two short pieces covering the
   * same span — so greedy silently produces plausible-but-wrong segmentations.
   */
  encode(text: string): number[] {
    if (text === '') return [];

    // SentencePiece's own normalisation: NFKC, then spaces become U+2581, with
    // one prepended so the first word is marked word-initial too.
    const normalised = '▁' + text.normalize('NFKC').replace(/ /g, '▁');

    // CODE POINTS, NOT UTF-16 UNITS. Spreading a string iterates code points, so
    // a piece boundary can never land inside a surrogate pair — which would
    // produce pieces matching nothing and byte fallback decoding to a different
    // character.
    const chars = [...normalised];
    const n = chars.length;

    const UNREACHABLE = -1e18;
    const best = new Array<number>(n + 1).fill(UNREACHABLE);
    const fromIndex = new Array<number>(n + 1).fill(0);
    const piece = new Array<string | null>(n + 1).fill(null);
    const hasPiece = new Array<boolean>(n + 1).fill(false);
    best[0] = 0;

    for (let i = 0; i < n; i++) {
      if (best[i] <= UNREACHABLE / 2) continue;

      const limit = Math.min(this.maxPieceLength, n - i);
      for (let len = 1; len <= limit; len++) {
        const candidate = chars.slice(i, i + len).join('');
        if (!Object.prototype.hasOwnProperty.call(this.ids, candidate)) continue;
        const score = best[i] + (this.scores[candidate] ?? 0);
        if (score > best[i + len]) {
          best[i + len] = score;
          fromIndex[i + len] = i;
          piece[i + len] = candidate;
          hasPiece[i + len] = true;
        }
      }

      // Byte fallback for this ONE code point, so no input is ever silent.
      const end = i + 1;
      const fallback = best[i] - FALLBACK_PENALTY;
      if (fallback > best[end]) {
        best[end] = fallback;
        fromIndex[end] = i;
        hasPiece[end] = false;
      }
    }

    const encoder = new TextEncoder();
    const reversed: number[] = [];
    let i = n;
    while (i > 0) {
      const start = fromIndex[i];
      const p = piece[i];
      if (hasPiece[i] && p !== null) {
        reversed.push(this.ids[p]);
      } else {
        // BACKWARDS, because this whole list is built backwards. The lattice is
        // walked from the end and reversed once at the bottom, so a multi-byte
        // character pushed in forward order comes out byte-reversed: é is UTF-8
        // C3 A9 and would be emitted A9 C3. Nothing throws — those are real
        // pieces with real ids — so the model simply says a different character.
        const raw = chars.slice(start, i).join('');
        const bytes = encoder.encode(raw);
        for (let b = bytes.length - 1; b >= 0; b--) {
          const key = `<0x${bytes[b].toString(16).toUpperCase().padStart(2, '0')}>`;
          if (Object.prototype.hasOwnProperty.call(this.ids, key)) reversed.push(this.ids[key]);
        }
      }
      i = start;
    }

    return reversed.reverse();
  }
}
