// hosting_cron_scheduler.test.ts
//
// Verifies the ported CircleAI.Hosting cron surface: CronScheduleParser
// (getNextOccurrence) field parsing, wildcards/lists/ranges/steps, month/hour
// advancement, impossible-expression rejection; the in-memory scheduled-task
// store; the cron job model factory; and ScheduledAIService's due-job cycle
// with the JobCompleted event.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  getNextOccurrence,
  CronScheduleError,
  InMemoryScheduledTaskStore,
  ScheduledAIService,
  cronJob,
  DeliveryTargetValues,
  CronJobStateValues,
  type CronJob,
  type IAIService,
} from "../src/hosting/index";

// A minimal IAIService whose askAsync echoes a canned reply and records prompts.
function fakeButler(reply: string, opts: { throwOnAsk?: boolean } = {}) {
  const prompts: string[] = [];
  const svc: IAIService = {
    isReady: true,
    async startAsync() {},
    async stopAsync() {},
    async askAsync(q: string) {
      prompts.push(q);
      if (opts.throwOnAsk) throw new Error("ask failed");
      return reply;
    },
    async chatAsync() {
      return reply;
    },
    async *streamAsync() {
      yield reply;
    },
    async invokeToolAsync() {
      return { toolName: "x", success: false };
    },
    async agenticChatAsync() {
      return reply;
    },
    async submitFeedbackAsync() {},
    async checkForUpgradesAsync() {
      return [];
    },
    async prewarmAsync() {},
    async disposeAsync() {},
  };
  return { svc, prompts };
}

describe("CronScheduleParser — parsing + next occurrence", () => {
  it("every-minute wildcard advances to the next whole minute", () => {
    const after = new Date(Date.UTC(2026, 6, 8, 6, 30, 30));
    const next = getNextOccurrence("* * * * *", after);
    assert.equal(next.toISOString(), new Date(Date.UTC(2026, 6, 8, 6, 31, 0)).toISOString());
  });

  it("fixed minute/hour picks the next matching instant", () => {
    const after = new Date(Date.UTC(2026, 6, 8, 5, 0, 0));
    const next = getNextOccurrence("30 6 * * *", after);
    assert.equal(next.toISOString(), new Date(Date.UTC(2026, 6, 8, 6, 30, 0)).toISOString());
  });

  it("rolls to the next day when today's time has passed", () => {
    const after = new Date(Date.UTC(2026, 6, 8, 7, 0, 0));
    const next = getNextOccurrence("30 6 * * *", after);
    assert.equal(next.toISOString(), new Date(Date.UTC(2026, 6, 9, 6, 30, 0)).toISOString());
  });

  it("supports step values (*/15 minute)", () => {
    const after = new Date(Date.UTC(2026, 6, 8, 6, 2, 0));
    const next = getNextOccurrence("*/15 * * * *", after);
    assert.equal(next.getUTCMinutes(), 15);
  });

  it("supports lists and ranges", () => {
    // Minutes 0,30; hours 9-17; only fires on the hour or half-hour in-window.
    const after = new Date(Date.UTC(2026, 6, 8, 8, 45, 0));
    const next = getNextOccurrence("0,30 9-17 * * *", after);
    assert.equal(next.getUTCHours(), 9);
    assert.equal(next.getUTCMinutes(), 0);
  });

  it("advances across months for a day-of-month expression", () => {
    // 09:00 on the 1st. Starting mid-July → 1 Aug 09:00.
    const after = new Date(Date.UTC(2026, 6, 15, 12, 0, 0));
    const next = getNextOccurrence("0 9 1 * *", after);
    assert.equal(next.getUTCFullYear(), 2026);
    assert.equal(next.getUTCMonth(), 7); // August (0-based)
    assert.equal(next.getUTCDate(), 1);
    assert.equal(next.getUTCHours(), 9);
  });

  it("throws on the wrong field count", () => {
    assert.throws(() => getNextOccurrence("* * *", new Date()), CronScheduleError);
  });

  it("rejects an impossible expression (Feb 31)", () => {
    assert.throws(() => getNextOccurrence("0 9 31 2 *", new Date()), CronScheduleError);
  });

  it("rejects out-of-range values", () => {
    assert.throws(() => getNextOccurrence("99 * * * *", new Date()), CronScheduleError);
  });
});

