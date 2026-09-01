// Casting to a television, and the music bed that plays under a clip.
//
// THE THING EVERYONE GETS WRONG ABOUT DLNA: the renderer PULLS. You do not push
// bytes to a television - you hand it a URL and it comes back and fetches from
// you. So this device has to be an HTTP server, reachable from the television,
// for as long as the thing is playing. A design that assumes a push works
// perfectly against a mock and not once against a real television.
//
// Three more, each of which silently breaks against half the devices on a
// network:
//
//   * SOAPACTION must be QUOTED. `SOAPACTION: "urn:...#Play"` works; the same
//     header without quotes is rejected by some renderers and accepted by
//     others, so it works on the television you tested on.
//
//   * SSDP headers are matched CASE-INSENSITIVELY. The spec says so and devices
//     rely on it - `LOCATION`, `Location` and `location` all appear in real
//     replies from real hardware.
//
//   * A control URL in a device description is RELATIVE, and relative to the
//     document's own base - not to the search target, and not to the root of
//     the host. Resolving it wrong sends every command to a 404.

// ─────────────────────────────────────────────────────────────────────────────
// What is being cast

/** What kind of thing is playing. */
export enum CastContentKind {
  Video = "video",
  Audio = "audio",
  Image = "image",
  /**
   * A document rendered to images first. Kept separate from Image because a
   * document has PAGES, and a renderer that treats it as one image shows only
   * the first.
   */
  Document = "document",
}

/** How to reach a renderer. */
export enum CastProtocol {
  /** UPnP/DLNA. What almost every television on a local network speaks. */
  Dlna = "dlna",
  /** Chromecast. Needs Google services, so it is not the default anywhere here. */
  GoogleCast = "google-cast",
  AirPlay = "airplay",
}

/** Where playback has got to. */
export enum CastPlaybackState {
  /** Nothing has been sent. The starting state, and distinct from Stopped. */
  Idle = "idle",
  /** The URL is sent and the renderer has not started fetching yet. */
  Buffering = "buffering",
  Playing = "playing",
  Paused = "paused",
  Stopped = "stopped",
  /**
   * The renderer reported a problem. Its OWN wording is carried, because a
   * television's error is usually more specific than anything this could infer.
   */
  Error = "error",
}

/** Something went wrong casting. */
export class CastException extends Error {
  constructor(
    message: string,
    /** The renderer's own words, when it gave any. Never invented. */
    readonly rendererMessage = "",
  ) {
    super(message);
    this.name = "CastException";
  }
}

/**
 * A control command was refused or failed.
 *
 * A SEPARATE type from CastException because the two need opposite handling: a
 * failed discovery is worth retrying, and a refused Play usually means the
 * renderer cannot handle the format, which retrying will not fix.
 */
export class CastControlException extends CastException {
  constructor(
    message: string,
    readonly action = "",
    /** The SOAP fault code, when the renderer sent one. */
    readonly faultCode = "",
    rendererMessage = "",
  ) {
    super(message, rendererMessage);
    this.name = "CastControlException";
  }
}

/**
 * A renderer's identity on this network.
 *
 * The UDN, not the IP address. A television's address changes when the lease
 * renews and its UDN does not, so a target remembered by address stops working
 * overnight while a target remembered by UDN is found again.
 */
export interface CastTargetId {
  readonly udn: string;
  readonly friendlyName: string;
}

export const castTargetId = (udn: string, friendlyName = ""): CastTargetId =>
  Object.freeze({ udn, friendlyName });

/**
 * Where the bytes are, from the RENDERER's point of view.
 *
 * The URL here must be reachable FROM THE TELEVISION, which is why a `file://`
 * path or `127.0.0.1` is useless however correct it looks on this device. The
 * local media host below exists to turn a local file into a URL that is.
 */
export interface CastMediaSource {
  readonly url: string;
  readonly mimeType: string;
  readonly sizeBytes: number;
}

/** One thing to cast. */
export interface CastMedia {
  readonly title: string;
  readonly kind: CastContentKind;
  readonly source: CastMediaSource;
  readonly durationSeconds: number;
  /** A still to show while it buffers. Optional, and absent is fine. */
  readonly posterUrl?: string;
}

