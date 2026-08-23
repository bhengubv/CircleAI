// voice_text_parity.test.ts
//
// Asserts the TypeScript SentenceSplitter / LanguageSpanSplitter /
// GeezRomanizer / ToneShaper / NchltPhonemizer ports against the same golden
// files the C# reference generates.
//
// Every case in these fixtures is adversarial. The splitter fixture carries a
// decimal point and a domain name that must NOT split next to a danda and a CJK
// stop that must; the Ge'ez fixture carries the numerals that used to romanise
// as syllables; the tone fixture separates the biquad (bit-reproducible) from
// the coefficient derivation (pow/sin/cos, which no language guarantees to the
// last bit).

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import * as fs from 'fs';
import * as path from 'path';
import { splitSentences, MAX_CHARS_PER_SEGMENT } from '../src/voice/sentence_splitter';
import {
  splitLanguageSpans,
  toSpokenForm,
  isForeignWord,
} from '../src/voice/language_spans';
import { isEthiopic, romanize } from '../src/voice/geez_romanizer';
import {
  WARM,
  biquad,
  lowShelfCoefficients,
  peakingCoefficients,
  applyToneShaper,
  type BiquadCoefficients,
} from '../src/voice/tone_shaper';
import { NchltPhonemizer } from '../src/voice/nchlt_phonemizer';

const FIXTURES = path.resolve(__dirname, '..', '..', 'fixtures');

function readFixture<T>(name: string): T {
  return JSON.parse(fs.readFileSync(path.join(FIXTURES, name), 'utf8')) as T;
}

describe('voice parity — SentenceSplitter', () => {
  const fixture = readFixture<{
    maxCharsPerSegment: number;
    pauses: { sentence: number; clause: number; paragraph: number; forced: number };
    cases: Array<{
      name: string;
      text: string;
      segments: Array<{ text: string; trailingPauseMs: number }>;
    }>;
  }>('voice_sentence_splitter.json');

  it('matches the reference for every case', () => {
    assert.equal(MAX_CHARS_PER_SEGMENT, fixture.maxCharsPerSegment);
    for (const c of fixture.cases) {
      assert.deepEqual(
        splitSentences(c.text).map((s) => ({ ...s })),
        c.segments,
        `segments for ${c.name}`,
      );
    }
  });

  it('splits scripts that do not punctuate in Latin', () => {
    // A Latin-only terminator list under-splits for about a billion people and
    // fails silently — the paragraph simply runs together. These four scripts
    // are the ones that were measured wrong on the P30.
    for (const name of ['devanagari-danda', 'urdu-full-stop', 'cjk-no-space', 'khmer-khan']) {
      const c = fixture.cases.find((x) => x.name === name)!;
      assert.ok(splitSentences(c.text).length > 1, `${name} must split`);
    }
  });

  it('does not split a decimal point or a domain name', () => {
    for (const name of ['decimal-point', 'domain-name']) {
      const c = fixture.cases.find((x) => x.name === name)!;
      const got = splitSentences(c.text);
      assert.equal(got.length, 2, `${name} splits only at the real sentence end`);
    }
  });

  it('gives the last segment no trailing pause', () => {
    for (const c of fixture.cases) {
      const got = splitSentences(c.text);
      if (got.length > 0) assert.equal(got[got.length - 1].trailingPauseMs, 0, c.name);
    }
  });
});

describe('voice parity — LanguageSpanSplitter', () => {
  const fixture = readFixture<{
    split: Array<{ text: string; spans: Array<{ text: string; isForeign: boolean }> }>;
    toSpokenForm: Array<{ input: string; output: string }>;
    isForeignWord: Array<{ word: string; foreign: boolean }>;
  }>('voice_language_spans.json');

  it('splits where the language changes', () => {
    for (const c of fixture.split) {
      assert.deepEqual(
        splitLanguageSpans(c.text).map((s) => ({ ...s })),
        c.spans,
        `spans for ${c.text}`,
      );
    }
  });

  it('rewrites compounds into a form the voice can say', () => {
    for (const c of fixture.toSpokenForm) {
      assert.equal(toSpokenForm(c.input), c.output, `spoken form of ${c.input}`);
    }
  });

  it('flags only what is unambiguous', () => {
    for (const c of fixture.isForeignWord) {
      assert.equal(isForeignWord(c.word), c.foreign, `isForeignWord(${c.word})`);
    }
    // The conservatism is the contract, not an accident: an ordinary lowercase
    // English word must NOT be flagged, because guessing wrong mispronounces a
    // native word to fix a foreign one.
    assert.equal(isForeignWord('hello'), false);
    assert.equal(isForeignWord('Ngiyabonga'), false);
  });
});

