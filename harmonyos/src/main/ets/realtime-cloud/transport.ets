/**
 * realtime-cloud/transport
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// realtime-cloud/transport.ts
//
// (3.3.0) Host-supplied WebSocket transport (IRealtimeTransport.cs). Connectors
// are framework-free; the host wires the actual WebSocket against this
// contract. Also a minimal logger seam (the C# uses Microsoft.Extensions.Logging
// ILogger) with a Null default — matching the port's "inject the platform seam"
// convention.
//
// Type mappings: ReadOnlyMemory<byte> → Uint8Array; IAsyncEnumerable<T> →
// AsyncIterable<T>; IAsyncDisposable → { disposeAsync(): Promise<void> }.

/** (3.3.0) WebSocket-style transport for a realtime session. Mirrors C# `IRealtimeTransport`. */
export interface IRealtimeTransport {
  /** Send one JSON text frame. */
  sendTextAsync(text: string, signal?: AbortSignal): Promise<void>;

  /** Send one binary frame. */
  sendBinaryAsync(bytes: Uint8Array, signal?: AbortSignal): Promise<void>;

  /** Stream incoming text frames. */
  receiveTextAsync(signal?: AbortSignal): AsyncIterable<string>;

  /** Stream incoming binary frames. */
  receiveBinaryAsync(signal?: AbortSignal): AsyncIterable<Uint8Array>;

  /** Close the connection cleanly. */
  closeAsync(signal?: AbortSignal): Promise<void>;

  /** Release resources (IAsyncDisposable). */
  disposeAsync(): Promise<void>;

  /** True while the underlying socket is open. */
  readonly isOpen: boolean;
}

/** (3.3.0) Factory that produces transports for a given endpoint. Mirrors C# `IRealtimeTransportFactory`. */
export interface IRealtimeTransportFactory {
  /** Connect to `endpoint` with the given headers. */
  connectAsync(
    endpoint: string,
    headers: ReadonlyMap<string, string> | null,
    signal?: AbortSignal,
  ): Promise<IRealtimeTransport>;
}

/**
 * (3.3.0) Default transport factory that throws on connect — the host wires the
 * real one. Mirrors C# `NullRealtimeTransportFactory`.
 */
export class NullRealtimeTransportFactory implements IRealtimeTransportFactory {
  static readonly instance = new NullRealtimeTransportFactory();

  connectAsync(
    _endpoint: string,
    _headers: ReadonlyMap<string, string> | null,
    _signal?: AbortSignal,
  ): Promise<IRealtimeTransport> {
    throw new Error(
      "No IRealtimeTransportFactory is registered. Add the host package that provides a real WebSocket-based factory.",
    );
  }
}

/**
 * Minimal logger seam — the injected analogue of
 * `Microsoft.Extensions.Logging.ILogger` reduced to the single `debug` call the
 * WebSocket session makes on an unparseable vendor frame.
 */
export interface IRealtimeLogger {
  debug(message: string, error?: unknown): void;
}

/** No-op {@link IRealtimeLogger}. Mirrors C# `NullLogger.Instance`. */
export class NullRealtimeLogger implements IRealtimeLogger {
  static readonly instance = new NullRealtimeLogger();
  debug(_message: string, _error?: unknown): void {
    /* nothing */
  }
}
