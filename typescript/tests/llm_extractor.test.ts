// llm_extractor.test.ts
//
// Verifies LlmKnowledgeGraphExtractor: parses a clean JSON array of triples,
// tolerates prose/markdown-fence-wrapped JSON, defaults confidence when "c" is
// missing/invalid, clamps out-of-range confidence, skips objects with blank
// s/p/o, and returns [] on garbage / on an empty turn / on a failing generator.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { LlmKnowledgeGraphExtractor } from '../src/memory/llm_extractor';
import type { IChatGenerator } from '../src/inference/index';
import type { ChatMessage } from '../src/models/index';

/** Minimal fake IChatGenerator that returns a canned reply, records the messages. */
class FakeChatGenerator implements IChatGenerator {
  lastMessages: readonly ChatMessage[] = [];
  constructor(private readonly reply: string) {}
  async generateAsync(messages: readonly ChatMessage[]): Promise<string> {
    this.lastMessages = messages;
    return this.reply;
  }
  async *streamAsync(): AsyncGenerator<string> {
    yield this.reply;
  }
  dispose(): void {}
}

/** A generator that always throws — exercises the graceful-degradation path. */
class ThrowingChatGenerator implements IChatGenerator {
  async generateAsync(): Promise<string> {
    throw new Error('model offline');
  }
  async *streamAsync(): AsyncGenerator<string> {}
  dispose(): void {}
}

describe('LlmKnowledgeGraphExtractor — clean JSON', () => {
  it('parses a plain JSON array of triples', async () => {
    const gen = new FakeChatGenerator(
      '[{"s":"Tony","p":"has_daughter","o":"Alex","c":0.9},' +
        '{"s":"Alex","p":"lives_in","o":"Durban","c":0.5}]',
    );
    const ex = new LlmKnowledgeGraphExtractor(gen);
    const triples = await ex.extractFromTurnAsync('hi', 'ok', 'ep1');

    assert.equal(triples.length, 2);
    assert.equal(triples[0].subject, 'Tony');
    assert.equal(triples[0].predicate, 'has_daughter');
    assert.equal(triples[0].object, 'Alex');
    assert.equal(triples[0].confidence, 0.9);
    assert.equal(triples[0].source, 'ep1');
    assert.ok(triples[0].recordedAtUtc instanceof Date);
    assert.equal(triples[1].object, 'Durban');
    assert.equal(triples[1].confidence, 0.5);
  });

  it('sends the verbatim system prompt + USER/ASSISTANT-framed user message', async () => {
    const gen = new FakeChatGenerator('[]');
    const ex = new LlmKnowledgeGraphExtractor(gen);
    await ex.extractFromTurnAsync('the weather', 'is sunny', 'ep1');

    assert.equal(gen.lastMessages.length, 2);
    assert.equal(gen.lastMessages[0].role, 'system');
    assert.ok(gen.lastMessages[0].content.startsWith('You are a knowledge-graph extractor.'));
    assert.equal(gen.lastMessages[1].role, 'user');
    assert.equal(gen.lastMessages[1].content, 'USER:\nthe weather\nASSISTANT:\nis sunny\n');
  });
});

