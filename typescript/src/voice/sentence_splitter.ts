// sentence_splitter.ts
//
// Port of src/CircleAI.Voice/SentenceSplitter.cs.
//
// Parity is asserted against fixtures/voice_sentence_splitter.json, which the
// C# reference generates. If this file and that disagree, one of them is wrong
// and the test names the case.
//
// Why this has to exist: the voices in use here were trained on text with the
// punctuation stripped out, so their vocabularies contain no '.', ',', '?' or
// ':' at all. Feeding a paragraph in one pass produces one unbroken run of
// speech — no pause between sentences, because there is no token that could
// encode one. The pause has to come from outside the model.
//
// It splits at SENTENCE boundaries only, never at commas. Each synthesis is an
// independent utterance and a VITS model ends every utterance with falling,
// sentence-final prosody, so cutting at a comma would make each clause land
// like a finished sentence — worse prosody than the run-on it was meant to fix.

/** One unit of speech, plus the silence that should follow it. */
export interface SpeechSegment {
  /** The text to synthesise. Never empty or whitespace. */
  readonly text: string;
  /**
   * Silence to append after this segment, in milliseconds. 0 for the final
   * segment — trailing silence at the end of a passage serves nothing.
   */
  readonly trailingPauseMs: number;
}

// Pause lengths are the perceptual point of this module, so they are named
// rather than buried. A full stop reads longer than a colon; a paragraph break
// longer than either.
const SENTENCE_PAUSE_MS = 280;
const CLAUSE_PAUSE_MS = 200; // ':' and ';' — a lighter break
const PARAGRAPH_PAUSE_MS = 400;
const FORCED_PAUSE_MS = 60; // an over-long run cut for latency

/**
 * Beyond this many characters a segment is cut even without punctuation. A
 * single unbroken clause of this size is already several seconds of audio, and
 * on a phone the whole segment must render before ANY of it can play. The cut
 * is taken at a word boundary and given only a token pause.
 */
export const MAX_CHARS_PER_SEGMENT = 220;

/**
 * Characters that end a sentence, across the scripts we speak.
 *
 * A Latin-only list silently under-splits every language that punctuates
 * differently. Measured on the P30: Hindi, Bengali and Urdu produced THREE
 * segments from the same five-sentence text that gave six in eleven other
 * languages, because Devanagari and Bengali end sentences with the danda and
 * Urdu with its own full stop — none of which were listed. The paragraph ran
 * together exactly as it did before the splitter existed, for about a billion
 * people, and nothing failed loudly enough to notice.
 */
const TERMINATORS = new Set<string>([
  '.', '!', '?', ':', ';',        // Latin / Cyrillic / Greek
  '।', '॥',             // danda, double danda — Devanagari, Bengali, Gurmukhi
  '۔', '؟', '؛',   // Arabic script — Urdu, Arabic, Persian, Pashto
  '。', '！', '？',   // CJK ideographic + fullwidth
  '．', '：', '；',   // fullwidth
  '።',                       // Ethiopic — Amharic, Tigrinya
  '។',                       // Khmer khan
  '၊', '။',             // Myanmar little/section
]);

/**
 * Terminators that can legitimately appear inside a token, and so need a
 * following space before they may be read as ending a sentence.
 */
const MAY_OCCUR_INSIDE_A_TOKEN = new Set<string>(['.', ':', ';']);

const CLOSERS = new Set<string>(['"', "'", ')', ']']);

function isWhitespace(c: string): boolean {
  return /\s/.test(c);
}

function isDigit(c: string): boolean {
  return c >= '0' && c <= '9';
}

/**
 * True when the terminator at `i` really ends a sentence.
 *
 * A period between digits is a decimal ("3.5"), and one followed directly by a
 * letter is usually an abbreviation or a URL — splitting there would cut a word
 * in half and insert a pause inside it.
 */