/** What a renderer says it is doing. */
export interface CastStatus {
  readonly state: CastPlaybackState;
  readonly positionSeconds: number;
  readonly durationSeconds: number;
  readonly volume: number;
  readonly isMuted: boolean;
  /** Populated only in the Error state, and only with the renderer's words. */
  readonly message: string;
}

export const castStatus = (partial: Partial<CastStatus> = {}): CastStatus =>
  Object.freeze({
    state: partial.state ?? CastPlaybackState.Idle,
    positionSeconds: partial.positionSeconds ?? 0,
    durationSeconds: partial.durationSeconds ?? 0,
    volume: partial.volume ?? 1,
    isMuted: partial.isMuted ?? false,
    message: partial.message ?? "",
  });

/**
 * A local file being served to a renderer.
 *
 * Named `CastFile` rather than `File`, which in TypeScript is a DOM global. A
 * type that shadows `File` compiles and then confuses every reader, and breaks
 * the first piece of code that wanted the real one.
 */
export interface CastFile {
  readonly path: string;
  readonly mimeType: string;
  readonly sizeBytes: number;
}

// ─────────────────────────────────────────────────────────────────────────────
// SSDP

/** One reply to a search. */
export interface SsdpResponse {
  /** Where the device description lives. The only field that really matters. */
  readonly location: string;
  readonly usn: string;
  readonly searchTarget: string;
  readonly server: string;
  readonly headers: ReadonlyMap<string, string>;
}

/**
 * Finds renderers by multicast search.
 *
 * The socket is the host's. What is here is the parsing, which is where the
 * interoperability problems live.
 */
export class SsdpClient {
  /** The multicast group and port SSDP uses. Not configurable; it is the spec. */
  static readonly ADDRESS = "239.255.255.250";
  static readonly PORT = 1900;
  /** The search target for a media renderer. */
  static readonly MEDIA_RENDERER = "urn:schemas-upnp-org:device:MediaRenderer:1";

  constructor(
    private readonly search?: (message: string, timeoutMs: number) => Promise<string[]>,
  ) {}

  /**
   * MX is the maximum seconds a device may wait before replying, and it exists
   * so that a hundred devices do not answer at once. Setting it to 1 to be
   * quick makes a busy network drop replies; 2 to 3 is what devices expect.
   *
   * The blank line at the end is REQUIRED - an HTTPU message without the
   * terminating CRLF CRLF is ignored by most stacks, silently.
   */
  static buildSearch(target: string = SsdpClient.MEDIA_RENDERER, mx = 2): string {
    return [
      "M-SEARCH * HTTP/1.1",
      `HOST: ${SsdpClient.ADDRESS}:${SsdpClient.PORT}`,
      'MAN: "ssdp:discover"',
      `MX: ${mx}`,
      `ST: ${target}`,
      "",
      "",
    ].join("\r\n");
  }

  /**
   * Parses a reply.
   *
   * HEADER NAMES ARE LOWER-CASED before lookup, because the spec says they are
   * case-insensitive and real devices genuinely send `LOCATION`, `Location` and
   * `location`. A parser that matches one spelling finds two thirds of the
   * televisions on a network and misses the rest.
   */
  static parseResponse(raw: string): SsdpResponse | undefined {
    const lines = raw.split(/\r?\n/);
    if (!lines[0]?.toUpperCase().startsWith("HTTP/1.1 200")) return undefined;
    const headers = new Map<string, string>();
    for (const line of lines.slice(1)) {
      const at = line.indexOf(":");
      if (at <= 0) continue;
      // The value may itself contain colons - a URL always does - so only the
      // FIRST colon separates.
      headers.set(line.slice(0, at).trim().toLowerCase(), line.slice(at + 1).trim());
    }
    const location = headers.get("location") ?? "";
    if (!location) return undefined;
    return Object.freeze({
      location,
      usn: headers.get("usn") ?? "",
      searchTarget: headers.get("st") ?? "",
      server: headers.get("server") ?? "",
      headers: headers as ReadonlyMap<string, string>,
    });
  }

