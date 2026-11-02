// StepFourServiceTests.cs
//
// STEP 4'S SERVICE HALF, OFF A PHONE: the balance that shrinks before it drops and never flaps, the
// kernel measured once per device, and a picture across the link and the process boundary. The
// settings, the Settings scope and "what can you do right now" are in StepFourSettingsTests.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Assistant.Requests;
using CircleAI.Core;
using CircleAI.Inference;
using CircleAI.Inference.Managed.Kernels;
using CircleAI.Linking;
using Xunit;

namespace CircleAI.Tests;

public sealed class BrainBalanceTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "circleai-balance-" + Guid.NewGuid().ToString("N") + ".json");
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { File.Delete(_file); } catch { /* temp */ }
    }

    private static BalanceReading Battery(int pct, bool charging = false) => new(pct, charging, 0, false);

    private static BalanceDecision Apply(BrainBalance balance, BalanceReading reading, DateTimeOffset at)
    {
        var decision = balance.Decide(reading, at);
        Assert.NotNull(decision);
        balance.Applied(decision!, at);
        return decision!;
    }

    [Fact]
    public void The_ladder_gives_ground_cheapest_first()
    {
        Assert.Null(BalanceShape.Full.KvMode);
        Assert.Equal(KvCompressionMode.TurboQuant3Bit, BalanceShape.Of(BalanceRung.LeanMemory).KvMode);
        Assert.Equal(0.5, BalanceShape.Of(BalanceRung.ShortContext).ContextShare);
        Assert.False(BalanceShape.Of(BalanceRung.NoExtras).Vision);
        Assert.False(BalanceShape.Of(BalanceRung.NoExtras).Drafter);
        Assert.True(BalanceShape.Of(BalanceRung.SmallerBrain).SmallerBrain);
        Assert.True(BalanceShape.Of(BalanceRung.Released).Released);

        Assert.True(BalanceShape.Of(BalanceRung.LeanMemory).Rebuilds(BalanceShape.Full));
        Assert.False(BalanceShape.Of(BalanceRung.NoExtras).Rebuilds(BalanceShape.Of(BalanceRung.NoExtras)));

        // An engine that does not read the working memory's bits is not reloaded for them alone.
        Assert.False(BalanceShape.Of(BalanceRung.LeanMemory).Rebuilds(BalanceShape.Full, workingMemoryCounts: false));
        Assert.True(BalanceShape.Of(BalanceRung.ShortContext).Rebuilds(BalanceShape.Full, workingMemoryCounts: false));
    }

    [Fact]
    public void A_low_battery_steps_down_one_rung_at_a_time_never_closer_than_the_gap()
    {
        var balance = new BrainBalance(file: _file);
        Assert.Equal(BalanceRung.LeanMemory, Apply(balance, Battery(18), T0).To);

        Assert.Null(balance.Decide(Battery(18), T0.AddMinutes(5)));                  // inside the gap
        Assert.Equal(BalanceRung.ShortContext, Apply(balance, Battery(18), T0.AddMinutes(10)).To);
        Assert.Equal(BalanceRung.NoExtras, Apply(balance, Battery(17), T0.AddMinutes(20)).To);
        Assert.Null(balance.Decide(Battery(16), T0.AddMinutes(40)));                  // the floor for a low battery
    }

    [Fact]
    public void The_band_holds_a_wobbling_battery_and_calm_grows_the_brain_back_once()
    {
        var balance = new BrainBalance(file: _file);
        Apply(balance, Battery(19), T0);

        // 22% is above the line it came in on and below the one it leaves on: still pressed.
        Assert.Equal(BalanceRung.ShortContext, Apply(balance, Battery(22), T0.AddMinutes(10)).To);

        // Charging ends it; the brain grows back only after ten calm minutes, in one step.
        Assert.Null(balance.Decide(Battery(22, charging: true), T0.AddMinutes(25)));
        var back = Apply(balance, Battery(23, charging: true), T0.AddMinutes(36));
        Assert.Equal(BalanceRung.Full, back.To);
        Assert.Null(back.Kind);
        Assert.Equal(BalanceRung.ShortContext, balance.Worked[PressureKind.LowBattery]);
    }

    [Fact]
    public void Heat_that_comes_back_goes_straight_to_what_it_needed_last_time_and_a_restart_remembers()
    {
        var balance = new BrainBalance(file: _file);
        var hot = new BalanceReading(80, false, Heat: 1, MemoryCritical: false);
        Apply(balance, hot, T0);
        Apply(balance, hot, T0.AddMinutes(10));
        Apply(balance, hot, T0.AddMinutes(20));                                       // NoExtras
        Assert.Null(balance.Decide(BalanceReading.Calm, T0.AddMinutes(25)));
        Apply(balance, BalanceReading.Calm, T0.AddMinutes(36));                        // back to Full

        // The next hot spell starts where the last one ended up.
        var again = new BrainBalance(file: _file);                                    // and a restart keeps it
        Assert.Equal(BalanceRung.Full, again.Current);
        Assert.Equal(BalanceRung.NoExtras, Apply(again, hot, T0.AddMinutes(50)).To);
    }

    [Fact]
    public void A_critical_device_may_go_all_the_way_to_letting_the_brain_go()
    {
        var balance = new BrainBalance();
        var dying = new BalanceReading(8, false, 0, MemoryCritical: true);
        var at = T0;
        BalanceDecision? last = null;
        for (var i = 0; i < 6; i++, at = at.AddMinutes(10))
            if (balance.Decide(dying, at) is { } d) { balance.Applied(d, at); last = d; }
        Assert.Equal(BalanceRung.Released, last!.To);
        Assert.Equal(PressureKind.Memory, last.Kind);
        Assert.Contains("out of memory", last.Reason);
    }

    [Fact]
    public async Task The_loop_changes_only_between_turns_then_primes_and_rests_when_released()
    {
        var balance = new BrainBalance(new BalanceOptions { MinimumGap = TimeSpan.Zero, CalmFor = TimeSpan.Zero });
        var idle = false;
        var shapes = new List<BalanceShape>();
        var primed = 0;
        var rested = 0;
        var reading = new BalanceReading(5, false, 2, true);
        using var loop = new BalanceLoop(balance, () => reading, () => idle,
            apply: (shape, _) => { shapes.Add(shape); return Task.CompletedTask; },
            prime: _ => { primed++; return Task.CompletedTask; },
            rest: _ => { rested++; return Task.CompletedTask; });

        Assert.Null(await loop.TickAsync(CancellationToken.None));                    // mid-answer: nothing changes
        Assert.Empty(shapes);

        idle = true;
        for (var i = 0; i < 5; i++) await loop.TickAsync(CancellationToken.None);
        Assert.Equal(BalanceRung.Released, balance.Current);
        Assert.Equal(4, primed);                                                       // every change that kept a brain
        await loop.TickAsync(CancellationToken.None);
        Assert.Equal(1, rested);                                                       // released: let go again

        reading = BalanceReading.Calm;
        await loop.TickAsync(CancellationToken.None);                                  // calm starts
        Assert.Equal(BalanceRung.Full, (await loop.TickAsync(CancellationToken.None))?.To ?? balance.Current);
        Assert.Equal(5, primed);
    }

    [Fact]
    public async Task Switched_off_by_the_person_the_brain_grows_back_and_stays_as_built()
    {
        var balance = new BrainBalance(new BalanceOptions { MinimumGap = TimeSpan.Zero, CalmFor = TimeSpan.Zero });
        var on = true;
        using var loop = new BalanceLoop(balance, () => new BalanceReading(5, false, 0, false), () => true,
            apply: (_, _) => Task.CompletedTask, enabled: () => on);
        await loop.TickAsync(CancellationToken.None);
        Assert.NotEqual(BalanceRung.Full, balance.Current);

        on = false;
        await loop.TickAsync(CancellationToken.None);
        await loop.TickAsync(CancellationToken.None);
        Assert.Equal(BalanceRung.Full, balance.Current);
    }
}

