/**
 * voice/piper_config
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// piper_config.ts
//
// Ports of src/CircleAI.Voice/PiperVoiceConfig.cs, LexiconTokeniser.cs and
// AudioFormat.cs.
//
// Parity is asserted against fixtures/voice_piper_config.json,
// fixtures/voice_lexicon_tokeniser.json and fixtures/voice_audio_format.json.

// AudioFormat IS ALREADY IN THIS PORT — PCM16_MONO_16K in voice/contracts.ts. It was ported with the
// original voice module and does not belong here; declaring it again would
// shadow the real one and split the type in two.

/** What a `phonemesToIds` call did, beyond the ids. */
export interface PhonemeMapping {
  readonly ids: number[];
  /** How many symbols the vocabulary had no entry for. */
  readonly skipped: number;
  /**
   * WHICH symbols were dropped. A dropped symbol is inaudible, so this list is
   * the only evidence a front-end is broken.
   */
  readonly skippedSymbols: string[];
  /**
   * Symbols APPROXIMATED rather than spoken exactly — a diacritic the voice
   * lacks, folded to its base letter. A compromise, not a success.
   */
  readonly approximatedSymbols: string[];
}

// Piper's special phoneme symbols (piper-phonemize defaults).
const PAD = '_';
const BOS = '^';
const EOS = '$';

/** A Piper-layout voice's phoneme→id vocabulary and inference settings. */
export class PiperVoiceConfig {
  private readonly map: Record<string, number[]>;
  readonly sampleRate: number;
  readonly noiseScale: number;
  readonly lengthScale: number;
  readonly noiseW: number;
  /** e.g. `espeak` (needs a phonemizer) or `text` (graphemes are phonemes). */
  readonly phonemeType: string;

  constructor(
    map: Record<string, number[]>,
    sampleRate = 22050,
    noiseScale = 0.667,
    lengthScale = 1.0,
    noiseW = 0.8,
    phonemeType = 'espeak',
  ) {
    this.map = map;
    this.sampleRate = sampleRate;
    this.noiseScale = noiseScale;
    this.lengthScale = lengthScale;
    this.noiseW = noiseW;
    this.phonemeType = phonemeType;
  }

  /** Parse a Piper `.onnx.json` sidecar. */
  static parse(root: Record<string, any>): PiperVoiceConfig {
    const sampleRate = root.audio?.sample_rate ?? 22050;
    const inference = root.inference ?? {};
    const map: Record<string, number[]> = {};
    const pim = root.phoneme_id_map;
    if (pim && typeof pim === 'object') {
      for (const [symbol, ids] of Object.entries(pim)) {
        if (Array.isArray(ids)) map[symbol] = ids as number[];
      }
    }
    return new PiperVoiceConfig(
      map,
      sampleRate,
      inference.noise_scale ?? 0.667,
      inference.length_scale ?? 1.0,
      inference.noise_w ?? 0.8,
      typeof root.phoneme_type === 'string' ? root.phoneme_type : 'espeak',
    );
  }

  /** True when this config has a usable phoneme→id map. */
  get hasPhonemeMap(): boolean {
    return Object.keys(this.map).length > 0;
  }

  /**
   * THE PAD RULE: the id THIS voice uses for blank.
   *
   * It is 0 in sherpa/MMS exports and 3 in Piper-family ones, and pointing it at
   * an ordinary vocabulary entry is what made 42 MMS voices speak fluent
   * nonsense. Never assume a constant — read it from the model. Falls back to 0
   * only when the vocabulary has no `_` at all.
   */
  get padId(): number {
    const p = this.map[PAD];
    return p && p.length > 0 ? p[0] : 0;
  }

  /**
   * Turn a phoneme sequence into model token ids, in piper-phonemize's exact
   * layout with interspersed pad:
   * `[BOS, PAD, id(p1), PAD, id(p2), PAD, …, id(pN), PAD, EOS]`.
   *
   * BOS and EOS appear only when the vocabulary HAS them — the MMS-family
   * exports do not. Unknown symbols are SKIPPED and REPORTED, never fatal.
   */
  phonemesToIds(phonemes: readonly string[]): PhonemeMapping {
    const ids: number[] = [];
    const dropped: string[] = [];
    const approximated: string[] = [];
    let skipped = 0;

    const bos = this.map[BOS];
    if (bos) ids.push(...bos);
    const pad = this.map[PAD];
    if (pad) ids.push(...pad);

    for (const phoneme of phonemes) {
      const mapped = this.mapSymbol(phoneme);
      if (!mapped) {
        skipped++;
        if (!dropped.includes(phoneme)) dropped.push(phoneme);
        continue;
      }
      if (mapped.approximated && !approximated.includes(phoneme)) approximated.push(phoneme);
      ids.push(...mapped.ids);
      if (pad) ids.push(...pad);
    }

    const eos = this.map[EOS];
    if (eos) ids.push(...eos);

    return { ids, skipped, skippedSymbols: dropped, approximatedSymbols: approximated };
  }

