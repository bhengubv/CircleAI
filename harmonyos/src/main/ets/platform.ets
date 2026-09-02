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
  statFile?: (path: string) => Promise<FileStat>;
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
  bindings.statFile = next.statFile;
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
  /** Node spells it `sep`, and the ported code says `path.sep`. */
  static readonly sep: string = '/';

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

  // ───────────────────────────────────────────────────────────────────────────
  // The Node-shaped surface
  //
  // The ported core was written against `fs.promises` and calls it with Node's
  // argument shapes — `readFile(path, "utf8")`, `mkdir(path, { recursive })`,
  // `rm(path, { force })`. These provide exactly those shapes over the sandbox
  // methods above, so the call sites did not have to be rewritten one at a
  // time. Every one of them still goes through `contained()`, so the sandbox
  // check is not something a caller can route around by using the familiar
  // name.
  //
  // WHAT IS DELIBERATELY NOT PROVIDED: any synchronous variant. HarmonyOS file
  // access is asynchronous, and a `readFileSync` here would either block the UI
  // thread or lie about having read anything.

  /**
   * With an encoding, a string; without one, bytes.
   *
   * The overload matters: the core calls both forms, and a version that always
   * returned a string would hand text to the callers reading model weights.
   */
  static readFile(relative: string, encoding: string): Promise<string>;
  static readFile(relative: string): Promise<Bytes>;
  static async readFile(relative: string, encoding?: string): Promise<string | Bytes> {
    const text = await Files.read(relative);
    if (encoding === undefined) {
      return Bytes.wrap(BufferShim.encodeUtf8(text));
    }
    return text;
  }

  static async writeFile(
    relative: string,
    content: string | Uint8Array,
    _encoding?: string
  ): Promise<void> {
    const text =
      typeof content === 'string' ? content : BufferShim.decodeUtf8(content);
    await Files.write(relative, text);
  }

  /**
   * `{ recursive: true }` is the only option honoured, and the only one the
   * core passes. A non-recursive mkdir that quietly succeeded would leave the
   * parent missing and fail at the write instead, naming the wrong path.
   */
  static async mkdir(relative: string, _options?: MakeDirectoryOptions): Promise<void> {
    const path = Files.contained(relative);
    if (path === null) {
      throw new Error('that path is outside this app’s own storage');
    }
    const make = bindings.makeDirectory;
    if (make === undefined) {
      throw new Error('this build has no filesystem');
    }
    await make(path);
  }

  static readdir(relative: string): Promise<string[]>;
  static readdir(relative: string, options: ReadDirOptions): Promise<DirectoryEntry[]>;
  static async readdir(
    relative: string,
    options?: ReadDirOptions
  ): Promise<string[] | DirectoryEntry[]> {
    const names = await Files.list(relative);
    if (options === undefined || options.withFileTypes !== true) {
      return names;
    }
    // `withFileTypes` needs a stat per entry, which is why the plain form is
    // kept: the callers that only want names should not pay for the walk.
    const out: DirectoryEntry[] = [];
    for (const name of names) {
      let directory = false;
      try {
        directory = (await Files.stat(Path.join(relative, name))).isDirectory();
      } catch (error) {
        // An entry that vanished between the listing and the stat is skipped
        // rather than failing the whole walk - a directory being written while
        // it is read is normal, not an error.
        continue;
      }
      out.push(new DirectoryEntry(name, directory));
    }
    return out;
  }

  /**
   * `{ force: true }` means a missing file is not an error, which is what the
   * core relies on when clearing a cache that may never have been written.
   */
  static async rm(relative: string, options?: RemoveOptions): Promise<void> {
    const force = options !== undefined && options.force === true;
    try {
      await Files.remove(relative);
    } catch (error) {
      if (!force) {
        throw error as Error;
      }
    }
  }

  /** Node's name for it; the port calls both. */
  static async unlink(relative: string): Promise<void> {
    await Files.remove(relative);
  }

  static async rename(from: string, to: string): Promise<void> {
    const source = await Files.read(from);
    await Files.write(to, source);
    await Files.remove(from);
  }

  static async copyFile(from: string, to: string): Promise<void> {
    await Files.write(to, await Files.read(from));
  }

  static async access(relative: string): Promise<void> {
    if (!(await Files.exists(relative))) {
      throw new Error(`${relative} is not there`);
    }
  }

  /** Only what the core reads off a stat: whether it is a directory, and size. */
  static async stat(relative: string): Promise<FileStat> {
    const path = Files.contained(relative);
    if (path === null) {
      throw new Error('that path is outside this app’s own storage');
    }
    const stat = bindings.statFile;
    if (stat === undefined) {
      throw new Error('this build has no filesystem');
    }
    return stat(path);
  }
}

/** Node's `readdir` options, narrowed to what is honoured. */
export interface ReadDirOptions {
  readonly withFileTypes?: boolean;
}

/**
 * One entry from a directory listing.
 *
 * `isDirectory()` is a METHOD because that is how every call site reads it. A
 * field of that name would be a function reference, which is truthy, and every
 * check would pass.
 */
export class DirectoryEntry {
  constructor(readonly name: string, private readonly directory: boolean) {}

  isDirectory(): boolean {
    return this.directory;
  }

  isFile(): boolean {
    return !this.directory;
  }
}

/** Node's `mkdir` options, narrowed to what is honoured. */
export interface MakeDirectoryOptions {
  readonly recursive?: boolean;
}

/** Node's `rm` options, narrowed to what is honoured. */
export interface RemoveOptions {
  readonly force?: boolean;
  readonly recursive?: boolean;
}

/**
 * What a stat carries here.
 *
 * `isDirectory()` is a METHOD, not a field, because that is how every call site
 * reads it — Node's stat is an object with methods, and a field named
 * `isDirectory` would be truthy as a function reference and pass every check.
 */
