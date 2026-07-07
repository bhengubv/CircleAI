// companion_session.test.ts
//
// Verifies the concrete CompanionSession end-to-end: a turn recalls fused memory
// + the user's own facts into the system prompt, calls the generator, persists
// the exchange, hands it to the background encoder, recalls it on a later turn,
// and streams.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { CompanionSession } from '../src/companion/session';
import { InterfaceKind } from '../src/companion';
import { HeuristicBeliefExtractor, SelfBeliefStore } from '../src/companion/belief';
import { CompanionMemoryEncoder } from '../src/companion/memory_encoder';
import { InMemoryEpisodicStore } from '../src/memory/stores';
import { FusedRecall } from '../src/memory/recall';
import { InMemoryKnowledgeGraph } from '../src/memory/graph';
import { HeuristicKnowledgeGraphExtractor } from '../src/memory/extractor';
import type { ChatMessage, IChatGenerator } from '../src/inference/index';

/** Records the prompt it was handed and returns a canned reply / chunks. */
class CapturingGenerator implements IChatGenerator {
  lastMessages: readonly ChatMessage[] = [];
  constructor(
    private readonly reply: string,
    private readonly chunks?: string[],
  ) {}
  async generateAsync(messages: readonly ChatMessage[]): Promise<string> {
    this.lastMessages = messages;
    return this.reply;
  }
  async *streamAsync(messages: readonly ChatMessage[]): AsyncGenerator<string> {
    this.lastMessages = messages;
    for (const c of this.chunks ?? [this.reply]) yield c;
  }
  dispose(): void {}
}

async function recordSelfFact(beliefs: SelfBeliefStore, text: string): Promise<void> {
  const bx = new HeuristicBeliefExtractor();
  for (const b of await bx.extractAsync(text, 't0')) beliefs.record(b);
}

function makeSession(
  generator: IChatGenerator,
  episodic: InMemoryEpisodicStore,
  extras: {
    beliefs?: SelfBeliefStore;
    encoder?: CompanionMemoryEncoder;
    graph?: InMemoryKnowledgeGraph;
  } = {},
): CompanionSession {
  const recall = new FusedRecall(episodic, null);
  return new CompanionSession(generator, episodic, recall, {
    sessionId: 's1',
    identityId: 'u1',
    interface: InterfaceKind.Mobile,
    beliefs: extras.beliefs ?? null,
    encoder: extras.encoder ?? null,
  });
}

describe('CompanionSession — send path', () => {
  it('injects recalled memories AND user facts into the system prompt', async () => {
    const episodic = new InMemoryEpisodicStore();
    await episodic.addAsync({
      id: 'seed1',
      recordedAtUtc: new Date('2026-01-01T00:00:00Z'),
      userText: 'I have a peanut allergy',
      assistantText: 'Noted',
    });
    const beliefs = new SelfBeliefStore();
    await recordSelfFact(beliefs, 'i am vegetarian');

    const gen = new CapturingGenerator('Here are some options');
    const session = makeSession(gen, episodic, { beliefs });

    const reply = await session.sendAsync('what can I eat?');
    assert.equal(reply, 'Here are some options');

    const system = gen.lastMessages[0];
    assert.equal(system.role, 'system');
    assert.match(system.content, /peanut allergy/, 'recalled memory should be in the prompt');
    assert.match(system.content, /vegetarian/, 'user fact should be in the prompt');

    // The user's actual message is the last turn handed to the generator.
    assert.equal(gen.lastMessages[gen.lastMessages.length - 1].content, 'what can I eat?');
  });

  it('persists the turn and grows the history', async () => {
    const episodic = new InMemoryEpisodicStore();
    const session = makeSession(new CapturingGenerator('ok'), episodic);

    await session.sendAsync('hello');
    assert.equal(await episodic.countAsync(), 1);
    assert.equal(session.history.length, 2); // user + assistant
    assert.equal(session.history[0].role, 'user');
    assert.equal(session.history[1].role, 'assistant');
  });

  it('recalls a prior turn on a later turn (memory persists across the session)', async () => {
    const episodic = new InMemoryEpisodicStore();
    const gen = new CapturingGenerator('noted');
    const session = makeSession(gen, episodic);

    await session.sendAsync('my favourite colour is blue');
    await session.sendAsync("what's my favourite colour?");

    const system = gen.lastMessages[0];
    assert.match(system.content, /favourite colour is blue/, 'the earlier turn should be recalled');
  });

  it('hands the turn to the background encoder, filling the graph', async () => {
    const episodic = new InMemoryEpisodicStore();
    const graph = new InMemoryKnowledgeGraph();
    const encoder = new CompanionMemoryEncoder(new HeuristicKnowledgeGraphExtractor(), graph);
    const session = makeSession(new CapturingGenerator('ok'), episodic, { encoder });

    await session.sendAsync('remember my dentist appointment');
    await encoder.closeAsync();

    assert.ok(
      graph.allTriples().some((t) => t.object === 'dentist'),
      'the encoder should have extracted the turn into the graph',
    );
  });
});

describe('CompanionSession — stream + context', () => {
  it('streams chunks and still persists the full reply', async () => {
    const episodic = new InMemoryEpisodicStore();
    const gen = new CapturingGenerator('unused', ['Hel', 'lo']);
    const session = makeSession(gen, episodic);

    const chunks: string[] = [];
    for await (const c of session.streamAsync('hi')) chunks.push(c);

    assert.deepEqual(chunks, ['Hel', 'lo']);
    assert.equal(await episodic.countAsync(), 1);
    assert.equal(session.history[1].content, 'Hello'); // accumulated reply persisted
  });

  it('getContext reflects the memories recalled on the last turn', async () => {
    const episodic = new InMemoryEpisodicStore();
    await episodic.addAsync({
      id: 'seed1',
      recordedAtUtc: new Date('2026-01-01T00:00:00Z'),
      userText: 'I live in Durban',
      assistantText: 'Nice',
    });
    const session = makeSession(new CapturingGenerator('ok'), episodic);

    await session.sendAsync('where do I live?');
    assert.ok(session.getContext().recentMemorySnippets.includes('I live in Durban'));
  });

  it('agentAsync returns a reply and persists (no tool loop in the pilot)', async () => {
    const episodic = new InMemoryEpisodicStore();
    const session = makeSession(new CapturingGenerator('done'), episodic);
    const reply = await session.agentAsync('do the thing');
    assert.equal(reply, 'done');
    assert.equal(await episodic.countAsync(), 1);
  });
});
