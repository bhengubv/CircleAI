// IRealtimeTransport.cs
//
// (3.3.0) Host-supplied WebSocket transport. Connectors are
// framework-free; the ASP.NET / native host wires the actual
// ClientWebSocket against this contract.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Realtime.Cloud;

/// <summary>(3.3.0) WebSocket-style transport for a realtime session.</summary>
public interface IRealtimeTransport : IAsyncDisposable
{
    /// <summary>Send one JSON text frame.</summary>
    ValueTask SendTextAsync(string text, CancellationToken ct = default);

    /// <summary>Send one binary frame.</summary>
    ValueTask SendBinaryAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default);

    /// <summary>Stream incoming text frames.</summary>
    IAsyncEnumerable<string> ReceiveTextAsync(CancellationToken ct = default);

    /// <summary>Stream incoming binary frames.</summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveBinaryAsync(CancellationToken ct = default);

    /// <summary>Close the connection cleanly.</summary>
    ValueTask CloseAsync(CancellationToken ct = default);

    /// <summary>True while the underlying socket is open.</summary>
    bool IsOpen { get; }
}

/// <summary>(3.3.0) Factory that produces transports for a given endpoint.</summary>
public interface IRealtimeTransportFactory
{
    /// <summary>
    /// Connect to <paramref name="endpoint"/> with the given headers.
    /// </summary>
    ValueTask<IRealtimeTransport> ConnectAsync(
        Uri                                     endpoint,
        IReadOnlyDictionary<string, string>?    headers,
        CancellationToken                       ct = default);
}

/// <summary>(3.3.0) Default transport factory that throws on connect — host wires the real one.</summary>
public sealed class NullRealtimeTransportFactory : IRealtimeTransportFactory
{
    public static readonly NullRealtimeTransportFactory Instance = new();

    public ValueTask<IRealtimeTransport> ConnectAsync(
        Uri                                  endpoint,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken                    ct = default)
    {
        throw new InvalidOperationException(
            "No IRealtimeTransportFactory is registered. Add the host package that provides a real ClientWebSocket-based factory.");
    }
}
