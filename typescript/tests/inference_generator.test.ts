// inference_generator.test.ts
//
// Exercises DeterministicChatGenerator + the <think> token router: prompt
// building parity, reasoning/content split, stop-sequence handling,
// PowerBudget caps, prefix-cache round-trip, and session markers.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import type { ChatMessage } from '../src/models/index';
import { ChatFragmentKind } from '../src/models/index';
import { PowerBudget } from '../src/inference/index';
import {
  DeterministicChatGenerator,
  buildQwenChatPrompt,
  DEFAULT_STOP_SEQUENCES,
} from '../src/inference/generator';
import {
  TokenRouterSink,
  routeChunk,
  drainRemainder,
  tryDrainUtf8,
  tryFindStopSequence,
} from '../src/inference/token_router';
import type { ChatFragment } from '../src/models/index';

describe('buildQwenChatPrompt', () => {
  it('wraps every turn in ChatML and leaves the assistant turn open', () => {
    const msgs: ChatMessage[] = [
      { role: 'system', content: 'be nice' },
      { role: 'user', content: 'hi' },
    ];
    const prompt = buildQwenChatPrompt(msgs);
    assert.equal(
      prompt,
      '<|im_start|>system\nbe nice\n<|im_end|>\n<|im_start|>user\nhi\n<|im_end|>\n<|im_start|>assistant\n',
    );
  });

  it('defaults a blank role to user and lower-cases the role', () => {
    const prompt = buildQwenChatPrompt([{ role: '  USER ', content: 'x' }]);
    assert.ok(prompt.startsWith('<|im_start|>user\nx\n<|im_end|>\n'));
  });
});

describe('token router — <think> split', () => {
  function route(text: string, includeReasoning: boolean, chunk = 5): ChatFragment[] {
    const out: ChatFragment[] = [];
    const sink = new TokenRouterSink(DEFAULT_STOP_SEQUENCES, (f) => out.push(f), includeReasoning);
    for (let i = 0; i < text.length; i += chunk) {
      if (routeChunk(sink, text.substring(i, i + chunk))) break;
    }
    drainRemainder(sink);
    return out;
  }

  it('routes reasoning to Reasoning and answer to Content', () => {
    const frags = route('<think>because</think>answer', true, 4);
    const reasoning = frags.filter((f) => f.kind === ChatFragmentKind.Reasoning).map((f) => f.text).join('');
    const content = frags.filter((f) => f.kind === ChatFragmentKind.Content).map((f) => f.text).join('');
    assert.equal(reasoning, 'because');
    assert.equal(content, 'answer');
  });

  it('drops reasoning text when includeReasoning is false', () => {
    const frags = route('<think>secret</think>answer', false, 3);
    assert.equal(frags.filter((f) => f.kind === ChatFragmentKind.Reasoning).length, 0);
    const content = frags.filter((f) => f.kind === ChatFragmentKind.Content).map((f) => f.text).join('');
    assert.equal(content, 'answer');
  });

  it('stops at a stop sequence and does not leak the marker', () => {
    const out: ChatFragment[] = [];
    const sink = new TokenRouterSink(['<|im_end|>'], (f) => out.push(f), true);
    routeChunk(sink, 'hello');
    const stopped = routeChunk(sink, '<|im_end|>tail');
    drainRemainder(sink);
    assert.equal(stopped, true);
    const content = out.map((f) => f.text).join('');
    assert.equal(content, 'hello');
  });
});

describe('tryDrainUtf8 + tryFindStopSequence', () => {
  it('holds back a partial multi-byte codepoint until complete', () => {
    // 'é' = 0xC3 0xA9. Feed only the lead byte first.
    const buf: number[] = [0x61, 0xc3]; // 'a' + partial
    const first = tryDrainUtf8(buf);
    assert.equal(first, 'a');
    assert.deepEqual(buf, [0xc3]); // partial retained
    buf.push(0xa9);
    const second = tryDrainUtf8(buf);
    assert.equal(second, 'é');
  });

  it('finds the first stop sequence index', () => {
    assert.equal(tryFindStopSequence('abc<|im_end|>', ['<|im_end|>']), 3);
    assert.equal(tryFindStopSequence('nostop', ['<|im_end|>']), -1);
  });
});

describe('DeterministicChatGenerator', () => {
  it('echoes the last user turn deterministically', async () => {
    const gen = new DeterministicChatGenerator();
    const out = await gen.generateAsync([{ role: 'user', content: 'ping' }]);
    assert.equal(out, 'You said: ping');
    gen.dispose();
  });

  it('is deterministic across calls', async () => {
    const g1 = new DeterministicChatGenerator();
    const g2 = new DeterministicChatGenerator();
    const a = await g1.generateAsync([{ role: 'user', content: 'same' }]);
    const b = await g2.generateAsync([{ role: 'user', content: 'same' }]);
    assert.equal(a, b);
  });

  it('splits reasoning into ChatResponse.reasoningContent when emitReasoning', async () => {
    const gen = new DeterministicChatGenerator({ emitReasoning: true });
    const resp = await gen.generateResponse([{ role: 'user', content: 'q' }]);
    assert.equal(resp.text, 'You said: q');
    assert.ok(resp.reasoningContent && resp.reasoningContent.length > 0);
    assert.equal(resp.finishReason, 0 /* Stop */);
  });

  it('streamAsync yields content only (no reasoning)', async () => {
    const gen = new DeterministicChatGenerator({ emitReasoning: true });
    let streamed = '';
    for await (const chunk of gen.streamAsync([{ role: 'user', content: 'z' }])) streamed += chunk;
    assert.equal(streamed, 'You said: z');
  });

  it('caps output words to the PowerBudget.Low token cap (64)', async () => {
    const gen = new DeterministicChatGenerator();
    const long = 'word '.repeat(200).trim();
    const out = await gen.generateAsync([{ role: 'user', content: long }], { budget: PowerBudget.Low });
    // "You said: word word ..." capped to 64 words total.
    assert.equal(out.split(' ').length, 64);
    gen.dispose();
  });

  it('acknowledges an image when vision-capable', async () => {
    const gen = new DeterministicChatGenerator({ visionCapable: true });
    const out = await gen.generateAsync([
      { role: 'user', content: 'what is this', imageBytes: new Uint8Array([1, 2, 3]) },
    ]);
    assert.ok(out.includes('I see the image'));
  });

  it('round-trips a session marker', async () => {
    const gen = new DeterministicChatGenerator();
    const ok = await gen.saveSessionAsync('/tmp/sess-1');
    assert.equal(ok, true);
    const loaded = await gen.loadSessionAsync('/tmp/sess-1');
    assert.equal(loaded, true);
    const missing = await gen.loadSessionAsync('/tmp/does-not-exist');
    assert.equal(missing, false);
  });

  it('throws after dispose', async () => {
    const gen = new DeterministicChatGenerator();
    gen.dispose();
    await assert.rejects(() => gen.generateAsync([{ role: 'user', content: 'x' }]));
  });
});
