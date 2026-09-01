/**
 * video/generation
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// Video, what a camera sees, the carriers, and the last of the seams.
//
// THE STYLE REFERENCE IS THE INTERESTING PIECE. A generated video in somebody
// else's visual style is a thing that needs saying out loud: this carries an
// ATTRIBUTION with every reference, the attribution travels with anything made
// from it, and a reference with no attribution is refused. Not because a
// licence file demands it - because a video made in somebody's style and
// published without their name is passing off, and the code should make that
// hard rather than convenient.

// ─────────────────────────────────────────────────────────────────────────────
// Video

/** Output size. */
export interface VideoResolution {
  readonly width: number;
  readonly height: number;
  readonly framesPerSecond: number;
}

export const videoResolution = (
  width = 1920,
  height = 1080,
  framesPerSecond = 30,
): VideoResolution => {
  if (width <= 0 || height <= 0) throw new Error("a resolution must be positive");
  // ODD DIMENSIONS BREAK MOST ENCODERS, which subsample chroma by two and
  // silently crop or refuse. Caught here rather than as an encoder error nobody
  // can read.
  if (width % 2 !== 0 || height % 2 !== 0) {
    throw new Error(`${width}x${height} has an odd dimension, which most encoders cannot take`);
  }
  return Object.freeze({ width, height, framesPerSecond });
};

/** Sound to lay under a clip. */
export interface AudioTrack {
  readonly pcm: Uint8Array;
  readonly sampleRateHz: number;
  readonly channels: number;
  /** 0..1. Under narration this should be well below it - a bed at the same
   * level as the voice is a bed that competes with it. */
  readonly level: number;
  /** Where in the clip it starts, so a bed can come in after the first words
   * rather than under them. */
  readonly startAtMs: number;
}

/** A named visual style. */
export interface StyleId {
  readonly value: string;
}

export const styleId = (value: string): StyleId => {
  if (!value.trim()) throw new Error("a style needs an identifier");
  return Object.freeze({ value: value.trim() });
};

/**
 * Who a style belongs to.
 *
 * REQUIRED ON EVERY REFERENCE. A style reference with no attribution cannot be
 * constructed, because the whole risk of style transfer is producing something
 * in somebody's manner with their name removed.
 */
export interface StyleAttribution {
  readonly creator: string;
  /** Where it came from, so the claim can be checked. */
  readonly sourceUrl: string;
  /** What the creator permits, in their words. Not interpreted here - a licence
   * this code summarised would be a licence this code got wrong. */
  readonly licence: string;
  readonly consentGiven: boolean;
}

export function styleAttribution(partial: Partial<StyleAttribution> = {}): StyleAttribution {
  const creator = (partial.creator ?? "").trim();
  if (!creator) throw new Error("a style attribution must name a creator");
  return Object.freeze({
    creator,
    sourceUrl: partial.sourceUrl ?? "",
    licence: partial.licence ?? "",
    // Consent is FALSE by default and has to be set explicitly. A style
    // somebody has not agreed to is one this will not generate from.
    consentGiven: partial.consentGiven ?? false,
  });
}

/** One frame of a reference. */
export interface StyleReferenceFrame {
  readonly bytes: Uint8Array;
  readonly mimeType: string;
  readonly atMs: number;
}

/** A style, with who it belongs to. */
export interface StyleReference {
  readonly id: StyleId;
  readonly attribution: StyleAttribution;
  readonly frames: readonly StyleReferenceFrame[];
  readonly description: string;
}

/** Holds style references. */
export interface StyleReferenceStore {
  put(reference: StyleReference): { stored: boolean; reason: string };
  get(id: StyleId): StyleReference | undefined;
  all(): readonly StyleReference[];
}

/**
 * References in memory.
 *
 * REFUSES A REFERENCE WITHOUT CONSENT, at the door rather than at generation
 * time. A store that holds one and refuses later has already made the copy.
 */
export class InMemoryStyleReference implements StyleReferenceStore {
  private readonly references = new Map<string, StyleReference>();

  put(reference: StyleReference): { stored: boolean; reason: string } {
    if (!reference.attribution.consentGiven) {
      return {
        stored: false,
        reason: `${reference.attribution.creator} has not agreed to this style being used`,
      };
    }
    if (reference.frames.length === 0) {
      return { stored: false, reason: "a style reference needs at least one frame" };
    }
    this.references.set(reference.id.value, reference);
    return { stored: true, reason: "" };
  }

  get(id: StyleId): StyleReference | undefined {
    return this.references.get(id.value);
  }

  all(): readonly StyleReference[] {
    return Object.freeze([...this.references.values()]);
  }

  remove(id: StyleId): boolean {
    return this.references.delete(id.value);
  }
}

/** What to turn into a script. */
export interface StyleScriptRequest {
  readonly brief: string;
  readonly styleId?: StyleId;
  readonly durationSeconds: number;
  readonly language: string;
}

/** A script, shot by shot. */
export interface StyleScriptResult {
  readonly shots: readonly { description: string; seconds: number; narration: string }[];
  readonly totalSeconds: number;
  readonly error: string;
}

/** Turns a brief into a script. */
export interface StyleScript {
  readonly isAvailable: boolean;
  write(request: StyleScriptRequest): Promise<StyleScriptResult>;
}

/** Writes nothing. */
export class NullStyleScript implements StyleScript {
  readonly isAvailable = false;
  async write(): Promise<StyleScriptResult> {
    return Object.freeze({
      shots: [],
      totalSeconds: 0,
      error: "no script writer is available on this device",
    });
  }
}

/** What to generate. */
export interface VideoGenerationRequest {
  readonly script: StyleScriptResult;
  readonly resolution: VideoResolution;
  readonly styleId?: StyleId;
  readonly audio?: AudioTrack;
  readonly seed?: number;
}

