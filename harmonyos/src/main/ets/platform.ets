/**
 * The HarmonyOS platform layer.
 *
 * THE PORTED CORE ASKS NODE FOR FOUR THINGS - a filesystem, paths, a little
 * about the machine, and cryptography. None of those exist on HarmonyOS under
 * those names, so this module provides them over `@ohos.*` and the port imports
 * from here instead. Every function keeps the Node signature the core already
 * calls, so the core did not have to be rewritten around a different shape - it
 * only had to stop importing `node:`.
 *
 * EVERY `@ohos` IMPORT IS BEHIND A SEAM. The core is plain ArkTS that can be
 * type-checked and run by an ordinary TypeScript toolchain; only this file
 * touches the device, and it is written so that a build without the device APIs
 * present still compiles and reports honestly that it cannot do the thing.
 *
 * WHAT IS NOT HERE, DELIBERATELY: there is no `process`, no environment
 * variables and no current working directory. HarmonyOS applications do not have
 * any of the three - an app has a sandbox path handed to it by its context, and
 * code that reaches for `process.env` on this platform is code that will read
 * `undefined` and carry on with a wrong default.
 */

/** What this build can actually do. Each is checked once, at load. */
export interface PlatformCapabilities {
  /** Whether the sandbox filesystem is reachable. */
  readonly files: boolean;
  /** Whether the system's own digest and signing are reachable. */
  readonly crypto: boolean;
  /** Whether the device can be asked about itself. */
  readonly deviceInfo: boolean;
}

/**
 * The seams the host fills in.
 *
 * Held as a settable object rather than resolved by importing `@ohos.*` at the
 * top of this file, because an import of a device module fails outright in a
 * plain TypeScript test run - and a core that cannot be tested off the device is
 * a core nobody tests.
 */
export interface PlatformBindings {
  readFile?: (path: string) => Promise<string>;
  writeFile?: (path: string, content: string) => Promise<void>;
  deleteFile?: (path: string) => Promise<void>;
  listDirectory?: (path: string) => Promise<string[]>;
  makeDirectory?: (path: string) => Promise<void>;
  fileExists?: (path: string) => Promise<boolean>;
  /**
   * The app's own sandbox directory. THE ONLY PLACE an app may write, and it is
   * handed over by the ability's context - it cannot be guessed or built from a
   * home directory, because there is no home directory.
   */
  sandboxDirectory?: () => string;
  /** SHA-256 of a string, as lower-case hex. */
  sha256?: (text: string) => string;
  /** Cryptographically secure random bytes. */
  randomBytes?: (count: number) => Uint8Array;
  /** Total and available physical memory, in bytes. */
  memoryBytes?: () => [number, number];
  /** How many cores. */
  cpuCount?: () => number;
}

const bindings: PlatformBindings = {};

/**
 * Wires the platform in. Called once by the ability that owns the context.
 *
 * Takes the whole set rather than one at a time so a partially wired platform is
 * visible in one place: a build with a filesystem and no crypto is a build that
 * will fail at its first signature, and this is where that is knowable.
 */
export function bindPlatform(next: PlatformBindings): void {
  bindings.readFile = next.readFile;
  bindings.writeFile = next.writeFile;
  bindings.deleteFile = next.deleteFile;
  bindings.listDirectory = next.listDirectory;
  bindings.makeDirectory = next.makeDirectory;
  bindings.fileExists = next.fileExists;
  bindings.sandboxDirectory = next.sandboxDirectory;
  bindings.sha256 = next.sha256;
  bindings.randomBytes = next.randomBytes;
  bindings.memoryBytes = next.memoryBytes;
  bindings.cpuCount = next.cpuCount;
}

export function platformCapabilities(): PlatformCapabilities {
  return {
    files: bindings.readFile !== undefined && bindings.writeFile !== undefined,
    crypto: bindings.sha256 !== undefined && bindings.randomBytes !== undefined,
    deviceInfo: bindings.memoryBytes !== undefined,
  };
}

// ─────────────────────────────────────────────────────────────────────────────
// Paths

/**
 * Path handling, POSIX-only.
 *
 * HarmonyOS sandbox paths are always forward-slashed, so there is no separator
 * to detect and no drive letter to parse. Carrying the Windows cases would be
 * carrying code that can never run, and the one place it would matter - a path
 * arriving from somewhere else - is handled by `normalize` rather than by
 * pretending to support two conventions.
 */
export class Path {
  static readonly separator: string = '/';