export interface FileStat {
  readonly size: number;
  readonly mtimeMs: number;
  isDirectory(): boolean;
  isFile(): boolean;
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
  static randomBytes(count: number): Bytes {
    const random = bindings.randomBytes;
    if (random === undefined) {
      throw new Error('this build has no secure random source');
    }
    if (count <= 0) {
      return new Bytes(0);
    }
    return Bytes.wrap(random(count));
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
  static timingSafeEqual(
    a: string | Uint8Array,
    b: string | Uint8Array
  ): boolean {
    if (typeof a !== 'string' || typeof b !== 'string') {
      const x = typeof a === 'string' ? BufferShim.encodeUtf8(a) : a;
      const y = typeof b === 'string' ? BufferShim.encodeUtf8(b) : b;
      let bytewise = x.length ^ y.length;
      const longest = x.length > y.length ? x.length : y.length;
      for (let i = 0; i < longest; i++) {
        bytewise |= (i < x.length ? x[i] : 0) ^ (i < y.length ? y[i] : 0);
      }
      return bytewise === 0;
    }
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

  // ───────────────────────────────────────────────────────────────────────────
  // The os-shaped surface
  //
  // The ported core imports `node:os` for four things. Three map onto the
  // device probes above; the fourth does not exist here at all and is answered
  // with the only place an app may write.

  /**
   * There is NO home directory on HarmonyOS.
   *
   * An app has a sandbox handed to it by its ability's context, and that is
   * both where it may write and the whole of what it knows about the
   * filesystem. Returning the sandbox root is the honest answer to what the
   * call sites are actually asking — "where do my files go" — rather than a
   * path that does not exist.
   */
  static homedir(): string {
    return Files.root();
  }

  /** Inside the sandbox, for the same reason: there is no shared `/tmp`. */
  static tmpdir(): string {
    return Path.join(Files.root(), 'temp');
  }

  /** Zero when nothing was measured — NOT a claim that there is no memory. */
  static freemem(): number {
    return Device.info().availableMemoryBytes;
  }

  static totalmem(): number {
    return Device.info().totalMemoryBytes;
  }

  /**
   * One entry per core, so `os.cpus().length` reads correctly.
   *
   * The entries carry no model or speed: HarmonyOS does not expose either, and
   * inventing them would put a made-up figure where a scheduler might use it.
   */
  static cpus(): CpuInfo[] {
    const out: CpuInfo[] = [];
    const count = Device.info().cpuCount;
    for (let i = 0; i < count; i++) {
      out.push({ model: '', speed: 0 });
    }
    return out;
  }

  static platform(): string {
    return 'harmonyos';
  }
}

/** One core, as `os.cpus()` shapes it. */
export interface CpuInfo {
  readonly model: string;
  readonly speed: number;
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

export function randomBytes(count: number): Bytes {
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
  private parts: Uint8Array[] = [];

  update(data: string | Uint8Array, encoding?: string): HashBuilder {
    this.parts.push(
      typeof data === 'string'
        ? BufferShim.from(data, encoding === undefined ? 'utf8' : encoding)
        : data
    );
    return this;
  }

  /** With an encoding, a string; without one, raw bytes — as Node does. */
  digest(): Bytes;
  digest(encoding: string): string;
  digest(encoding?: string): string | Bytes {
    const hex = Crypto.sha256Hex(
      BufferShim.decodeUtf8(BufferShim.concat(this.parts))
    );
    if (encoding === undefined) {
      return Bytes.wrap(BufferShim.from(hex, 'hex'));
    }
    if (encoding === 'hex') {
      return hex;
    }
    return BufferShim.toString(BufferShim.from(hex, 'hex'), encoding);
  }
}

export function timingSafeEqual(
  a: string | Uint8Array,
  b: string | Uint8Array
): boolean {
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
   * The PUBLIC bytes, in the requested form.
   *
   * There is no form that returns the private half: it lives in the system key
   * store and the platform signs with it. Asking for one is refused by name
   * rather than returning something empty that a caller might ship.
   */
  export(options: KeyExportOptions): Uint8Array {
    if (options.type !== 'spki') {
      throw new Error(
        `${options.type} export is not available; only the public key (spki) can leave the key store`
      );
    }
    if (options.format !== 'der') {
      throw new Error(`${options.format} is not available on this platform; only der is`);
    }
    return BufferShim.from(this.publicKey, 'base64');
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

/** What `sign` and `verify` are handed. */
export interface SignOptions {
  readonly key: KeyObject;
  /**
   * `ieee-p1363` is the fixed-width r||s form; `der` is the ASN.1 form.
   *
   * THE TWO ARE NOT INTERCHANGEABLE and neither is self-describing: a P-1363
   * signature handed to a DER verifier fails as though the key were wrong,
   * which sends somebody looking at key management for a signature-format bug.
   */
  readonly dsaEncoding?: string;
}

/** What `KeyObject.export` accepts. */
export interface KeyExportOptions {
  readonly type: string;
  readonly format: string;
}

/** Accumulates data, then signs it. Mirrors the Node builder the core uses. */
export class Signer {
  private parts: Uint8Array[] = [];
  private ended: boolean = false;

  constructor(private readonly algorithm: string) {}

  update(data: string | Uint8Array): Signer {
    this.parts.push(typeof data === 'string' ? BufferShim.encodeUtf8(data) : data);
    return this;
  }

  /** Node's builders take an explicit end; accepting it keeps the call sites. */
  end(): void {
    this.ended = true;
  }

  sign(keyOrOptions: KeyObject | SignOptions): Bytes {
    // Node accepts both a bare key and an options object; the port uses
    // both forms, so both are taken rather than one call site rewritten.
    const options: SignOptions =
      keyOrOptions instanceof KeyObject ? { key: keyOrOptions } : keyOrOptions;
    if (!options.key.canSign()) {
      throw new Error('that key is public only, so nothing can be signed with it');
    }
    const sign = cryptoBindings.signWithHandle;
    if (sign === undefined) {
      throw new Error('this build cannot sign');
    }
    const message = BufferShim.toString(BufferShim.concat(this.parts), 'base64');
    return BufferShim.from(sign(options.key.handle, message), 'hex');
  }
}

/** Accumulates data, then verifies a signature over it. */
export class Verifier {
  private parts: Uint8Array[] = [];
  private ended: boolean = false;

  constructor(private readonly algorithm: string) {}

  update(data: string | Uint8Array): Verifier {
    this.parts.push(typeof data === 'string' ? BufferShim.encodeUtf8(data) : data);
    return this;
  }

  end(): void {
    this.ended = true;
  }

  /**
   * FALSE when there is no verifier - never true.
   *
   * A verifier that returns true when it cannot check accepts every signature,
   * which is the single worst default available here.
   */
  verify(
    keyOrOptions: KeyObject | SignOptions,
    signature: string | Uint8Array
  ): boolean {
    const options: SignOptions =
      keyOrOptions instanceof KeyObject ? { key: keyOrOptions } : keyOrOptions;
    const verify = cryptoBindings.verifyWithPublicKey;
    if (verify === undefined) {
      return false;
    }
    const message = BufferShim.toString(BufferShim.concat(this.parts), 'base64');
    const provided =
      typeof signature === 'string'
        ? signature
        : BufferShim.toString(signature, 'hex');
    return verify(options.key.publicKey, message, provided, this.algorithm);
  }
}

export function createSign(algorithm: string): Signer {
  return new Signer(algorithm);
}

export function createVerify(algorithm: string): Verifier {
  return new Verifier(algorithm);
}

/** What a generated pair looks like. */
export interface KeyPair {
  readonly privateKey: KeyObject;
  readonly publicKey: KeyObject;
}

/** Node's key-generation options, narrowed to the one curve the port uses. */
export interface KeyPairOptions {
  readonly namedCurve?: string;
}

/**
 * Makes a key pair in the system key store.
 *
 * The private half NEVER leaves the platform: `privateKey` carries a handle the
 * system signs with, and `publicKey` carries the public bytes. A private key
 * this code could hold is one that could be logged, copied into a crash report,
 * or written to a file somebody later shares.
 */
export function generateKeyPairSync(
  type: string,
  options?: KeyPairOptions
): KeyPair {
  const generate = cryptoBindings.generateKeyPair;
  if (generate === undefined) {
    throw new Error('this build cannot generate keys');
  }
  const curve =
    options !== undefined && options.namedCurve !== undefined
      ? options.namedCurve
      : 'prime256v1';
  const pair = generate(curve);
  return {
    privateKey: new KeyObject(curve, pair[1], pair[0]),
    publicKey: new KeyObject(curve, pair[1], ''),
  };
}

/** Accumulates text, then HMACs it. Only SHA-256 is available. */
export class HmacBuilder {
  private parts: Uint8Array[] = [];

  constructor(private readonly key: string) {}

  update(data: string | Uint8Array): HmacBuilder {
    this.parts.push(typeof data === 'string' ? BufferShim.encodeUtf8(data) : data);
    return this;
  }

  /** With an encoding, a string; without one, raw bytes — as Node does. */
  digest(): Bytes;
  digest(encoding: string): string;
  digest(encoding?: string): string | Bytes {
    const hmac = cryptoBindings.hmacSha256;
    if (hmac === undefined) {
      throw new Error('this build has no HMAC available');
    }
    const hex = hmac(
      this.key,
      BufferShim.decodeUtf8(BufferShim.concat(this.parts))
    ).toLowerCase();
    if (encoding === undefined) {
      return Bytes.wrap(BufferShim.from(hex, 'hex'));
    }
    if (encoding === 'hex') {
      return hex;
    }
    return BufferShim.toString(BufferShim.from(hex, 'hex'), encoding);
  }
}

export function createHmac(algorithm: string, key: string | Uint8Array): HmacBuilder {
  const normalized = algorithm.toLowerCase().replace('-', '');
  if (normalized !== 'sha256') {
    throw new Error(`${algorithm} is not available on this platform; only sha256 is`);
  }
  return new HmacBuilder(
    typeof key === 'string' ? key : BufferShim.toString(key, 'hex')
  );
}

/**
 * The WebCrypto-shaped seam, for the one call site that wants it.
 *
 * Only `getRandomValues` is provided, and it THROWS rather than filling the
 * buffer with anything weak. A caller that silently receives predictable bytes
 * from something called `getRandomValues` has no way to notice.
 */
export class WebCryptoShim {
  private subtleInstance: SubtleCryptoShim | undefined = undefined;

  /**
   * AES-GCM through the platform's own crypto framework.
   *
   * LAZY, and it has to be: `SubtleCryptoShim` is declared further down this
   * file, and a stored property would evaluate the name while that class is
   * still in its temporal dead zone — constructing a WebCryptoShim would throw
   * before doing anything. A getter is only evaluated on use, by which point
   * the whole module has been.
   */
  get subtle(): SubtleCryptoShim {
    if (this.subtleInstance === undefined) {
      this.subtleInstance = new SubtleCryptoShim();
    }
    return this.subtleInstance;
  }

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

// ─────────────────────────────────────────────────────────────────────────────
// The environment
//
// HarmonyOS applications have no environment variables. An app is handed a
// sandbox path by its ability's context and that is the whole of what it knows
// about where it is running.
//
// `Env.get` therefore always returns `undefined` — and that is the point of it
// existing rather than the sites being deleted. The ported code already handles
// `process.env.X` being absent, because it had to on a browser too; keeping the
// same `string | undefined` shape means that handling still runs. The first
// pass of this port substituted a bare `undefined` literal instead, which put
// `undefined` into positions typed `string` and turned a platform fact into a
// type error.

export class Env {
  /** Always `undefined` on this platform. Kept for the shape, not the value. */
  static get(_name: string): string | undefined {
    return undefined;
  }

  /** Why it is always undefined, for anything that wants to explain itself. */
  static readonly REASON: string =
    'HarmonyOS apps have no environment variables; use the ability context';
}

// ─────────────────────────────────────────────────────────────────────────────
// Cancellation
//
// ArkTS has no `AbortController`. The ported core uses one throughout to carry
// cancellation into long operations, so it is implemented here rather than the
// 865 call sites being rewritten around a different mechanism — a cancellation
// shape is the kind of thing that must be one thing everywhere or it is nothing.
//
// The ability installs these as globals at start-up; `installCancellation()`
// below is what it calls.

export type AbortListener = () => void;

/** The one listener option the port uses. */
export interface AbortListenerOptions {
  readonly once?: boolean;
}

export class AbortSignalShim {
  private listeners: AbortListener[] = [];
  private onceOnly: AbortListener[] = [];
  private _aborted: boolean = false;
  private _reason: Object | undefined = undefined;

  get aborted(): boolean {
    return this._aborted;
  }

  get reason(): Object | undefined {
    return this._reason;
  }

  /**
   * A listener added AFTER the signal already fired is called immediately.
   *
   * Without this, a task that starts late on an already-cancelled signal never
   * hears about it and runs to completion — which is the failure mode that
   * makes cancellation look intermittent rather than broken.
   */
  addEventListener(
    _type: string,
    listener: AbortListener,
    options?: AbortListenerOptions
  ): void {
    if (this._aborted) {
      listener();
      return;
    }
    // `{ once: true }` is HONOURED, not accepted and ignored. Every call site
    // in the port passes it, and a listener that fires twice on a retry loop
    // rejects a promise that was already settled - which surfaces as a
    // cancellation that works the first time and misbehaves after.
    this.listeners.push(listener);
    if (options !== undefined && options.once === true) {
      this.onceOnly.push(listener);
    }
  }

  removeEventListener(_type: string, listener: AbortListener): void {
    const kept: AbortListener[] = [];
    for (const existing of this.listeners) {
      if (existing !== listener) {
        kept.push(existing);
      }
    }
    this.listeners = kept;
  }

  /** Throws if already cancelled. The check every long loop makes. */
  throwIfAborted(): void {
    if (this._aborted) {
      throw new Error('the operation was cancelled');
    }
  }

  /** Internal: fires once and only once. */
  fire(reason: Object | undefined): void {
    if (this._aborted) {
      return;
    }
    this._aborted = true;
    this._reason = reason;
    // The list is copied before iterating: a listener that removes itself while
    // being called would otherwise skip the next one.
    const snapshot = this.listeners.slice();
    this.listeners = [];
    for (const listener of snapshot) {
      listener();
    }
  }
}

export class AbortControllerShim {
  readonly signal: AbortSignalShim = new AbortSignalShim();

  abort(reason?: Object): void {
    this.signal.fire(reason);
  }
}

/**
 * A signal that fires after a delay, for a deadline.
 *
 * The timer is CLEARED when the signal fires for any other reason, so a
 * cancelled operation does not leave a timer holding the ability awake.
 */
export function timeoutSignal(milliseconds: number): AbortSignalShim {
  const controller = new AbortControllerShim();
  const timer = setTimeout(() => controller.abort(new Error('timed out')), milliseconds);
  controller.signal.addEventListener('abort', () => clearTimeout(timer));
  return controller.signal;
}

/**
 * Installs cancellation as globals.
 *
 * Called once by the ability. The core refers to `AbortController` unqualified,
 * as every other port does, and pushing that difference into 865 call sites
 * would make this port's code diverge from the others for no behavioural
 * reason.
 */
export function installCancellation(): void {
  const scope = globalThis as unknown as Record<string, Object>;
  if (scope['AbortController'] === undefined) {
    scope['AbortController'] = AbortControllerShim;
  }
  if (scope['AbortSignal'] === undefined) {
    scope['AbortSignal'] = AbortSignalShim;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Bytes
//
// There is no `Buffer` on HarmonyOS — that is a Node type. The ported core uses
// it in about forty places for byte handling, all of which are things
// `Uint8Array` does; this provides the Node-shaped statics over it so those call
// sites did not have to be rewritten one at a time.

export class BufferShim {
  /**
   * From a string or from bytes.
   *
   * ONLY utf-8, hex and base64 — the three the port actually uses. An unknown
   * encoding is refused rather than silently treated as utf-8, because a base64
   * string read as utf-8 produces bytes that are not an error and are not the
   * data either.
   */
  static from(
    value: string | Uint8Array | number[] | ArrayBufferLike,
    encodingOrOffset?: string | number,
    length?: number
  ): Bytes {
    // The three-argument ArrayBuffer form is a VIEW in Node, not a copy, and
    // the port uses it to re-encode an existing slice without duplicating it.
    if (!(value instanceof Uint8Array) && !Array.isArray(value) && typeof value !== 'string') {
      const offset = typeof encodingOrOffset === 'number' ? encodingOrOffset : 0;
      const buffer = value as ArrayBuffer;
      const count = length === undefined ? buffer.byteLength - offset : length;
      return Bytes.wrap(new Uint8Array(buffer, offset, count));
    }
    const encoding =
      typeof encodingOrOffset === 'string' ? encodingOrOffset : 'utf8';
    if (typeof value !== 'string') {
      return Bytes.wrap(
        value instanceof Uint8Array ? new Uint8Array(value) : new Uint8Array(value)
      );
    }
    const normalized = encoding.toLowerCase().replace('-', '');
    if (normalized === 'utf8') {
      return Bytes.wrap(BufferShim.encodeUtf8(value));
    }
    if (normalized === 'hex') {
      const out = new Uint8Array(Math.floor(value.length / 2));
      for (let i = 0; i < out.length; i++) {
        out[i] = parseInt(value.substring(i * 2, i * 2 + 2), 16);
      }
      return Bytes.wrap(out);
    }
    if (normalized === 'base64') {
      return Bytes.wrap(BufferShim.decodeBase64(value));
    }
    throw new Error(`${encoding} is not available on this platform`);
  }

  static alloc(size: number, fill: number = 0): Bytes {
    const out = new Bytes(size);
    if (fill !== 0) {
      out.fill(fill);
    }
    return out;
  }

  /**
   * The BYTE length of a string, not its character count.
   *
   * The distinction that matters everywhere text meets a wire: "é" is one
   * character and two bytes, and a length prefix computed from `.length`
   * truncates every message containing one.
   */
  static byteLength(value: string, encoding: string = 'utf8'): number {
    return BufferShim.from(value, encoding).length;
  }

  static concat(parts: Uint8Array[]): Bytes {
    let total = 0;
    for (const part of parts) {
      total += part.length;
    }
    const out = new Bytes(total);
    let offset = 0;
    for (const part of parts) {
      out.set(part, offset);
      offset += part.length;
    }
    return out;
  }

  static toString(bytes: Uint8Array, encoding: string = 'utf8'): string {
    const normalized = encoding.toLowerCase().replace('-', '');
    if (normalized === 'utf8') {
      return BufferShim.decodeUtf8(bytes);
    }
    if (normalized === 'hex') {
      const parts: string[] = [];
      for (let i = 0; i < bytes.length; i++) {
        parts.push(bytes[i].toString(16).padStart(2, '0'));
      }
      return parts.join('');
    }
    if (normalized === 'base64') {
      return BufferShim.encodeBase64(bytes);
    }
    throw new Error(`${encoding} is not available on this platform`);
  }

  /** UTF-8 encoding, written out because ArkTS has no `TextEncoder`. */
  static encodeUtf8(text: string): Uint8Array {
    const out: number[] = [];
    for (let i = 0; i < text.length; i++) {
      let code = text.charCodeAt(i);
      // A surrogate PAIR is one code point in two units. Encoding each unit on
      // its own produces CESU-8, which looks like UTF-8 and is rejected by
      // anything strict — and every emoji in the system is a surrogate pair.
      if (code >= 0xd800 && code <= 0xdbff && i + 1 < text.length) {
        const low = text.charCodeAt(i + 1);
        if (low >= 0xdc00 && low <= 0xdfff) {
          code = 0x10000 + ((code - 0xd800) << 10) + (low - 0xdc00);
          i++;
        }
      }
      if (code < 0x80) {
        out.push(code);
      } else if (code < 0x800) {
        out.push(0xc0 | (code >> 6), 0x80 | (code & 0x3f));
      } else if (code < 0x10000) {
        out.push(0xe0 | (code >> 12), 0x80 | ((code >> 6) & 0x3f), 0x80 | (code & 0x3f));
      } else {
        out.push(
          0xf0 | (code >> 18),
          0x80 | ((code >> 12) & 0x3f),
          0x80 | ((code >> 6) & 0x3f),
          0x80 | (code & 0x3f)
        );
      }
    }
    return new Uint8Array(out);
  }

  static decodeUtf8(bytes: Uint8Array): string {
    const parts: string[] = [];
    let i = 0;
    while (i < bytes.length) {
      const b = bytes[i];
      let code: number;
      let width: number;
      if (b < 0x80) {
        code = b;
        width = 1;
      } else if ((b & 0xe0) === 0xc0) {
        code = b & 0x1f;
        width = 2;
      } else if ((b & 0xf0) === 0xe0) {
        code = b & 0x0f;
        width = 3;
      } else {
        code = b & 0x07;
        width = 4;
      }
      // A truncated sequence at the end of a buffer is REPLACED, not dropped
      // and not thrown: a chunked reader hits this on every boundary, and
      // failing there would make every large file unreadable.
      if (i + width > bytes.length) {
        parts.push('�');
        break;
      }
      for (let k = 1; k < width; k++) {
        code = (code << 6) | (bytes[i + k] & 0x3f);
      }
      i += width;
      if (code > 0xffff) {
        const adjusted = code - 0x10000;
        parts.push(
          String.fromCharCode(0xd800 + (adjusted >> 10), 0xdc00 + (adjusted & 0x3ff))
        );
      } else {
        parts.push(String.fromCharCode(code));
      }
    }
    return parts.join('');
  }

  static readonly BASE64_ALPHABET: string =
    'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';

  static encodeBase64(bytes: Uint8Array): string {
    const alphabet = BufferShim.BASE64_ALPHABET;
    const parts: string[] = [];
    for (let i = 0; i < bytes.length; i += 3) {
      const b0 = bytes[i];
      const b1 = i + 1 < bytes.length ? bytes[i + 1] : 0;
      const b2 = i + 2 < bytes.length ? bytes[i + 2] : 0;
      parts.push(alphabet[b0 >> 2]);
      parts.push(alphabet[((b0 & 0x03) << 4) | (b1 >> 4)]);
      parts.push(i + 1 < bytes.length ? alphabet[((b1 & 0x0f) << 2) | (b2 >> 6)] : '=');
      parts.push(i + 2 < bytes.length ? alphabet[b2 & 0x3f] : '=');
    }
    return parts.join('');
  }

  static decodeBase64(text: string): Uint8Array {
    const alphabet = BufferShim.BASE64_ALPHABET;
    const clean = text.replace(/[^A-Za-z0-9+/]/g, '');
    const out: number[] = [];
    for (let i = 0; i < clean.length; i += 4) {
      const c0 = alphabet.indexOf(clean[i]);
      const c1 = i + 1 < clean.length ? alphabet.indexOf(clean[i + 1]) : -1;
      const c2 = i + 2 < clean.length ? alphabet.indexOf(clean[i + 2]) : -1;
      const c3 = i + 3 < clean.length ? alphabet.indexOf(clean[i + 3]) : -1;
      if (c1 >= 0) {
        out.push(((c0 << 2) | (c1 >> 4)) & 0xff);
      }
      if (c2 >= 0) {
        out.push(((c1 << 4) | (c2 >> 2)) & 0xff);
      }
      if (c3 >= 0) {
        out.push(((c2 << 6) | c3) & 0xff);
      }
    }
    return new Uint8Array(out);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// The web-shaped names
//
// None of these is a HarmonyOS global. They are implemented here because the
// ported core uses them and because rewriting a thousand call sites around
// platform-specific spellings would make this port diverge from the other seven
// for no behavioural reason.
//
// Each one is thin and says what it does not do. A shim that quietly covers for
// a missing capability is worse than an absent one: it turns a failure that
// would be found at start-up into a wrong answer found much later.

/** Text encoding, over the UTF-8 already written for `Buffer`. */
export class TextEncoderShim {
  readonly encoding: string = 'utf-8';

  encode(input: string = ''): Uint8Array {
    return BufferShim.encodeUtf8(input);
  }
}

/**
 * Text decoding.
 *
 * ONLY UTF-8. The core decodes nothing else, and an unrecognised label is
 * refused rather than silently treated as UTF-8 — text decoded with the wrong
 * encoding is not an error, it is mojibake that reaches a person.
 */
export class TextDecoderShim {
  constructor(readonly encoding: string = 'utf-8') {
    const normalized = encoding.toLowerCase().replace('_', '-');
    if (normalized !== 'utf-8' && normalized !== 'utf8') {
      throw new Error(`${encoding} is not available on this platform; only utf-8 is`);
    }
  }

  decode(input?: Uint8Array): string {
    return input === undefined ? '' : BufferShim.decodeUtf8(input);
  }
}

/** The query half of a URL. Order is preserved, and repeats are kept. */
export class UrlSearchParamsShim {
  private pairs: [string, string][] = [];

  constructor(init?: string | Record<string, string> | [string, string][]) {
    if (init === undefined) {
      return;
    }
    // An object or a pair list, as well as a query string. The port builds
    // parameters all three ways.
    if (typeof init !== 'string') {
      const pairs = Array.isArray(init) ? init : Object.entries(init);
      for (const pair of pairs) {
        this.pairs.push([pair[0], String(pair[1])]);
      }
      return;
    }
    if (init.length === 0) {
      return;
    }
    for (const part of init.replace(/^\?/, '').split('&')) {
      if (part.length === 0) {
        continue;
      }
      const cut = part.indexOf('=');
      const key = cut < 0 ? part : part.substring(0, cut);
      const value = cut < 0 ? '' : part.substring(cut + 1);
      this.pairs.push([decodeURIComponent(key), decodeURIComponent(value)]);
    }
  }

  /** The FIRST match, which is what every caller here means. */
  get(name: string): string | null {
    for (const pair of this.pairs) {
      if (pair[0] === name) {
        return pair[1];
      }
    }
    return null;
  }

  getAll(name: string): string[] {
    const out: string[] = [];
    for (const pair of this.pairs) {
      if (pair[0] === name) {
        out.push(pair[1]);
      }
    }
    return out;
  }

  has(name: string): boolean {
    return this.get(name) !== null;
  }

  /** APPENDS. `set` replaces; conflating the two silently drops repeats. */
  append(name: string, value: string): void {
    this.pairs.push([name, value]);
  }

  set(name: string, value: string): void {
    const kept: [string, string][] = [];
    let replaced = false;
    for (const pair of this.pairs) {
      if (pair[0] !== name) {
        kept.push(pair);
      } else if (!replaced) {
        kept.push([name, value]);
        replaced = true;
      }
    }
    if (!replaced) {
      kept.push([name, value]);
    }
    this.pairs = kept;
  }

  delete(name: string): void {
    const kept: [string, string][] = [];
    for (const pair of this.pairs) {
      if (pair[0] !== name) {
        kept.push(pair);
      }
    }
    this.pairs = kept;
  }

  toString(): string {
    const parts: string[] = [];
    for (const pair of this.pairs) {
      parts.push(
        `${encodeURIComponent(pair[0])}=${encodeURIComponent(pair[1])}`
      );
    }
    return parts.join('&');
  }
}

/**
 * A URL.
 *
 * Parsed rather than pattern-matched, because the one thing every caller uses
 * this for is deciding whether a host is allowed — and a host read off a naive
 * split is the classic way past an allow-list. `https://evil.com@allowed.host/`
 * has host `allowed.host`, and userinfo is stripped here for exactly that
 * reason.
 */
export class UrlShim {
  readonly protocol: string;
  readonly hostname: string;
  readonly port: string;
  readonly pathname: string;
  readonly search: string;
  readonly hash: string;
  readonly searchParams: UrlSearchParamsShim;

  constructor(input: string | UrlShim, base?: string | UrlShim) {
    // Both accept a parsed URL as well as a string: `new URL(href, base)`
    // is called with an already-parsed base throughout the port, and
    // requiring a string there would mean `.href` at every call site.
    const text0 = typeof input === 'string' ? input : input.href;
    const base0 = base === undefined || typeof base === 'string' ? base : base.href;
    let text = text0;
    if (base0 !== undefined && !UrlShim.hasScheme(text0)) {
      const parent = new UrlShim(base0);
      text = text0.startsWith('/')
        ? `${parent.protocol}//${parent.host}${text0}`
        : `${parent.protocol}//${parent.host}${Path.dirname(parent.pathname)}/${text0}`;
    }
    const schemeCut = text.indexOf('://');
    if (schemeCut < 0) {
      throw new Error(`${text0} is not an absolute URL`);
    }
    this.protocol = text.substring(0, schemeCut + 1);
    let rest = text.substring(schemeCut + 3);

    const hashCut = rest.indexOf('#');
    this.hash = hashCut < 0 ? '' : rest.substring(hashCut);
    if (hashCut >= 0) {
      rest = rest.substring(0, hashCut);
    }

    const queryCut = rest.indexOf('?');
    this.search = queryCut < 0 ? '' : rest.substring(queryCut);
    if (queryCut >= 0) {
      rest = rest.substring(0, queryCut);
    }

    const pathCut = rest.indexOf('/');
    let authority = pathCut < 0 ? rest : rest.substring(0, pathCut);
    this.pathname = pathCut < 0 ? '/' : rest.substring(pathCut);

    // Userinfo is dropped. Everything after the LAST `@` is the host, which is
    // what a browser does and what an allow-list must agree with.
    const atCut = authority.lastIndexOf('@');
    if (atCut >= 0) {
      authority = authority.substring(atCut + 1);
    }
    const portCut = authority.lastIndexOf(':');
    if (portCut > 0 && authority.indexOf(']') < portCut) {
      this.hostname = authority.substring(0, portCut).toLowerCase();
      this.port = authority.substring(portCut + 1);
    } else {
      this.hostname = authority.toLowerCase();
      this.port = '';
    }
    this.searchParams = new UrlSearchParamsShim(this.search);
  }

  static hasScheme(input: string): boolean {
    return input.indexOf('://') > 0;
  }

  get host(): string {
    return this.port.length > 0 ? `${this.hostname}:${this.port}` : this.hostname;
  }

  get origin(): string {
    return `${this.protocol}//${this.host}`;
  }

  get href(): string {
    return `${this.origin}${this.pathname}${this.search}${this.hash}`;
  }

  toString(): string {
    return this.href;
  }
}

/**
 * A monotonic-ish clock for measuring durations.
 *
 * NOT the wall clock, and it says so: `Date.now()` moves when the device's time
 * is corrected, and a duration measured across a correction comes out negative
 * or enormous. HarmonyOS exposes a real monotonic source, and the ability passes
 * it in; without one this falls back to the wall clock and `isMonotonic` reports
 * false so a caller can decline to trust a measurement.
 */
export class PerformanceShim {
  private static monotonicMs: (() => number) | undefined = undefined;
  private readonly origin: number = PerformanceShim.read();

  static bind(monotonic: () => number): void {
    PerformanceShim.monotonicMs = monotonic;
  }

  static isMonotonic(): boolean {
    return PerformanceShim.monotonicMs !== undefined;
  }

  private static read(): number {
    const monotonic = PerformanceShim.monotonicMs;
    return monotonic === undefined ? Date.now() : monotonic();
  }

  now(): number {
    return PerformanceShim.read() - this.origin;
  }

  get timeOrigin(): number {
    return this.origin;
  }
}

/** The web error type a few call sites throw and catch by name. */
export class DomExceptionShim extends Error {
  constructor(message: string = '', readonly domName: string = 'Error') {
    super(message);
    this.name = domName;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Network
//
// There is no `fetch` on HarmonyOS — network access goes through
// `@ohos.net.http`. The ability supplies a transport shaped like `fetch`, and
// until it does, calling `fetch` throws rather than resolving to an empty
// response: a network call that silently returns nothing is indistinguishable
// from a server that returned nothing, and the two need opposite reactions.

/** What a response carries. Only what the core reads off one. */
export interface FetchResponse {
  readonly ok: boolean;
  readonly status: number;
  readonly statusText: string;
  readonly headers: Map<string, string>;
  text(): Promise<string>;
  json(): Promise<Object>;
  arrayBuffer(): Promise<Uint8Array>;
}

export type FetchTransport = (input: Object, init?: Object) => Promise<FetchResponse>;

let fetchTransport: FetchTransport | undefined = undefined;

/** Called by the ability with a transport over `@ohos.net.http`. */
export function installFetch(transport: FetchTransport): void {
  fetchTransport = transport;
  const scope = globalThis as unknown as Record<string, Object>;
  scope['fetch'] = (input: Object, init?: Object): Promise<FetchResponse> => {
    const send = fetchTransport;
    if (send === undefined) {
      return Promise.reject(new Error('this build has no network transport'));
    }
    return send(input, init);
  };
}

export function hasNetwork(): boolean {
  return fetchTransport !== undefined;
}

/**
 * Bytes that can re-encode themselves.
 *
 * WHY THIS EXISTS. Node's `Buffer` is a `Uint8Array` whose `toString` takes an
 * encoding, and the ported core relies on that in about forty places -
 * `randomBytes(32).toString("base64")`, `hash.digest().toString("hex")`. A
 * plain `Uint8Array` has a `toString` that takes no arguments and returns a
 * comma-separated list of numbers: the call compiles in JavaScript and produces
 * "34,181,9,..." where a base64 token was meant. That is the worst shape of
 * bug - no error, plausible-looking output, and a token that will never verify.
 *
 * Extending `Uint8Array` keeps it usable everywhere bytes are expected, so
 * nothing else had to change.
 */
export class Bytes extends Uint8Array {
  toString(encoding: string = 'hex'): string {
    return BufferShim.toString(new Uint8Array(this), encoding);
  }

  static wrap(source: Uint8Array): Bytes {
    const out = new Bytes(source.length);
    out.set(source);
    return out;
  }

  /**
   * Little-endian float access, as Node's Buffer has.
   *
   * LITTLE-ENDIAN EXPLICITLY, on every call. `DataView` defaults to BIG-endian
   * while the host's own typed arrays are little-endian on every device this
   * runs on — so a default-endianness read of a float written by a typed array
   * returns a different number, silently, and an embedding decoded that way is
   * noise that still looks like an embedding.
   */
  writeFloatLE(value: number, offset: number): number {
    new DataView(this.buffer, this.byteOffset, this.byteLength).setFloat32(
      offset,
      value,
      true
    );
    return offset + 4;
  }

  readFloatLE(offset: number): number {
    return new DataView(this.buffer, this.byteOffset, this.byteLength).getFloat32(
      offset,
      true
    );
  }

  writeUInt32LE(value: number, offset: number): number {
    new DataView(this.buffer, this.byteOffset, this.byteLength).setUint32(
      offset,
      value,
      true
    );
    return offset + 4;
  }

  readUInt32LE(offset: number): number {
    return new DataView(this.buffer, this.byteOffset, this.byteLength).getUint32(
      offset,
      true
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Module-level aliases
//
// The ported core reaches some of these as free functions rather than through a
// class - `import { join } from "node:path"`, and `await import("node:os")`
// followed by `os.freemem()`. Exporting them at module level lets both forms
// resolve against this one implementation instead of a second copy.

export function join(...parts: string[]): string {
  return Path.join(...parts);
}

export function dirname(input: string): string {
  return Path.dirname(input);
}

export function basename(input: string): string {
  return Path.basename(input);
}

export function extname(input: string): string {
  return Path.extname(input);
}

export function resolve(...parts: string[]): string {
  const joined = Path.join(...parts);
  return Path.isAbsolute(joined) ? joined : Path.join(Files.root(), joined);
}

export const sep: string = '/';

/** There is no home directory on HarmonyOS; the sandbox is the honest answer. */
export function homedir(): string {
  return Device.homedir();
}

export function freemem(): number {
  return Device.freemem();
}

export function totalmem(): number {
  return Device.totalmem();
}

export function cpus(): CpuInfo[] {
  return Device.cpus();
}

export function platform(): string {
  return Device.platform();
}

// ─────────────────────────────────────────────────────────────────────────────
// The synchronous filesystem, which HarmonyOS does not have
//
// One sampler in the port reads Linux's thermal sysfs through `existsSync` and
// `readFileSync`. Neither the sync API nor that path exists here: HarmonyOS file
// access is asynchronous, and `/sys/class/thermal` is not readable by an
// application.
//
// `existsSync` therefore returns FALSE rather than throwing, which is the
// truth — the file is not there — and lets that sampler return "unknown" and
// move on, which is what it does on any non-Linux host. `readFileSync` throws,
// because a caller that reached it despite `existsSync` saying no has a bug and
// should hear about it rather than receive an empty string.

export function existsSync(_path: string): boolean {
  return false;
}

export function readFileSync(path: string, _encoding?: string): string {
  throw new Error(
    `${path} cannot be read synchronously: HarmonyOS file access is asynchronous`
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// AES-GCM, through the platform's own crypto framework
//
// The port encrypts local caches with AES-GCM through the WebCrypto `subtle`
// shape. HarmonyOS provides AES-GCM in `@ohos.security.cryptoFramework`, so this
// is the same primitive reached by a different name — not a reimplementation.
// Nothing here implements a cipher: a hand-written AES in ArkTS would be slower,
// unaudited, and would not use the hardware the device already has.

/** A key held by the platform. The bytes are not retrievable through it. */
export class ImportedKeyHandle {
  constructor(readonly handle: string, readonly algorithm: string) {}
}

/** The seams the ability fills from `@ohos.security.cryptoFramework`. */
export interface SubtleBindings {
  /** Imports raw key bytes; returns a handle the platform keeps. */
  importAesKey?: (raw: Uint8Array) => string;
  /** AES-GCM seal. Returns ciphertext with the tag appended, as the web does. */
  aesGcmEncrypt?: (
    handle: string,
    nonce: Uint8Array,
    plaintext: Uint8Array,
    additional?: Uint8Array
  ) => Uint8Array;
  /** AES-GCM open. Returns null when the tag does not verify. */
  aesGcmDecrypt?: (
    handle: string,
    nonce: Uint8Array,
    ciphertext: Uint8Array,
    additional?: Uint8Array
  ) => Uint8Array | null;
}

const subtleBindings: SubtleBindings = {};

export function bindSubtle(next: SubtleBindings): void {
  subtleBindings.importAesKey = next.importAesKey;
  subtleBindings.aesGcmEncrypt = next.aesGcmEncrypt;
  subtleBindings.aesGcmDecrypt = next.aesGcmDecrypt;
}

/** What an AES-GCM call is given. */
export interface AesGcmParams {
  readonly name: string;
  readonly iv: BufferSourceLike;
  readonly additionalData?: Uint8Array;
  /**
   * The authentication tag length, in BITS.
   *
   * 128 is the only value this accepts and the only one WebCrypto's default
   * uses. A shorter tag is weaker in a way nothing downstream can detect - the
   * ciphertext still decrypts - so a caller asking for one is refused rather
   * than quietly given 128 or quietly given what it asked for.
   */
  readonly tagLength?: number;
}

export interface KeyAlgorithm {
  readonly name: string;
}

/**
 * The WebCrypto `subtle` surface, narrowed to what the port uses.
 *
 * ONLY AES-GCM, and only raw key import. A broader shim would be a claim to
 * support algorithms nobody here has tested against the platform's framework,
 * and an unsupported algorithm is refused by name rather than falling through
 * to something else.
 */
export type BufferSourceLike = Uint8Array | ArrayBuffer;

/** Normalises either form of BufferSource to bytes. */
function asBytes(source: BufferSourceLike): Uint8Array {
  return source instanceof Uint8Array ? source : new Uint8Array(source);
}

export class SubtleCryptoShim {
  async importKey(
    format: string,
    keyData: BufferSourceLike,
    algorithm: KeyAlgorithm,
    _extractable: boolean,
    _usages: string[]
  ): Promise<ImportedKeyHandle> {
    if (format !== 'raw') {
      throw new Error(`${format} key import is not available on this platform`);
    }
    if (algorithm.name !== 'AES-GCM') {
      throw new Error(`${algorithm.name} is not available on this platform`);
    }
    const load = subtleBindings.importAesKey;
    if (load === undefined) {
      throw new Error('this build has no cipher available');
    }
    return new ImportedKeyHandle(load(asBytes(keyData)), algorithm.name);
  }

  async encrypt(
    params: AesGcmParams,
    key: ImportedKeyHandle,
    data: BufferSourceLike
  ): Promise<Uint8Array> {
    if (params.name !== 'AES-GCM') {
      throw new Error(`${params.name} is not available on this platform`);
    }
    if (params.tagLength !== undefined && params.tagLength !== 128) {
      throw new Error(
        `a ${params.tagLength}-bit tag is not available on this platform; only 128 is`
      );
    }
    const seal = subtleBindings.aesGcmEncrypt;
    if (seal === undefined) {
      throw new Error('this build has no cipher available');
    }
    return seal(key.handle, asBytes(params.iv), asBytes(data), params.additionalData);
  }

  /**
   * THROWS when the tag does not verify, as WebCrypto does.
   *
   * Returning empty bytes on a failed tag would let a caller carry on with data
   * that was tampered with — which is the one thing an authenticated cipher
   * exists to prevent.
   */
  async decrypt(
    params: AesGcmParams,
    key: ImportedKeyHandle,
    data: BufferSourceLike
  ): Promise<Uint8Array> {
    if (params.name !== 'AES-GCM') {
      throw new Error(`${params.name} is not available on this platform`);
    }
    if (params.tagLength !== undefined && params.tagLength !== 128) {
      throw new Error(
        `a ${params.tagLength}-bit tag is not available on this platform; only 128 is`
      );
    }
    const open = subtleBindings.aesGcmDecrypt;
    if (open === undefined) {
      throw new Error('this build has no cipher available');
    }
    const plaintext = open(key.handle, asBytes(params.iv), asBytes(data), params.additionalData);
    if (plaintext === null) {
      throw new DomExceptionShim('the data could not be authenticated', 'OperationError');
    }
    return plaintext;
  }
}
