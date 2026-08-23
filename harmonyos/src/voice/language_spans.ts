// language_spans.ts
//
// Port of src/CircleAI.Voice/LanguageSpanSplitter.cs.
//
// Parity is asserted against fixtures/voice_language_spans.json, which the C#
// reference generates.
//
// People do not speak one language per sentence. "Igama lami ngu-CircleAI" is
// isiZulu with an English name inside it, and read wholly in isiZulu the name
// comes out mangled — the listener hears the machine fail at a word they know
// perfectly well. South African speech is full of this: brand names, acronyms,
// borrowed nouns, all carried inside an African-language sentence with an
// isiZulu or Sesotho prefix glued on the front.
//
// A multi-lingual model takes ONE language id per utterance, so the fix is to
// cut the text where the language changes and synthesise each run under its own
// id.

/** A run of text to be spoken in one language. */
export interface LanguageSpan {
  /** The words, with their spacing preserved. */
  readonly text: string;
  /**
   * True when this run is the embedded language (English), false for the
   * surrounding one. The caller maps that to whatever ids its model uses.
   */
  readonly isForeign: boolean;
}

const LETTER_OR_DIGIT = /[\p{L}\p{N}]/u;
const UPPER = /\p{Lu}/u;
const LOWER = /\p{Ll}/u;
const LETTER = /\p{L}/u;

function isLetterOrDigit(c: string): boolean {
  return LETTER_OR_DIGIT.test(c);
}

/**
 * Is this token unmistakably foreign (English) inside African-language text?
 *
 * Two signals only, both chosen because native orthographies do not produce
 * them:
 *
 *   internal capitals    — CircleAI, WhatsApp, MTN's brand spellings
 *   all-caps, 2-5 letters — GPS, SMS, ATM, PIN
 *
 * isiZulu, isiXhosa, Sesotho and the rest capitalise the first letter of a
 * sentence or a proper noun and nothing else, so neither pattern arises
 * naturally. A sentence-initial capital is therefore NOT a signal, which is why
 * only capitals after position zero count.
 *
 * It does NOT try to spot ordinary lowercase English words like "computer" —
 * that needs a lexicon per language pair, and guessing wrong is worse than not
 * guessing: mispronouncing a native word to "fix" a foreign one insults the
 * speaker in their own language.
 */
export function isForeignWord(word: string): boolean {
  if (word.length < 2) return false;

  let upper = 0;
  let lower = 0;
  let hasInternalCapital = false;

  for (let i = 0; i < word.length; i++) {
    const c = word[i];
    if (!LETTER.test(c)) continue;
    if (UPPER.test(c)) {
      upper++;
      if (i > 0) hasInternalCapital = true;
    } else {
      lower++;
    }
  }

  if (hasInternalCapital && lower > 0) return true; // CircleAI, WhatsApp
  if (upper >= 2 && lower === 0 && word.length <= 5) return true; // GPS, SMS, ATM
  return false;
}

/**
 * Splits `text` into spans. Returns a single span when the text is all one
 * language, which is the overwhelmingly common case — callers can check
 * `length === 1` and take their existing single-language path.
 */
export function splitLanguageSpans(text: string | null | undefined): LanguageSpan[] {
  if (text === null || text === undefined || text.trim().length === 0) return [];

  const spans: LanguageSpan[] = [];
  let current = '';
  let currentIsForeign: boolean | null = null;

  let i = 0;
  while (i < text.length) {
    // Separators (spaces, punctuation, the hyphen in "ngu-CircleAI") ride along
    // with whatever run they FOLLOW, so a language change never strands a comma
    // on its own or splits mid-punctuation.
    if (!isLetterOrDigit(text[i])) {
      const sepStart = i;
      while (i < text.length && !isLetterOrDigit(text[i])) i++;
      current += text.slice(sepStart, i);
      continue;
    }

    const wordStart = i;
    while (i < text.length && isLetterOrDigit(text[i])) i++;
    const word = text.slice(wordStart, i);
    const foreign = isForeignWord(word);

    if (currentIsForeign !== null && currentIsForeign !== foreign) {
      // The run ends at the last word, not at the separators that follow it —
      // those have already been appended and belong to the join.
      spans.push({ text: current, isForeign: currentIsForeign });
      current = '';
    }

    currentIsForeign = foreign;
    current += word;
  }

  if (current.length > 0 && currentIsForeign !== null) {
    spans.push({ text: current, isForeign: currentIsForeign });
  }

  return spans;
}

/**
 * Rewrites a run into the form a voice can actually pronounce, without changing
 * what is displayed.
 *
 * A compound like `CircleAI` is one token to a synthesiser and it has no idea
 * where the words are, so it produces a mumble. Written `Circle AI` it is two
 * things the voice already knows how to say. This is why the name came out
 * garbled even after it was correctly switched to English — the language was
 * right and the word was still unreadable.
 */
export function toSpokenForm(text: string): string {
  if (!text) return text;

  // 1. Break the compound into words at case boundaries, which is where the
  //    word boundaries genuinely are in this naming style.
  let spaced = '';
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (i > 0 && UPPER.test(c)) {
      const prev = text[i - 1];
      const next = i + 1 < text.length ? text[i + 1] : '';

      // lower->Upper is a word boundary (Circle|AI, You|Tube).
      const afterLower = LOWER.test(prev);
      // Upper->Upper->lower ends a run of capitals (API|Key).
      const endOfAcronym = UPPER.test(prev) && next !== '' && LOWER.test(next);

      if (afterLower || endOfAcronym) spaced += ' ';
    }
    spaced += c;
  }

  // 2. Punctuate the acronyms. "AI" as a bare token gets read as a word — "ay" —
  //    where "A.I." is read as the letters, which is what it is. Same for GPS,
  //    API, SMS. The full stops are for the voice, not the reader.
  let out = '';
  for (let i = 0; i < spaced.length;) {
    if (!UPPER.test(spaced[i])) { out += spaced[i++]; continue; }

    const start = i;
    while (i < spaced.length && UPPER.test(spaced[i])) i++;
    const run = spaced.slice(start, i);

    // A lone capital is an ordinary word opening ("Sawubona"), not an acronym,
    // and a run followed by lowercase was already split above.
    if (run.length < 2) { out += run; continue; }

    for (const ch of run) out += ch + '.';
  }
  return out;
}
