// MqttTransportCommons.cs
//
// (3.3.0) Shared types + helpers for the MQTT transport: topic
// descriptor, QoS enum, retained-message store, subscription
// matcher.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Networking.Mqtt;

public enum MqttQos { AtMostOnce = 0, AtLeastOnce = 1, ExactlyOnce = 2 }

public sealed record MqttTopicDescriptor(string Topic, MqttQos Qos);
public sealed record MqttRetainedMessage(string Topic, ReadOnlyMemory<byte> Payload, DateTimeOffset RetainedAtUtc);
public sealed record MqttClientDescriptor(string ClientId, string Host, int Port, bool UseTls, TimeSpan KeepAlive);

public sealed class InMemoryMqttBroker
{
    private readonly ConcurrentDictionary<string, MqttRetainedMessage> _retained = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MqttClientDescriptor> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HashSet<string>> _subscriptions = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public void Connect(MqttClientDescriptor c) { ArgumentNullException.ThrowIfNull(c); _clients[c.ClientId] = c; }
    public void Disconnect(string clientId) => _clients.TryRemove(clientId, out _);
    public IReadOnlyList<MqttClientDescriptor> ConnectedClients => _clients.Values.ToArray();

    public void Subscribe(string clientId, string topicFilter)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("clientId required");
        if (string.IsNullOrWhiteSpace(topicFilter)) throw new ArgumentException("topicFilter required");
        lock (_lock)
        {
            var set = _subscriptions.GetOrAdd(clientId, _ => new HashSet<string>(StringComparer.Ordinal));
            set.Add(topicFilter);
        }
    }

    public bool Matches(string topic, string topicFilter)
    {
        if (string.IsNullOrEmpty(topic) || string.IsNullOrEmpty(topicFilter)) return false;
        var t = topic.Split('/');
        var f = topicFilter.Split('/');
        for (var i = 0; i < f.Length; i++)
        {
            if (f[i] == "#") return true;
            if (i >= t.Length) return false;
            if (f[i] == "+") continue;
            if (!string.Equals(f[i], t[i], StringComparison.Ordinal)) return false;
        }
        return t.Length == f.Length;
    }

    public void PublishRetained(MqttRetainedMessage m) { ArgumentNullException.ThrowIfNull(m); _retained[m.Topic] = m; }
    public MqttRetainedMessage? GetRetained(string topic) => _retained.GetValueOrDefault(topic);

    public IReadOnlyList<string> MatchingSubscribers(string topic)
    {
        lock (_lock)
        {
            return _subscriptions
                .Where(kv => kv.Value.Any(f => Matches(topic, f)))
                .Select(kv => kv.Key)
                .ToArray();
        }
    }
}