/** What came out. */
export interface VideoGenerationResult {
  readonly bytes: Uint8Array;
  readonly mimeType: string;
  readonly resolution: VideoResolution;
  readonly durationSeconds: number;
  /**
   * Carried with the output and expected to be shown wherever it is.
   *
   * A video generated in somebody's style, published without their name, is
   * passing off - and the attribution travelling with the bytes is what makes
   * doing the right thing the easy path.
   */
  readonly attribution?: StyleAttribution;
  readonly error: string;
}

/** Makes a video. */
export interface VideoGenerator {
  readonly isAvailable: boolean;
  generate(request: VideoGenerationRequest): Promise<VideoGenerationResult>;
}

/** Makes nothing. */
export class NullVideoGenerator implements VideoGenerator {
  readonly isAvailable = false;
  async generate(request: VideoGenerationRequest): Promise<VideoGenerationResult> {
    return Object.freeze({
      bytes: new Uint8Array(0),
      mimeType: "video/mp4",
      resolution: request.resolution,
      durationSeconds: 0,
      error: "no video generator is available on this device",
    });
  }
}

/**
 * Wraps a generator so the attribution cannot be lost.
 *
 * The generator produces bytes; this attaches whose style they are in. Putting
 * it here rather than trusting each generator means a new generator cannot
 * forget.
 */
export class AttributedVideoGenerator implements VideoGenerator {
  constructor(
    private readonly inner: VideoGenerator,
    private readonly styles: StyleReferenceStore,
  ) {}

  get isAvailable(): boolean {
    return this.inner.isAvailable;
  }

