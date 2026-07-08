// proactive_scheduler.test.ts
//
// Verifies the ported CircleAI.Companion.Proactive scheduler surface
// (ProactiveScheduler.cs + NullImplementations.cs + BackgroundService): source
// snapshotting, cron tick firing once per matching minute, event dispatch,
// manual run-by-id, per-context last-run isolation, refresh state-drop, and the
// default null implementations.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  ProactiveScheduler,
  InMemoryProactiveTaskSource,
  DelegateProactiveTaskRunner,
  NullProactiveTaskSource,
  NullProactiveTaskRunner,
  ProactiveSchedulerBackgroundService,
  proactiveTask,
  proactiveTrigger,
  proactiveTaskRunResult,
  type ProactiveTask,
} from '../src/proactive/index';

/** A runner that records which task ids it ran (and how many times). */
function recordingRunner() {
  const runs: string[] = [];
  const runner = new DelegateProactiveTaskRunner(async (task: ProactiveTask) => {
    runs.push(task.id);
    return proactiveTaskRunResult(task.id, true);
  });
  return { runner, runs };
}

describe('ProactiveScheduler — refresh + snapshot', () => {
  it('snapshots tasks + errors from the source on refresh', async () => {
    const source = new InMemoryProactiveTaskSource();
    source.upsert(proactiveTask('t1', proactiveTrigger('* * * * *'), { kind: 'noop' }));
    const { runner } = recordingRunner();
    const scheduler = new ProactiveScheduler(source, runner);
    assert.equal(scheduler.tasks.length, 0);
    await scheduler.refreshAsync();
    assert.equal(scheduler.tasks.length, 1);
    assert.equal(scheduler.tasks[0].id, 't1');
  });

  it('getNextRun returns null for a non-cron trigger', async () => {
    const source = new InMemoryProactiveTaskSource();
    const manual = proactiveTask('m', proactiveTrigger(null, null, true), {});
    source.upsert(manual);
    const { runner } = recordingRunner();
    const scheduler = new ProactiveScheduler(source, runner);
    await scheduler.refreshAsync();
    assert.equal(scheduler.getNextRun(manual, new Date()), null);
  });
});

describe('ProactiveScheduler — tick', () => {
  it('fires an every-minute cron task once for the current minute', async () => {
    const source = new InMemoryProactiveTaskSource();
    source.upsert(proactiveTask('every', proactiveTrigger('* * * * *'), {}));
    const { runner, runs } = recordingRunner();
    const scheduler = new ProactiveScheduler(source, runner);
    await scheduler.refreshAsync();

    const now = new Date(Date.UTC(2026, 6, 8, 6, 30, 0));
    await scheduler.tickAsync(now);
    assert.deepEqual(runs, ['every']);

    // Ticking again in the SAME minute must not re-fire (last-run guard).
    await scheduler.tickAsync(now);
    assert.deepEqual(runs, ['every']);

    // A tick in the NEXT minute fires again.
    await scheduler.tickAsync(new Date(Date.UTC(2026, 6, 8, 6, 31, 0)));
    assert.deepEqual(runs, ['every', 'every']);
  });

  it('does not fire a cron task whose time has not come', async () => {
    const source = new InMemoryProactiveTaskSource();
    source.upsert(proactiveTask('sixthirty', proactiveTrigger('30 6 * * *'), {}));
    const { runner, runs } = recordingRunner();
    const scheduler = new ProactiveScheduler(source, runner);
    await scheduler.refreshAsync();
    // 05:00 — before 06:30.
    await scheduler.tickAsync(new Date(Date.UTC(2026, 6, 8, 5, 0, 0)));
    assert.deepEqual(runs, []);
    // 06:30 — fires.
    await scheduler.tickAsync(new Date(Date.UTC(2026, 6, 8, 6, 30, 0)));
    assert.deepEqual(runs, ['sixthirty']);
  });
});

describe('ProactiveScheduler — event dispatch + run-by-id', () => {
  it('dispatches to every task matching the event name (case-insensitive)', async () => {
    const source = new InMemoryProactiveTaskSource();
    source.upsert(proactiveTask('a', proactiveTrigger(null, 'note-saved'), {}));
    source.upsert(proactiveTask('b', proactiveTrigger(null, 'NOTE-SAVED'), {}));
    source.upsert(proactiveTask('c', proactiveTrigger(null, 'other'), {}));
    const { runner, runs } = recordingRunner();
    const scheduler = new ProactiveScheduler(source, runner);
    await scheduler.refreshAsync();
    await scheduler.dispatchEventAsync('note-saved');
    assert.deepEqual(runs.sort(), ['a', 'b']);
  });

  it('runByIdAsync runs a known task and reports failure for an unknown one', async () => {
    const source = new InMemoryProactiveTaskSource();
    source.upsert(proactiveTask('job', proactiveTrigger(null, null, true), {}));
    const { runner, runs } = recordingRunner();
    const scheduler = new ProactiveScheduler(source, runner);
    await scheduler.refreshAsync();

    const ok = await scheduler.runByIdAsync('job');
    assert.equal(ok.success, true);
    assert.deepEqual(runs, ['job']);

    const missing = await scheduler.runByIdAsync('ghost');
    assert.equal(missing.success, false);
    assert.match(missing.failureMessage!, /No task with id 'ghost'/);
  });
});

