// voice_parity.test.ts
//
// Asserts the TypeScript voice port against the SAME golden files the C#
// reference generates (tools/voice-fixtures). Not "does TS do something
// sensible" — "does TS produce identical answers to every other port".
//
// The fixtures are adversarial on purpose: the SentencePiece vocabulary is built
// so greedy longest-match and Viterbi DISAGREE, and the X-SAMPA cases carry a
// multi-character token, the script-g that is U+0261 rather than ASCII 'g', and
// a phone that cannot map and must be REPORTED rather than dropped.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import * as fs from 'fs';
import * as path from 'path';
import {
  xsampaToIpa,
  xsampaCanSayAll,
  xsampaKnownPhones,
  SentencePieceUnigram,
} from '../src/voice/xsampa_to_ipa';

// tests/ -> typescript/ -> CircleAI/ -> fixtures/
const FIXTURES = path.resolve(__dirname, '..', '..', 'fixtures');

function readFixture<T>(name: string): T {
  return JSON.parse(fs.readFileSync(path.join(FIXTURES, name), 'utf8')) as T;
}

interface XsampaFixture {
  knownPhones: string[];
  cases: Array<{
    xsampa: string[];
    ipa: string[];
    unmapped: string[];
    canSayAll: boolean;
  }>;
}

interface SpFixture {
  vocab: Record<string, number>;
  scores: Record<string, number>;
  cases: Array<{ text: string; ids: number[] }>;
}

describe('voice parity — X-SAMPA to IPA', () => {
  const fixture = readFixture<XsampaFixture>('voice_xsampa_to_ipa.json');

  it('matches the reference for every case', () => {
    assert.ok(fixture.cases.length > 0, 'fixture has no cases');
    for (const c of fixture.cases) {
      const { ipa, unmapped } = xsampaToIpa(c.xsampa);
      assert.deepEqual(ipa, c.ipa, `ipa for ${JSON.stringify(c.xsampa)}`);
      assert.deepEqual(unmapped, c.unmapped, `unmapped for ${JSON.stringify(c.xsampa)}`);
      assert.equal(
        xsampaCanSayAll(c.xsampa),
        c.canSayAll,
        `canSayAll for ${JSON.stringify(c.xsampa)}`,
      );
    }
  });

  it('has the same phone table as the reference', () => {
    assert.deepEqual(
      new Set(xsampaKnownPhones()),
      new Set(fixture.knownPhones),
      'the phone table itself has drifted from the reference',
    );
  });

  it('maps g to U+0261 script g, not ASCII g', () => {
    // Called out on its own because it is invisible in a diff: the voice's
    // vocabulary carries ɡ (U+0261) and a plain ASCII 'g' silently misses.
    const { ipa } = xsampaToIpa(['g']);
    assert.deepEqual(ipa, ['ɡ']);
    assert.notDeepEqual(ipa, ['g'], 'ASCII g would be dropped by the voice');
  });
});

describe('voice parity — SentencePiece unigram', () => {
  const fixture = readFixture<SpFixture>('voice_sentencepiece_unigram.json');
  const sp = new SentencePieceUnigram(fixture.vocab, fixture.scores);

  it('matches the reference for every case', () => {
    assert.ok(fixture.cases.length > 0, 'fixture has no cases');
    for (const c of fixture.cases) {
      assert.deepEqual(sp.encode(c.text), c.ids, `ids for ${JSON.stringify(c.text)}`);
    }
  });

  it('does Viterbi, not greedy longest-match', () => {
    // The fixture vocabulary is built so the two disagree: "▁hello" scores WORSE
    // than "▁hell" + "o". Greedy picks the long piece; Viterbi does not. Without
    // this, a greedy port looks correct.
    const want = [fixture.vocab['▁hell'], fixture.vocab['o'], fixture.vocab['▁world']];
    const greedy = [fixture.vocab['▁hello'], fixture.vocab['▁world']];
    const got = sp.encode('hello world');
    assert.deepEqual(got, want);
    assert.notDeepEqual(got, greedy, 'this is the greedy answer — the port is not doing Viterbi');
  });

  it('emits byte fallback in UTF-8 order', () => {
    // é is UTF-8 C3 A9. Emitting A9 C3 does not throw — both are real pieces with
    // real ids — the model just says a different character, and only outside
    // ASCII, which is exactly the languages this catalogue serves.
    const got = sp.encode('hé');
    assert.ok(got.length >= 2, `expected byte fallback pieces, got ${got}`);
    assert.deepEqual(
      got.slice(-2),
      [fixture.vocab['<0xC3>'], fixture.vocab['<0xA9>']],
      'byte fallback emitted UTF-8 bytes in the wrong order',
    );
  });

  it('encodes empty text to nothing', () => {
    assert.deepEqual(sp.encode(''), []);
  });
});