  static join(...parts: string[]): string {
    const kept: string[] = [];
    for (const part of parts) {
      if (part.length === 0) {
        continue;
      }
      kept.push(part);
    }
    if (kept.length === 0) {
      return '.';
    }
    return Path.normalize(kept.join('/'));
  }

  /**
   * Resolves `.` and `..` and collapses repeated separators.
   *
   * A LEADING `..` IS DROPPED when the path is absolute. Keeping it would let a
   * path climb out of the sandbox, and the sandbox is the only thing standing
   * between one app's files and another's.
   */
  static normalize(input: string): string {
    const absolute = input.startsWith('/');
    const out: string[] = [];
    for (const segment of input.split('/')) {
      if (segment.length === 0 || segment === '.') {
        continue;
      }
      if (segment === '..') {
        if (out.length > 0 && out[out.length - 1] !== '..') {
          out.pop();
        } else if (!absolute) {
          out.push('..');
        }
        continue;
      }
      out.push(segment);
    }
    const joined = out.join('/');
    if (absolute) {
      return '/' + joined;
    }
    return joined.length === 0 ? '.' : joined;
  }

  static dirname(input: string): string {
    const normalized = Path.normalize(input);
    const cut = normalized.lastIndexOf('/');
    if (cut < 0) {
      return '.';
    }
    if (cut === 0) {
      return '/';
    }
    return normalized.substring(0, cut);
  }

  static basename(input: string): string {
    const normalized = Path.normalize(input);
    const cut = normalized.lastIndexOf('/');
    return cut < 0 ? normalized : normalized.substring(cut + 1);
  }

  /**
   * The extension, with its dot, or an empty string.
   *
   * A LEADING DOT IS NOT AN EXTENSION: `.gitignore` has none, and treating it as
   * one makes every dotfile look like a file of type `gitignore`.
   */
  static extname(input: string): string {
    const name = Path.basename(input);
    const cut = name.lastIndexOf('.');
    return cut <= 0 ? '' : name.substring(cut);
  }

  static isAbsolute(input: string): boolean {
    return input.startsWith('/');
  }

