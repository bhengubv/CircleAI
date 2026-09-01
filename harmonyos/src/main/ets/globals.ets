/**
 * The runtime names the ported core uses unqualified.
 *
 * WHY THIS FILE EXISTS RATHER THAN A THOUSAND REWRITTEN CALL SITES. The core
 * refers to `console`, `setTimeout`, `URL` and the rest the way every other port
 * of it does. Rewriting those references would make this port's code diverge
 * from the other seven for no behavioural reason, and divergence between ports
 * of the same logic is what costs later.
 *
 * WHAT IS ACTUALLY TRUE ON THE DEVICE, name by name — because "declared" and
 * "provided by the platform" are different claims and this file makes both:
 *
 *   REAL IN ARKTS, declared here only so the checker knows what the runtime
 *   already has:
 *     console                routed to hilog
 *     setTimeout, setInterval, clearTimeout, clearInterval
 *
 *   SHIPPED BY THIS PACKAGE, implemented in `platform.ets` and installed by the
 *   ability's `installGlobals()` call. If that call is missing, every one of
 *   these throws on first use — which is the right failure, because a shim that
 *   silently did nothing would make a cancellation that never fires or a digest
 *   that is always the same look like working code:
 *     AbortController, AbortSignal    ArkTS has no cancellation primitive
 *     Buffer                          a Node type; over Uint8Array here
 *     TextEncoder, TextDecoder        ArkTS has these behind `@ohos.util`
 *     URL, URLSearchParams            ArkTS has these behind `@ohos.url`
 *     performance                     no such global; over the system clock
 *     crypto                          the WebCrypto shape, over the platform
 *     DOMException                    not a platform type at all
 *     atob, btoa                      not present; over the base64 in Buffer
 *     fetch                           ArkTS uses `@ohos.net.http`; the ability
 *                                     supplies the transport
 *
 * Nothing here is a claim that HarmonyOS provides all of these. Four are real
 * and the rest are this package's own, and the ability has to install them.
 */

import {
  AbortControllerShim,
  Bytes,
  AbortSignalShim,
  BufferShim,
  DomExceptionShim,
  FetchResponse,
  PerformanceShim,
  TextDecoderShim,
  TextEncoderShim,
  UrlSearchParamsShim,
  UrlShim,
  WebCryptoShim,
  installFetch,
  webcrypto,
} from './platform';

declare global {
  /** Provided by ArkTS itself, routed to hilog. */
  const console: {
    log(...args: Object[]): void;
    info(...args: Object[]): void;
    warn(...args: Object[]): void;
    error(...args: Object[]): void;
    debug(...args: Object[]): void;
  };

  /**
   * Provided by ArkTS. The trailing arguments are part of the signature: the
   * core calls the three-argument form, and a declaration without it rejects
   * fifty call sites that are correct.
   */
  /**
   * The handle a timer returns.
   *
   * `unref()` is Node's way of saying "this timer should not hold the process
   * open". HarmonyOS has no such notion - an ability's lifetime is decided by
   * the system, not by outstanding timers - so it is a no-op here rather than
   * absent: the twenty-eight call sites that call it are asking for something
   * that is already true on this platform, and making them all conditional
   * would be noise.
   */
  interface TimerHandle {
    unref(): TimerHandle;
    ref(): TimerHandle;
  }
  function setTimeout(handler: Object, timeout?: number, ...args: Object[]): TimerHandle;
  function setInterval(handler: Object, timeout?: number, ...args: Object[]): TimerHandle;
  function clearTimeout(handle?: TimerHandle | number): void;
  function clearInterval(handle?: TimerHandle | number): void;

  /** Shipped by this package; installed by the ability. */
  type AbortSignal = AbortSignalShim;
  type AbortController = AbortControllerShim;
  const AbortController: typeof AbortControllerShim;
  const AbortSignal: typeof AbortSignalShim;

  /** `Buffer` is used as both a value and a type; both are declared. */
  type Buffer = Bytes;
  const Buffer: typeof BufferShim;

  type TextEncoder = TextEncoderShim;
  const TextEncoder: typeof TextEncoderShim;
  type TextDecoder = TextDecoderShim;
  const TextDecoder: typeof TextDecoderShim;

  type URL = UrlShim;
  const URL: typeof UrlShim;
  type URLSearchParams = UrlSearchParamsShim;
  const URLSearchParams: typeof UrlSearchParamsShim;

  type DOMException = DomExceptionShim;
  const DOMException: typeof DomExceptionShim;

  const performance: PerformanceShim;
  const crypto: WebCryptoShim;

  function atob(encoded: string): string;
  function btoa(raw: string): string;

  function fetch(input: Object, init?: Object): Promise<FetchResponse>;
}

/**
 * Installs everything the declarations above promise.
 *
 * ONE CALL, made by the ability before anything else runs. Split across several
 * it would be possible to install half of them, and a build with cancellation
 * but no `Buffer` fails somewhere far from the missing call.
 *
 * `fetch` is deliberately NOT installed here: it needs a transport the ability
 * supplies from `@ohos.net.http`, and a `fetch` that resolves to nothing would
 * turn every network call into a silent empty response. The ability passes it to
 * `installFetch` separately, and until it does, calling `fetch` throws.
 */
export function installGlobals(): void {
  const scope = globalThis as unknown as Record<string, Object>;
  // `unknown`, not `Object`: a class constructor is not assignable to
  // `Object` under ArkTS's stricter rules, and every value installed here
  // is a constructor or an instance rather than a plain object.
  const provide = (name: string, value: unknown): void => {
    if (scope[name] === undefined) {
      scope[name] = value as Object;
    }
  };
  provide('AbortController', AbortControllerShim);
  provide('AbortSignal', AbortSignalShim);
  provide('Buffer', BufferShim);
  provide('TextEncoder', TextEncoderShim);
  provide('TextDecoder', TextDecoderShim);
  provide('URL', UrlShim);
  provide('URLSearchParams', UrlSearchParamsShim);
  provide('DOMException', DomExceptionShim);
  provide('performance', new PerformanceShim());
  provide('crypto', webcrypto);
  provide('atob', (encoded: string): string =>
    BufferShim.toString(BufferShim.decodeBase64(encoded), 'utf8')
  );
  provide('btoa', (raw: string): string =>
    BufferShim.encodeBase64(BufferShim.encodeUtf8(raw))
  );
}

export { installFetch };
