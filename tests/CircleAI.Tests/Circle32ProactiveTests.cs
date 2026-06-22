// Circle32ProactiveTests.cs
//
// (3.2.0) Tests for CircleAI.Companion.Proactive — CronExpression
// parsing, ProactiveScheduler refresh/tick/event/manual paths, and the
// null + in-memory + delegate impls.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Companion.Proactive;
using Xunit;

namespace CircleAI.Tests;

public sealed class Circle32ProactiveTests
{
    // ── CronExpression ────────────────────────────────────────────────

    [Fact]
    public void CronExpression_ParsesEveryMinute()
    {
        var expr = CronExpression.Parse("* * * * *");
        var moment = new DateTimeOffset(2026, 6, 18, 12, 34, 0, TimeSpan.Zero);
        Assert.True(expr.Matches(moment));
    }

    [Fact]
    public void CronExpression_ParsesSpecificMinute()
    {
        var expr = CronExpression.Parse("15 * * * *");
        var hits = new DateTimeOffset(2026, 6, 18, 12, 15, 0, TimeSpan.Zero);
        var miss = new DateTimeOffset(2026, 6, 18, 12, 16, 0, TimeSpan.Zero);
        Assert.True(expr.Matches(hits));
        Assert.False(expr.Matches(miss));
    }

