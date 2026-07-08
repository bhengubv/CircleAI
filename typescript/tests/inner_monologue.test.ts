// inner_monologue.test.ts
//
// Verifies the two IInnerMonologue implementations against the C# reference:
//   TemplateInnerMonologue      (HerJarvisRealImplementations.cs #13) and
//   ReasoningLoopInnerMonologue (ReasoningLoopInnerMonologue.cs).
//
// TemplateInnerMonologue's frame selection replaces C#'s non-reproducible
// string.GetHashCode() with a deterministic FNV-1a hash (see the port note),
// so the same context always yields the same frame — asserted here for
// determinism and correct {summary}/{direction} substitution.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  TemplateInnerMonologue,
  ReasoningLoopInnerMonologue,
} from '../src/companion/reasoning/index';
import { ChatFragmentKind } from '../src/inference/index';
import type {
  ChatFragment,
  ChatMessage,
  GenerationOptions,
  IChatGenerator,
} from '../src/inference/index';

const FRAMES = [
  'Observation: {summary}. Implication: this likely means {direction}.',
  'Looking at {summary}, the salient pattern is {direction}.',
  'Given {summary}, my next step is to {direction}.',
];

function isOneOfTheFrames(thought: string, summary: string, direction: string): boolean {
  return FRAMES.some(
    (f) => f.replace('{summary}', summary).replace('{direction}', direction) === thought,
  );
}

describe('TemplateInnerMonologue', () => {
  const im = new TemplateInnerMonologue();

  it('infers "diagnose the failure first" when the context mentions error', async () => {
    const r = await im.reflectAsync('{"error":"disk full"}');
    // summarise replaces {}[]"" with spaces then splits on space (drop empties):
    // {"error":"disk full"} → tokens: error : disk full  (the colon survives as
    // its own token because it is surrounded by the removed quotes' spaces).
    assert.ok(r.thought.includes('diagnose the failure first'));
    assert.ok(isOneOfTheFrames(r.thought, 'error : disk full', 'diagnose the failure first'));
  });

  it('prefers error over goal over user (first hit wins, in that order)', async () => {
    // Contains all three keywords; error must win.
    const r = await im.reflectAsync('{"error":"x","goal":"y","user":"z"}');
    assert.ok(r.thought.includes('diagnose the failure first'));
    // goal beats user
    const r2 = await im.reflectAsync('{"goal":"y","user":"z"}');
    assert.ok(r2.thought.includes('advance toward the stated goal'));
    // user alone
    const r3 = await im.reflectAsync('{"user":"z"}');
    assert.ok(r3.thought.includes('respond to the user'));
    // none → default
    const r4 = await im.reflectAsync('{"weather":"sunny"}');
    assert.ok(r4.thought.includes('gather more context'));
  });

  it('is deterministic: same context → identical thought', async () => {
    const ctx = '{"weather":"sunny","temp":22}';
    const a = await im.reflectAsync(ctx);
    const b = await im.reflectAsync(ctx);
    assert.equal(a.thought, b.thought);
    assert.ok(isOneOfTheFrames(a.thought, 'weather : sunny , temp :22', 'gather more context'));
  });

  it('summarises to at most the first 12 whitespace tokens', async () => {
    // 15 bare words; only the first 12 survive into the summary.
    const words = Array.from({ length: 15 }, (_, i) => `w${i}`).join(' ');
    const r = await im.reflectAsync(words);
    // The summary segment is the 12-token prefix.
    const summary12 = Array.from({ length: 12 }, (_, i) => `w${i}`).join(' ');
    assert.ok(r.thought.includes(summary12));
    assert.ok(!r.thought.includes('w12'));
  });

  it('throws on null context', async () => {
    await assert.rejects(
      // @ts-expect-error hitting the null guard
      () => im.reflectAsync(null),
      /contextJson required/,
    );
  });
});

