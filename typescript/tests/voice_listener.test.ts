// voice_listener.test.ts
//
// Verifies VoiceCompanionListener (VoiceCompanionListener.cs): a transcription
// raises UtteranceDetected, is forwarded to the session, and the reply raises
// ResponseReady; start/stop drive the pipeline; disposeAsync unsubscribes and
// disposes both the pipeline and the session; a session error does not crash.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  VoiceCompanionListener,
  type IVoicePipeline,
  type TranscribedHandler,
  type TranscribedEventArgs,
} from '../src/companion/index';
import type { ICompanionSession } from '../src/companion/index';

/** A controllable fake pipeline that lets the test push a transcription. */
class FakePipeline implements IVoicePipeline {
  private handlers = new Set<TranscribedHandler>();
  started = false;
  stopped = false;
  disposed = false;

  onTranscribed(h: TranscribedHandler): void {
    this.handlers.add(h);
  }
  offTranscribed(h: TranscribedHandler): void {
    this.handlers.delete(h);
  }
  async startAsync(): Promise<void> {
    this.started = true;
  }
  async stopAsync(): Promise<void> {
    this.stopped = true;
  }
  async disposeAsync(): Promise<void> {
    this.disposed = true;
  }
  emit(args: TranscribedEventArgs): void {
    for (const h of [...this.handlers]) h(args);
  }
  get handlerCount(): number {
    return this.handlers.size;
  }
}

function fakeSession(reply: string | (() => Promise<string>)): ICompanionSession & {
  disposeAsync: () => Promise<void>;
  disposed: boolean;
} {
  const state = { disposed: false };
  return {
    sessionId: 's',
    identityId: 'i',
    interface: 0 as never,
    onProactiveMessageReady: null,
    history: [],
    async sendAsync(): Promise<string> {
      return typeof reply === 'function' ? reply() : reply;
    },
    async *streamAsync() {
      /* unused */
    },
    async agentAsync() {
      return '';
    },
    getContext() {
      return {} as never;
    },
    async refreshContextAsync() {
      /* unused */
    },
    async signalFeedbackAsync() {
      /* unused */
    },
    async disposeAsync() {
      state.disposed = true;
    },
    get disposed() {
      return state.disposed;
    },
  } as unknown as ICompanionSession & { disposeAsync: () => Promise<void>; disposed: boolean };
}

function transcription(text: string, confidence = 0.9): TranscribedEventArgs {
  return { result: { text, confidence }, completedAt: new Date() };
}

describe('VoiceCompanionListener', () => {
  it('raises UtteranceDetected then ResponseReady with the reply', async () => {
    const pipeline = new FakePipeline();
    const session = fakeSession('the reply');
    const listener = new VoiceCompanionListener(pipeline, session);

    const utterances: string[] = [];
    const responses: { text: string; original: string }[] = [];
    listener.onUtteranceDetected((a) => utterances.push(a.text));
    listener.onResponseReady((a) => responses.push({ text: a.text, original: a.originalUtterance }));

    pipeline.emit(transcription('what time is it'));
    // ResponseReady fires from an async continuation; give it a tick.
    await new Promise((r) => setTimeout(r, 5));

    assert.deepEqual(utterances, ['what time is it']);
    assert.deepEqual(responses, [{ text: 'the reply', original: 'what time is it' }]);
    await listener.disposeAsync();
  });

  it('start/stop drive the underlying pipeline', async () => {
    const pipeline = new FakePipeline();
    const listener = new VoiceCompanionListener(pipeline, fakeSession('x'));
    await listener.startAsync();
    assert.equal(pipeline.started, true);
    await listener.stopAsync();
    assert.equal(pipeline.stopped, true);
    await listener.disposeAsync();
  });

  it('disposeAsync unsubscribes and disposes pipeline + session', async () => {
    const pipeline = new FakePipeline();
    const session = fakeSession('x');
    const listener = new VoiceCompanionListener(pipeline, session);
    assert.equal(pipeline.handlerCount, 1);
    await listener.disposeAsync();
    assert.equal(pipeline.handlerCount, 0);
    assert.equal(pipeline.disposed, true);
    assert.equal((session as unknown as { disposed: boolean }).disposed, true);
    // start after dispose throws.
    await assert.rejects(() => listener.startAsync(), /disposed/);
  });

  it('a session error does not crash the listener (no ResponseReady)', async () => {
    const pipeline = new FakePipeline();
    const session = fakeSession(async () => {
      throw new Error('model down');
    });
    const listener = new VoiceCompanionListener(pipeline, session);
    const responses: string[] = [];
    listener.onResponseReady((a) => responses.push(a.text));
    pipeline.emit(transcription('hello'));
    await new Promise((r) => setTimeout(r, 5));
    assert.equal(responses.length, 0); // failure swallowed
    await listener.disposeAsync();
  });

  it('rejects null pipeline / session in the constructor', () => {
    // @ts-expect-error deliberate null pipeline
    assert.throws(() => new VoiceCompanionListener(null, fakeSession('x')), /pipeline required/);
  });
});
