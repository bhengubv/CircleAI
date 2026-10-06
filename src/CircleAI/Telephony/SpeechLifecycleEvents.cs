// SpeechLifecycleEvents.cs
//
// (3.3.0) Lifecycle events for every speaking moment in a call:
// caller-speech-started, transcript-final, agent-thinking,
// agent-speaking-started, agent-speaking-finished, plus errors.
// Apps subscribe for analytics, UX (waveform animations), or audit.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) Discriminator for the union of lifecycle events.</summary>
public abstract record SpeechLifecycleEvent(string CallId, DateTimeOffset At);

public sealed record CallerSpeechStartedEvent (string CallId, DateTimeOffset At) : SpeechLifecycleEvent(CallId, At);
public sealed record CallerSpeechEndedEvent   (string CallId, DateTimeOffset At) : SpeechLifecycleEvent(CallId, At);
public sealed record TranscriptInterimEvent   (string CallId, DateTimeOffset At, string Text) : SpeechLifecycleEvent(CallId, At);
public sealed record TranscriptFinalEvent_v2  (string CallId, DateTimeOffset At, string Text) : SpeechLifecycleEvent(CallId, At);
public sealed record AgentThinkingEvent       (string CallId, DateTimeOffset At) : SpeechLifecycleEvent(CallId, At);
public sealed record AgentSpeakingStartedEvent(string CallId, DateTimeOffset At) : SpeechLifecycleEvent(CallId, At);
public sealed record AgentSpeakingFinishedEvent(string CallId, DateTimeOffset At, TimeSpan SpokenDuration) : SpeechLifecycleEvent(CallId, At);
public sealed record SpeechErrorEvent         (string CallId, DateTimeOffset At, string Stage, string Message) : SpeechLifecycleEvent(CallId, At);

/// <summary>(3.3.0) Subscription handle.</summary>
public interface ISpeechSubscription : IDisposable { }

/// <summary>(3.3.0) Speech lifecycle pub/sub.</summary>
public interface ISpeechLifecycleBus
{
    /// <summary>(3.3.0) Subscribe to a specific event type. Use <see cref="SpeechLifecycleEvent"/> for all.</summary>
    ISpeechSubscription Subscribe<TEvent>(Action<TEvent> handler) where TEvent : SpeechLifecycleEvent;

    /// <summary>(3.3.0) Publish one event. All matching subscribers are invoked synchronously.</summary>
    void Publish(SpeechLifecycleEvent ev);
}

/// <summary>(3.3.0) Default in-memory bus.</summary>
public sealed class InMemorySpeechLifecycleBus : ISpeechLifecycleBus
{
    private readonly ConcurrentDictionary<Type, ConcurrentDictionary<long, Delegate>> _subscribers = new();
    private long _nextHandle;

    public ISpeechSubscription Subscribe<TEvent>(Action<TEvent> handler) where TEvent : SpeechLifecycleEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        var bucket = _subscribers.GetOrAdd(typeof(TEvent), _ => new ConcurrentDictionary<long, Delegate>());
        var id = System.Threading.Interlocked.Increment(ref _nextHandle);
        bucket[id] = handler;
        return new SubHandle(() => bucket.TryRemove(id, out _));
    }

    public void Publish(SpeechLifecycleEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        var type = ev.GetType();
        // Walk class hierarchy so a SpeechLifecycleEvent subscriber receives every concrete type.
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            if (_subscribers.TryGetValue(t, out var bucket))
            {
                foreach (var del in bucket.Values)
                {
                    del.DynamicInvoke(ev);
                }
            }
        }
    }

    private sealed class SubHandle : ISpeechSubscription
    {
        private readonly Action _dispose;
        public SubHandle(Action dispose) { _dispose = dispose; }
        public void Dispose() => _dispose();
    }
}