describe('voice parity — GeezRomanizer', () => {
  const fixture = readFixture<{
    isEthiopic: Array<{ text: string; ethiopic: boolean }>;
    romanize: Array<{ input: string; output: string }>;
  }>('voice_geez_romanizer.json');

  it('detects the script', () => {
    for (const c of fixture.isEthiopic) {
      assert.equal(isEthiopic(c.text), c.ethiopic, `isEthiopic(${c.text})`);
    }
  });

  it('romanises exactly like the reference', () => {
    for (const c of fixture.romanize) {
      assert.equal(romanize(c.input), c.output, `romanize(${c.input})`);
    }
  });

  it('drops the numerals instead of speaking them', () => {
    // The eight-per-consonant layout stops at U+1357. Sizing the range check off
    // the consonant table swept seven numerals back into the syllabary, and they
    // came out as sound, so nothing failed.
    assert.equal(romanize('፩፪፫'), '');
    assert.equal(romanize('ፘፙፚ'), 'ryamyafya', 'the three LONE syllables are not a row');
  });
});

describe('voice parity — ToneShaper', () => {
  const fixture = readFixture<{
    waveformTolerance: number;
    coefficientTolerance: number;
    settings: {
      lowShelfHz: number; lowShelfDb: number;
      presenceHz: number; presenceDb: number; presenceQ: number;
      lowShelfSlope: number;
    };
    coefficients: Array<{
      sampleRate: number;
      lowShelf: { b: number[]; a: number[] };
      peaking: { b: number[]; a: number[] };
    }>;
    waveform: { sampleRate: number; input: number[]; output: number[] };
    silenceStaysSilent: number[];
  }>('voice_tone_shaper.json');

  it('uses the measured settings', () => {
    // Field by field, and NOT deepEqual against the whole fixture object: the
    // shelf slope is a private constant of the filter, not a setting anyone may
    // pass in, so it appears in the fixture without belonging on WARM.
    assert.equal(WARM.lowShelfHz, fixture.settings.lowShelfHz);
    assert.equal(WARM.lowShelfDb, fixture.settings.lowShelfDb);
    assert.equal(WARM.presenceHz, fixture.settings.presenceHz);
    assert.equal(WARM.presenceDb, fixture.settings.presenceDb);
    assert.equal(WARM.presenceQ, fixture.settings.presenceQ);
    assert.equal(fixture.settings.lowShelfSlope, 0.9, 'the shelf slope is fixed at 0.9');
  });

  it('derives the same coefficients', () => {
    // 1e-9 relative, not exact: pow, sin and cos are not bit-identical across
    // languages, and pretending otherwise makes a flaky test rather than a
    // strict one.
    const tol = fixture.coefficientTolerance;
    for (const c of fixture.coefficients) {
      const got = {
        lowShelf: lowShelfCoefficients(WARM, c.sampleRate),
        peaking: peakingCoefficients(WARM, c.sampleRate),
      };
      for (const [name, want] of [['lowShelf', c.lowShelf], ['peaking', c.peaking]] as const) {
        const g = got[name as 'lowShelf' | 'peaking'];
        for (let i = 0; i < 3; i++) {
          assertClose(g.b[i], want.b[i], tol, `${name} b[${i}] at ${c.sampleRate}`);
          assertClose(g.a[i], want.a[i], tol, `${name} a[${i}] at ${c.sampleRate}`);
        }
      }
    }
  });

  it('filters the fixture waveform to the same samples', () => {
    // The biquad is add and multiply on doubles, so THIS half is expected to
    // agree everywhere. Driving it from the fixture's own coefficients keeps the
    // transcendental functions out of the comparison.
    const { sampleRate, input, output } = fixture.waveform;
    const coeffs = fixture.coefficients.find((c) => c.sampleRate === sampleRate)!;

    const x = Float32Array.from(input);
    const before = peakOf(x);
    biquad(x, coeffs.lowShelf as BiquadCoefficients);
    biquad(x, coeffs.peaking as BiquadCoefficients);
    const after = peakOf(x);
    if (after > 0 && after > before) {
      const g = Math.fround(before / after);
      for (let i = 0; i < x.length; i++) x[i] = Math.fround(x[i] * g);
    }

    for (let i = 0; i < output.length; i++) {
      assertClose(x[i], output[i], fixture.waveformTolerance, `sample ${i}`);
    }
  });

  it('leaves silence alone rather than dividing by its peak', () => {
    const silence = new Float32Array(fixture.silenceStaysSilent.length);
    applyToneShaper(silence, fixture.waveform.sampleRate);
    for (let i = 0; i < silence.length; i++) {
      assert.equal(silence[i], fixture.silenceStaysSilent[i], `silence ${i}`);
    }
  });

  it('applies both filters, not just one', () => {
    // A port that dropped the presence dip would still change the waveform, so
    // "it moved" proves nothing — the two stages must differ from each other.
    const x = Float32Array.from(fixture.waveform.input);
    const onlyShelf = Float32Array.from(fixture.waveform.input);
    applyToneShaper(x, fixture.waveform.sampleRate);
    biquad(onlyShelf, lowShelfCoefficients(WARM, fixture.waveform.sampleRate));
    assert.ok(
      Array.from(x).some((v, i) => Math.abs(v - onlyShelf[i]) > 1e-4),
      'the presence dip made no difference — it was not applied',
    );
  });

  function peakOf(x: Float32Array): number {
    let p = 0;
    for (const v of x) { const a = Math.abs(v); if (a > p) p = a; }
    return p;
  }

  function assertClose(got: number, want: number, tol: number, what: string): void {
    const scale = Math.max(1, Math.abs(want));
    assert.ok(
      Math.abs(got - want) <= tol * scale,
      `${what}: got ${got}, want ${want} (tolerance ${tol})`,
    );
  }
});

