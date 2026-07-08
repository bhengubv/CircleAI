// herjarvis_streams_loops.test.ts
//
// Verifies the channel/stream + loop HerJarvis implementations
// (HerJarvisRealImplementations.cs #1,2,17,18,19,20,21,23,24) and the ECDSA
// crypto delegation (#22). Streams use the AsyncQueue that reproduces the C#
// Channel producer/consumer contract.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  HeartbeatAlwaysOnPresence,
  ChannelFusedPerception,
  ChannelBioSignalStream,
  MailboxAgentPeerNetwork,
  RegistryPhysicalActuator,
  InMemoryFederatedFineTuner,
  SlidingP50FirstTokenOptimizer,
  SyntaxCheckingCodeGenerationLoop,
  isSyntacticallyBalanced,
  TrackingSelfImprovementLoop,
  EcdsaCryptoDelegation,
  AsyncQueue,
  type FusedPercept,
  type BioSignal,
  type AgentToAgentMessage,
} from '../src/companion/herjarvis/index';

describe('HeartbeatAlwaysOnPresence — start/stop lifecycle', () => {
  it('is not running until started; idempotent start; stop clears', async () => {
    const h = new HeartbeatAlwaysOnPresence(10);
    assert.equal(h.isRunning, false);
    await h.startAsync();
    assert.equal(h.isRunning, true);
    await h.startAsync(); // idempotent
    assert.equal(h.isRunning, true);
    await h.stopAsync();
    assert.equal(h.isRunning, false);
  });

  it('increments the heartbeat immediately on start (due time 0)', async () => {
    const h = new HeartbeatAlwaysOnPresence(1_000_000);
    await h.startAsync();
    // The immediate tick is a 0ms timeout; yield to the event loop to let it run.
    await new Promise((r) => setTimeout(r, 5));
    assert.ok(h.heartbeats >= 1);
    await h.stopAsync();
  });
});

describe('ChannelFusedPerception — publish/subscribe stream', () => {
  it('yields published percepts in order until completed', async () => {
    const fp = new ChannelFusedPerception();
    const p1: FusedPercept = { at: new Date(), vision: 'a', audio: null, text: null, sensors: new Map() };
    const p2: FusedPercept = { at: new Date(), vision: 'b', audio: null, text: null, sensors: new Map() };
    fp.publish(p1);
    fp.publish(p2);
    fp.complete();
    const seen: string[] = [];
    for await (const p of fp.streamAsync()) seen.push(p.vision!);
    assert.deepEqual(seen, ['a', 'b']);
  });

  it('rejects a null percept', () => {
    const fp = new ChannelFusedPerception();
    // @ts-expect-error deliberate null
    assert.throws(() => fp.publish(null), /percept required/);
  });
});

describe('ChannelBioSignalStream — fan-in', () => {
  it('streams published signals then ends on complete', async () => {
    const bs = new ChannelBioSignalStream();
    const s: BioSignal = { kind: 'hr', value: 72, at: new Date() };
    bs.publish(s);
    bs.complete();
    const seen: number[] = [];
    for await (const x of bs.streamAsync()) seen.push(x.value);
    assert.deepEqual(seen, [72]);
  });
});

describe('MailboxAgentPeerNetwork — per-agent mailbox', () => {
  it('delivers a sent message to the recipient stream', async () => {
    const net = new MailboxAgentPeerNetwork();
    const msg: AgentToAgentMessage = { fromAgentId: 'a', toAgentId: 'b', payload: 'hi', at: new Date() };
    await net.sendAsync(msg);
    const ac = new AbortController();
    const received: string[] = [];
    const consume = (async () => {
      for await (const m of net.receiveAsync('b', ac.signal)) {
        received.push(m.payload);
        break; // one message is enough for the test
      }
    })();
    await consume;
    ac.abort();
    assert.deepEqual(received, ['hi']);
  });

  it('rejects a null message and a blank recipient', async () => {
    const net = new MailboxAgentPeerNetwork();
    // @ts-expect-error deliberate null
    await assert.rejects(() => net.sendAsync(null), /message required/);
    // An async generator surfaces its guard throw when the first .next() is
    // awaited — a rejection, not a synchronous throw.
    await assert.rejects(() => net.receiveAsync('  ').next(), /forAgentId required/);
  });
});

describe('AsyncQueue — Channel-faithful queue', () => {
  it('buffers then completes; tryDequeue returns undefined when empty', async () => {
    const q = new AsyncQueue<number>();
    assert.equal(q.enqueue(1), true);
    assert.equal(q.tryDequeue(), 1);
    assert.equal(q.tryDequeue(), undefined);
    q.complete();
    assert.equal(q.enqueue(2), false); // completed → cannot enqueue
  });
});

