/**
 * voice/nchlt_phonemizer
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// nchlt_phonemizer.ts
//
// Port of src/CircleAI.Voice/NchltPhonemizer.cs.
//
// Parity is asserted against fixtures/voice_nchlt_phonemizer.json, which the C#
// reference generates.
//
// A fully sovereign, permissive-licence grapheme-to-phoneme front-end for the
// South African languages — the piece that turns written text into the X-SAMPA
// phonemes a voice model consumes. It is the click-and-tone-aware component
// that generic engines get wrong on Nguni languages.
//
// WHY THIS EXISTS (and what it deliberately is NOT):
//   * NOT espeak-ng. espeak is GPLv3; linking it taints the app.
//   * NOT phonemeza. That project is unlicensed (all-rights-reserved) and its
//     trained weights are not even published — a hostage we will not depend on.
//   * NOT a neural model. No GPU to build, no runtime for inference.
//
// It is a faithful port of the NCHLT pronunciation predictor (Marelie Davel,
// pron_predict.pl) driven by the NCHLT-inlang resources, all © DAC / CSIR / NWU
// and licensed CC BY 3.0.
//
// Because the rule set covers any word there is no "OOV gap": a word is either
// in the dictionary (exact) or synthesised by the rules, which is what makes
// agglutinative isiZulu tractable.

/** One context rule: grapheme `g` in left/right context -> code. */
interface Rule {
  readonly order: number;
  readonly left: string;
  readonly right: string;
  readonly code: string;
}

export class NchltPhonemizer {
  // word -> its X-SAMPA phone tokens (the exact catalogued pronunciation).
  private readonly dict: Map<string, string[]>;
  // grapheme -> its rules, sorted by order DESCENDING (most specific first).
  private readonly rules: Map<string, Rule[]>;
  // rule phoneme code (single char) -> X-SAMPA symbol.
  private readonly phoneMap: Map<string, string>;
  // grapheme remap applied before rule application (usually identity).
  private readonly graphMap: Map<string, string>;
  // grapheme-null insertions: substring -> replacement (empty for Nguni).
  private readonly gnulls: Array<[string, string]>;

  /**
   * Words in the last `phonemize` call that were synthesised by the rule engine
   * rather than found in the dictionary. A coverage diagnostic, never a failure
   * — the rules always produce output.
   */
  lastRulePredictedWords = 0;

  /**
   * Graphemes in the last call that no rule covered (e.g. stray punctuation
   * that survived normalisation). Skipped, never guessed.
   */
  lastUnknownGraphemes: string[] = [];

  private constructor(
    dict: Map<string, string[]>,
    rules: Map<string, Rule[]>,
    phoneMap: Map<string, string>,
    graphMap: Map<string, string>,
    gnulls: Array<[string, string]>,
  ) {
    this.dict = dict;
    this.rules = rules;
    this.phoneMap = phoneMap;
    this.graphMap = graphMap;
    this.gnulls = gnulls;
  }

  /**
   * Build from the file CONTENTS rather than paths, so a caller can load from an
   * embedded resource or a downloaded bundle with no filesystem in reach.
   */
  static fromText(
    dictText: string,
    rulesText: string,
    phoneMapText: string,
    graphMapText?: string | null,
    gnullsText?: string | null,
  ): NchltPhonemizer {
    return new NchltPhonemizer(
      parseDict(dictText),
      parseRules(rulesText),
      parsePhoneMap(phoneMapText),
      graphMapText ? parseGraphMap(graphMapText) : new Map(),
      gnullsText ? parseGnulls(gnullsText) : [],
    );
  }

  phonemize(text: string): string[] {
    this.lastRulePredictedWords = 0;
    this.lastUnknownGraphemes = [];
    if (!text || text.trim().length === 0) return [];

    const phones: string[] = [];
    for (const word of tokenize(text)) {
      const known = this.dict.get(word);
      if (known !== undefined) {
        phones.push(...known);
      } else {
        phones.push(...this.predictWord(word));
        this.lastRulePredictedWords++;
      }
    }
    return phones;
  }

  /**
   * Predict a single word's X-SAMPA phones from the context rules — the exact
   * algorithm of `g2p_word_olist`: for each grapheme take the highest-order rule
   * whose left/right context matches, emit its code, drop nulls, then remap
   * codes to X-SAMPA.
   */
  predictWord(word: string): string[] {
    if (!word) return [];

    // Grapheme remap (usually identity) then grapheme-null insertion.
    const w = this.applyGnulls(this.mapGraphemes(word));

    const codes: string[] = [];
    for (let i = 0; i < w.length; i++) {
      const g = w[i];
      const gRules = this.rules.get(g);
      if (gRules === undefined) {
        // Skip an unknown grapheme rather than fabricate a phone for it.
        if (!this.lastUnknownGraphemes.includes(g)) this.lastUnknownGraphemes.push(g);
        continue;
      }

      // pat = " " + left-context + "-" + g + "-" + right-context + " "
      const pat = ' ' + w.slice(0, i) + '-' + g + '-' + w.slice(i + 1) + ' ';

      // Rules are pre-sorted most-specific-first; the first context match wins.
      let code = '0';
      for (const r of gRules) {
        if (pat.includes(r.left + '-' + g + '-' + r.right)) {
          code = r.code.length > 0 ? r.code[0] : '0';
          break;
        }
      }
      if (code !== '0') codes.push(code);
    }

    return codes.map((c) => this.phoneMap.get(c) ?? c);
  }

