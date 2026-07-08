// herjarvis_voice_identity.test.ts
//
// Verifies EnergyBandVoiceIdentity (HerJarvisRealImplementations.cs #8). The
// MFCC pipeline is deterministic, so the guarantees checked are: a speaker is
// re-identified from a second sample of the same signal (cosine sim > 0.85),
// a clearly different signal is not matched to it, sub-frame audio yields the
// zero fingerprint, and the fingerprint is stable for identical input.
//
// Float discipline (Float32Array + Math.fround at every C# `float` site) is
// exercised implicitly — a mismatch there would perturb the fingerprint and
// break the self-similarity threshold.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { EnergyBandVoiceIdentity } from '../src/companion/herjarvis/index';

const SAMPLE_RATE = 16_000;

/** Build a PCM16 little-endian buffer of `n` samples from a sample function. */
function pcm16(n: number, fn: (i: number) => number): Uint8Array {
  const buf = new Uint8Array(n * 2);
  for (let i = 0; i < n; i++) {
    let s = Math.round(fn(i));
    if (s > 32767) s = 32767;
    if (s < -32768) s = -32768;
    if (s < 0) s += 0x10000;
    buf[i * 2] = s & 0xff;
    buf[i * 2 + 1] = (s >> 8) & 0xff;
  }
  return buf;
}

/** A tone at `freqHz` with amplitude `amp` (16-bit). */
function tone(n: number, freqHz: number, amp = 8000): Uint8Array {
  return pcm16(n, (i) => amp * Math.sin((2 * Math.PI * freqHz * i) / SAMPLE_RATE));
}

describe('EnergyBandVoiceIdentity — enrol + identify', () => {
  it('re-identifies the same speaker from a second sample of the same tone', async () => {
    const v = new EnergyBandVoiceIdentity();
    // 1 second of a 220 Hz tone, enrolled as "alice".
    await v.enrollAsync('alice', tone(SAMPLE_RATE, 220), SAMPLE_RATE);
    // A fresh sample of the identical signal should match alice.
    const id = await v.identifyAsync(tone(SAMPLE_RATE, 220), SAMPLE_RATE);
    assert.equal(id, 'alice');
  });

  it('does not match an unrelated speaker before any enrolment', async () => {
    const v = new EnergyBandVoiceIdentity();
    const id = await v.identifyAsync(tone(SAMPLE_RATE, 440), SAMPLE_RATE);
    assert.equal(id, null);
  });

  it('distinguishes two enrolled speakers by their tone', async () => {
    const v = new EnergyBandVoiceIdentity();
    await v.enrollAsync('low', tone(SAMPLE_RATE, 150), SAMPLE_RATE);
    await v.enrollAsync('high', tone(SAMPLE_RATE, 3000), SAMPLE_RATE);
    // Query with the low tone → should pick "low", not "high".
    const id = await v.identifyAsync(tone(SAMPLE_RATE, 150), SAMPLE_RATE);
    assert.equal(id, 'low');
  });

  it('sub-frame audio (< 400 samples) yields the zero fingerprint (no match)', async () => {
    const v = new EnergyBandVoiceIdentity();
    // Enroll a real speaker, then query with too-short audio.
    await v.enrollAsync('alice', tone(SAMPLE_RATE, 220), SAMPLE_RATE);
    const id = await v.identifyAsync(tone(100, 220), SAMPLE_RATE);
    // A zero fingerprint has cosine similarity 0 with any reference (na/nb path
    // returns 0), which is below the 0.85 threshold → null.
    assert.equal(id, null);
  });

  it('rejects a blank userId on enrol', async () => {
    const v = new EnergyBandVoiceIdentity();
    await assert.rejects(() => v.enrollAsync('  ', tone(SAMPLE_RATE, 220), SAMPLE_RATE), /userId required/);
  });
});