describe('ProactiveScheduler — per-context last-run isolation + state drop', () => {
  it('keeps last-run state independent per sourceContext', async () => {
    const source = new InMemoryProactiveTaskSource();
    // Same task id "sync" in two tenants.
    source.upsert(proactiveTask('sync', proactiveTrigger('* * * * *'), {}, 'tenant-a'));
    source.upsert(proactiveTask('sync', proactiveTrigger('* * * * *'), {}, 'tenant-b'));
    const { runner, runs } = recordingRunner();
    const scheduler = new ProactiveScheduler(source, runner);
    await scheduler.refreshAsync();
    const now = new Date(Date.UTC(2026, 6, 8, 6, 30, 0));
    await scheduler.tickAsync(now);
    // Both tenants' tasks fire (independent last-run maps).
    assert.equal(runs.length, 2);
  });

  it('drops last-run state for tasks the source no longer reports', async () => {
    const source = new InMemoryProactiveTaskSource();
    source.upsert(proactiveTask('gone', proactiveTrigger('* * * * *'), {}));
    const { runner, runs } = recordingRunner();
    const scheduler = new ProactiveScheduler(source, runner);
    await scheduler.refreshAsync();
    const now = new Date(Date.UTC(2026, 6, 8, 6, 30, 0));
    await scheduler.tickAsync(now); // fires once, marks last-run
    assert.deepEqual(runs, ['gone']);

    // Remove the task, refresh (drops its last-run), re-add, refresh again.
    source.remove('gone');
    await scheduler.refreshAsync();
    source.upsert(proactiveTask('gone', proactiveTrigger('* * * * *'), {}));
    await scheduler.refreshAsync();
    // Because last-run was dropped, the same-minute tick fires again.
    await scheduler.tickAsync(now);
    assert.deepEqual(runs, ['gone', 'gone']);
  });
});

describe('Null / delegate default implementations', () => {
  it('NullProactiveTaskSource reports no tasks and no errors', async () => {
    const s = NullProactiveTaskSource.instance;
    assert.equal(s.backendId, 'null');
    assert.equal((await s.getTasksAsync()).length, 0);
    assert.equal((await s.getErrorsAsync()).length, 0);
  });

  it('NullProactiveTaskRunner fails closed with a helpful message', async () => {
    const r = NullProactiveTaskRunner.instance;
    const res = await r.runAsync(proactiveTask('x', proactiveTrigger(null, null, true), {}));
    assert.equal(res.success, false);
    assert.match(res.failureMessage!, /No IProactiveTaskRunner registered/);
  });

  it('InMemoryProactiveTaskSource upsert/remove/clear behave', async () => {
    const s = new InMemoryProactiveTaskSource();
    s.upsert(proactiveTask('t', proactiveTrigger('* * * * *'), {}));
    assert.equal((await s.getTasksAsync()).length, 1);
    assert.equal(s.remove('t'), true);
    assert.equal((await s.getTasksAsync()).length, 0);
    s.upsert(proactiveTask('t', proactiveTrigger('* * * * *'), {}));
    s.clear();
    assert.equal((await s.getTasksAsync()).length, 0);
  });
});

describe('ProactiveSchedulerBackgroundService', () => {
  it('refreshes on start and can be stopped cleanly', async () => {
    const source = new InMemoryProactiveTaskSource();
    source.upsert(proactiveTask('t', proactiveTrigger('* * * * *'), {}));
    const { runner } = recordingRunner();
    const scheduler = new ProactiveScheduler(source, runner);
    // Very short tick so the loop runs at least once during the test.
    const bg = new ProactiveSchedulerBackgroundService(scheduler, { tickIntervalMs: 5, refreshIntervalMs: 1_000_000 });
    bg.start();
    // Let the initial refresh + a tick or two happen.
    await new Promise((r) => setTimeout(r, 30));
    await bg.stop();
    // After start, the scheduler was refreshed → sees the task.
    assert.equal(scheduler.tasks.length, 1);
  });
});