describe('voice parity — NchltPhonemizer', () => {
  const fixture = readFixture<{
    dict: string;
    rules: string;
    phoneMap: string;
    graphMap: string;
    gnulls: string;
    cases: Array<{
      name: string; text: string; phones: string[];
      rulePredictedWords: number; unknownGraphemes: string[];
    }>;
    predictWord: Array<{ word: string; phones: string[] }>;
  }>('voice_nchlt_phonemizer.json');

  function make(): NchltPhonemizer {
    return NchltPhonemizer.fromText(
      fixture.dict, fixture.rules, fixture.phoneMap, fixture.graphMap, fixture.gnulls,
    );
  }

  it('matches the reference for every case', () => {
    for (const c of fixture.cases) {
      const p = make();
      assert.deepEqual(p.phonemize(c.text), c.phones, `phones for ${c.name}`);
      assert.equal(p.lastRulePredictedWords, c.rulePredictedWords, `ruleWords for ${c.name}`);
      assert.deepEqual(p.lastUnknownGraphemes, c.unknownGraphemes, `unknown for ${c.name}`);
    }
  });

  it('predicts unseen words from the rules', () => {
    for (const c of fixture.predictWord) {
      assert.deepEqual(make().predictWord(c.word), c.phones, `predictWord(${c.word})`);
    }
  });

  it('prefers the dictionary over the rules', () => {
    // Both paths can pronounce this word. The dictionary must win, and the rule
    // counter must show it did — the counter is the only evidence of which path
    // ran, and a port that always predicted would still return sensible phones.
    const p = make();
    p.phonemize('sawubona');
    assert.equal(p.lastRulePredictedWords, 0, 'a catalogued word must not be predicted');
  });

  it('reports an unknown grapheme instead of guessing', () => {
    const p = make();
    p.phonemize('azb');
    assert.deepEqual(p.lastUnknownGraphemes, ['z']);
  });
});