  async discover(
    target: string = SsdpClient.MEDIA_RENDERER,
    timeoutMs = 3000,
  ): Promise<readonly SsdpResponse[]> {
    if (!this.search) return [];
    const raw = await this.search(SsdpClient.buildSearch(target), timeoutMs);
    const seen = new Set<string>();
    const out: SsdpResponse[] = [];
    for (const message of raw) {
      const parsed = SsdpClient.parseResponse(message);
      // Deduplicated on USN. A device answers a search several times on
      // purpose, and a list that shows the same television four times looks
      // broken.
      if (parsed && !seen.has(parsed.usn)) {
        seen.add(parsed.usn);
        out.push(parsed);
      }
    }
    return Object.freeze(out);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// The device description

/** One service a device offers. */
export interface RendererDescription {
  readonly serviceType: string;
  /** Absolute, resolved against the description document's own base. */
  readonly controlUrl: string;
  readonly eventSubUrl: string;
}

/** What a device says about itself. */
export interface DeviceDescription {
  readonly udn: string;
  readonly friendlyName: string;
  readonly manufacturer: string;
  readonly modelName: string;
  readonly services: readonly RendererDescription[];
}

/**
 * Resolves a possibly-relative URL against a base.
 *
 * THIS IS THE ONE THAT BREAKS EVERYTHING. A control URL in a description is
 * usually relative - `/AVTransport/ctrl` or even `ctrl` - and it resolves
 * against the URL the DESCRIPTION was fetched from, not against the root of the
 * host and not against the search target. Getting it wrong sends every
 * subsequent command to a 404, and the symptom is a television that is
 * discovered perfectly and then ignores everything.
 */
export function resolveAgainst(base: string, reference: string): string {
  if (!reference) return base;
  if (/^https?:\/\//i.test(reference)) return reference;
  try {
    return new URL(reference, base).toString();
  } catch {
    // No URL support, or a base that is not a URL. Fall back to the same
    // arithmetic by hand rather than returning something that will 404.
    const match = /^(https?:\/\/[^/]+)(\/.*)?$/i.exec(base);
    if (!match) return reference;
    if (reference.startsWith("/")) return match[1] + reference;
    const dir = (match[2] ?? "/").replace(/[^/]*$/, "");
    return match[1] + dir + reference;
  }
}

/** Reads a UPnP device description. */
export class DeviceDescriptionParser {
  /**
   * Parsed with a regular expression on purpose.
   *
   * A DOM parser is not present in Node without a dependency, and this document
   * is small, flat and machine-generated. What it does NOT do is try to be a
   * general XML parser - it looks for the three elements it needs, and anything
   * it cannot find comes back empty rather than guessed.
   */
  static parse(xml: string, baseUrl: string): DeviceDescription {
    const text = (tag: string, source = xml): string => {
      const m = new RegExp(`<${tag}[^>]*>([\\s\\S]*?)</${tag}>`, "i").exec(source);
      return m ? m[1].trim() : "";
    };
    const services: RendererDescription[] = [];
    const serviceBlocks = xml.match(/<service[^>]*>[\s\S]*?<\/service>/gi) ?? [];
    for (const block of serviceBlocks) {
      const serviceType = text("serviceType", block);
      if (!serviceType) continue;
      services.push(
        Object.freeze({
          serviceType,
          controlUrl: resolveAgainst(baseUrl, text("controlURL", block)),
          eventSubUrl: resolveAgainst(baseUrl, text("eventSubURL", block)),
        }),
      );
    }
    return Object.freeze({
      udn: text("UDN"),
      friendlyName: text("friendlyName"),
      manufacturer: text("manufacturer"),
      modelName: text("modelName"),
      services: Object.freeze(services),
    });
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Control

/**
 * The metadata a renderer wants alongside a URL.
 *
 * MANY RENDERERS REFUSE TO PLAY WITHOUT IT, and the refusal is silent - the
 * television accepts SetAVTransportURI, reports success, and then does nothing.
 * So this is built and sent every time rather than only when convenient.
 */
export class DidlLite {
  /**
   * XML-escaped, and then the whole document is escaped AGAIN when it goes into
   * the SOAP body, because it travels as a string inside XML. Escaping once is
   * the commonest DIDL bug and it breaks on the first title containing an
   * ampersand - which is to say, on somebody's actual media.
   */
  static escape(text: string): string {
    return text
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&apos;");
  }

  /**
   * Many renderers REFUSE TO PLAY without this, and the refusal is silent - the
   * television accepts SetAVTransportURI, reports success, and does nothing. So
   * it is built and sent every time rather than only when convenient.
   *
   * `protocolInfo` must name the MIME type the renderer will actually receive.
   * A mismatch there is another silent refusal.
   */
  static build(media: CastMedia): string {
    const upnpClass =
      media.kind === CastContentKind.Audio
        ? "object.item.audioItem.musicTrack"
        : media.kind === CastContentKind.Image
          ? "object.item.imageItem.photo"
          : "object.item.videoItem";
    const size = media.source.sizeBytes > 0 ? ` size="${media.source.sizeBytes}"` : "";
    return [
      '<DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/"',
      ' xmlns:dc="http://purl.org/dc/elements/1.1/"',
      ' xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/">',
      '<item id="0" parentID="-1" restricted="1">',
      `<dc:title>${DidlLite.escape(media.title)}</dc:title>`,
      `<upnp:class>${upnpClass}</upnp:class>`,
      `<res protocolInfo="http-get:*:${media.source.mimeType}:*"${size}>`,
      DidlLite.escape(media.source.url),
      "</res></item></DIDL-Lite>",
    ].join("");
  }
}

/** Sends SOAP actions to a renderer. */
export class UpnpControlPoint {
  static readonly AV_TRANSPORT = "urn:schemas-upnp-org:service:AVTransport:1";
  static readonly RENDERING_CONTROL = "urn:schemas-upnp-org:service:RenderingControl:1";

  constructor(
    private readonly post?: (
      url: string,
      headers: Record<string, string>,
      body: string,
    ) => Promise<string>,
  ) {}

  /**
   * The SOAPACTION header MUST be quoted.
   *
   * `SOAPACTION: "urn:...#Play"` works everywhere; the same header without
   * quotes is accepted by some renderers and rejected by others, so a build
   * that omits them works on the television it was tested against and fails on
   * somebody else's.
   */
  static headersFor(serviceType: string, action: string): Record<string, string> {
    return {
      "Content-Type": 'text/xml; charset="utf-8"',
      SOAPACTION: `"${serviceType}#${action}"`,
    };
  }

  static envelope(serviceType: string, action: string, args: Record<string, string>): string {
    const body = Object.entries(args)
      .map(([k, v]) => `<${k}>${v}</${k}>`)
      .join("");
    return [
      '<?xml version="1.0" encoding="utf-8"?>',
      '<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"',
      ' s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">',
      "<s:Body>",
      `<u:${action} xmlns:u="${serviceType}">${body}</u:${action}>`,
      "</s:Body></s:Envelope>",
    ].join("");
  }

  async invoke(
    controlUrl: string,
    serviceType: string,
    action: string,
    args: Record<string, string>,
  ): Promise<string> {
    if (!this.post) throw new CastControlException("no transport configured", action);
    let reply: string;
    try {
      reply = await this.post(
        controlUrl,
        UpnpControlPoint.headersFor(serviceType, action),
        UpnpControlPoint.envelope(serviceType, action, args),
      );
    } catch (error) {
      throw new CastControlException(
        `${action} could not be sent`,
        action,
        "",
        error instanceof Error ? error.message : String(error),
      );
    }
    // A SOAP fault comes back with HTTP 500 AND a body. A transport that throws
    // on 500 loses the fault code, which is the only useful part - so the fault
    // is looked for in whatever body did arrive.
    const fault = /<errorCode>(\d+)<\/errorCode>/i.exec(reply);
    if (fault) {
      const description = /<errorDescription>([\s\S]*?)<\/errorDescription>/i.exec(reply);
      throw new CastControlException(
        `the renderer refused ${action}`,
        action,
        fault[1],
        description ? description[1].trim() : "",
      );
    }
    return reply;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Sessions and targets

/** A renderer this device can cast to. */
export interface CastTarget {
  readonly id: CastTargetId;
  readonly protocol: CastProtocol;
  readonly description?: DeviceDescription;
}

/** Finds renderers. */
export interface CastDiscovery {
  discover(timeoutMs?: number): Promise<readonly CastTarget[]>;
}

/** Finds nothing. The default: a device does not scan a network unprompted. */
export class NullCastDiscovery implements CastDiscovery {
  async discover(): Promise<readonly CastTarget[]> {
    return [];
  }
}

/** A live cast. */
export interface CastSession {
  play(media: CastMedia): Promise<void>;
  pause(): Promise<void>;
  resume(): Promise<void>;
  stop(): Promise<void>;
  seek(seconds: number): Promise<void>;
  status(): Promise<CastStatus>;
}

/** A DLNA renderer found on the network. */
export class DlnaCastTarget implements CastTarget {
  readonly protocol = CastProtocol.Dlna;

  constructor(
    readonly id: CastTargetId,
    readonly location: string,
    readonly description?: DeviceDescription,
  ) {}

  /**
   * The AVTransport control URL, or empty when the device does not offer one.
   *
   * A device may answer a MediaRenderer search and not carry AVTransport - a
   * renderer with only RenderingControl can change its own volume and cannot be
   * given something to play. Discovering that at Play time is too late.
   */
  get controlUrl(): string {
    return (
      this.description?.services.find((s) => s.serviceType === UpnpControlPoint.AV_TRANSPORT)
        ?.controlUrl ?? ""
    );
  }

  get canPlay(): boolean {
    return this.controlUrl.length > 0;
  }
}

/** Discovers DLNA renderers. */
export class DlnaCastDiscovery implements CastDiscovery {
  constructor(
    private readonly ssdp: SsdpClient = new SsdpClient(),
    private readonly fetchText?: (url: string) => Promise<string>,
  ) {}

  async discover(timeoutMs = 3000): Promise<readonly CastTarget[]> {
    const replies = await this.ssdp.discover(SsdpClient.MEDIA_RENDERER, timeoutMs);
    const out: DlnaCastTarget[] = [];
    for (const reply of replies) {
      let description: DeviceDescription | undefined;
      if (this.fetchText) {
        try {
          description = DeviceDescriptionParser.parse(
            await this.fetchText(reply.location),
            reply.location,
          );
        } catch {
          // A device that answered and then would not describe itself is still
          // listed, WITHOUT a description - it may come back. Dropping it makes
          // a television flicker in and out of the list.
          description = undefined;
        }
      }
      out.push(
        new DlnaCastTarget(
          castTargetId(description?.udn || reply.usn, description?.friendlyName ?? ""),
          reply.location,
          description,
        ),
      );
    }
    return Object.freeze(out);
  }
}

/** A live DLNA cast. */
export class DlnaCastSession implements CastSession {
  private lastKnown: CastStatus = castStatus();

  constructor(
    private readonly target: DlnaCastTarget,
    private readonly control: UpnpControlPoint = new UpnpControlPoint(),
  ) {}

  /**
   * `InstanceID` is 0 for every renderer in practice. It is in the protocol for
   * devices with several transports and no consumer television has one.
   */
  private static readonly INSTANCE = "0";

  async play(media: CastMedia): Promise<void> {
    if (!this.target.canPlay) {
      throw new CastControlException(
        `${this.target.id.friendlyName || "that device"} cannot be given something to play`,
        "SetAVTransportURI",
      );
    }
    // SetAVTransportURI FIRST, then Play - and they are two separate actions.
    // A renderer given Play without a URI plays whatever was there before,
    // which on a television somebody else was using is somebody else's video.
    await this.control.invoke(
      this.target.controlUrl,
      UpnpControlPoint.AV_TRANSPORT,
      "SetAVTransportURI",
      {
        InstanceID: DlnaCastSession.INSTANCE,
        CurrentURI: DidlLite.escape(media.source.url),
        // Escaped a SECOND time: the DIDL document travels as a string inside
        // this XML, so its own markup has to survive being XML.
        CurrentURIMetaData: DidlLite.escape(DidlLite.build(media)),
      },
    );
    await this.control.invoke(this.target.controlUrl, UpnpControlPoint.AV_TRANSPORT, "Play", {
      InstanceID: DlnaCastSession.INSTANCE,
      Speed: "1",
    });
    this.lastKnown = castStatus({
      state: CastPlaybackState.Buffering,
      durationSeconds: media.durationSeconds,
    });
  }

  async pause(): Promise<void> {
    await this.control.invoke(this.target.controlUrl, UpnpControlPoint.AV_TRANSPORT, "Pause", {
      InstanceID: DlnaCastSession.INSTANCE,
    });
    this.lastKnown = castStatus({ ...this.lastKnown, state: CastPlaybackState.Paused });
  }

  async resume(): Promise<void> {
    await this.control.invoke(this.target.controlUrl, UpnpControlPoint.AV_TRANSPORT, "Play", {
      InstanceID: DlnaCastSession.INSTANCE,
      Speed: "1",
    });
    this.lastKnown = castStatus({ ...this.lastKnown, state: CastPlaybackState.Playing });
  }

  async stop(): Promise<void> {
    await this.control.invoke(this.target.controlUrl, UpnpControlPoint.AV_TRANSPORT, "Stop", {
      InstanceID: DlnaCastSession.INSTANCE,
    });
    this.lastKnown = castStatus({ state: CastPlaybackState.Stopped });
  }

  /**
   * Seeking takes `REL_TIME` in `H:MM:SS`, not seconds.
   *
   * Hours are NOT zero-padded and minutes and seconds are - `0:05:03`. Padding
   * the hour is rejected by some renderers, which is the sort of thing only a
   * real television tells you.
   */
  async seek(seconds: number): Promise<void> {
    const total = Math.max(0, Math.floor(seconds));
    const h = Math.floor(total / 3600);
    const m = Math.floor((total % 3600) / 60);
    const s = total % 60;
    await this.control.invoke(this.target.controlUrl, UpnpControlPoint.AV_TRANSPORT, "Seek", {
      InstanceID: DlnaCastSession.INSTANCE,
      Unit: "REL_TIME",
      Target: `${h}:${m.toString().padStart(2, "0")}:${s.toString().padStart(2, "0")}`,
    });
  }

  async status(): Promise<CastStatus> {
    try {
      const reply = await this.control.invoke(
        this.target.controlUrl,
        UpnpControlPoint.AV_TRANSPORT,
        "GetTransportInfo",
        { InstanceID: DlnaCastSession.INSTANCE },
      );
      const state = /<CurrentTransportState>([^<]*)<\/CurrentTransportState>/i.exec(reply)?.[1];
      this.lastKnown = castStatus({
        ...this.lastKnown,
        state:
          state === "PLAYING"
            ? CastPlaybackState.Playing
            : state === "PAUSED_PLAYBACK"
              ? CastPlaybackState.Paused
              : state === "STOPPED"
                ? CastPlaybackState.Stopped
                : this.lastKnown.state,
      });
    } catch (error) {
      // A failed status poll returns the LAST KNOWN state rather than Error.
      // Televisions drop a poll routinely and a UI that flips to an error on
      // one missed reply is a UI that looks broken while playing perfectly.
      if (error instanceof CastControlException && error.faultCode) {
        this.lastKnown = castStatus({
          state: CastPlaybackState.Error,
          message: error.rendererMessage,
        });
      }
    }
    return this.lastKnown;
  }
}

/** Casts to a renderer. */
export interface CastEngine {
  readonly discovery: CastDiscovery;
  connect(target: CastTarget): CastSession;
}

/** The DLNA engine. */
export class DlnaCastEngine implements CastEngine {
  constructor(
    readonly discovery: CastDiscovery = new DlnaCastDiscovery(),
    private readonly control: UpnpControlPoint = new UpnpControlPoint(),
  ) {}

  connect(target: CastTarget): CastSession {
    if (!(target instanceof DlnaCastTarget)) {
      throw new CastException("that target does not speak DLNA");
    }
    return new DlnaCastSession(target, this.control);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Serving the bytes

/**
 * This device's address ON THE NETWORK THE RENDERER IS ON.
 *
 * Not `127.0.0.1`, which is the answer a naive lookup gives and which a
 * television cannot reach. Not the first interface either - a phone with a
 * mobile connection and Wi-Fi has two, and only one of them is where the
 * television is.
 */
export class LocalAddress {
  /**
   * Prefers a private IPv4 address, because that is what a renderer on the same
   * Wi-Fi will be able to route to. A public address may be correct and is
   * usually behind a NAT the television cannot traverse.
   */
  static isPrivateV4(address: string): boolean {
    const parts = address.split(".").map((n) => Number(n));
    if (parts.length !== 4 || parts.some((n) => !Number.isInteger(n) || n < 0 || n > 255)) {
      return false;
    }
    const [a, b] = parts;
    return a === 10 || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168);
  }

  static isLoopback(address: string): boolean {
    return address === "127.0.0.1" || address === "::1" || address.startsWith("127.");
  }

  /** Empty when nothing usable was offered, which a caller must handle. */
  static pick(candidates: readonly string[]): string {
    const usable = candidates.filter((a) => a && !LocalAddress.isLoopback(a));
    return usable.find(LocalAddress.isPrivateV4) ?? usable[0] ?? "";
  }
}

/** Serves a local file to a renderer over HTTP. */
export interface LocalMediaHost {
  readonly isRunning: boolean;
  /** The URL a RENDERER can fetch. Empty when the host is not running. */
  urlFor(file: CastFile): string;
  start(): Promise<boolean>;
  stop(): Promise<void>;
}

/**
 * A tiny HTTP host, because the renderer pulls.
 *
 * RANGE REQUESTS ARE NOT OPTIONAL. A television seeking in a video sends
 * `Range: bytes=...` and expects 206 with a `Content-Range`; a host that always
 * answers 200 with the whole file makes seeking either fail or restart from the
 * beginning, and on a large file it re-downloads the lot.
 */
export class TcpMediaHost implements LocalMediaHost {
  private running = false;
  private readonly served = new Map<string, CastFile>();
  private counter = 0;

  constructor(
    private readonly address: string,
    private readonly port: number,
    private readonly listen?: (port: number) => Promise<boolean>,
    private readonly close?: () => Promise<void>,
  ) {}

  get isRunning(): boolean {
    return this.running;
  }

  async start(): Promise<boolean> {
    if (this.running) return true;
    if (LocalAddress.isLoopback(this.address) || !this.address) {
      // Refused rather than started. A host on loopback serves a URL no
      // television can fetch, and the failure appears much later as a
      // television that buffers forever.
      return false;
    }
    this.running = this.listen ? await this.listen(this.port) : false;
    return this.running;
  }

  async stop(): Promise<void> {
    this.running = false;
    this.served.clear();
    if (this.close) await this.close();
  }

  /**
   * A file is served under an OPAQUE id, not its path.
   *
   * Putting the path in the URL would let anything on the network read any file
   * this process can, by asking for it - and a television is not the only thing
   * on a café's Wi-Fi.
   */
  urlFor(file: CastFile): string {
    if (!this.running) return "";
    const existing = [...this.served.entries()].find(([, f]) => f.path === file.path);
    const id = existing?.[0] ?? `m${(++this.counter).toString(36)}`;
    this.served.set(id, file);
    return `http://${this.address}:${this.port}/${id}`;
  }

  fileFor(id: string): CastFile | undefined {
    return this.served.get(id);
  }

  /**
   * Parses a Range header into a byte span.
   *
   * `bytes=500-` means from 500 to the end, and `bytes=-500` means the LAST
   * 500 bytes - not the first 500. Reading the second as the first serves the
   * wrong part of the file and a television shows the middle of a video when
   * asked for its end.
   */
  static parseRange(header: string, sizeBytes: number): { start: number; end: number } | undefined {
    const m = /^bytes=(\d*)-(\d*)$/.exec((header ?? "").trim());
    if (!m || sizeBytes <= 0) return undefined;
    const [, rawStart, rawEnd] = m;
    if (!rawStart && !rawEnd) return undefined;
    if (!rawStart) {
      const length = Math.min(sizeBytes, Number(rawEnd));
      return { start: sizeBytes - length, end: sizeBytes - 1 };
    }
    const start = Number(rawStart);
    if (start >= sizeBytes) return undefined;
    // The end is INCLUSIVE in HTTP, which is one off from every array slice in
    // this file - and getting it wrong drops the last byte of every response.
    const end = rawEnd ? Math.min(Number(rawEnd), sizeBytes - 1) : sizeBytes - 1;
    return end < start ? undefined : { start, end };
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Documents

/** A document being cast, page by page. */
export interface CastDocument {
  readonly title: string;
  readonly pageCount: number;
  /** Rendered images, one per page, in order. */
  readonly pageUrls: readonly string[];
}

/** Turns a document into something castable. */
export interface DocumentCastAdapter {
  readonly isAvailable: boolean;
  prepare(path: string): Promise<CastDocument | undefined>;
}

/** Prepares nothing. */
export class NullDocumentCastAdapter implements DocumentCastAdapter {
  readonly isAvailable = false;
  async prepare(): Promise<CastDocument | undefined> {
    return undefined;
  }
}

// The C# spellings, kept so the two trees line up.
export type ICastEngine = CastEngine;
export type ICastSession = CastSession;
export type ICastTarget = CastTarget;
export type ICastDiscovery = CastDiscovery;
export type ILocalMediaHost = LocalMediaHost;
export type IDocumentCastAdapter = DocumentCastAdapter;
export type CastFileAlias = CastFile;