describe("InMemoryScheduledTaskStore", () => {
  it("upserts, lists, gets, deletes", async () => {
    const store = new InMemoryScheduledTaskStore();
    const j = cronJob("a", "A", "hello", "* * * * *", DeliveryTargetValues.Local);
    await store.upsertAsync(j);
    assert.equal((await store.listAsync()).length, 1);
    assert.equal((await store.getAsync("a"))?.name, "A");
    await store.deleteAsync("a");
    assert.equal(await store.getAsync("a"), null);
  });

  it("returns only enabled, past-due jobs", async () => {
    const store = new InMemoryScheduledTaskStore();
    const past = new Date(Date.now() - 60_000);
    const future = new Date(Date.now() + 60_000);
    await store.upsertAsync({
      ...cronJob("due", "Due", "p", "* * * * *", DeliveryTargetValues.Local),
      nextRunUtc: past,
    });
    await store.upsertAsync({
      ...cronJob("later", "Later", "p", "* * * * *", DeliveryTargetValues.Local),
      nextRunUtc: future,
    });
    await store.upsertAsync({
      ...cronJob("disabled", "Off", "p", "* * * * *", DeliveryTargetValues.Local, past, past, CronJobStateValues.Pending, false),
    });
    const due = await store.getDueJobsAsync();
    assert.deepEqual(due.map((d) => d.id).sort(), ["due"]);
  });
});

describe("ScheduledAIService — due-job processing", () => {
  it("runs a due job, marks it succeeded, schedules next, fires the event", async () => {
    const store = new InMemoryScheduledTaskStore();
    await store.upsertAsync({
      ...cronJob("j", "J", "summarise", "* * * * *", DeliveryTargetValues.Local),
      nextRunUtc: new Date(Date.now() - 60_000),
    });
    const { svc, prompts } = fakeButler("done");
    const service = new ScheduledAIService(svc, store);

    const completed: { id: string; response: string; error: Error | null }[] = [];
    service.onJobCompleted((a) =>
      completed.push({ id: a.job.id, response: a.response, error: a.error }),
    );

    await service.processDueJobsAsync();

    assert.deepEqual(prompts, ["summarise"]);
    assert.equal(completed.length, 1);
    assert.equal(completed[0].response, "done");
    assert.equal(completed[0].error, null);

    const stored = await store.getAsync("j");
    assert.equal(stored?.state, CronJobStateValues.Succeeded);
    assert.ok(stored?.lastRunUtc);
    assert.ok(stored?.nextRunUtc);
  });

  it("marks a failed job Failed and reports the error in the event", async () => {
    const store = new InMemoryScheduledTaskStore();
    await store.upsertAsync({
      ...cronJob("bad", "Bad", "p", "* * * * *", DeliveryTargetValues.Local),
      nextRunUtc: new Date(Date.now() - 60_000),
    });
    const { svc } = fakeButler("", { throwOnAsk: true });
    const service = new ScheduledAIService(svc, store);

    let captured: Error | null = null;
    service.onJobCompleted((a) => {
      captured = a.error;
    });
    await service.processDueJobsAsync();

    assert.ok(captured);
    const stored = await store.getAsync("bad");
    assert.equal(stored?.state, CronJobStateValues.Failed);
  });

  it("start/stop are idempotent and do not throw", async () => {
    const store = new InMemoryScheduledTaskStore();
    const { svc } = fakeButler("x");
    const service = new ScheduledAIService(svc, store);
    await service.startAsync();
    await service.startAsync();
    await service.stopAsync();
    await service.disposeAsync();
    assert.ok(true);
  });
});