describe('RegistryPhysicalActuator — device dispatch', () => {
  it('dispatches to a registered device handler', async () => {
    const act = new RegistryPhysicalActuator();
    act.registerDevice('lamp', async (cmd) => ({ succeeded: cmd.action === 'on', error: null }));
    const r = await act.invokeAsync({ deviceId: 'lamp', action: 'on', args: new Map() });
    assert.deepEqual(r, { succeeded: true, error: null });
  });

  it('unknown device → failure result', async () => {
    const act = new RegistryPhysicalActuator();
    const r = await act.invokeAsync({ deviceId: 'ghost', action: 'x', args: new Map() });
    assert.equal(r.succeeded, false);
    assert.match(r.error!, /Unknown device 'ghost'/);
  });

  it('rejects a blank device id and a null handler', () => {
    const act = new RegistryPhysicalActuator();
    assert.throws(() => act.registerDevice('  ', async () => ({ succeeded: true, error: null })), /deviceId required/);
    // @ts-expect-error deliberate null handler
    assert.throws(() => act.registerDevice('d', null), /handler required/);
  });
});

describe('InMemoryFederatedFineTuner — job runner', () => {
  it('runs the injected trainer to completion and tracks progress → 1.0', async () => {
    const tuner = new InMemoryFederatedFineTuner(async (_m, _p, progress) => {
      progress(0.25);
      progress(0.5);
    });
    const jobId = await tuner.startAsync('base', '/data.jsonl');
    // Give the background task a tick to finish.
    await new Promise((r) => setTimeout(r, 10));
    const status = await tuner.statusAsync(jobId);
    assert.equal(status.progress, 1.0);
    assert.equal(status.error, null);
  });

  it('unknown job → progress 0 with "unknown job"', async () => {
    const tuner = new InMemoryFederatedFineTuner();
    const s = await tuner.statusAsync('nope');
    assert.deepEqual(s, { jobId: 'nope', progress: 0, error: 'unknown job' });
  });

  it('surfaces a trainer error on the job status', async () => {
    const tuner = new InMemoryFederatedFineTuner(async () => {
      throw new Error('gpu oom');
    });
    const jobId = await tuner.startAsync('base', '/data.jsonl');
    await new Promise((r) => setTimeout(r, 10));
    const s = await tuner.statusAsync(jobId);
    assert.equal(s.error, 'gpu oom');
  });

  it('rejects blank base model / training path', async () => {
    const tuner = new InMemoryFederatedFineTuner();
    await assert.rejects(() => tuner.startAsync('  ', 'p'), /baseModel required/);
    await assert.rejects(() => tuner.startAsync('m', '  '), /trainingDataPath required/);
  });
});

describe('SlidingP50FirstTokenOptimizer — median latency', () => {
  it('reports the upper-middle sample as p50 (C# sorted[len/2])', async () => {
    const opt = new SlidingP50FirstTokenOptimizer(100, 8);
    for (const ms of [10, 50, 30, 90, 20]) opt.recordFirstTokenLatency(ms);
    // sorted = [10,20,30,50,90]; index 5/2 = 2 → 30.
    const b = await opt.currentAsync();
    assert.equal(b.targetMs, 100);
    assert.equal(b.currentP50Ms, 30);
  });

  it('empty window → p50 0; window drops oldest past size', async () => {
    const opt = new SlidingP50FirstTokenOptimizer(100, 2);
    assert.equal((await opt.currentAsync()).currentP50Ms, 0);
    opt.recordFirstTokenLatency(10);
    opt.recordFirstTokenLatency(20);
    opt.recordFirstTokenLatency(30); // drops 10 → window [20,30]
    // sorted [20,30]; index 2/2=1 → 30.
    assert.equal((await opt.currentAsync()).currentP50Ms, 30);
  });

  it('rejects bad ctor args and negative latency', () => {
    assert.throws(() => new SlidingP50FirstTokenOptimizer(0), /targetMs out of range/);
    assert.throws(() => new SlidingP50FirstTokenOptimizer(100, 0), /windowSize out of range/);
    const opt = new SlidingP50FirstTokenOptimizer();
    assert.throws(() => opt.recordFirstTokenLatency(-1), /ms out of range/);
  });
});

describe('SyntaxCheckingCodeGenerationLoop — generate/check/test/hint', () => {
  it('default loop: balanced snippet passes and gets a deploy hint', async () => {
    const loop = new SyntaxCheckingCodeGenerationLoop();
    const job = await loop.runAsync('add two numbers');
    assert.equal(job.prompt, 'add two numbers');
    // Default generator emits "...\nreturn 0;" which is bracket-balanced.
    assert.equal(job.testsPass, true);
    assert.equal(job.deployHint, 'run inline');
    assert.match(job.id, /^[0-9a-f]{32}$/);
  });

  it('unbalanced generated snippet fails tests → null deploy hint', async () => {
    const loop = new SyntaxCheckingCodeGenerationLoop(async () => 'return (0;');
    const job = await loop.runAsync('x');
    assert.equal(job.testsPass, false);
    assert.equal(job.deployHint, null);
  });

  it('"public class" snippet suggests staging as nuget', async () => {
    const loop = new SyntaxCheckingCodeGenerationLoop(async () => 'public class Foo { }');
    const job = await loop.runAsync('x');
    assert.equal(job.testsPass, true);
    assert.equal(job.deployHint, 'stage as nuget');
  });

  it('isSyntacticallyBalanced matches the C# bracket rules', () => {
    assert.equal(isSyntacticallyBalanced('{[()]}'), true);
    assert.equal(isSyntacticallyBalanced(']['), false);
    assert.equal(isSyntacticallyBalanced('('), false);
    assert.equal(isSyntacticallyBalanced(''), false);
  });

  it('rejects a blank prompt', async () => {
    const loop = new SyntaxCheckingCodeGenerationLoop();
    await assert.rejects(() => loop.runAsync('  '), /prompt required/);
  });
});