[Collection(nameof(StepFourStaticCollection))]
public sealed class KernelThreadsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("circleai-kernel-").FullName;

    public KernelThreadsTests() => KernelThreads.Forget();

    public void Dispose()
    {
        KernelThreads.MeasureForTest = null;
        KernelThreads.MeasurePq2ForTest = null;
        KernelThreads.Forget();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private static KernelBench.Result At(int threads, double gbPerSecond) =>
        new(KernelPath.Lut, 6144, 2048, 1, gbPerSecond, threads);

    [Fact]
    public void Both_packings_are_measured_once_and_read_back_by_the_planner()
    {
        // OPEN-GAPS C8: how fast each packing reads its weights here is kept beside the threads, and
        // the main process - which plans and never builds a brain - reads it back without measuring.
        KernelThreads.MeasureForTest = _ => [At(1, 0.30), At(2, 0.50), At(3, 0.60)];
        KernelThreads.MeasurePq2ForTest = _ => [At(1, 0.40), At(3, 1.80)];
        Assert.Equal(3, KernelThreads.Choose(fallback: 2, directory: _dir));

        KernelThreads.Forget();
        var kept = KernelThreads.Remembered(_dir)!;
        Assert.Equal(0.60, kept.Ptq1GigabytesPerSecond, 6);
        Assert.Equal(1.80, kept.Pq2GigabytesPerSecond, 6);
        Assert.Equal(3.0, kept.PackingFactor("PTQ1_0", "PQ2_0")!.Value, 6);
        Assert.Equal(1 / 3.0, kept.PackingFactor("PQ2_0-MTP", "PTQ1_0")!.Value, 6);
        Assert.Null(kept.PackingFactor("Q8_0", "PTQ1_0"));
    }

    [Fact]
    public void A_choice_kept_before_both_packings_were_measured_is_measured_again()
    {
        // The file every device that ran S7 holds: three lines, no rates, the fingerprint before.
        var old = KernelThreads.Fingerprint().Replace("|packings", string.Empty, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(_dir, KernelThreads.FileName), $"3\n0.86\n{old}");
        Assert.Null(KernelThreads.Remembered(_dir));

        var measured = 0;
        KernelThreads.MeasureForTest = _ => { measured++; return [At(1, 0.37), At(3, 0.30)]; };
        Assert.Equal(1, KernelThreads.Choose(fallback: 2, directory: _dir));
        Assert.Equal(1, measured);
        Assert.Equal(0.37, KernelThreads.Remembered(_dir)!.Ptq1GigabytesPerSecond, 6);
    }

    [Fact]
    public void The_fewest_threads_within_five_percent_of_the_fastest_win()
    {
        var p30 = new[] { At(1, 0.30), At(2, 0.55), At(3, 0.71), At(4, 0.73), At(8, 0.60) };
        Assert.Equal(3, KernelThreads.Pick(p30, "f")!.Threads);    // 4 is faster, by less than 5%
        Assert.Equal(4, KernelThreads.Pick([At(1, 0.3), At(4, 0.9)], "f")!.Threads);
        Assert.Null(KernelThreads.Pick([], "f"));
    }

    [Fact]
    public void The_choice_is_measured_once_per_device_and_read_back_after_that()
    {
        var measured = 0;
        KernelThreads.MeasureForTest = most => { measured++; return [At(1, 0.3), At(2, 0.5), At(3, 0.6)]; };
        Assert.Equal(3, KernelThreads.Choose(fallback: 2, directory: _dir));
        Assert.Equal(1, measured);
        Assert.Equal(3, KernelThreads.Known!.Threads);

        // A new process: nothing in memory, the file answers, nothing is measured again.
        KernelThreads.Forget();
        KernelThreads.MeasureForTest = _ => throw new InvalidOperationException("measured twice");
        Assert.Equal(3, KernelThreads.Choose(fallback: 2, directory: _dir));
    }

    [Fact]
    public void A_measurement_that_fails_falls_back_to_the_devices_rule_of_thumb()
    {
        KernelThreads.MeasureForTest = _ => throw new InvalidOperationException("no kernel");
        Assert.Equal(2, KernelThreads.Choose(fallback: 2, directory: _dir));
    }

    [Fact]
    public void The_brain_factory_asks_the_measurement_before_the_rule_of_thumb()
    {
        // THE CALLER: a brain on the C# engine that the host gave no thread count is built on the
        // measured count (BrainFactory), so the measurement is reachable, not a library on its own.
        var factory = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CircleAI", "Inference", "BrainFactory.cs"));
        Assert.Contains("KernelThreads.Choose(fallback: DeviceContext.Read().BrainThreads)", factory, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "CircleAI"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root was not found");
    }
}

public sealed class PictureAcrossTheLinkTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("circleai-pictures-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    // A parcel, in memory: the order the codec writes is the order it reads.
    private sealed class FakeParcel : LinkAudioCodec.IWriter, LinkAudioCodec.IReader,
                                      LinkImageCodec.IHandleWriter, LinkImageCodec.IHandleReader
    {
        private readonly Queue<object?> _fields = new();
        public byte[]? Handle;
        public int Handles;

        public void WriteInt(int value) => _fields.Enqueue(value);
        public void WriteString(string? value) => _fields.Enqueue(value);
        public void WriteBytes(byte[] value) => _fields.Enqueue(value);
        public void WriteHandle(byte[] bytes) { Handle = bytes; Handles++; }
        public int ReadInt() => (int)_fields.Dequeue()!;
        public string? ReadString() => (string?)_fields.Dequeue();
        public byte[] ReadBytes() => (byte[])_fields.Dequeue()!;
        public byte[]? ReadHandle(int length) => Handle is { } h && h.Length == length ? h : null;
    }

    [Fact]
    public void A_small_picture_rides_inline_and_a_big_one_behind_a_file_handle()
    {
        foreach (var (size, byHandle) in new[] { (300 * 1024, false), (3 * 1024 * 1024, true) })
        {
            var picture = new byte[size];
            new Random(size).NextBytes(picture);
            var parcel = new FakeParcel();
            Assert.True(LinkImageCodec.TryWriteRequest(parcel, parcel, new LinkImageRequest("what is this?", "s", "r", picture), out _));
            Assert.Equal(byHandle ? 1 : 0, parcel.Handles);
            Assert.Equal(byHandle, LinkImage.ByHandle(size));

            var back = LinkImageCodec.TryReadRequest(parcel, parcel);
            Assert.NotNull(back);
            Assert.Equal("what is this?", back!.Question);
            Assert.Equal(picture, back.Image);
        }
    }

    [Fact]
    public void A_picture_past_the_limit_or_shorter_than_it_claims_is_refused()
    {
        var parcel = new FakeParcel();
        Assert.False(LinkImageCodec.TryWriteRequest(parcel, parcel,
            new LinkImageRequest("q", "s", "r", new byte[LinkImage.MaxBytes + 1]), out var why));
        Assert.Contains("MB", why);
        Assert.False(LinkImage.Fits(0, out _));

        // A handle whose file is shorter than the length written is refused, never padded.
        var liar = new FakeParcel();
        Assert.True(LinkImageCodec.TryWriteRequest(liar, liar, new LinkImageRequest("q", "s", "r", new byte[2 * 1024 * 1024]), out _));
        liar.Handle = new byte[10];
        Assert.Null(LinkImageCodec.TryReadRequest(liar, liar));
    }

    [Fact]
    public void A_picture_waits_beside_its_ask_and_crosses_to_the_worker_as_a_file()
    {
        var store = new LinkImageStore(Path.Combine(_dir, "pictures"));
        var picture = new byte[] { 0xFF, 0xD8, 1, 2, 3 };
        var held = store.Hold(picture, "app:1", "rid-1");
        Assert.Equal(picture, store.For("app:1", "rid-1"));
        Assert.Null(store.For("app:1", "rid-2"));
        Assert.Null(store.For("app:2", "rid-1"));   // another app's request id is not this app's picture
        Assert.Null(store.For("app:1", null));
        Assert.DoesNotContain("rid-1", held);        // an app's text never becomes a path

        // A resend of the same request keeps the picture already there.
        Assert.Equal(held, store.Hold(new byte[] { 9 }, "app:1", "rid-1"));
        Assert.Equal(picture, store.For("app:1", "rid-1"));

        var dropped = store.Hold(picture, "app:1", "rid-3");
        store.Drop(dropped);
        Assert.False(File.Exists(dropped));
        Assert.Equal(0, store.Sweep(TimeSpan.FromDays(1)));
        Assert.Equal(1, store.Sweep(TimeSpan.Zero - TimeSpan.FromSeconds(1)));

        // The worker map carries a path, not the bytes; the other side reads them back.
        var map = new Dictionary<string, string>();
        var spilled = Path.Combine(_dir, "spill.img");
        WorkerWire.PutMessages(map, [new ChatMessage("user", "what is it?") { ImageBytes = picture }],
            spill: bytes => { File.WriteAllBytes(spilled, bytes); return spilled; });
        Assert.DoesNotContain(map.Values, v => v.Contains("ÿ"));
        var back = WorkerWire.GetMessages(map);
        Assert.Equal(picture, back[0].ImageBytes);

        // Without a spill a message is text, as it always was.
        var plain = new Dictionary<string, string>();
        WorkerWire.PutMessages(plain, [new ChatMessage("user", "x") { ImageBytes = picture }]);
        Assert.Null(WorkerWire.GetMessages(plain)[0].ImageBytes);
    }

    private sealed class SeeingBrain : IBrainWorker
    {
        public BrainWorkerStatus Status { get; private set; } = BrainWorkerStatus.NotStarted(DateTimeOffset.UtcNow);
        public event EventHandler<BrainWorkerStatus>? StatusChanged;
        public readonly List<BrainJob> Jobs = new();

        public Task EnsureReadyAsync(CancellationToken ct)
        {
            Status = Status with { Phase = BrainPhase.Ready, ModelId = "vision" };
            StatusChanged?.Invoke(this, Status);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<BrainChunk> GenerateAsync(BrainJob job, [EnumeratorCancellation] CancellationToken ct)
        {
            Jobs.Add(job);
            await Task.Yield();
            yield return new BrainChunk("a cat ");
            yield return new BrainChunk(string.Empty, Final: true, Timing: new TurnTiming(PrefillMs: 1, PromptTokens: 1, DecodeMs: 1, OutputTokens: 2));
        }

        public Task ReleaseAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task The_queue_hands_the_brain_the_picture_its_ask_is_about()
    {
        using var asks = new SqliteAskStore(Path.Combine(_dir, "asks.db"));
        var brain = new SeeingBrain();
        var store = new LinkImageStore(Path.Combine(_dir, "pictures"));
        await using var front = new RequestCoordinator(asks, brain, ServiceLimits.Default with { TextWriteInterval = TimeSpan.Zero })
        {
            Images = ask => store.For(ask.CallerKey, ask.ClientRequestId),
        };

        // THE QUEUE IS ALREADY RUNNING, as it is on the phone: the picture is on disk before the ask
        // exists, so the brain can never be handed the question without it.
        front.Start();
        var picture = new byte[] { 0x89, 0x50, 0x4E, 0x47, 9, 9 };
        store.Hold(picture, "app:1", "rid");
        var admission = front.Submit(new AskSubmission("app:1", "rid", "", AskLane.Foreground, "What is in this picture?"));

        var done = await front.WaitAsync("app:1", admission.Record!.Id, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.Equal(AskState.Succeeded, done!.State);
        Assert.Equal(picture, brain.Jobs.Single().Messages[^1].ImageBytes);

        // An ask with no picture stays text.
        var plain = front.Submit(new AskSubmission("app:1", "other", "", AskLane.Foreground, "Hello"));
        await front.WaitAsync("app:1", plain.Record!.Id, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.Null(brain.Jobs[^1].Messages[^1].ImageBytes);
    }
}
