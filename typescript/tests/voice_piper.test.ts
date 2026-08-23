// voice_piper.test.ts
//
// Asserts the TypeScript PiperVoiceConfig / LexiconTokeniser / AudioFormat ports
// against the same golden files the C# reference generates.
//
// The piper fixture carries TWO configs on purpose — one with pad 0 and one with
// pad 3 — so a port that hard-codes either fails on the other. That is THE PAD
// RULE, and getting it wrong is what made 42 MMS voices speak fluent nonsense.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import * as fs from 'fs';
import * as path from 'path';
import {
  PiperVoiceConfig,
  LexiconTokeniser,
  splitPhonemeString,
} from '../src/voice/piper_config';
import { PCM16_MONO_16K } from '../src/voice/contracts';

const FIXTURES = path.resolve(__dirname, '..', '..', 'fixtures');

function readFixture<T>(name: string): T {
  return JSON.parse(fs.readFileSync(path.join(FIXTURES, name), 'utf8')) as T;
}

interface PiperFixture {
  configs: Array<{
    name: string;
    configJson: Record<string, number[]>;
    sampleRate: number;
    padId: number;
    hasPhonemeMap: boolean;
    cases: Array<{
      phonemes: string[];
      ids: number[];
      skipped: number;
      skippedSymbols: string[];
      approximatedSymbols: string[];
    }>;
  }>;
  splitPhonemeString: Array<{ input: string; elements: string[] }>;
}

interface LexFixture {
  tokens: Record<string, number>;
  lexicon: Array<{ word: string; phonemes: string[] }>;
  blank: number;
  cases: Array<{ text: string; ids: number[]; idsWithBlank: number[]; unmapped: string[] }>;
}

describe('voice parity — PiperVoiceConfig', () => {
  const fixture = readFixture<PiperFixture>('voice_piper_config.json');

  it('matches the reference for every config and case', () => {
    assert.equal(fixture.configs.length, 2, 'both pad conventions must be covered');
    for (const c of fixture.configs) {
      const cfg = new PiperVoiceConfig(c.configJson, c.sampleRate);
      assert.equal(cfg.padId, c.padId, `padId for ${c.name}`);
      assert.equal(cfg.hasPhonemeMap, c.hasPhonemeMap, `hasPhonemeMap for ${c.name}`);

      for (const one of c.cases) {
        const got = cfg.phonemesToIds(one.phonemes);
        assert.deepEqual(got.ids, one.ids, `ids for ${one.phonemes} in ${c.name}`);
        assert.equal(got.skipped, one.skipped, `skipped for ${one.phonemes}`);
        assert.deepEqual(got.skippedSymbols, one.skippedSymbols, `skipped for ${one.phonemes}`);
        assert.deepEqual(
          got.approximatedSymbols,
          one.approximatedSymbols,
          `approximated for ${one.phonemes}`,
        );
      }
    }
  });

  it('reads the pad from the model rather than assuming it', () => {
    // THE PAD RULE. The two fixture configs disagree — 0 in the Piper-layout
    // one, 3 in the MMS-layout one — so a port that hard-codes either fails.
    const pads = new Set(fixture.configs.map((c) => c.padId));
    assert.deepEqual(pads, new Set([0, 3]), 'the fixture must cover BOTH pad conventions');
    for (const c of fixture.configs) {
      assert.equal(new PiperVoiceConfig(c.configJson).padId, c.padId);
    }
  });

  it('folds Tshivenda but refuses Thai', () => {
    // The asymmetry is the whole point. Latin ṱ still sounds like a t with the
    // mark gone; Thai ก's marks ARE the vowels, so folding deletes the word.
    const cfg = new PiperVoiceConfig(fixture.configs[0].configJson);
    assert.deepEqual(
      cfg.phonemesToIds(['ṱ']).approximatedSymbols,
      ['ṱ'],
      'ṱ should fold to a Latin base and be REPORTED as approximate',
    );
    assert.deepEqual(
      cfg.phonemesToIds(['ก']).skippedSymbols,
      ['ก'],
      'Thai must be skipped, not folded',
    );
  });

  it('splits into grapheme clusters like the reference', () => {
    for (const c of fixture.splitPhonemeString) {
      assert.deepEqual(splitPhonemeString(c.input), c.elements, `clusters for ${c.input}`);
    }
  });
});

describe('voice parity — LexiconTokeniser', () => {
  const fixture = readFixture<LexFixture>('voice_lexicon_tokeniser.json');

  function makeLexicon(): LexiconTokeniser {
    const tokensText = Object.entries(fixture.tokens)
      .map(([s, id]) => `${s} ${id}`)
      .join('\n');
    const lexiconText = fixture.lexicon
      .map((e) => `${e.word} ${e.phonemes.join(' ')}`)
      .join('\n');
    const lex = LexiconTokeniser.fromText(tokensText, lexiconText, fixture.blank);
    assert.ok(lex, 'fixture lexicon failed to load');
    return lex!;
  }

  it('matches the reference for every case', () => {
    const lex = makeLexicon();
    assert.ok(fixture.cases.length > 0, 'fixture has no cases');
    for (const c of fixture.cases) {
      assert.deepEqual(lex.encode(c.text, false), c.ids, `ids for ${c.text}`);
      assert.deepEqual(lex.lastUnmapped, c.unmapped, `unmapped for ${c.text}`);
      assert.deepEqual(lex.encode(c.text, true), c.idsWithBlank, `idsWithBlank for ${c.text}`);
    }
  });

  it('takes the longest match', () => {
    // あい, あいさつ and あいかわらず all start the same way. Taking the shortest
    // pronounces a different word.
    const lex = makeLexicon();
    const full = lex.encode('あいさつ', false);
    const short = lex.encode('あい', false);
    assert.ok(
      full.length > short.length,
      'あいさつ matched only the あい prefix — this is shortest-match',
    );
  });
});

describe('voice parity — AudioFormat', () => {
  it('matches the reference', () => {
    const fixture = readFixture<{
      pcm16Mono16k: { sampleRate: number; channels: number; bitsPerSample: number };
    }>('voice_audio_format.json');
    assert.deepEqual({ ...PCM16_MONO_16K }, fixture.pcm16Mono16k);
  });
});