describe('LlmKnowledgeGraphExtractor — defensive parsing', () => {
  it('extracts JSON embedded in prose / markdown fences', async () => {
    const gen = new FakeChatGenerator(
      'Sure! Here are the triples:\n```json\n[{"s":"Paris","p":"capital_of","o":"France","c":0.95}]\n```\nHope that helps.',
    );
    const ex = new LlmKnowledgeGraphExtractor(gen);
    const triples = await ex.extractFromTurnAsync('u', 'a', 'ep2');

    assert.equal(triples.length, 1);
    assert.equal(triples[0].subject, 'Paris');
    assert.equal(triples[0].predicate, 'capital_of');
    assert.equal(triples[0].object, 'France');
    assert.equal(triples[0].confidence, 0.95);
  });

  it('defaults confidence to 0.75 when "c" is missing', async () => {
    const gen = new FakeChatGenerator('[{"s":"a","p":"b","o":"c"}]');
    const ex = new LlmKnowledgeGraphExtractor(gen);
    const triples = await ex.extractFromTurnAsync('u', 'a', 'ep3');
    assert.equal(triples.length, 1);
    assert.equal(triples[0].confidence, 0.75);
  });

  it('defaults confidence to 0.75 when "c" is non-numeric', async () => {
    const gen = new FakeChatGenerator('[{"s":"a","p":"b","o":"c","c":"high"}]');
    const ex = new LlmKnowledgeGraphExtractor(gen);
    const triples = await ex.extractFromTurnAsync('u', 'a', 'ep3');
    assert.equal(triples[0].confidence, 0.75);
  });

  it('clamps confidence into [0,1]', async () => {
    const gen = new FakeChatGenerator(
      '[{"s":"a","p":"b","o":"c","c":5},{"s":"d","p":"e","o":"f","c":-2}]',
    );
    const ex = new LlmKnowledgeGraphExtractor(gen);
    const triples = await ex.extractFromTurnAsync('u', 'a', 'ep3');
    assert.equal(triples[0].confidence, 1);
    assert.equal(triples[1].confidence, 0);
  });

  it('skips objects whose s/p/o are blank or missing', async () => {
    const gen = new FakeChatGenerator(
      '[{"s":"","p":"b","o":"c"},{"s":"a","p":"  ","o":"c"},{"s":"a","p":"b"},{"s":"keep","p":"p","o":"o"}]',
    );
    const ex = new LlmKnowledgeGraphExtractor(gen);
    const triples = await ex.extractFromTurnAsync('u', 'a', 'ep3');
    assert.equal(triples.length, 1);
    assert.equal(triples[0].subject, 'keep');
  });

  it('skips non-object array entries', async () => {
    const gen = new FakeChatGenerator('[1, "two", null, {"s":"a","p":"b","o":"c"}]');
    const ex = new LlmKnowledgeGraphExtractor(gen);
    const triples = await ex.extractFromTurnAsync('u', 'a', 'ep3');
    assert.equal(triples.length, 1);
    assert.equal(triples[0].subject, 'a');
  });
});

describe('LlmKnowledgeGraphExtractor — empty results', () => {
  it('returns [] on pure garbage (no brackets)', async () => {
    const gen = new FakeChatGenerator('I could not find any facts, sorry.');
    const ex = new LlmKnowledgeGraphExtractor(gen);
    assert.deepEqual(await ex.extractFromTurnAsync('u', 'a', 'ep4'), []);
  });

  it('returns [] on malformed JSON inside brackets', async () => {
    const gen = new FakeChatGenerator('[{"s":"a", "p": }]');
    const ex = new LlmKnowledgeGraphExtractor(gen);
    assert.deepEqual(await ex.extractFromTurnAsync('u', 'a', 'ep4'), []);
  });

  it('returns [] when the JSON is an object, not an array', async () => {
    const gen = new FakeChatGenerator('{"s":"a","p":"b","o":"c"}');
    const ex = new LlmKnowledgeGraphExtractor(gen);
    // No '[' before ']' — object braces only, so no valid slice.
    assert.deepEqual(await ex.extractFromTurnAsync('u', 'a', 'ep4'), []);
  });

  it('returns [] when both user and assistant text are blank (no LLM call)', async () => {
    const gen = new FakeChatGenerator('[{"s":"a","p":"b","o":"c"}]');
    const ex = new LlmKnowledgeGraphExtractor(gen);
    assert.deepEqual(await ex.extractFromTurnAsync('   ', '', null), []);
  });

  it('returns [] when the generator throws', async () => {
    const ex = new LlmKnowledgeGraphExtractor(new ThrowingChatGenerator());
    assert.deepEqual(await ex.extractFromTurnAsync('u', 'a', 'ep5'), []);
  });
});