    [Fact]
    public void CronExpression_ParsesStepValue()
    {
        var expr = CronExpression.Parse("*/15 * * * *");
        Assert.True(expr.Matches(new DateTimeOffset(2026, 6, 18, 12,  0, 0, TimeSpan.Zero)));
        Assert.True(expr.Matches(new DateTimeOffset(2026, 6, 18, 12, 15, 0, TimeSpan.Zero)));
        Assert.True(expr.Matches(new DateTimeOffset(2026, 6, 18, 12, 30, 0, TimeSpan.Zero)));
        Assert.True(expr.Matches(new DateTimeOffset(2026, 6, 18, 12, 45, 0, TimeSpan.Zero)));
        Assert.False(expr.Matches(new DateTimeOffset(2026, 6, 18, 12, 22, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void CronExpression_ParsesRange()
    {
        var expr = CronExpression.Parse("0 9-17 * * *");
        Assert.True(expr.Matches(new DateTimeOffset(2026, 6, 18,  9, 0, 0, TimeSpan.Zero)));
        Assert.True(expr.Matches(new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero)));
        Assert.True(expr.Matches(new DateTimeOffset(2026, 6, 18, 17, 0, 0, TimeSpan.Zero)));
        Assert.False(expr.Matches(new DateTimeOffset(2026, 6, 18, 18, 0, 0, TimeSpan.Zero)));
        Assert.False(expr.Matches(new DateTimeOffset(2026, 6, 18,  8, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void CronExpression_ParsesList()
    {
        var expr = CronExpression.Parse("0,15,30,45 * * * *");
        Assert.True(expr.Matches(new DateTimeOffset(2026, 6, 18, 12, 30, 0, TimeSpan.Zero)));
        Assert.False(expr.Matches(new DateTimeOffset(2026, 6, 18, 12,  5, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void CronExpression_GetNextOccurrence_FindsNext()
    {
        var expr = CronExpression.Parse("0 9 * * *");           // every day at 09:00
        var from = new DateTimeOffset(2026, 6, 18, 8, 45, 0, TimeSpan.Zero);
        var next = expr.GetNextOccurrence(from);
        Assert.Equal(new DateTimeOffset(2026, 6, 18, 9, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void CronExpression_RejectsTooFewFields()
    {
        Assert.Throws<FormatException>(() => CronExpression.Parse("* * * *"));
    }

    [Fact]
    public void CronExpression_RejectsOutOfRange()
    {
        Assert.Throws<FormatException>(() => CronExpression.Parse("60 * * * *"));
    }

    // ── Null impls ────────────────────────────────────────────────────

    [Fact]
    public async Task NullProactiveTaskSource_ReturnsEmpty()
    {
        Assert.Empty(await NullProactiveTaskSource.Instance.GetTasksAsync());
        Assert.Empty(await NullProactiveTaskSource.Instance.GetErrorsAsync());
    }

    [Fact]
    public async Task NullProactiveTaskRunner_AlwaysFailsClosed()
    {
        var task = new ProactiveTask("t1", new ProactiveTrigger(Manual: true), Payload: "noop");
        var r = await NullProactiveTaskRunner.Instance.RunAsync(task);
        Assert.False(r.Success);
        Assert.Equal("t1", r.TaskId);
        Assert.NotNull(r.FailureMessage);
    }

    // ── InMemoryProactiveTaskSource ───────────────────────────────────

    [Fact]
    public async Task InMemorySource_UpsertGetRemove_RoundTrips()
    {
        var src = new InMemoryProactiveTaskSource();
        var t = new ProactiveTask("daily", new ProactiveTrigger(Cron: "0 9 * * *"), Payload: "do thing");
        src.Upsert(t);

        var tasks = await src.GetTasksAsync();
        Assert.Single(tasks);
        Assert.Equal("daily", tasks[0].Id);

        Assert.True(src.Remove("daily"));
        Assert.Empty(await src.GetTasksAsync());
    }

    [Fact]
    public async Task InMemorySource_SurfacesRecordedErrors()
    {
        var src = new InMemoryProactiveTaskSource();
        src.RecordError(new ProactiveTaskLoadError("bad-task", "cron parse failed"));
        var errors = await src.GetErrorsAsync();
        Assert.Single(errors);
        Assert.Equal("bad-task", errors[0].TaskId);
    }

    // ── ProactiveScheduler — refresh / tick / dispatch / runById ──────

    [Fact]
    public async Task Scheduler_Refresh_PopulatesTasks()
    {
        var src = new InMemoryProactiveTaskSource();
        src.Upsert(new ProactiveTask("a", new ProactiveTrigger(Cron: "0 9 * * *"), "payload-a"));
        src.Upsert(new ProactiveTask("b", new ProactiveTrigger(Manual: true),        "payload-b"));

        var runner    = NullProactiveTaskRunner.Instance;
        var scheduler = new ProactiveScheduler(src, runner);

        Assert.Empty(scheduler.Tasks);
        await scheduler.RefreshAsync();
        Assert.Equal(2, scheduler.Tasks.Count);
    }

    [Fact]
    public async Task Scheduler_GetNextRun_ReturnsCronNextFire()
    {
        var src = new InMemoryProactiveTaskSource();
        var task = new ProactiveTask("morning",
            new ProactiveTrigger(Cron: "0 9 * * *"),
            Payload: null!);
        src.Upsert(task);

        var scheduler = new ProactiveScheduler(src, NullProactiveTaskRunner.Instance);
        await scheduler.RefreshAsync();

        var from = new DateTimeOffset(2026, 6, 18, 8, 30, 0, TimeSpan.Zero);
        var next = scheduler.GetNextRun(scheduler.Tasks[0], from);
        Assert.Equal(new DateTimeOffset(2026, 6, 18, 9, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public async Task Scheduler_GetNextRun_NullForNonCronTrigger()
    {
        var src = new InMemoryProactiveTaskSource();
        src.Upsert(new ProactiveTask("manual", new ProactiveTrigger(Manual: true), null!));
        var scheduler = new ProactiveScheduler(src, NullProactiveTaskRunner.Instance);
        await scheduler.RefreshAsync();
        Assert.Null(scheduler.GetNextRun(scheduler.Tasks[0], DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Scheduler_Tick_FiresCronTaskOnce()
    {
        var src = new InMemoryProactiveTaskSource();
        src.Upsert(new ProactiveTask("every-minute",
            new ProactiveTrigger(Cron: "* * * * *"),
            Payload: "x"));

        var calls = 0;
        var runner = new DelegateProactiveTaskRunner((task, vars, ct) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(new ProactiveTaskRunResult(task.Id, Success: true));
        });

        var scheduler = new ProactiveScheduler(src, runner);
        await scheduler.RefreshAsync();

        var now = new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero);
        await scheduler.TickAsync(now);
        Assert.Equal(1, calls);

        // Same minute — should not fire again.
        await scheduler.TickAsync(now);
        Assert.Equal(1, calls);

        // One minute later — should fire again.
        await scheduler.TickAsync(now.AddMinutes(1));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Scheduler_DispatchEvent_FiresMatchingTasksOnly()
    {
        var src = new InMemoryProactiveTaskSource();
        src.Upsert(new ProactiveTask("on-save",
            new ProactiveTrigger(OnEvent: "note-saved"), "a"));
        src.Upsert(new ProactiveTask("on-task",
            new ProactiveTrigger(OnEvent: "task-created"), "b"));
        src.Upsert(new ProactiveTask("cron",
            new ProactiveTrigger(Cron: "0 9 * * *"), "c"));

        var fired = new List<string>();
        var runner = new DelegateProactiveTaskRunner((task, vars, ct) =>
        {
            lock (fired) fired.Add(task.Id);
            return ValueTask.FromResult(new ProactiveTaskRunResult(task.Id, Success: true));
        });

        var scheduler = new ProactiveScheduler(src, runner);
        await scheduler.RefreshAsync();

        await scheduler.DispatchEventAsync("note-saved");
        Assert.Single(fired);
        Assert.Equal("on-save", fired[0]);
    }

    [Fact]
    public async Task Scheduler_RunById_OneShot()
    {
        var src = new InMemoryProactiveTaskSource();
        src.Upsert(new ProactiveTask("explicit",
            new ProactiveTrigger(Manual: true), "p"));

        var runner = new DelegateProactiveTaskRunner((task, vars, ct) =>
            ValueTask.FromResult(new ProactiveTaskRunResult(task.Id, Success: true)));

        var scheduler = new ProactiveScheduler(src, runner);
        await scheduler.RefreshAsync();

        var r = await scheduler.RunByIdAsync("explicit");
        Assert.True(r.Success);
        Assert.Equal("explicit", r.TaskId);
    }

    [Fact]
    public async Task Scheduler_RunById_UnknownTask_Fails()
    {
        var src = new InMemoryProactiveTaskSource();
        var scheduler = new ProactiveScheduler(src, NullProactiveTaskRunner.Instance);
        await scheduler.RefreshAsync();
        var r = await scheduler.RunByIdAsync("does-not-exist");
        Assert.False(r.Success);
    }

    // ── Multi-tenant context separation ───────────────────────────────

    [Fact]
    public async Task Scheduler_TwoContexts_KeepLastRunStateSeparate()
    {
        var src = new InMemoryProactiveTaskSource();
        // Same task id "daily" in two different contexts.
        src.Upsert(new ProactiveTask("daily",
            new ProactiveTrigger(Cron: "* * * * *"),
            Payload: "a",
            SourceContext: "tenant-a"));
        src.Upsert(new ProactiveTask("daily",
            new ProactiveTrigger(Cron: "* * * * *"),
            Payload: "b",
            SourceContext: "tenant-b"));

        var fired = new List<(string id, string ctx)>();
        var runner = new DelegateProactiveTaskRunner((task, vars, ct) =>
        {
            lock (fired) fired.Add((task.Id, task.SourceContext ?? ""));
            return ValueTask.FromResult(new ProactiveTaskRunResult(task.Id, Success: true));
        });

        var scheduler = new ProactiveScheduler(src, runner);
        await scheduler.RefreshAsync();

        var now = new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero);
        await scheduler.TickAsync(now);

        // Both contexts' tasks fire — context separation prevents one
        // tenant's last-run from blocking another tenant.
        Assert.Equal(2, fired.Count);
        Assert.Contains(("daily", "tenant-a"), fired);
        Assert.Contains(("daily", "tenant-b"), fired);
    }
}
