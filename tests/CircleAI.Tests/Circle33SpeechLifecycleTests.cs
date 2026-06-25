// Circle33SpeechLifecycleTests.cs
//
// (3.3.0) Tests for the speech-lifecycle bus.

using System;
using System.Collections.Generic;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33SpeechLifecycleTests
{
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    [Fact]
    public void Subscribe_ConcreteType_OnlyReceivesThatType()
    {
        var bus = new InMemorySpeechLifecycleBus();
        var caller = new List<CallerSpeechStartedEvent>();
        bus.Subscribe<CallerSpeechStartedEvent>(e => caller.Add(e));

        bus.Publish(new CallerSpeechStartedEvent("c1", Now));
        bus.Publish(new TranscriptInterimEvent("c1", Now, "hello"));

        Assert.Single(caller);
    }

    [Fact]
    public void Subscribe_BaseType_ReceivesAll()
    {
        var bus = new InMemorySpeechLifecycleBus();
        var all = new List<SpeechLifecycleEvent>();
        bus.Subscribe<SpeechLifecycleEvent>(e => all.Add(e));

        bus.Publish(new CallerSpeechStartedEvent("c1", Now));
        bus.Publish(new TranscriptInterimEvent("c1", Now, "hi"));
        bus.Publish(new AgentSpeakingFinishedEvent("c1", Now, TimeSpan.FromSeconds(1)));

        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void Dispose_Unsubscribes()
    {
        var bus = new InMemorySpeechLifecycleBus();
        var calls = 0;
        var sub = bus.Subscribe<CallerSpeechStartedEvent>(_ => calls++);

        bus.Publish(new CallerSpeechStartedEvent("c1", Now));
        sub.Dispose();
        bus.Publish(new CallerSpeechStartedEvent("c1", Now));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void MultipleSubscribers_AllInvoked()
    {
        var bus = new InMemorySpeechLifecycleBus();
        var a = 0; var b = 0;
        bus.Subscribe<TranscriptInterimEvent>(_ => a++);
        bus.Subscribe<TranscriptInterimEvent>(_ => b++);

        bus.Publish(new TranscriptInterimEvent("c1", Now, "hi"));

        Assert.Equal(1, a);
        Assert.Equal(1, b);
    }

    [Fact]
    public void Subscribe_NullHandler_Throws()
    {
        var bus = new InMemorySpeechLifecycleBus();
        Assert.Throws<ArgumentNullException>(() =>
            bus.Subscribe<CallerSpeechStartedEvent>(null!));
    }

    [Fact]
    public void Publish_NullEvent_Throws()
    {
        var bus = new InMemorySpeechLifecycleBus();
        Assert.Throws<ArgumentNullException>(() => bus.Publish(null!));
    }

    [Fact]
    public void ErrorEvent_HasStageAndMessage()
    {
        var bus = new InMemorySpeechLifecycleBus();
        var errors = new List<SpeechErrorEvent>();
        bus.Subscribe<SpeechErrorEvent>(e => errors.Add(e));

        bus.Publish(new SpeechErrorEvent("c1", Now, "tts", "service down"));

        Assert.Single(errors);
        Assert.Equal("tts", errors[0].Stage);
        Assert.Equal("service down", errors[0].Message);
    }
}
