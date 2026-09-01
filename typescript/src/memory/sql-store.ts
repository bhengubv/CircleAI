// The SQL seam, identity matching, ambient sensing, and the tool surface.
//
// EVERY QUERY HERE IS PARAMETERISED. Not a style preference: every value that
// reaches these stores came from something somebody said to an assistant, so a
// store that formats values into SQL can be rewritten by saying the right
// sentence out loud. There is not one template literal containing a value in
// this file - only identifiers, validated against a strict pattern before the
// dialect quotes them.
//
// BIOMETRICS ARE TEMPLATES, NEVER SAMPLES. What is stored is a vector that
// cannot be played back or shown. And the threshold is set where a false ACCEPT
// is the expensive error: letting the wrong person in is worse than asking the
// right one again.

// ─────────────────────────────────────────────────────────────────────────────
// SQL

/**
 * Which database is on the other end.
 *
 * THE DIFFERENCES ARE SMALL AND ALL OF THEM BREAK A QUERY. A placeholder
 * written for one and sent to another is a syntax error at best and, on the
 * databases that accept it, a literal string where a parameter was meant.
 */
export enum SqlDialect {
  Sqlite = "sqlite",
  Postgres = "postgres",
  SqlServer = "sqlserver",
  MySql = "mysql",
}

/**
 * `?` for SQLite and MySQL, `$1` for Postgres, `@p1` for SQL Server.
 *
 * Postgres and SQL Server are ONE-BASED and positional, so an index starting at
 * zero silently shifts every parameter by one - which usually produces a type
 * error on the first column and a wrong row on the rest.
 */
export function placeholder(dialect: SqlDialect, index: number): string {
  if (dialect === SqlDialect.Postgres) return `$${index + 1}`;
  if (dialect === SqlDialect.SqlServer) return `@p${index + 1}`;
  return "?";
}

export const placeholders = (dialect: SqlDialect, count: number): string =>
  Array.from({ length: count }, (_, i) => placeholder(dialect, i)).join(", ");

/**
 * Quotes a TABLE OR COLUMN name, having first checked it is one.
 *
 * A quoted identifier is not safe by itself - MySQL's backtick and the standard
 * double quote can both be closed by a value containing them. So the name is
 * validated against a strict pattern and refused if it is anything other than a
 * plain identifier, and only then quoted.
 */
export function quoteIdentifier(dialect: SqlDialect, identifier: string): string {
  if (!/^[A-Za-z_][A-Za-z0-9_]{0,62}$/.test(identifier ?? "")) {
    throw new Error(`'${identifier}' is not a plain identifier and will not be put into a statement`);
  }
  if (dialect === SqlDialect.MySql) return `\`${identifier}\``;
  if (dialect === SqlDialect.SqlServer) return `[${identifier}]`;
  return `"${identifier}"`;
}

/** Every one of these spells an upsert differently and none of them warn. */
export function upsertClause(dialect: SqlDialect): string {
  // SQL Server has no upsert clause; the caller must MERGE or check first.
  // Saying so here beats emitting something that parses and then duplicates
  // rows.
  if (dialect === SqlDialect.SqlServer) return "";
  if (dialect === SqlDialect.MySql) return "ON DUPLICATE KEY UPDATE";
  return "ON CONFLICT";
}

export const textType = (dialect: SqlDialect): string =>
  dialect === SqlDialect.SqlServer ? "NVARCHAR(MAX)" : "TEXT";

export const supportsReturning = (dialect: SqlDialect): boolean =>
  dialect === SqlDialect.Postgres || dialect === SqlDialect.Sqlite;

/**
 * Memory atoms in a relational database.
 *
 * THE TABLE NAME IS VALIDATED AND QUOTED, once at construction, so a bad name
 * fails where it was configured rather than on the first query in production.
 * A store where the table name is configurable is a store where the
 * configuration is an injection point.
 */