// ── Fakes for the reasoning-loop generator ──────────────────────────────────

/** Emits caller-supplied fragments via streamFragmentsAsync (reasoning-aware). */
class FragmentGenerator implements IChatGenerator {
  lastOptions?: GenerationOptions;
  constructor(private readonly frags: ChatFragment[]) {}
  async generateAsync(): Promise<string> {
    return this.frags
      .filter((f) => f.kind === ChatFragmentKind.Content)
      .map((f) => f.text)
      .join('');
  }
  async *streamAsync(): AsyncGenerator<string> {
    for (const f of this.frags) if (f.kind === ChatFragmentKind.Content) yield f.text;
  }
  async *streamFragmentsAsync(
    _messages: readonly ChatMessage[],
    options?: GenerationOptions,
  ): AsyncGenerator<ChatFragment> {
    this.lastOptions = options;
    for (const f of this.frags) yield f;
  }
  dispose(): void {}
}

/** Only implements streamAsync — exercises the Content-only fallback path. */
class ContentOnlyGenerator implements IChatGenerator {
  constructor(private readonly chunks: string[]) {}
  async generateAsync(): Promise<string> {
    return this.chunks.join('');
  }
  async *streamAsync(): AsyncGenerator<string> {
    for (const c of this.chunks) yield c;
  }
  dispose(): void {}
}

/** Throws mid-stream to exercise the swallow-and-fallback branch. */
class ThrowingGenerator implements IChatGenerator {
  async generateAsync(): Promise<string> {
    return '';
  }
  async *streamAsync(): AsyncGenerator<string> {
    throw new Error('boom');
  }
  dispose(): void {}
}

describe('ReasoningLoopInnerMonologue', () => {
  it('prefers the reasoning trace as the thought', async () => {
    const gen = new FragmentGenerator([
      { kind: ChatFragmentKind.Reasoning, text: 'Let me think. ' },
      { kind: ChatFragmentKind.Reasoning, text: 'The user seems tired.' },
      { kind: ChatFragmentKind.Content, text: 'You seem a bit worn out today.' },
    ]);
    const im = new ReasoningLoopInnerMonologue(gen);
    const r = await im.reflectAsync('{"mood":"low"}');
    assert.equal(r.thought, 'Let me think. The user seems tired.'); // trimmed reasoning
  });

  it('passes MaxTokens=256, Temperature=0.5, IncludeReasoning=true', async () => {
    const gen = new FragmentGenerator([{ kind: ChatFragmentKind.Reasoning, text: 'ok' }]);
    const im = new ReasoningLoopInnerMonologue(gen);
    await im.reflectAsync('{}');
    assert.equal(gen.lastOptions?.maxTokens, 256);
    assert.equal(gen.lastOptions?.temperature, 0.5);
    assert.equal(gen.lastOptions?.includeReasoning, true);
  });

  it('falls back to visible content when there is no reasoning', async () => {
    const gen = new ContentOnlyGenerator(['A short ', 'reflection.']);
    const im = new ReasoningLoopInnerMonologue(gen);
    const r = await im.reflectAsync('{"x":1}');
    assert.equal(r.thought, 'A short reflection.');
  });

  it('yields "(no inner state)" when the stream throws and produces nothing', async () => {
    const im = new ReasoningLoopInnerMonologue(new ThrowingGenerator());
    const r = await im.reflectAsync('{"x":1}');
    assert.equal(r.thought, '(no inner state)');
  });

  it('throws on a null generator or null context', async () => {
    // @ts-expect-error null generator
    assert.throws(() => new ReasoningLoopInnerMonologue(null), /llm required/);
    const im = new ReasoningLoopInnerMonologue(
      new FragmentGenerator([{ kind: ChatFragmentKind.Content, text: 'x' }]),
    );
    await assert.rejects(
      // @ts-expect-error null context
      () => im.reflectAsync(null),
      /contextJson required/,
    );
  });
});