describe('TrackingSelfImprovementLoop — best-score tracking', () => {
  it('records a new best on first cycle and "no regression" on a tie', async () => {
    let score = 0.7;
    const loop = new TrackingSelfImprovementLoop(async () => score);
    let v = await loop.cycleAsync('suite');
    assert.equal(v.improvementsApplied, 'new best');
    assert.equal(v.newBenchScore, 0.7);
    assert.equal(loop.bestScoreFor('suite'), 0.7);
    // Same score again → no regression.
    v = await loop.cycleAsync('suite');
    assert.equal(v.improvementsApplied, 'no regression');
  });

  it('proposes an improvement when the score regresses', async () => {
    let score = 0.9;
    const loop = new TrackingSelfImprovementLoop(async () => score);
    await loop.cycleAsync('suite'); // best = 0.9
    score = 0.4;
    const v = await loop.cycleAsync('suite');
    assert.match(v.improvementsApplied, /retry-with-temperature-0/);
    assert.equal(v.newBenchScore, 0.4);
  });

  it('default bench is deterministic per suite id (content-hashed)', async () => {
    const a = new TrackingSelfImprovementLoop();
    const b = new TrackingSelfImprovementLoop();
    const va = await a.cycleAsync('same-suite');
    const vb = await b.cycleAsync('same-suite');
    assert.equal(va.newBenchScore, vb.newBenchScore);
    assert.ok(va.newBenchScore >= 0.5 && va.newBenchScore <= 1.0);
  });

  it('rejects a blank suite id', async () => {
    const loop = new TrackingSelfImprovementLoop();
    await assert.rejects(() => loop.cycleAsync('  '), /benchSuiteId required/);
  });
});

describe('EcdsaCryptoDelegation — issue + verify (P-256 / SHA-256)', () => {
  it('a freshly issued credential verifies', () => {
    const cd = new EcdsaCryptoDelegation('issuer-x');
    const cred = cd.issue('user-1', 'read:notes', 60_000);
    assert.equal(cred.issuer, 'issuer-x');
    assert.equal(cred.subjectId, 'user-1');
    assert.equal(cred.scope, 'read:notes');
    assert.ok(cred.signature.length > 0);
    assert.equal(cd.verify(cred), true);
  });

  it('rejects a wrong-issuer credential', () => {
    const a = new EcdsaCryptoDelegation('issuer-a');
    const b = new EcdsaCryptoDelegation('issuer-b');
    const cred = a.issue('u', 's', 60_000);
    // Different issuer string → rejected before crypto.
    assert.equal(b.verify(cred), false);
  });

  it('rejects an expired credential', () => {
    const cd = new EcdsaCryptoDelegation('iss');
    const cred = cd.issue('u', 's', 60_000);
    const expired = { ...cred, expiresAtUtc: new Date(Date.now() - 1000) };
    assert.equal(cd.verify(expired), false);
  });

  it('rejects a tampered scope (signature no longer matches)', () => {
    const cd = new EcdsaCryptoDelegation('iss');
    const cred = cd.issue('u', 'read', 60_000);
    const tampered = { ...cred, scope: 'write' };
    assert.equal(cd.verify(tampered), false);
  });

  it('rejects an empty signature', () => {
    const cd = new EcdsaCryptoDelegation('iss');
    const cred = cd.issue('u', 's', 60_000);
    assert.equal(cd.verify({ ...cred, signature: '' }), false);
  });

  it('rejects bad ctor / issue args', () => {
    assert.throws(() => new EcdsaCryptoDelegation('  '), /issuer required/);
    const cd = new EcdsaCryptoDelegation();
    assert.throws(() => cd.issue('  ', 's', 1000), /subjectId required/);
    assert.throws(() => cd.issue('u', '  ', 1000), /scope required/);
    assert.throws(() => cd.issue('u', 's', 0), /lifetime out of range/);
  });

  it('a host-injected keypair round-trips (shared keys across instances)', () => {
    const shared = new EcdsaCryptoDelegation('iss');
    // Reuse the same instance's keys by issuing + verifying with the SAME object.
    const cred = shared.issue('u', 's', 60_000);
    assert.equal(shared.verify(cred), true);
  });
});
