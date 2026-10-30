// CircleLogTests.cs
//
// The S1 log hub: levels by area, fan-out to sinks, ILogger through it, the rolling
// file, health events and their fingerprints - and the privacy claim, that what a
// person said never reaches a log file as text.
//
// CircleLog is process-wide and xUnit runs classes in parallel, so every test writes
// under an area of its own and reads back only that area.

using CircleAI.Core.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CircleAI.Tests;

public sealed class CircleLogTests
{
    private sealed class Capture(string area) : ICircleLogSink
    {
        private readonly List<CircleLogLine> _lines = [];
        public IReadOnlyList<CircleLogLine> Lines { get { lock (_lines) return _lines.ToList(); } }
        public void Write(in CircleLogLine line)
        {
            if (line.Area != area) return;
            lock (_lines) _lines.Add(line);
        }
    }

    private sealed class Throwing : ICircleLogSink
    {
        public void Write(in CircleLogLine line) => throw new InvalidOperationException("broken sink");
    }

    [Fact]
    public void Healing_areas_are_verbose_by_default_and_the_rest_start_at_info()
    {
        Assert.Equal(CircleLogLevel.Verbose, CircleLog.LevelFor("Engine"));
        Assert.Equal(CircleLogLevel.Verbose, CircleLog.LevelFor("Engine.Kernels"));
        Assert.Equal(CircleLogLevel.Verbose, CircleLog.LevelFor("Heal"));
        Assert.Equal(CircleLogLevel.Verbose, CircleLog.LevelFor("CircleAI.Inference.QwenTextGenerator"));
        Assert.Equal(CircleLogLevel.Info, CircleLog.LevelFor("Voice"));
    }

    [Fact]
    public void A_prefix_rule_stops_at_a_dot_boundary()
    {
        CircleLog.SetLevel("LogTestPrefix", CircleLogLevel.Error);
        Assert.Equal(CircleLogLevel.Error, CircleLog.LevelFor("LogTestPrefix.Child"));
        Assert.Equal(CircleLog.DefaultLevel, CircleLog.LevelFor("LogTestPrefixed"));
    }

    [Fact]
    public void Levels_apply_from_a_switch_file_and_nonsense_is_skipped()
    {
        Assert.Equal(2, CircleLog.ApplyLevels("LogTestA=Debug, not a rule, LogTestB=Off"));
        Assert.Equal(CircleLogLevel.Debug, CircleLog.LevelFor("LogTestA"));
        Assert.False(CircleLog.IsEnabled("LogTestB", CircleLogLevel.Error));
    }

    [Fact]
    public void A_throwing_sink_costs_the_other_sinks_nothing()
    {
        var capture = new Capture("LogTestFanOut");
        using var broken = CircleLog.AddSink(new Throwing());
        using var good = CircleLog.AddSink(capture);

        CircleLog.Info("LogTestFanOut", "hello");

        var line = Assert.Single(capture.Lines);
        Assert.Equal("hello", line.Message);
        Assert.Equal(CircleLogLevel.Info, line.Level);
    }

    [Fact]
    public void An_ILogger_writes_through_the_hub_under_its_type_name()
    {
        var area = typeof(CircleLogTests).FullName!;
        var capture = new Capture(area);
        using var _ = CircleLog.AddSink(capture);

        CircleLog.LoggerFor<CircleLogTests>().LogWarning("disk {Percent}% full", 93);

        var line = Assert.Single(capture.Lines);
        Assert.Equal("disk 93% full", line.Message);
        Assert.Equal(CircleLogLevel.Warn, line.Level);
    }

    [Fact]
    public void What_a_person_said_never_reaches_the_log_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "circlelog-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var file = new RollingFileSink(Path.Combine(dir, "test.log"));
            using var _ = CircleLog.AddSink(file);

            CircleLog.Info("LogTestPrivacy", $"asked: {Sensitive.Text("my bank pin is 4321")}");
            var health = HealthEvents.From("LogTestPrivacy", "answer",
                new InvalidOperationException("could not parse 'my bank pin is 4321' at 17"));
            CircleLog.Warn("LogTestPrivacy", health.Template);
            file.Dispose();

            var text = File.ReadAllText(file.FilePath);
            Assert.Contains("asked: [19 chars]", text);
            Assert.DoesNotContain("bank pin", text);
            Assert.DoesNotContain("4321", text);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void One_failure_has_one_fingerprint_whatever_its_numbers()
    {
        static void Fail(int n) => throw new IndexOutOfRangeException($"index {n} outside 0..{n * 2}");

        HealthEvent Catch(int n)
        {
            try { Fail(n); }
            catch (Exception ex) { return HealthEvents.From("Engine", "decode", ex); }
            throw new InvalidOperationException("unreachable");
        }

        var a = Catch(3);
        var b = Catch(12345);

        Assert.Equal(a.Fingerprint, b.Fingerprint);
        Assert.Equal(a.Template, b.Template);
        Assert.StartsWith("CircleAI:IndexOutOfRangeException:", a.Fingerprint);
        Assert.DoesNotMatch(@"\d", a.Template);
    }

    [Fact]
    public void An_unhandled_failure_becomes_an_error_line_and_a_health_event()
    {
        var capture = new Capture("Crash");
        using var _ = CircleLog.AddSink(capture);
        HealthEvent? seen = null;
        void Seen(HealthEvent e) { if (e.Attempted == "log-test unhandled") seen = e; }
        HealthEvents.Raised += Seen;
        try
        {
            var raised = CircleLog.ReportUnhandled(new InvalidOperationException("boom"), "log-test unhandled");

            Assert.Same(raised, seen);
            Assert.Equal("Crash", raised.Area);
            Assert.Contains(HealthEvents.Recent, e => ReferenceEquals(e, raised));
            Assert.Contains(capture.Lines, l => l.Level == CircleLogLevel.Error && l.Exception is InvalidOperationException);
        }
        finally
        {
            HealthEvents.Raised -= Seen;
        }
    }

    [Fact]
    public void The_log_file_rolls_over_at_its_cap()
    {
        var dir = Path.Combine(Path.GetTempPath(), "circlelog-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var file = new RollingFileSink(Path.Combine(dir, "roll.log"), maxBytes: 1024);
            for (var i = 0; i < 40; i++)
                file.Write(new CircleLogLine(DateTimeOffset.UtcNow, CircleLogLevel.Info, "LogTestRoll", new string('x', 80), null));
            file.Dispose();

            Assert.True(File.Exists(file.FilePath + ".1"));
            Assert.True(new FileInfo(file.FilePath).Length <= 1024);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