export class AdoAtomStore {
  private readonly table: string;

  constructor(
    readonly dialect: SqlDialect = SqlDialect.Sqlite,
    private readonly execute?: (sql: string, params: readonly unknown[]) => unknown[][],
    table = "atoms",
  ) {
    this.table = quoteIdentifier(dialect, table);
  }

  private q(identifier: string): string {
    return quoteIdentifier(this.dialect, identifier);
  }

  createTableSql(): string {
    return (
      `CREATE TABLE IF NOT EXISTS ${this.table} (` +
      ` ${this.q("id")} ${textType(this.dialect)} PRIMARY KEY,` +
      ` ${this.q("kind")} ${textType(this.dialect)} NOT NULL,` +
      ` ${this.q("text")} ${textType(this.dialect)} NOT NULL,` +
      ` ${this.q("stability")} REAL NOT NULL,` +
      ` ${this.q("created_at")} ${textType(this.dialect)} NOT NULL,` +
      ` ${this.q("last_recalled_at")} ${textType(this.dialect)})`
    );
  }

  insertSql(): string {
    const columns = ["id", "kind", "text", "stability", "created_at", "last_recalled_at"]
      .map((c) => this.q(c))
      .join(", ");
    return `INSERT INTO ${this.table} (${columns}) VALUES (${placeholders(this.dialect, 6)})`;
  }

  selectByKindSql(): string {
    return (
      `SELECT ${this.q("id")}, ${this.q("text")}, ${this.q("stability")} ` +
      `FROM ${this.table} WHERE ${this.q("kind")} = ${placeholder(this.dialect, 0)} ` +
      `ORDER BY ${this.q("stability")} DESC`
    );
  }

  initialise(): boolean {
    if (!this.execute) return false;
    this.execute(this.createTableSql(), []);
    return true;
  }

  put(atomId: string, kind: string, text: string, stability = 90, createdAtIso = ""): boolean {
    if (!this.execute || !atomId) return false;
    this.execute(this.insertSql(), [atomId, kind, text, stability, createdAtIso, null]);
    return true;
  }

  byKind(kind: string): unknown[][] {
    return this.execute ? this.execute(this.selectByKindSql(), [kind]) : [];
  }