  /**
   * Whether `child` stays inside `root`.
   *
   * Compared after NORMALISING both, not by checking whether the text starts
   * with the root: `/data/app-x` starts with `/data/app` and is a different
   * directory, which is exactly the check a naive prefix test gets wrong.
   */
  static contains(root: string, child: string): boolean {
    const r = Path.normalize(root);
    const c = Path.normalize(child);
    return c === r || c.startsWith(r.endsWith('/') ? r : r + '/');
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Files

/**
 * The sandbox filesystem.
 *
 * EVERY PATH IS CHECKED against the sandbox root before anything happens. A path
 * arriving from a catalogue, a manifest or a model id is input, and input that
 * joins cleanly can still climb out with `..`.
 */
export class Files {
  static root(): string {
    const directory = bindings.sandboxDirectory;
    return directory === undefined ? '' : directory();
  }

  static isAvailable(): boolean {
    return bindings.readFile !== undefined && Files.root().length > 0;
  }

  /** `null` when the path escapes the sandbox, so a caller cannot ignore it. */
  static contained(relative: string): string | null {
    const root = Files.root();
    if (root.length === 0) {
      return null;
    }
    if (Path.isAbsolute(relative)) {
      return Path.contains(root, relative) ? Path.normalize(relative) : null;
    }
    const joined = Path.join(root, relative);
    return Path.contains(root, joined) ? joined : null;
  }

  static async read(relative: string): Promise<string> {
    const path = Files.contained(relative);
    if (path === null) {
      throw new Error('that path is outside this app’s own storage');
    }
    const read = bindings.readFile;
    if (read === undefined) {
      throw new Error('this build has no filesystem');
    }
    return read(path);
  }

  static async write(relative: string, content: string): Promise<void> {
    const path = Files.contained(relative);
    if (path === null) {
      throw new Error('that path is outside this app’s own storage');
    }
    const write = bindings.writeFile;
    if (write === undefined) {
      throw new Error('this build has no filesystem');
    }
    // The parent is created first. Writing into a directory that does not exist
    // fails with an error naming the file rather than the directory, which sends
    // somebody looking in the wrong place.
    const makeDirectory = bindings.makeDirectory;
    if (makeDirectory !== undefined) {
      await makeDirectory(Path.dirname(path));
    }
    await write(path, content);
  }

  static async exists(relative: string): Promise<boolean> {
    const path = Files.contained(relative);
    const check = bindings.fileExists;
    if (path === null || check === undefined) {
      return false;
    }
    return check(path);
  }

  static async list(relative: string): Promise<string[]> {
    const path = Files.contained(relative);
    const list = bindings.listDirectory;
    if (path === null || list === undefined) {
      return [];
    }
    return list(path);
  }

  static async remove(relative: string): Promise<void> {
    const path = Files.contained(relative);
    if (path === null) {
      throw new Error('that path is outside this app’s own storage');
    }
    const remove = bindings.deleteFile;
    if (remove === undefined) {
      throw new Error('this build has no filesystem');
    }
    await remove(path);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Cryptography

/**
 * The cryptographic primitives the core asks for.
 *
 * NOTHING HERE IMPLEMENTS A PRIMITIVE. Digests and randomness come from
 * `@ohos.security.cryptoFramework` through the binding; a hand-written SHA-256
 * in ArkTS would be slower, unaudited, and would not use the hardware the device
 * already has. What this class adds is the shape the core expects and the
 * refusals that belong at this level.
 */
export class Crypto {
  static isAvailable(): boolean {
    return bindings.sha256 !== undefined && bindings.randomBytes !== undefined;
  }

  static sha256Hex(text: string): string {
    const digest = bindings.sha256;
    if (digest === undefined) {
      throw new Error('this build has no digest available');
    }
    return digest(text).toLowerCase();
  }

  /**
   * Random bytes for a key, a nonce or an identifier.
   *
   * THROWS rather than falling back to `Math.random()`. A weak random here is
   * invisible - the bytes look random - and it produces identifiers that collide
   * and nonces that repeat, which is how a ratchet breaks.
   */
  static randomBytes(count: number): Uint8Array {
    const random = bindings.randomBytes;
    if (random === undefined) {
      throw new Error('this build has no secure random source');
    }
    if (count <= 0) {
      return new Uint8Array(0);
    }
    return random(count);
  }

  /** A version-4 UUID, from secure random bytes. */
  static randomUuid(): string {
    const bytes = Crypto.randomBytes(16);
    // Version and variant bits, per RFC 4122. Without them the string is a
    // random hex blob that some parsers reject and others accept, which is the
    // worst of both.
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    const hex: string[] = [];
    for (let i = 0; i < bytes.length; i++) {
      hex.push(bytes[i].toString(16).padStart(2, '0'));
    }
    const s = hex.join('');
    return `${s.substring(0, 8)}-${s.substring(8, 12)}-${s.substring(12, 16)}-${s.substring(16, 20)}-${s.substring(20)}`;
  }

  /**
   * Compares two strings in time that does not depend on where they differ.
   *
   * `===` returns as soon as two bytes differ, and the timing difference is
   * measurable over a network - which is how a key gets guessed one byte at a
   * time. The length is folded into the accumulator rather than checked first,
   * for the same reason.
   */
  static timingSafeEqual(a: string, b: string): boolean {
    let difference = a.length ^ b.length;
    const longest = a.length > b.length ? a.length : b.length;
    for (let i = 0; i < longest; i++) {
      const x = i < a.length ? a.charCodeAt(i) : 0;
      const y = i < b.length ? b.charCodeAt(i) : 0;
      difference |= x ^ y;
    }
    return difference === 0;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// The device

/** What this device is, as far as it can be asked. */
export interface HarmonyDeviceInfo {
  /** Total physical memory in bytes. Zero means NOT MEASURED, not zero memory. */
  readonly totalMemoryBytes: number;
  readonly availableMemoryBytes: number;
  readonly cpuCount: number;
  /**
   * Whether the memory figure came from the operating system.
   *
   * The distinction this whole field exists for: a runtime's own heap and the
   * device's physical memory are both "a number of bytes about memory", and
   * using one where the other was meant picks a model to fit a heap rather than
   * a phone.
   */
  readonly memoryIsPhysical: boolean;
}

export class Device {
  static info(): HarmonyDeviceInfo {
    const memory = bindings.memoryBytes;
    const cpus = bindings.cpuCount;
    if (memory === undefined) {
      return {
        totalMemoryBytes: 0,
        availableMemoryBytes: 0,
        cpuCount: cpus === undefined ? 1 : cpus(),
        memoryIsPhysical: false,
      };
    }
    const measured = memory();
    return {
      totalMemoryBytes: measured[0],
      availableMemoryBytes: measured[1],
      cpuCount: cpus === undefined ? 1 : cpus(),
      memoryIsPhysical: true,
    };
  }

  /**
   * Whether a model may be sized against this device's memory.
   *
   * FALSE when nothing was measured. An unmeasured device sizing a model by a
   * guessed figure is the failure the whole distinction exists to prevent.
   */
  static canSizeModels(): boolean {
    const info = Device.info();
    return info.memoryIsPhysical && info.totalMemoryBytes > 0;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Node compatibility

/**
 * The Node names the ported core imports, over the HarmonyOS layer above.
 *
 * These exist so the port could be moved across without rewriting every call
 * site around a different shape. They are thin on purpose: the behaviour is in
 * `Files`, `Path` and `Crypto`, and anything that reads as clever here would be
 * behaviour hidden in a compatibility layer.
 */
export function randomUUID(): string {
  return Crypto.randomUuid();
}

export function randomBytes(count: number): Uint8Array {
  return Crypto.randomBytes(count);
}

/** Only SHA-256 is supported, and an unknown algorithm is refused rather than
 *  silently substituted - a digest computed with the wrong algorithm verifies
 *  against nothing and reads as a corrupt file. */
export function createHash(algorithm: string): HashBuilder {
  const normalized = algorithm.toLowerCase().replace('-', '');
  if (normalized !== 'sha256') {
    throw new Error(`${algorithm} is not available on this platform; only sha256 is`);
  }
  return new HashBuilder();
}

/** Accumulates text, then digests it. Mirrors the Node builder the core uses. */
export class HashBuilder {
  private parts: string[] = [];

  update(text: string): HashBuilder {
    this.parts.push(text);
    return this;
  }

  digest(encoding: string): string {
    if (encoding !== 'hex') {
      throw new Error(`${encoding} is not available on this platform; only hex is`);
    }
    return Crypto.sha256Hex(this.parts.join(''));
  }
}

export function timingSafeEqual(a: string, b: string): boolean {
  return Crypto.timingSafeEqual(a, b);
}

/**
 * The temporary directory.
 *
 * INSIDE THE SANDBOX, always. There is no shared `/tmp` an app may write to on
 * this platform, and a core that assumed one would fail at its first cache
 * write.
 */
export function tmpdir(): string {
  return Path.join(Files.root(), 'temp');
}

// ─────────────────────────────────────────────────────────────────────────────
// Signing, HMAC and key pairs
//
// THE PRIVATE KEY NEVER LEAVES THE PLATFORM. HarmonyOS keeps a generated key in
// the system key store and hands back a handle; the signing happens there, not
// here. That is why `generateKeyPair` returns a `KeyObject` carrying a handle
// and a public key rather than two PEM strings - a private key that this code
// could hold is a private key that could be logged, copied into a crash report,
// or written to a file somebody later shares.

/** Extra seams for the parts of cryptography that need the system key store. */
export interface CryptoBindings {
  /** HMAC-SHA-256 over `message` with `key`, as lower-case hex. */
  hmacSha256?: (key: string, message: string) => string;
  /**
   * Generates a key pair in the system key store. Returns `[handle, publicKey]`
   * - and never the private half, which stays where it was made.
   */
  generateKeyPair?: (algorithm: string) => [string, string];
  /** Signs with the key behind `handle`. Returns the signature as hex. */
  signWithHandle?: (handle: string, message: string) => string;
  /** Verifies a signature against a public key. */
  verifyWithPublicKey?: (
    publicKey: string,
    message: string,
    signature: string,
    algorithm: string
  ) => boolean;
  /** Random bytes, for the WebCrypto-shaped seam below. */
  fillRandom?: (buffer: Uint8Array) => void;
}

const cryptoBindings: CryptoBindings = {};

export function bindCrypto(next: CryptoBindings): void {
  cryptoBindings.hmacSha256 = next.hmacSha256;
  cryptoBindings.generateKeyPair = next.generateKeyPair;
  cryptoBindings.signWithHandle = next.signWithHandle;
  cryptoBindings.verifyWithPublicKey = next.verifyWithPublicKey;
  cryptoBindings.fillRandom = next.fillRandom;
}

/**
 * A key that lives in the system key store.
 *
 * `handle` is empty for a public-only key, which is the normal case for one
 * received from somebody else - and it is why signing checks the handle rather
 * than assuming a key object can sign.
 */
export class KeyObject {
  constructor(
    readonly algorithm: string,
    readonly publicKey: string,
    readonly handle: string = ''
  ) {}

  canSign(): boolean {
    return this.handle.length > 0;
  }

  /**
   * Whether two signatures over the same message can be compared byte for byte.
   *
   * FALSE for Ed25519 as some implementations produce it: a randomised
   * signature is valid and different every time, so comparing bytes to decide
   * whether two devices signed the same thing gives the wrong answer. Verify
   * both instead.
   */
  signaturesAreDeterministic(): boolean {
    const a = this.algorithm.toLowerCase();
    return a === 'p-256' || a === 'ecdsa' || a === 'secp256r1';
  }
}

/** Accumulates text, then signs it. Mirrors the Node builder the core uses. */
export class Signer {
  private parts: string[] = [];

  constructor(private readonly algorithm: string) {}

  update(text: string): Signer {
    this.parts.push(text);
    return this;
  }

  sign(key: KeyObject, encoding: string): string {
    if (encoding !== 'hex') {
      throw new Error(`${encoding} is not available on this platform; only hex is`);
    }
    if (!key.canSign()) {
      throw new Error('that key is public only, so nothing can be signed with it');
    }
    const sign = cryptoBindings.signWithHandle;
    if (sign === undefined) {
      throw new Error('this build cannot sign');
    }
    return sign(key.handle, this.parts.join(''));
  }
}

/** Accumulates text, then verifies a signature over it. */
export class Verifier {
  private parts: string[] = [];

  constructor(private readonly algorithm: string) {}

  update(text: string): Verifier {
    this.parts.push(text);
    return this;
  }

  /**
   * FALSE when there is no verifier - never true.
   *
   * A verifier that returns true when it cannot check accepts every signature,
   * which is the single worst default available here.
   */
  verify(key: KeyObject, signature: string, encoding: string): boolean {
    if (encoding !== 'hex') {
      return false;
    }
    const verify = cryptoBindings.verifyWithPublicKey;
    if (verify === undefined) {
      return false;
    }
    return verify(key.publicKey, this.parts.join(''), signature, this.algorithm);
  }
}

export function createSign(algorithm: string): Signer {
  return new Signer(algorithm);
}

export function createVerify(algorithm: string): Verifier {
  return new Verifier(algorithm);
}

/**
 * Makes a key pair in the system key store.
 *
 * Returns the public half and a handle. There is deliberately no way to get the
 * private half out: the platform will sign with it, and that is the whole of
 * what anything here needs.
 */
export function generateKeyPairSync(algorithm: string): KeyObject {
  const generate = cryptoBindings.generateKeyPair;
  if (generate === undefined) {
    throw new Error('this build cannot generate keys');
  }
  const pair = generate(algorithm);
  return new KeyObject(algorithm, pair[1], pair[0]);
}

/** Accumulates text, then HMACs it. Only SHA-256 is available. */
export class HmacBuilder {
  private parts: string[] = [];

  constructor(private readonly key: string) {}

  update(text: string): HmacBuilder {
    this.parts.push(text);
    return this;
  }

  digest(encoding: string): string {
    if (encoding !== 'hex') {
      throw new Error(`${encoding} is not available on this platform; only hex is`);
    }
    const hmac = cryptoBindings.hmacSha256;
    if (hmac === undefined) {
      throw new Error('this build has no HMAC available');
    }
    return hmac(this.key, this.parts.join('')).toLowerCase();
  }
}

export function createHmac(algorithm: string, key: string): HmacBuilder {
  const normalized = algorithm.toLowerCase().replace('-', '');
  if (normalized !== 'sha256') {
    throw new Error(`${algorithm} is not available on this platform; only sha256 is`);
  }
  return new HmacBuilder(key);
}

/**
 * The WebCrypto-shaped seam, for the one call site that wants it.
 *
 * Only `getRandomValues` is provided, and it THROWS rather than filling the
 * buffer with anything weak. A caller that silently receives predictable bytes
 * from something called `getRandomValues` has no way to notice.
 */
export class WebCryptoShim {
  getRandomValues(buffer: Uint8Array): Uint8Array {
    const fill = cryptoBindings.fillRandom;
    if (fill === undefined) {
      throw new Error('this build has no secure random source');
    }
    fill(buffer);
    return buffer;
  }

  randomUUID(): string {
    return Crypto.randomUuid();
  }
}

export const webcrypto: WebCryptoShim = new WebCryptoShim();