  private mapSymbol(symbol: string): { ids: number[]; approximated: boolean } | null {
    const exact = this.map[symbol];
    if (exact) return { ids: exact, approximated: false };

    // A grapheme voice's vocabulary is built AFTER the training text has been
    // through the model's own cleaner, and every cleaner in use here lower-cases.
    // Such a vocab contains no capitals at all, so matching on the raw character
    // silently discarded every sentence-initial letter — the model received
    // "awubona" for "Sawubona".
    const lower = symbol.toLowerCase();
    if (lower !== symbol && this.map[lower]) return { ids: this.map[lower], approximated: false };

    // A GRAPHEME CLUSTER the vocabulary stores as separate codepoints. Burmese
    // "ကြို" arrives as ONE symbol while the vocabulary holds each codepoint on
    // its own. Splitting it back keeps every mark, so this must be tried BEFORE
    // any approximation.
    const scalars = [...symbol];
    if (scalars.length > 1) {
      const parts: number[] = [];
      let whole = true;
      for (const ch of scalars) {
        // Zero-width formatting characters shape how text is DRAWN and say
        // nothing about how it sounds. Persian writes them constantly, as do
        // most Indic scripts, and one invisible character was failing the whole
        // cluster.
        if (isFormat(ch)) continue;
        const part = this.map[ch] ?? this.map[ch.toLowerCase()];
        if (part) parts.push(...part);
        else {
          whole = false;
          break;
        }
      }
      if (whole && parts.length > 0) return { ids: parts, approximated: false }; // exact
    }

    // A letter the voice never learned. Dropping it deletes a consonant from the
    // middle of a word, so an approximation is worth more than a hole — so long
    // as it is declared rather than passed off as correct.
    for (const candidate of approximations(symbol)) {
      const a = this.map[candidate] ?? this.map[candidate.toLowerCase()];
      if (a) return { ids: a, approximated: true };
    }

    return null;
  }
}

/**
 * Split into grapheme clusters: a base code point plus any combining marks that
 * follow it, so "bát" is three elements and not four.
 */
export function splitPhonemeString(s: string): string[] {
  const out: string[] = [];
  let cur = '';
  for (const ch of s) {
    if (cur !== '' && isCombiningMark(ch)) {
      cur += ch;
      continue;
    }
    if (cur !== '') out.push(cur);
    cur = ch;
  }
  if (cur !== '') out.push(cur);
  return out;
}

function approximations(symbol: string): string[] {
  const out: string[] = [];

  // Where the vocabulary carries the true phoneme under a different spelling,
  // use it — Tshivenda's ṅ IS /ŋ/, so that substitution loses nothing at all.
  if (symbol === 'ṅ' || symbol === 'Ṅ') out.push('ŋ');
  if (symbol === 'š' || symbol === 'Š') out.push('ʃ');

  // Folding a diacritic away is only defensible where the mark modifies a letter
  // that still carries most of the sound without it — Latin š→s, ṱ→t. In Thai,
  // Burmese, Devanagari, Arabic and Vietnamese the marks ARE the vowels and
  // tones; dropping them does not approximate the word, it deletes it. Thai
  // measured 4.3 s instead of ~15 s because every vowel sign was folded off a
  // consonant and filed as a harmless approximation.
  const stripped = stripDiacritics(symbol);
  if (stripped === '' || stripped === symbol || !isLatinBase(stripped)) return out;
  out.push(stripped);
  return out;
}

/**
 * Judges the BASE that remains, not the composed character: Tshivenda ṱ lives at
 * U+1E71, far above the Latin block, yet strips to a plain 't'. Thai วั strips to
 * ว, which is not Latin at all — the case to refuse.
 */