  forget(atomId: string): boolean {
    if (!this.execute) return false;
    this.execute(
      `DELETE FROM ${this.table} WHERE ${this.q("id")} = ${placeholder(this.dialect, 0)}`,
      [atomId],
    );
    return true;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Identity

/**
 * Who somebody is to this device.
 *
 * NO NAME IS REQUIRED. A device can recognise a person without knowing who they
 * are, and requiring a name would mean asking for one before the first
 * recognition - which is exactly the moment a person is least willing to give
 * it.
 */
export interface IdentityRecord {
  readonly identityId: string;
  readonly displayName: string;
  readonly enrolledAtMs: number;
  /** Templates only. Never a photograph, never a recording. */
  readonly templateCount: number;
}

/**
 * Identities and their templates.
 *
 * FORGETTING IS ONE CALL and removes everything. A person who asks to be
 * forgotten must not depend on a caller enumerating what to delete.
 */
export class InMemoryIdentityStore {
  private readonly records = new Map<string, IdentityRecord>();
  private readonly templates = new Map<string, number[][]>();

  enrol(identityId: string, template: readonly number[], name = "", atMs = 0): boolean {
    if (!identityId || template.length === 0) return false;
    const existing = this.templates.get(identityId) ?? [];
    existing.push([...template]);
    this.templates.set(identityId, existing);
    this.records.set(identityId, {
      identityId,
      displayName: name || this.records.get(identityId)?.displayName || "",
      enrolledAtMs: this.records.get(identityId)?.enrolledAtMs ?? atMs,
      templateCount: existing.length,
    });
    return true;
  }

  get(identityId: string): IdentityRecord | undefined {
    return this.records.get(identityId);
  }

  allTemplates(): Readonly<Record<string, number[][]>> {
    return Object.freeze(
      Object.fromEntries([...this.templates].map(([k, v]) => [k, v.map((t) => [...t])])),
    );
  }

  forget(identityId: string): boolean {
    const had = this.records.has(identityId);
    this.records.delete(identityId);
    this.templates.delete(identityId);
    return had;
  }

  forgetEveryone(): number {
    const count = this.records.size;
    this.records.clear();
    this.templates.clear();
    return count;
  }
}

/** A match, or the absence of one. */
export interface BiometricMatch {
  readonly identityId: string;
  readonly similarity: number;
  readonly matched: boolean;
  /** How far clear of the runner-up. A match that only just beat another person
   * is not a match, and this is what lets a caller see that. */
  readonly margin: number;
}

/**
 * Matches a live template against enrolled ones.
 *
 * TWO TESTS, NOT ONE. A similarity above the threshold is not enough: it must
 * also beat the second-best by a margin. Two siblings produce embeddings that
 * both clear a threshold, and picking the higher of two near-identical scores
 * is a coin flip with somebody's identity.
 */
export class BiometricMatcher {
  /** Cosine similarity. */
  static readonly THRESHOLD = 0.75;
  /** How far clear of second place a match must be. */
  static readonly MIN_MARGIN = 0.06;

  static cosine(a: readonly number[], b: readonly number[]): number {
    if (a.length === 0 || a.length !== b.length) return 0;
    let dot = 0;
    let na = 0;
    let nb = 0;
    for (let i = 0; i < a.length; i++) {
      dot += a[i] * b[i];
      na += a[i] * a[i];
      nb += b[i] * b[i];
    }
    return na === 0 || nb === 0 ? 0 : dot / (Math.sqrt(na) * Math.sqrt(nb));
  }

  static match(
    live: readonly number[],
    enrolled: Readonly<Record<string, number[][]>>,
  ): BiometricMatch {
    const none = Object.freeze({ identityId: "", similarity: 0, matched: false, margin: 0 });
    if (live.length === 0) return none;

    // The BEST of a person's templates, not the average. Averaging a person
    // photographed in two very different lightings produces a template that
    // matches neither.
    const scores = Object.entries(enrolled)
      .filter(([, templates]) => templates.length > 0)
      .map(([identityId, templates]) => ({
        identityId,
        score: Math.max(...templates.map((t) => BiometricMatcher.cosine(live, t))),
      }))
      .sort((a, b) => b.score - a.score);
    if (scores.length === 0) return none;

    const best = scores[0];
    const margin = best.score - (scores[1]?.score ?? 0);
    if (best.score < BiometricMatcher.THRESHOLD) {
      return Object.freeze({ identityId: "", similarity: best.score, matched: false, margin });
    }
    if (margin < BiometricMatcher.MIN_MARGIN && scores.length > 1) {
      // Two people scored almost the same. Refusing is the only honest answer;
      // returning the higher one would be guessing.
      return Object.freeze({ identityId: "", similarity: best.score, matched: false, margin });
    }
    return Object.freeze({
      identityId: best.identityId,
      similarity: best.score,
      matched: true,
      margin,
    });
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Ambient

/**
 * Notices what is going on without recording it.
 *
 * IT KEEPS COUNTS AND LEVELS, NEVER AUDIO. The whole design question for an
 * always-listening feature is what it retains, and the answer here is: a number
 * per window, and nothing that can be played back.
 */
export class AmbientCompanionMonitor {
  private speechSeconds = 0;
  private quietSeconds = 0;
  private events = 0;

  constructor(private readonly windowMs = 5 * 60_000) {}

  observe(seconds: number, speechPresent: boolean): void {
    if (speechPresent) this.speechSeconds += seconds;
    else this.quietSeconds += seconds;
    this.events += 1;
  }

  summary(): Readonly<{ windowSeconds: number; speechFraction: number; observations: number }> {
    const total = this.speechSeconds + this.quietSeconds;
    return Object.freeze({
      windowSeconds: Math.round(total * 10) / 10,
      speechFraction: total > 0 ? Math.round((this.speechSeconds / total) * 1000) / 1000 : 0,
      observations: this.events,
    });
  }

  reset(): void {
    this.speechSeconds = 0;
    this.quietSeconds = 0;
    this.events = 0;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tools and devices

/**
 * The tool-protocol surface.
 *
 * TOOLS ARE LISTED ONLY IF THEY ARE ALLOWED. Advertising a tool that will then
 * refuse teaches a caller to try it every time, and each attempt is a prompt
 * that reached a model and a refusal that reached a person.
 */
export class McpEndpoints {
  constructor(
    private readonly tools: readonly Record<string, unknown>[] = [],
    private readonly invoke?: (name: string, args: Record<string, unknown>) => Promise<unknown>,
    private readonly isAllowed?: (name: string) => boolean,
  ) {}

  listTools(): readonly Record<string, unknown>[] {
    return Object.freeze(
      this.tools.filter((t) => !this.isAllowed || this.isAllowed(String(t.name ?? ""))),
    );
  }

  async callTool(
    name: string,
    args: Record<string, unknown>,
  ): Promise<{ isError: boolean; content: { type: string; text: string }[] }> {
    if (this.isAllowed && !this.isAllowed(name)) {
      return {
        isError: true,
        content: [{ type: "text", text: `${name} is not available on this device` }],
      };
    }
    if (!this.invoke) {
      return {
        isError: true,
        content: [{ type: "text", text: "no tools are wired up on this device" }],
      };
    }
    try {
      const result = await this.invoke(name, args);
      return { isError: false, content: [{ type: "text", text: String(result) }] };
    } catch (error) {
      // The error goes back as CONTENT with isError set, not as a transport
      // failure. A tool that threw is a result the model should see and work
      // around, not a broken connection it should retry.
      return {
        isError: true,
        content: [{ type: "text", text: error instanceof Error ? error.message : String(error) }],
      };
    }
  }
}

/**
 * Devices in the house, and what the companion may do with them.
 *
 * READ IS FREE, WRITE IS NOT. Asking a thermostat its temperature is not the
 * same as changing it, and a pipeline that treats them alike will eventually
 * unlock a door because a sentence was ambiguous.
 */
export class IoTCompanionPipeline {
  /** Actions never taken without an explicit confirmation, whatever was asked.
   * Each is either a safety matter or irreversible. */
  static readonly GUARDED = Object.freeze(["unlock", "open", "disarm", "off", "unmute"]);

  constructor(
    private readonly read?: (deviceId: string) => Promise<unknown>,
    private readonly write?: (deviceId: string, value: unknown) => Promise<boolean>,
    private readonly confirm?: (deviceId: string, action: string) => Promise<boolean>,
  ) {}

  async readState(deviceId: string): Promise<{ value?: unknown; reason: string }> {
    if (!this.read) return { reason: "nothing is connected to this device" };
    return { value: await this.read(deviceId), reason: "" };
  }

  async act(
    deviceId: string,
    action: string,
    value?: unknown,
  ): Promise<{ done: boolean; reason: string }> {
    if (!this.write) return { done: false, reason: "nothing is connected to this device" };
    if (IoTCompanionPipeline.GUARDED.includes(action.toLowerCase())) {
      if (!this.confirm) {
        // No confirmation route means the guarded action does not happen.
        // Falling through would be the worst possible default.
        return {
          done: false,
          reason: `${action} needs you to confirm, and there is no way to ask you right now`,
        };
      }
      if (!(await this.confirm(deviceId, action))) {
        return { done: false, reason: `${action} was not confirmed` };
      }
    }
    return { done: await this.write(deviceId, value ?? action), reason: "" };
  }
}
