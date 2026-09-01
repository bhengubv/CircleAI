/**
 * voice/geez_romanizer
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// geez_romanizer.ts
//
// Port of src/CircleAI.Voice/GeezRomanizer.cs.
//
// Parity is asserted against fixtures/voice_geez_romanizer.json, which the C#
// reference generates.
//
// Ethiopic (Ge'ez) script -> Latin, because the Amharic and Tigrinya voices do
// not read Ethiopic at all. Meta ships those two MMS models with
// `is_uroman: true`: their vocabularies are 28 and 27 LATIN letters and they
// expect text already transliterated. Measured on the P30, Amharic lost 43
// distinct characters and produced 3.2 s of noise for a 15 s paragraph. The
// model has simply never seen an Ethiopic codepoint.
//
// The transliteration is computed, not tabulated, because Unicode lays the
// syllabary out exactly as the script is taught: each consecutive block of
// EIGHT codepoints is one consonant across its vowel orders, so
// consonant = (cp - 0x1200) / 8 and vowel = (cp - 0x1200) % 8. Two small tables
// replace three hundred entries, and every character in the block is covered
// including ones no test phrase happens to contain.

const BASE = 0x1200;
const ORDERS_PER_CONSONANT = 8;

/**
 * Last codepoint that follows the eight-orders-per-consonant layout. The
 * syllabary ends here; everything above is lone syllables, marks and numerals,
 * and treating any of it as a row invents a pronunciation.
 */
const LAST_SYLLABLE = 0x1357;

/**
 * Consonant per 8-codepoint row, in Unicode order. ASCII only: these voices
 * hold 27-28 plain Latin letters, so a transliteration carrying the Ethiopist
 * diacritics would be dropped as surely as the Ethiopic was.
 *
 * Six rows are LABIALISED — the consonant carries a built-in /w/. Writing them
 * plain turns "kwa" into "ka", which silently changes the word.
 */
const CONSONANTS: readonly string[] = [
  'h', 'l', 'h', 'm', 's', 'r', 's', 'sh',
  'q', 'qw', 'q', 'qw', 'b', 'v', 't', 'ch',
  'h', 'hw', 'n', 'ny', '', 'k', 'kw', 'k',
  'kw', 'w', '', 'z', 'zh', 'y', 'd', 'd',
  'j', 'g', 'gw', 'ng', 't', 'ch', 'p', 'ts',
  'ts', 'f', 'p',
];

/**
 * Vowel per order. The sixth is SILENT — it marks a bare consonant, which is
 * why the greeting romanises with no trailing vowel.
 */
const VOWELS: readonly string[] = ['e', 'u', 'i', 'a', 'e', '', 'o', 'wa'];

/**
 * The three syllables Unicode assigns singly rather than as a row of eight.
 * They are already in the -a order, so the vowel is part of the value.
 */
const LONE_SYLLABLES: Readonly<Map<string, string>> = Object.freeze({
  'ፘ': 'rya',
  'ፙ': 'mya',
  'ፚ': 'fya',
});

/**
 * Combining marks. They modify the syllable before them and have no sound of
 * their own, so they are dropped rather than passed through — a bare mark
 * reaching a Latin-only vocabulary is one more unmapped symbol.
 */
const MARKS: ReadonlySet<string> = new Set(['፝', '፞', '፟']);

/** Ethiopic punctuation, mapped so sentence splitting still works. */
const PUNCTUATION: Readonly<Map<string, string>> = Object.freeze({
  '፠': ' ',   // section
  '፡': ' ',   // word separator
  '።': '.',   // full stop
  '፣': ',',   // comma
  '፤': ';',   // semicolon
  '፥': ':',   // colon
  '፦': ':',   // preface colon
  '፧': '?',   // question mark
  '፨': ' ',   // paragraph separator
});

/** True when `text` contains any Ethiopic character. */
export function isEthiopic(text: string | null | undefined): boolean {
  if (!text) return false;
  for (const c of text) {
    const cp = c.codePointAt(0)!;
    if ((cp >= 0x1200 && cp <= 0x137F) || (cp >= 0x1380 && cp <= 0x139F)) return true;
  }
  return false;
}

/**
 * Ethiopic -> Latin. Characters outside the script pass through untouched, so
 * mixed text (numerals, Latin names, punctuation) survives intact.
 */
export function romanize(text: string | null | undefined): string {
  if (!text) return text ?? '';

  let out = '';
  for (const c of text) {
    const p = PUNCTUATION[c];
    if (p !== undefined) { out += p; continue; }

    // THE EIGHT-PER-CONSONANT LAYOUT STOPS AT U+1357, and the range check has to
    // stop with it. Beyond that the block is no longer a syllabary: U+1358..
    // U+135A are three LONE syllables already in their -a order, U+135D..U+135F
    // are combining marks, and U+1369 onward are the numerals. Sizing the check
    // off the consonant table instead swept seven of those numerals back into
    // the syllabary — a table one row too long made the digits read as speech,
    // and it read as sound, so nothing failed.
    if (MARKS.has(c)) continue;
    const lone = LONE_SYLLABLES[c];
    if (lone !== undefined) { out += lone; continue; }

    const cp = c.codePointAt(0)!;
    const i = cp - BASE;
    if (i < 0 || i > LAST_SYLLABLE - BASE) {
      // Numerals and the rarely-used supplement blocks have no sound we can
      // render; anything else is not Ethiopic and is left alone.
      if (cp >= 0x1369 && cp <= 0x137C) continue;
      out += c;
      continue;
    }

    const row = Math.floor(i / ORDERS_PER_CONSONANT);
    const order = i % ORDERS_PER_CONSONANT;

    const consonant = CONSONANTS[row];
    let vowel = VOWELS[order];

    if (consonant.length === 0) {
      // The glottal and pharyngeal rows write no consonant in Latin, so the
      // vowel IS the character. First order is heard as "a", and the sixth —
      // silent after a real consonant — must still sound here, or the word-
      // initial one disappears entirely.
      if (order === 0) vowel = 'a';
      else if (vowel.length === 0) vowel = 'e';
    }

    out += consonant + vowel;
  }
  return out;
}