function isLatinBase(stripped: string): boolean {
  if (stripped === '') return false;
  for (const ch of stripped) if ((ch.codePointAt(0) ?? 0) > 0x024f) return false;
  return true;
}

/** Decompose and remove combining marks: ṱ → t. */
function stripDiacritics(s: string): string {
  let out = '';
  for (const ch of s.normalize('NFD')) if (!isCombiningMark(ch)) out += ch;
  return out;
}

function isCombiningMark(ch: string): boolean {
  // Mn / Mc / Me. Unicode property escapes give the real categories rather than
  // a hand-kept range table.
  return /\p{Mn}|\p{Mc}|\p{Me}/u.test(ch);
}

function isFormat(ch: string): boolean {
  return /\p{Cf}/u.test(ch);
}

// ---------------------------------------------------------------------------
// LexiconTokeniser
// ---------------------------------------------------------------------------

/**
 * Turns text into model tokens using a voice's own lexicon files — a
 * word→phoneme table and a phoneme→id table beside the model. No phonemizer
 * process, no second package, no licence wall.
 */
export class LexiconTokeniser {
  private readonly words: Map<string, number[]>;
  private readonly longest: number;
  /** Blank id, interleaved between tokens when the model expects it. */
  blank: number;
  /** Symbols the lexicon had no entry for on the last call. */
  lastUnmapped: string[] = [];

  private constructor(words: Map<string, number[]>, longest: number, blank: number) {
    this.words = words;
    this.longest = longest;
    this.blank = blank;
  }

  /** Build from a voice's `tokens.txt` and `lexicon.txt` content. */
  static fromText(tokensText: string, lexiconText: string, blank = 0): LexiconTokeniser | null {
    // tokens.txt is "<symbol> <id>" per line. The symbol MAY BE A SPACE, so
    // split on the LAST space rather than the first.
    const ids = new Map<string, number>();
    for (const raw of tokensText.split('\n')) {
      const line = raw.replace(/\r$/, '');
      const cut = line.lastIndexOf(' ');
      if (cut <= 0) continue;
      const id = Number.parseInt(line.slice(cut + 1), 10);
      if (Number.isNaN(id)) continue;
      ids.set(line.slice(0, cut), id);
    }
    if (ids.size === 0) return null;

    // lexicon.txt is "<word> <phoneme> <phoneme> ...".
    const words = new Map<string, number[]>();
    let longest = 1;
    for (const raw of lexiconText.split('\n')) {
      const parts = raw.replace(/\r$/, '').split(' ').filter((p) => p !== '');
      if (parts.length < 2) continue;
      const seq: number[] = [];
      for (const p of parts.slice(1)) {
        const id = ids.get(p);
        if (id !== undefined) seq.push(id);
      }
      if (seq.length === 0) continue;
      words.set(parts[0], seq);
      const n = [...parts[0]].length;
      if (n > longest) longest = n;
    }
    return words.size === 0 ? null : new LexiconTokeniser(words, longest, blank);
  }

  /**
   * Segment `text` and return the model's tokens.
   *
   * LONGEST MATCH FIRST, because these lexicons are word-keyed and the words
   * overlap: あい, あいさつ and あいかわらず all start the same way, and taking the
   * shortest would pronounce a different word. Falls back to the single
   * character when no word matches.
   */
  encode(text: string, interleaveBlank = true): number[] {
    const out: number[] = [];
    const unmapped: string[] = [];
    // CODE POINTS, NOT UTF-16 UNITS: these lexicons are keyed on CJK words, and
    // an index into the raw string would cut a surrogate pair in half.
    const chars = [...text];

    let i = 0;
    while (i < chars.length) {
      let taken = 0;
      const max = Math.min(this.longest, chars.length - i);
      for (let len = max; len > 0; len--) {
        const seq = this.words.get(chars.slice(i, i + len).join(''));
        if (seq) {
          out.push(...seq);
          taken = len;
          break;
        }
      }
      if (taken === 0) {
        if (!/\s/u.test(chars[i])) unmapped.push(chars[i]);
        taken = 1;
      }
      i += taken;
    }

    this.lastUnmapped = unmapped;
    if (!interleaveBlank) return out;

    // add_blank: a blank opens the utterance and follows every token.
    const padded: number[] = [this.blank];
    for (const id of out) {
      padded.push(id, this.blank);
    }
    return padded;
  }
}