  private mapGraphemes(word: string): string {
    if (this.graphMap.size === 0) return word;
    let out = '';
    for (const c of word) out += this.graphMap.get(c) ?? c;
    return out;
  }

  private applyGnulls(word: string): string {
    if (this.gnulls.length === 0) return word;
    let w = word;
    for (const [from, to] of this.gnulls) w = w.split(from).join(to);
    return w;
  }
}

/**
 * Lower-case and split into word tokens on anything that is not a letter.
 * Diacritics are preserved (Afrikaans ê/ë/ô are real graphemes); digits and
 * punctuation become separators. Number and abbreviation expansion is out of
 * scope and belongs to a text-normalisation pass upstream.
 */
function tokenize(text: string): string[] {
  const words: string[] = [];
  let sb = '';
  for (const ch of text.trim()) {
    if (/\p{L}/u.test(ch)) {
      sb += ch.toLowerCase();
    } else if (sb.length > 0) {
      words.push(sb);
      sb = '';
    }
  }
  if (sb.length > 0) words.push(sb);
  return words;
}

function lines(text: string): string[] {
  // Split the way a StreamReader does, so a CRLF file parses identically.
  return text.split('\n').map((l) => (l.endsWith('\r') ? l.slice(0, -1) : l));
}

function parseDict(text: string): Map<string, string[]> {
  const dict = new Map<string, string[]>();
  for (const line of lines(text)) {
    if (line.length === 0) continue;
    const tab = line.indexOf('\t');
    if (tab <= 0) continue;
    const word = line.slice(0, tab);
    const pron = line.slice(tab + 1).trim();
    if (pron.length === 0 || dict.has(word)) continue; // keep the FIRST variant
    dict.set(word, pron.split(' ').filter((p) => p.length > 0));
  }
  return dict;
}

function parseRules(text: string): Map<string, Rule[]> {
  const byGrapheme = new Map<string, Rule[]>();
  for (const line of lines(text)) {
    if (line.length === 0) continue;
    // grapheme ; left ; right ; code ; order [ ; count ]
    const f = line.split(';');
    if (f.length < 5 || f[0].length === 0) continue;
    const g = f[0][0];
    const order = parseIntStrict(f[4]);
    if (order === null) continue;
    const list = byGrapheme.get(g);
    const rule: Rule = { order, left: f[1], right: f[2], code: f[3] };
    if (list === undefined) byGrapheme.set(g, [rule]);
    else list.push(rule);
  }

  // STABLE sort, descending by order. Two rules of equal order must stay in file
  // order — the reference uses LINQ's OrderByDescending, which is stable, and a
  // port that reaches for an unstable sort will disagree on ties in exactly the
  // dense rule sets where ties are common.
  for (const list of byGrapheme.values()) {
    list.sort((x, y) => y.order - x.order);
  }
  return byGrapheme;
}

/** Integer parse that rejects anything the reference's int.TryParse would. */
function parseIntStrict(s: string): number | null {
  const t = s.trim();
  if (!/^[+-]?\d+$/.test(t)) return null;
  return parseInt(t, 10);
}

function parsePhoneMap(text: string): Map<string, string> {
  // Line: "<code>\t<xsampa>"  (code is a single char).
  const map = new Map<string, string>();
  for (const line of lines(text)) {
    if (line.length === 0) continue;
    const tab = line.indexOf('\t');
    if (tab <= 0) continue;
    const code = line.slice(0, tab);
    if (code.length === 1) map.set(code, line.slice(tab + 1));
  }
  return map;
}

function parseGraphMap(text: string): Map<string, string> {
  // File line: "<funny>\t<std>" — we map std->funny (per remap_dict's gmap).
  const map = new Map<string, string>();
  for (const line of lines(text)) {
    if (line.length === 0) continue;
    const f = line.split('\t');
    if (f.length === 2 && f[0].length === 1 && f[1].length === 1 && f[0] !== f[1]) {
      map.set(f[1], f[0]);
    }
  }
  return map;
}

function parseGnulls(text: string): Array<[string, string]> {
  // File line: "<from>;<to>" — insert grapheme-nulls (empty for Nguni).
  const list: Array<[string, string]> = [];
  for (const line of lines(text)) {
    if (line.length === 0) continue;
    const f = line.split(';');
    if (f.length === 2) list.push([f[0], f[1]]);
  }
  return list;
}