function endsSentence(text: string, i: number): boolean {
  // Absorb any run of closing punctuation ("...", "?!", ".").
  let j = i + 1;
  while (j < text.length && (TERMINATORS.has(text[j]) || CLOSERS.has(text[j]))) j++;

  if (j >= text.length) return true; // end of input

  // Only SOME terminators can appear inside a token — '.' in 3.5 and co.za,
  // ':' in 12:30. For those, a following space is what separates a sentence end
  // from a decimal point. The rest cannot occur mid-token in any script, and
  // demanding a space after them would never split Chinese, Japanese, Khmer,
  // Thai or Burmese at all: those scripts write without spaces between words,
  // so their full stop is followed by the next letter.
  if (!MAY_OCCUR_INSIDE_A_TOKEN.has(text[i])) return true;

  if (!isWhitespace(text[j])) return false; // 3.5, e.g., co.za

  if (text[i] === '.' && i > 0 && isDigit(text[i - 1]) && j + 1 < text.length
      && isDigit(text[j + 1])) {
    return false;
  }

  return true;
}

/**
 * True when the segment has something to say. A segment of nothing but
 * punctuation has no sound to make, and the voice has no token for it either.
 */
function hasSpeech(s: string): boolean {
  for (const ch of s) {
    // Letter or digit, across every script — not just ASCII.
    if (/[\p{L}\p{N}]/u.test(ch)) return true;
  }
  return false;
}

function flush(segments: SpeechSegment[], current: string, pauseMs: number): string {
  const s = current.trim();
  if (s.length === 0) return '';

  // The terminator STAYS in the segment text, deliberately.
  //
  // It is tempting to strip it — this module has already turned it into a pause,
  // and the MMS voices have no token for it. But the SA-11 voice's vocabulary
  // DOES carry '?' and '.', so it can render a real question rise that no
  // inserted silence could imitate. Stripping would have discarded that from all
  // eleven South African languages to tidy up a log line.

  if (!hasSpeech(s)) return '';

  segments.push({ text: s, trailingPauseMs: pauseMs });
  return '';
}

/**
 * Cuts an over-long run at the last space, so the break lands between words
 * rather than inside one. With no space to use the run is left intact — a
 * mid-word cut would be audibly worse than a long segment.
 */
function cutAtWordBoundary(segments: SpeechSegment[], current: string): string {
  const cut = current.lastIndexOf(' ');
  if (cut <= 0) return current;

  const head = current.slice(0, cut).trim();
  if (head.length > 0) segments.push({ text: head, trailingPauseMs: FORCED_PAUSE_MS });

  return current.slice(cut + 1);
}

/**
 * Splits `text` into segments. Returns a single segment when there is no
 * sentence punctuation, and an empty list for blank input.
 */
export function splitSentences(text: string | null | undefined): SpeechSegment[] {
  const segments: SpeechSegment[] = [];
  if (text === null || text === undefined || text.trim().length === 0) return segments;

  let current = '';
  const pending = SENTENCE_PAUSE_MS;

  // INDEXED BY UTF-16 UNIT, not by code point, to match the reference exactly.
  // Every terminator in the table is in the BMP, so a surrogate pair can never
  // be mistaken for one; iterating by code point instead would change where the
  // MAX_CHARS_PER_SEGMENT cut lands on emoji-bearing text.
  for (let i = 0; i < text.length; i++) {
    const c = text[i];

    if (c === '\r') continue;
    if (c === '\n') {
      current = flush(segments, current, PARAGRAPH_PAUSE_MS);
      continue;
    }

    current += c;

    if (TERMINATORS.has(c) && endsSentence(text, i)) {
      current = flush(segments, current,
        c === ':' || c === ';' ? CLAUSE_PAUSE_MS : SENTENCE_PAUSE_MS);
      continue;
    }

    if (current.length >= MAX_CHARS_PER_SEGMENT) {
      current = cutAtWordBoundary(segments, current);
    }
  }

  flush(segments, current, pending);

  // Nothing should follow the last word — a trailing pause is dead air.
  if (segments.length > 0) {
    segments[segments.length - 1] = {
      text: segments[segments.length - 1].text,
      trailingPauseMs: 0,
    };
  }

  return segments;
}