  async generate(request: VideoGenerationRequest): Promise<VideoGenerationResult> {
    let attribution: StyleAttribution | undefined;
    if (request.styleId) {
      const reference = this.styles.get(request.styleId);
      if (!reference) {
        return Object.freeze({
          bytes: new Uint8Array(0),
          mimeType: "video/mp4",
          resolution: request.resolution,
          durationSeconds: 0,
          error: `no style reference for ${request.styleId.value}`,
        });
      }
      if (!reference.attribution.consentGiven) {
        return Object.freeze({
          bytes: new Uint8Array(0),
          mimeType: "video/mp4",
          resolution: request.resolution,
          durationSeconds: 0,
          error: `${reference.attribution.creator} has not agreed to this style being used`,
        });
      }
      attribution = reference.attribution;
    }
    const result = await this.inner.generate(request);
    return Object.freeze({ ...result, attribution });
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// What a camera sees

/** How a frame's bytes are laid out. */
export enum VideoPixelFormat {
  Rgba8888 = "rgba8888",
  /** Android's camera default. Two planes, chroma subsampled - a frame read as
   * RGBA comes out green and stretched, which is the classic symptom. */
  Nv21 = "nv21",
  Yuv420 = "yuv420",
  Jpeg = "jpeg",
}

/** One frame from a camera. */
export interface VideoFrame {
  readonly bytes: Uint8Array;
  readonly width: number;
  readonly height: number;
  readonly format: VideoPixelFormat;
  /**
   * How far the frame is rotated from upright, in degrees.
   *
   * A phone camera sensor is mounted at 90 degrees to the screen on almost
   * every device, so a frame used without rotating it has faces on their side -
   * and a face detector finds nothing at all.
   */
  readonly rotationDegrees: number;
  readonly atMs: number;
}

/** Captures frames. */
export interface VideoCapture {
  readonly isAvailable: boolean;
  start(): Promise<boolean>;
  stop(): Promise<void>;
  onFrame(handler: (frame: VideoFrame) => void): () => void;
}

/**
 * Captures nothing.
 *
 * THE DEFAULT. A camera does not open because a module was imported, and a
 * build with no capture wired reads no frames rather than reading whatever
 * binding happens to exist.
 */
export class NullVideoCapture implements VideoCapture {
  readonly isAvailable = false;
  async start(): Promise<boolean> {
    return false;
  }
  async stop(): Promise<void> {
    /* nothing to stop */
  }
  onFrame(): () => void {
    return () => undefined;
  }
}

/** Notices unusual Bluetooth activity. */
export interface BluetoothAnomalyDetector {
  readonly isAvailable: boolean;
  observe(deviceId: string, rssi: number, atMs: number): void;
  /** Devices that have been following. Reported, never acted on. */
  persistent(nowMs: number): readonly string[];
}

/**
 * Notices nothing.
 *
 * The default, and the honest one: scanning for nearby Bluetooth is scanning
 * for nearby people, and a build that does it because it could is a build that
 * tracks a room.
 */
export class NullBluetoothAnomalyDetector implements BluetoothAnomalyDetector {
  readonly isAvailable = false;
  observe(): void {
    /* nothing is observed */
  }
  persistent(): readonly string[] {
    return [];
  }
}

/** Runs vision models. */
export class NullComputerVisionRuntime {
  readonly isAvailable = false;
  async run(): Promise<never> {
    throw new Error("no computer vision runtime on this device");
  }
}

/**
 * Verifies a document.
 *
 * Returns UNVERIFIED rather than throwing: an identity document that could not
 * be checked is not a forged one, and a system that conflates the two refuses
 * real people.
 */
export class NullDocumentVerifier {
  readonly isAvailable = false;
  async verify(): Promise<{ verified: boolean; reason: string }> {
    return { verified: false, reason: "this device cannot check a document" };
  }
}

/** Finds faces. */
export class NullFaceDetector {
  readonly isAvailable = false;
  async detect(): Promise<readonly { x: number; y: number; width: number; height: number }[]> {
    return [];
  }
}

/** Turns a face into an embedding. */
export class NullFaceEmbedder {
  readonly isAvailable = false;
  async embed(): Promise<readonly number[]> {
    return [];
  }
}

/**
 * Checks a face is a live person.
 *
 * Returns NOT LIVE when unavailable, which is the safe direction: treating an
 * unchecked face as live is what a photograph held up to a camera relies on.
 */
export class NullFaceLivenessDetector {
  readonly isAvailable = false;
  async isLive(): Promise<{ live: boolean; reason: string }> {
    return { live: false, reason: "this device cannot check whether that is a live person" };
  }
}

/** Reads a number plate. */
export class NullPlateRecognizer {
  readonly isAvailable = false;
  async read(): Promise<{ plate: string; confidence: number }> {
    return { plate: "", confidence: 0 };
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Playback across devices

/** Something playable. */
export interface MediaItem {
  readonly itemId: string;
  readonly title: string;
  /** A local path or a URL a peer can reach. Never a cloud identifier: a hub
   * that only works when a service is up is not a hub. */
  readonly source: string;
  readonly durationSeconds: number;
  readonly mediaType: string;
}

/**
 * Where a stream is, and WHEN that was true.
 *
 * The timestamp is the whole point. A position sent between devices is stale
 * the moment it is sent, so a receiver has to extrapolate - and it cannot
 * without knowing how old the reading is.
 */
export interface PlaybackPosition {
  readonly itemId: string;
  readonly positionSeconds: number;
  readonly isPlaying: boolean;
  /** MONOTONIC seconds on the sending device, not wall time: two phones' clocks
   * disagree by seconds, which is an eternity for audio. */
  readonly atMonotonic: number;
  readonly rate: number;
}

/**
 * Where it would be NOW, if it kept playing.
 *
 * Never runs backwards: a clock reporting an earlier reading than the one
 * recorded would otherwise rewind the playhead, which is audible and alarming.
 */
export function extrapolatedPosition(p: PlaybackPosition, nowMonotonic: number): number {
  if (!p.isPlaying) return p.positionSeconds;
  return p.positionSeconds + Math.max(0, nowMonotonic - p.atMonotonic) * p.rate;
}

/** Playback kept in step across devices. */
export interface SyncedPlayback {
  play(item: MediaItem, atSeconds?: number): void;
  pause(): void;
  seek(toSeconds: number): void;
  position(): PlaybackPosition;
}

/** Plays nothing. */
export class NullSyncedPlayback implements SyncedPlayback {
  play(): void {
    /* nothing to play on */
  }
  pause(): void {
    /* nothing to pause */
  }
  seek(): void {
    /* nothing to seek */
  }
  position(): PlaybackPosition {
    return Object.freeze({
      itemId: "", positionSeconds: 0, isPlaying: false, atMonotonic: 0, rate: 1,
    });
  }
}

/** Knows about nothing. */
export class NullMediaLibrary {
  list(): readonly MediaItem[] {
    return [];
  }
  get(): MediaItem | undefined {
    return undefined;
  }
}

/** Keeps a position, for testing and for a device that is only following. */
export class InMemorySyncedPlayback implements SyncedPlayback {
  /** How far out of step before a follower jumps rather than drifts back. Below
   * this, correcting by rate would be more noticeable than the error. */
  static readonly RESYNC_THRESHOLD_SECONDS = 0.35;

  private item?: MediaItem;
  private positionSeconds = 0;
  private playing = false;
  private since = 0;

  constructor(private readonly monotonic: () => number = () => 0) {}

  play(item: MediaItem, atSeconds = 0): void {
    this.item = item;
    this.positionSeconds = atSeconds;
    this.playing = true;
    this.since = this.monotonic();
  }

  /** The elapsed time is FOLDED IN before the flag flips. Setting the flag
   * first loses everything played since the last event, and the playhead jumps
   * backwards on every pause. */
  pause(): void {
    if (this.playing) this.positionSeconds += this.monotonic() - this.since;
    this.playing = false;
  }

  seek(toSeconds: number): void {
    this.positionSeconds = toSeconds;
    this.since = this.monotonic();
  }

  position(): PlaybackPosition {
    return Object.freeze({
      itemId: this.item?.itemId ?? "",
      positionSeconds: this.positionSeconds,
      isPlaying: this.playing,
      atMonotonic: this.since,
      rate: 1,
    });
  }

  shouldResync(remote: PlaybackPosition): boolean {
    const now = this.monotonic();
    const mine = extrapolatedPosition(this.position(), now);
    const theirs = extrapolatedPosition(remote, now);
    return Math.abs(mine - theirs) > InMemorySyncedPlayback.RESYNC_THRESHOLD_SECONDS;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Skills

/** One skill as it was written. */
export interface ParsedSkill {
  readonly skillId: string;
  readonly name: string;
  readonly description: string;
  readonly instructions: string;
  /** What the device must have. A skill listed on a device that cannot run it
   * teaches people the assistant is unreliable. */
  readonly requires: readonly string[];
}

/** What a pack says about itself. */
export interface SkillPackManifest {
  readonly packId: string;
  readonly version: string;
  readonly skills: readonly ParsedSkill[];
  readonly author: string;
  readonly sha256: string;
}

/**
 * Reads a pack.
 *
 * A MALFORMED SKILL IS SKIPPED, not fatal. A pack of twenty with one bad entry
 * should install nineteen and say which one it dropped - refusing all twenty
 * for one typo is how a pack becomes uninstallable for its author's mistake.
 */
export class SkillPackLoader {
  static parse(json: string): { manifest?: SkillPackManifest; problems: string[] } {
    let raw: Object;
    try {
      raw = JSON.parse(json);
    } catch {
      return { problems: ["the pack manifest is not readable as JSON"] };
    }
    if (!raw || typeof raw !== "object") return { problems: ["the pack manifest is not an object"] };
    const record = raw as Map<string, unknown>;
    const problems: string[] = [];
    const skills: ParsedSkill[] = [];

    for (const entry of (record.skills as Map<string, unknown>[] | undefined) ?? []) {
      const skillId = String(entry?.id ?? "").trim();
      const name = String(entry?.name ?? "").trim();
      if (!skillId || !name) {
        problems.push(`a skill with no ${skillId ? "name" : "id"} was skipped`);
        continue;
      }
      skills.push(
        Object.freeze({
          skillId,
          name,
          description: String(entry.description ?? ""),
          instructions: String(entry.instructions ?? ""),
          requires: Object.freeze(((entry.requires as string[] | undefined) ?? []).map(String)),
        }),
      );
    }
    const packId = String(record.id ?? "").trim();
    if (!packId) problems.push("the pack has no id");
    if (skills.length === 0) problems.push("the pack has no usable skills");

    return {
      manifest: packId
        ? Object.freeze({
            packId,
            version: String(record.version ?? ""),
            skills: Object.freeze(skills),
            author: String(record.author ?? ""),
            sha256: String(record.sha256 ?? ""),
          })
        : undefined,
      problems,
    };
  }
}

/** Skills on disk. */
export class FileSkillStore {
  private readonly skills = new Map<string, ParsedSkill>();

  constructor(
    private readonly readDirectory?: () => readonly string[],
    private readonly readFile?: (path: string) => string,
  ) {}

  load(): { loaded: number; problems: string[] } {
    if (!this.readDirectory || !this.readFile) {
      return { loaded: 0, problems: ["no filesystem access"] };
    }
    const problems: string[] = [];
    let loaded = 0;
    for (const path of this.readDirectory()) {
      if (!path.endsWith(".json")) continue;
      let text: string;
      try {
        text = this.readFile(path);
      } catch (error) {
        // A file that will not read is REPORTED and skipped. One unreadable
        // file must not stop the rest loading.
        problems.push(`${path}: ${error instanceof Error ? error.message : String(error)}`);
        continue;
      }
      const parsed = SkillPackLoader.parse(text);
      problems.push(...parsed.problems.map((p) => `${path}: ${p}`));
      for (const skill of parsed.manifest?.skills ?? []) {
        this.skills.set(skill.skillId, skill);
        loaded += 1;
      }
    }
    return { loaded, problems };
  }

  get(skillId: string): ParsedSkill | undefined {
    return this.skills.get(skillId);
  }

  all(): readonly ParsedSkill[] {
    return Object.freeze([...this.skills.values()]);
  }
}

/**
 * Skills, listed with what each one needs.
 *
 * A SKILL IS HIDDEN WHEN THE DEVICE CANNOT RUN IT, and the reason is kept so
 * somebody can be told WHY rather than left to wonder whether it exists.
 */
export class CapabilityManifestSkillStore {
  private readonly skills = new Map<string, ParsedSkill>();

  constructor(private readonly can: (capability: string) => boolean = () => false) {}

  register(skill: ParsedSkill): void {
    if (!skill.skillId.trim()) throw new Error("a skill needs an identifier");
    this.skills.set(skill.skillId, skill);
  }

  available(): readonly ParsedSkill[] {
    return Object.freeze(
      [...this.skills.values()].filter((s) => s.requires.every((r) => this.can(r))),
    );
  }

  unavailable(): readonly { skill: ParsedSkill; missing: readonly string[] }[] {
    return Object.freeze(
      [...this.skills.values()]
        .map((skill) => ({ skill, missing: skill.requires.filter((r) => !this.can(r)) }))
        .filter((e) => e.missing.length > 0),
    );
  }
}

/**
 * Fetches a skill pack.
 *
 * THE SAME RULE AS A MODEL AND A NATIVE RUNTIME: verified before it is
 * unpacked, and unpacked into a contained directory.
 */
export class HttpPackDownloader {
  /** Above this a pack is refused outright. A pack that needs a hundred
   * megabytes is not a skill. */
  static readonly MAX_BYTES = 32 * 1024 * 1024;

  constructor(
    private readonly fetch?: (url: string) => Promise<Uint8Array>,
    private readonly digestOf?: (bytes: Uint8Array) => string,
  ) {}

  /**
   * An archive entry that stays inside the directory it is unpacked to.
   *
   * Checked on the SEPARATOR-NORMALISED name, because a zip written on Windows
   * carries backslashes and a check that only looks for `/` reads `..\..\etc`
   * as a single ordinary filename.
   */
  static isSafeEntry(name: string): boolean {
    const normalised = (name ?? "").replace(/\\/g, "/");
    if (!normalised || normalised.startsWith("/")) return false;
    if (/^[A-Za-z]:/.test(normalised)) return false;
    return !normalised.split("/").includes("..");
  }

  async download(url: string, expectedSha256: string): Promise<{ bytes: Uint8Array; error: string }> {
    if (!this.fetch) return { bytes: new Uint8Array(0), error: "this device cannot download a pack" };
    if (!expectedSha256) {
      return { bytes: new Uint8Array(0), error: "a pack will not be installed without a checksum" };
    }
    let data: Uint8Array;
    try {
      data = await this.fetch(url);
    } catch (error) {
      return {
        bytes: new Uint8Array(0),
        error: `the pack did not download: ${error instanceof Error ? error.message : String(error)}`,
      };
    }
    if (data.length > HttpPackDownloader.MAX_BYTES) {
      return {
        bytes: new Uint8Array(0),
        error: `that pack is ${Math.round(data.length / (1024 * 1024))} MB, which is more than a skill should be`,
      };
    }
    if (this.digestOf && this.digestOf(data).toLowerCase() !== expectedSha256.trim().toLowerCase()) {
      return { bytes: new Uint8Array(0), error: "the pack does not match its checksum" };
    }
    return { bytes: data, error: "" };
  }
}

/**
 * Brings in packs a host has listed.
 *
 * ONLY FROM SOURCES SOMEBODY CONFIGURED. An auto-importer that discovers
 * sources is an auto-importer that installs whatever it finds.
 */
export class SkillPackAutoImporter {
  constructor(
    private readonly sources: readonly { url: string; sha256: string }[] = [],
    private readonly downloader = new HttpPackDownloader(),
    private readonly store = new CapabilityManifestSkillStore(),
  ) {}

  async importAll(): Promise<{ imported: number; problems: string[] }> {
    const problems: string[] = [];
    let imported = 0;
    for (const source of this.sources) {
      const result = await this.downloader.download(source.url, source.sha256);
      if (result.error) {
        problems.push(`${source.url}: ${result.error}`);
        continue;
      }
      const parsed = SkillPackLoader.parse(new TextDecoder().decode(result.bytes));
      problems.push(...parsed.problems.map((p) => `${source.url}: ${p}`));
      for (const skill of parsed.manifest?.skills ?? []) {
        this.store.register(skill);
        imported += 1;
      }
    }
    return { imported, problems };
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Carriers

/** What a carrier needs. */
export interface CarrierOptions {
  readonly enabled: boolean;
  readonly accountId: string;
  readonly fromNumberE164: string;
  readonly baseUrl: string;
  readonly apiKey: { reveal(): string; isSet: boolean };
}

const carrierOptions = (baseUrl: string, partial: Partial<CarrierOptions>): CarrierOptions =>
  Object.freeze({
    enabled: partial.enabled ?? false,
    accountId: partial.accountId ?? "",
    fromNumberE164: partial.fromNumberE164 ?? "",
    baseUrl: partial.baseUrl ?? baseUrl,
    apiKey: partial.apiKey ?? { reveal: () => "", isSet: false },
  });

export type TwilioOptions = CarrierOptions;
export const twilioOptions = (p: Partial<CarrierOptions> = {}): TwilioOptions =>
  carrierOptions("https://api.twilio.com/2010-04-01", p);

export type TelnyxOptions = CarrierOptions;
export const telnyxOptions = (p: Partial<CarrierOptions> = {}): TelnyxOptions =>
  carrierOptions("https://api.telnyx.com/v2", p);

export type PlivoOptions = CarrierOptions;
export const plivoOptions = (p: Partial<CarrierOptions> = {}): PlivoOptions =>
  carrierOptions("https://api.plivo.com/v1", p);

/** One live call. */
export interface CarrierCallSession {
  readonly callId: string;
  readonly isActive: boolean;
  hangUp(): Promise<boolean>;
  sendDtmf(digits: string): Promise<boolean>;
}

abstract class CarrierCallSessionBase implements CarrierCallSession {
  private active = true;

  constructor(
    readonly callId: string,
    protected readonly options: CarrierOptions,
    protected readonly post?: (
      path: string,
      headers: Map<string, string>,
      body: Map<string, unknown>,
    ) => Promise<Map<string, unknown>>,
  ) {}

  get isActive(): boolean {
    return this.active;
  }

  protected abstract hangUpPath(): string;
  protected abstract dtmfPath(): string;
  protected abstract headers(): Map<string, string>;

  /**
   * IDEMPOTENT. A call is hung up by the person, by the far end and by an error
   * path, and often by two of them within a second.
   */
  async hangUp(): Promise<boolean> {
    if (!this.active) return true;
    this.active = false;
    if (!this.post) return false;
    try {
      await this.post(this.hangUpPath(), this.headers(), { status: "completed" });
      return true;
    } catch {
      // The local state is already inactive. A failed hang-up on the carrier is
      // worth reporting and must not leave this device thinking it is still on
      // a call.
      return false;
    }
  }

  async sendDtmf(digits: string): Promise<boolean> {
    if (!this.active || !this.post) return false;
    // Only keypad characters go out. A stray letter is silently dropped by some
    // carriers and rejected by others, so it is filtered here rather than
    // producing a different result per carrier.
    const clean = [...digits].filter((c) => "0123456789*#ABCDw".includes(c.toUpperCase())).join("");
    if (!clean) return false;
    try {
      await this.post(this.dtmfPath(), this.headers(), { digits: clean });
      return true;
    } catch {
      return false;
    }
  }
}

/** A Twilio call. */
export class TwilioCallSession extends CarrierCallSessionBase {
  protected hangUpPath(): string {
    return `${this.options.baseUrl}/Accounts/${this.options.accountId}/Calls/${this.callId}.json`;
  }
  protected dtmfPath(): string {
    return `${this.hangUpPath()}`;
  }
  protected headers(): Map<string, string> {
    // Twilio uses BASIC auth with the account SID as the username. A bearer
    // token gets a 401 that reads exactly like a bad key.
    const basic = `${this.options.accountId}:${this.options.apiKey.reveal()}`;
    return { Authorization: `Basic ${basic}`, "Content-Type": "application/x-www-form-urlencoded" };
  }
}

/** A Telnyx call. */
export class TelnyxCallSession extends CarrierCallSessionBase {
  protected hangUpPath(): string {
    return `${this.options.baseUrl}/calls/${this.callId}/actions/hangup`;
  }
  protected dtmfPath(): string {
    return `${this.options.baseUrl}/calls/${this.callId}/actions/send_dtmf`;
  }
  protected headers(): Map<string, string> {
    return {
      Authorization: `Bearer ${this.options.apiKey.reveal()}`,
      "Content-Type": "application/json",
    };
  }
}

/** A Plivo call. */
export class PlivoCallSession extends CarrierCallSessionBase {
  protected hangUpPath(): string {
    return `${this.options.baseUrl}/Account/${this.options.accountId}/Call/${this.callId}/`;
  }
  protected dtmfPath(): string {
    return `${this.hangUpPath()}DTMF/`;
  }
  protected headers(): Map<string, string> {
    const basic = `${this.options.accountId}:${this.options.apiKey.reveal()}`;
    return { Authorization: `Basic ${basic}`, "Content-Type": "application/json" };
  }
}

/** Places calls through a carrier. */
abstract class CarrierBase {
  constructor(
    readonly carrierId: string,
    protected readonly options: CarrierOptions,
    protected readonly post?: (
      path: string,
      headers: Map<string, string>,
      body: Map<string, unknown>,
    ) => Promise<Map<string, unknown>>,
  ) {}

  /** Needs a FROM NUMBER as well as a key: a carrier will not place a call
   * without one, and finding that out at dial time wastes the dial. */
  get isAvailable(): boolean {
    return (
      this.options.enabled &&
      this.options.apiKey.isSet &&
      this.options.accountId.length > 0 &&
      this.options.fromNumberE164.length > 0 &&
      this.post !== undefined
    );
  }

  /** E.164 only. A national-format number reaches a different person in a
   * different country, and a carrier will happily dial it. */
  static isE164(number: string): boolean {
    return /^\+[1-9]\d{6,14}$/.test(number);
  }
}

/** Twilio. */
export class TwilioCarrier extends CarrierBase {
  constructor(options: TwilioOptions = twilioOptions(), post?: CarrierBase["post"]) {
    super("twilio", options, post);
  }
}

/** Telnyx. */
export class TelnyxCarrier extends CarrierBase {
  constructor(options: TelnyxOptions = telnyxOptions(), post?: CarrierBase["post"]) {
    super("telnyx", options, post);
  }
}

/** Plivo. */
export class PlivoCarrier extends CarrierBase {
  constructor(options: PlivoOptions = plivoOptions(), post?: CarrierBase["post"]) {
    super("plivo", options, post);
  }
}

/** A public address for a device behind a NAT, during development. */
export interface LocalDevTunnel {
  readonly isAvailable: boolean;
  readonly publicUrl: string;
  open(localPort: number): Promise<string>;
  close(): Promise<void>;
}

/**
 * No tunnel.
 *
 * THE DEFAULT, and the right one outside development: a tunnel puts a
 * development machine on the public internet, and that should never happen
 * because a default was left alone.
 */
export class NullLocalDevTunnel implements LocalDevTunnel {
  readonly isAvailable = false;
  readonly publicUrl = "";
  async open(): Promise<string> {
    return "";
  }
  async close(): Promise<void> {
    /* nothing to close */
  }
}

/** A URL somebody already has. */
export class StaticLocalDevTunnel implements LocalDevTunnel {
  constructor(readonly publicUrl = "") {}
  get isAvailable(): boolean {
    return this.publicUrl.length > 0;
  }
  async open(): Promise<string> {
    return this.publicUrl;
  }
  async close(): Promise<void> {
    /* a static URL is not ours to close */
  }
}

abstract class ProcessTunnelBase implements LocalDevTunnel {
  protected url = "";

  constructor(
    protected readonly run?: (port: number) => Promise<string>,
    protected readonly stop?: () => Promise<void>,
  ) {}

  get isAvailable(): boolean {
    return this.run !== undefined;
  }

  get publicUrl(): string {
    return this.url;
  }

  async open(localPort: number): Promise<string> {
    if (!this.run) return "";
    this.url = await this.run(localPort);
    return this.url;
  }

  async close(): Promise<void> {
    this.url = "";
    await this.stop?.();
  }
}

/** ngrok. */
export class NgrokTunnel extends ProcessTunnelBase {}

/** Cloudflare. */
export class CloudflareTunnel extends ProcessTunnelBase {}

/**
 * A settled transcript, second version.
 *
 * The `_v2` suffix is the C#'s and is kept: it carries a WORD-LEVEL breakdown
 * the first version did not, and renaming it here would hide that two shapes
 * exist on the wire.
 */
export interface TranscriptFinalEvent_v2 {
  readonly sessionId: string;
  readonly text: string;
  /** Undefined when the engine did not say. Zero is a real answer meaning "no
   * idea". */
  readonly confidence: number | undefined;
  readonly words: readonly { word: string; startMs: number; endMs: number }[];
  readonly atMs: number;
}

// ─────────────────────────────────────────────────────────────────────────────
// The last of the seams

/** Somebody a child may be handed to. */
export interface TrustedAdult {
  readonly adultId: string;
  readonly displayName: string;
  readonly phoneE164: string;
  /** Whether the CHILD confirmed this person, not only the parent. A list only
   * a parent can edit is a list a child cannot correct. */
  readonly confirmedByChild: boolean;
}

/**
 * An area a child is expected to be in.
 *
 * A RADIUS, not a shape, because a phone's location is accurate to tens of
 * metres and a precise polygon implies a precision that is not there.
 */
export interface Geofence {
  readonly name: string;
  readonly latitude: number;
  readonly longitude: number;
  readonly radiusMetres: number;
  /** Whether leaving it notifies. Entering usually should not - a child
   * arriving at school does not need an alert. */
  readonly notifyOnExit: boolean;
}

/** What a child-safety setup holds. */
export interface ChildSafetyBoard {
  addAdult(adult: TrustedAdult): { added: boolean; reason: string };
  adults(): readonly TrustedAdult[];
  addFence(fence: Geofence): void;
  fences(): readonly Geofence[];
  isInside(fence: Geofence, latitude: number, longitude: number): boolean;
}

/**
 * The default board.
 *
 * A CHILD BEING LOCATED KNOWS THEY ARE. Nothing here is silent, and an adult is
 * not trusted until the child has confirmed them - a list only a parent can
 * edit is a list a child cannot correct.
 */
export class InMemoryChildSafetyBoard implements ChildSafetyBoard {
  private readonly trusted = new Map<string, TrustedAdult>();
  private readonly geofences: Geofence[] = [];

  addAdult(adult: TrustedAdult): { added: boolean; reason: string } {
    if (!adult.confirmedByChild) {
      return {
        added: false,
        reason: `${adult.displayName} has not been confirmed by the child yet`,
      };
    }
    this.trusted.set(adult.adultId, adult);
    return { added: true, reason: "" };
  }

  adults(): readonly TrustedAdult[] {
    return Object.freeze([...this.trusted.values()]);
  }

  addFence(fence: Geofence): void {
    if (fence.radiusMetres <= 0) throw new Error("a geofence needs a positive radius");
    this.geofences.push(fence);
  }

  fences(): readonly Geofence[] {
    return Object.freeze([...this.geofences]);
  }

  /**
   * Great-circle distance, not a flat approximation.
   *
   * A flat one is wrong by kilometres at South African latitudes over the
   * distances a geofence covers, and the error is in the direction that reports
   * a child outside a fence they are inside.
   */
  isInside(fence: Geofence, latitude: number, longitude: number): boolean {
    const R = 6_371_000;
    const toRad = (d: number) => (d * Math.PI) / 180;
    const dLat = toRad(latitude - fence.latitude);
    const dLon = toRad(longitude - fence.longitude);
    const a =
      Math.sin(dLat / 2) ** 2 +
      Math.cos(toRad(fence.latitude)) * Math.cos(toRad(latitude)) * Math.sin(dLon / 2) ** 2;
    return 2 * R * Math.asin(Math.min(1, Math.sqrt(a))) <= fence.radiusMetres;
  }
}

/** Normalises a script so text can be compared and matched. */
export interface ScriptNormaliser {
  normalise(text: string): string;
}

/**
 * The languages this build knows about.
 *
 * SOUTH AFRICA'S ELEVEN FIRST, because they are the ones this is for and a
 * registry that starts with English by convention is a registry that treats the
 * rest as an afterthought.
 */
export class DefaultLanguageRegistry {
  private static readonly languages: Readonly<Map<string, string>> = Object.freeze({
    zu: "isiZulu", xh: "isiXhosa", af: "Afrikaans", nso: "Sepedi", tn: "Setswana",
    st: "Sesotho", ts: "Xitsonga", ss: "siSwati", ve: "Tshivenda", nr: "isiNdebele",
    en: "English",
    sw: "Kiswahili", am: "Amharic", yo: "Yoruba", ig: "Igbo", ha: "Hausa",
  });

  static nameOf(tag: string): string {
    return DefaultLanguageRegistry.languages[tag.split(/[-_]/)[0].toLowerCase()] ?? "";
  }

  static knows(tag: string): boolean {
    return DefaultLanguageRegistry.nameOf(tag).length > 0;
  }

  static all(): readonly { tag: string; name: string }[] {
    return Object.freeze(
      Object.entries(DefaultLanguageRegistry.languages).map(([tag, name]) => ({ tag, name })),
    );
  }
}

/**
 * Detects nothing.
 *
 * Returns an EMPTY tag rather than guessing English. Defaulting to English is
 * how a device that cannot detect a language ends up answering everybody in it.
 */
export class NullLanguageDetector {
  readonly isAvailable = false;
  detect(): { tag: string; confidence: number } {
    return { tag: "", confidence: 0 };
  }
}

/**
 * Converts between PCM formats.
 *
 * THE RATE CONVERSION IS LINEAR AND SAYS SO. It is good enough to feed a wake
 * detector and not good enough for a transcriber, because downsampling without
 * a low-pass folds everything above the new Nyquist back into the band.
 */
export class AudioFormatConverter {
  /** 16-bit little-endian to floats in -1..1. Divided by 32768, which is the
   * negative rail - so -32768 maps to exactly -1.0 and nothing overflows. */
  static toFloat(pcm: Uint8Array): number[] {
    const view = new DataView(pcm.buffer, pcm.byteOffset, pcm.byteLength);
    const count = Math.floor(pcm.length / 2);
    const out = new Array<number>(count);
    for (let i = 0; i < count; i++) out[i] = view.getInt16(i * 2, true) / 32768;
    return out;
  }

  /** Floats to 16-bit, clamped. Scaled by 32767 so +1.0 is representable. */
  static toPcm16(samples: readonly number[]): Uint8Array {
    const out = new Uint8Array(samples.length * 2);
    const view = new DataView(out.buffer);
    for (let i = 0; i < samples.length; i++) {
      const s = samples[i] < -1 ? -1 : samples[i] > 1 ? 1 : samples[i];
      view.setInt16(i * 2, Math.round(s * 32767), true);
    }
    return out;
  }

  /** AVERAGED, not left-channel-only. Taking one channel loses anything panned
   * away from it, and a phone's two microphones are the same voice with
   * different noise rather than a stereo image. */
  static toMono(samples: readonly number[], channels: number): number[] {
    if (channels <= 1) return [...samples];
    const out: number[] = [];
    for (let i = 0; i + channels <= samples.length; i += channels) {
      let sum = 0;
      for (let c = 0; c < channels; c++) sum += samples[i + c];
      out.push(sum / channels);
    }
    return out;
  }

  static resampleLinear(samples: readonly number[], fromHz: number, toHz: number): number[] {
    if (fromHz === toHz || samples.length === 0) return [...samples];
    const ratio = fromHz / toHz;
    const count = Math.max(1, Math.floor(samples.length / ratio));
    const out = new Array<number>(count);
    for (let i = 0; i < count; i++) {
      const position = i * ratio;
      const left = Math.floor(position);
      const frac = position - left;
      out[i] = samples[left] * (1 - frac) + samples[Math.min(left + 1, samples.length - 1)] * frac;
    }
    return out;
  }
}

/**
 * Voice activity from energy alone.
 *
 * CHEAP AND EASILY FOOLED - by a fan, a road, a television. It is the right
 * detector for gating a wake spotter and the wrong one for deciding whether
 * somebody spoke, and the name says which it is.
 */
export class EnergyVoiceActivityDetector {
  /** Adapted to the room rather than fixed: a fixed threshold is either deaf in
   * a kitchen or permanently triggered on a street. */
  private noiseFloor = 0.01;

  constructor(
    private readonly marginDb = 6,
    private readonly adaptRate = 0.05,
  ) {}

  static rms(frame: readonly number[]): number {
    if (frame.length === 0) return 0;
    let sum = 0;
    for (const s of frame) sum += s * s;
    return Math.sqrt(sum / frame.length);
  }

  isSpeech(frame: readonly number[]): boolean {
    const level = EnergyVoiceActivityDetector.rms(frame);
    const threshold = this.noiseFloor * 10 ** (this.marginDb / 20);
    const speech = level > threshold;
    // The floor adapts DOWNWARD quickly and upward slowly, so a moment of
    // silence lowers it at once while a burst of noise does not raise it enough
    // to go deaf.
    if (!speech) {
      this.noiseFloor = this.noiseFloor * (1 - this.adaptRate) + level * this.adaptRate;
    }
    return speech;
  }

  get currentNoiseFloor(): number {
    return this.noiseFloor;
  }
}

/**
 * Voice activity from a model.
 *
 * Far better than energy and needs a model file. Reports unavailable without
 * one rather than falling back silently - a caller that thinks it has a neural
 * detector and has an energy one will trust it too far.
 */
export class SileroVoiceActivityDetector {
  constructor(
    private readonly run?: (frame: readonly number[]) => number,
    private readonly threshold = 0.5,
  ) {}

  get isAvailable(): boolean {
    return this.run !== undefined;
  }

  isSpeech(frame: readonly number[]): boolean {
    return this.run ? this.run(frame) >= this.threshold : false;
  }
}

/** Marks this package present. */
export class RealtimePackageMarker {
  static readonly NAME = "circleai.realtime";
  /** A marker rather than a flag somebody sets: a build either has this module
   * or it does not, and asking the module is the only answer that cannot drift
   * from the truth. */
  static isPresent(): boolean {
    return true;
  }
}

/** Wires the realtime cloud providers a host has consented to. */
export class RealtimeCloudServiceCollectionExtensions {
  static addRealtimeCloud<T extends { providerId: string; isAvailable: boolean }>(
    candidates: readonly T[],
    consented: readonly string[],
  ): readonly T[] {
    const allowed = new Set(consented.map((c) => c.trim().toLowerCase()).filter(Boolean));
    return Object.freeze(
      candidates.filter((c) => allowed.has(c.providerId.toLowerCase()) && c.isAvailable),
    );
  }
}

/**
 * Fetches the native runtime for this device's ABI.
 *
 * THE DIGEST IS CHECKED BEFORE THE FILE IS PUT WHERE IT WILL BE LOADED. A
 * native library is code that will run in this process - a partial download or
 * a substituted file is not a corrupt asset, it is arbitrary code. A downloaded
 * file lands in a TEMPORARY name and moves into place only after it verifies.
 */
export class NativeRuntimeFetcher {
  constructor(
    private readonly download?: (url: string, path: string) => Promise<number>,
    private readonly digestOf?: (path: string) => string,
    private readonly move?: (from: string, to: string) => void,
    private readonly remove?: (path: string) => void,
  ) {}

  async fetch(
    url: string,
    targetPath: string,
    expectedSha256: string,
  ): Promise<{ installed: boolean; reason: string }> {
    if (!expectedSha256) {
      return { installed: false, reason: "a native library will not be installed without a checksum" };
    }
    if (!this.download || !this.digestOf || !this.move) {
      return { installed: false, reason: "this device cannot fetch a native runtime" };
    }
    const temporary = `${targetPath}.partial`;
    try {
      await this.download(url, temporary);
    } catch (error) {
      return {
        installed: false,
        reason: `the download did not finish: ${error instanceof Error ? error.message : String(error)}`,
      };
    }
    if (this.digestOf(temporary).trim().toLowerCase() !== expectedSha256.trim().toLowerCase()) {
      this.remove?.(temporary);
      return { installed: false, reason: "the downloaded runtime does not match its checksum" };
    }
    this.move(temporary, targetPath);
    return { installed: true, reason: "installed" };
  }
}

// The C# spellings, kept so the two trees line up.
export type IStyleReference = StyleReferenceStore;
export type IStyleScript = StyleScript;
export type IVideoGenerator = VideoGenerator;
export type IVideoCapture = VideoCapture;
export type IBluetoothAnomalyDetector = BluetoothAnomalyDetector;
export type ISyncedPlayback = SyncedPlayback;
export type ILocalDevTunnel = LocalDevTunnel;
export type IChildSafetyBoard = ChildSafetyBoard;
export type IScriptNormaliser = ScriptNormaliser;
