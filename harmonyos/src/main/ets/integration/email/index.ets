/**
 * integration/email/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// integration/email/index.ts
// Full-parity port of CircleAI.Integration.Email (C#). C# is the exact spec.
//
// Three IEmailConnector implementations:
//   GmailEmailConnector   — Gmail API v1 over a host-supplied bearer.
//   MsGraphEmailConnector — Microsoft Graph v1.0 mail over a bearer.
//   ImapEmailConnector    — generic IMAP. The C# is backed by MailKit (a native
//     IMAP socket client); per the coordinator's "inject native/socket deps
//     behind interfaces" rule we port the connector logic over an injected
//     `IImapTransport` (the exact slice of MailKit the connector calls) so the
//     search/slice/parse behaviour is faithful with no real socket.
//
// The Gmail/Graph connectors take the injected `IHttpClient` transport.

import {
  type EmailMessage,
  type IEmailConnector,
  type IHttpClient,
  type HttpResponse,
  emailMessage,
  ensureSuccess,
  isSuccessStatusCode,
  isNullOrWhiteSpace,
  isNullOrEmpty,
  DateTimeOffsetMinValue,
} from "../index";
import { tryParseDate } from "../calendar/index";

// ── Gmail API v1 ──────────────────────────────────────────────────────────────

/** Options for {@link GmailEmailConnector}. Mirrors C# `GmailOptions`. */
export interface GmailOptions {
  readonly accessTokenProvider: (ct?: AbortSignal) => Promise<string | null>;
}

/** Constructs {@link GmailOptions}. */
export function gmailOptions(accessTokenProvider: (ct?: AbortSignal) => Promise<string | null>): GmailOptions {
  return { accessTokenProvider };
}

const GMAIL_BASE = "https://gmail.googleapis.com/gmail/v1/users/me/";

/** Gmail API v1 client. Faithful port of C# `GmailEmailConnector`. */
export class GmailEmailConnector implements IEmailConnector {
  private readonly http: IHttpClient;
  private readonly opts: GmailOptions;

  constructor(opts: GmailOptions, http: IHttpClient) {
    if (opts == null) throw new Error("opts required");
    if (http == null) throw new Error("http required");
    this.opts = opts;
    this.http = http;
  }

  get providerId(): string {
    return "gmail";
  }
  get isConfigured(): boolean {
    return this.opts.accessTokenProvider != null;
  }

  listUnreadAsync(max: number, ct?: AbortSignal): Promise<readonly EmailMessage[]> {
    return this.searchAsync("is:unread", max, ct);
  }

  async searchAsync(query: string, max: number, ct?: AbortSignal): Promise<readonly EmailMessage[]> {
    if (isNullOrWhiteSpace(query)) throw new Error("query required");
    if (max <= 0) throw new Error("max");
    const token = await this.ensureAuth(ct);

    const listPath = `messages?q=${encodeURIComponent(query)}&maxResults=${Math.min(max, 100)}`;
    const listResp = ensureSuccess(await this.get(GMAIL_BASE + listPath, token, ct));
    const listRoot = JSON.parse(listResp.body) as Record<string, unknown>;

    const ids: string[] = [];
    const msgs = listRoot["messages"];
    if (Array.isArray(msgs)) {
      for (const mUnknown of msgs) {
        const m = mUnknown as Record<string, unknown>;
        if ("id" in m) ids.push(typeof m["id"] === "string" ? (m["id"] as string) : "");
      }
    }

    const result: EmailMessage[] = [];
    for (const id of ids) {
      const getResp = await this.get(GMAIL_BASE + `messages/${encodeURIComponent(id)}?format=full`, token, ct);
      if (!isSuccessStatusCode(getResp.statusCode)) continue;
      const root = JSON.parse(getResp.body) as Record<string, unknown>;
      result.push(parseGmailMessage(root));
    }
    return result;
  }

  async markReadAsync(messageId: string, ct?: AbortSignal): Promise<void> {
    if (isNullOrWhiteSpace(messageId)) throw new Error("messageId required");
    const token = await this.ensureAuth(ct);
    const resp = await this.http.send(
      {
        method: "POST",
        url: GMAIL_BASE + `messages/${encodeURIComponent(messageId)}/modify`,
        headers: new Map([
          ["Authorization", `Bearer ${token}`],
          ["Content-Type", "application/json; charset=utf-8"],
        ]),
        body: JSON.stringify({ removeLabelIds: ["UNREAD"] }),
      },
      ct,
    );
    ensureSuccess(resp);
  }

  private async ensureAuth(ct?: AbortSignal): Promise<string> {
    const token = await this.opts.accessTokenProvider(ct);
    if (isNullOrWhiteSpace(token)) throw new Error("Gmail access token unavailable; refresh OAuth.");
    return token as string;
  }

  private get(url: string, token: string, ct?: AbortSignal): Promise<HttpResponse> {
    return this.http.send({ method: "GET", url, headers: new Map([["Authorization", `Bearer ${token}`]]) }, ct);
  }
}

/** C# `GmailEmailConnector.ParseGmailMessage`. */
function parseGmailMessage(msg: Record<string, unknown>): EmailMessage {
  const id = typeof msg["id"] === "string" ? (msg["id"] as string) : "";
  const labels: string[] = [];
  const labs = msg["labelIds"];
  if (Array.isArray(labs)) for (const l of labs) labels.push(typeof l === "string" ? l : "");
  const unread = labels.some((l) => l.toUpperCase() === "UNREAD");

  // Case-insensitive header map (StringComparer.OrdinalIgnoreCase).
  const headers = new Map<string, string>();
  const payload = msg["payload"];
  if (payload != null && typeof payload === "object") {
    const hs = (payload as Record<string, unknown>)["headers"];
    if (Array.isArray(hs)) {
      for (const hUnknown of hs) {
        const h = hUnknown as Record<string, unknown>;
        if ("name" in h && "value" in h) {
          const name = typeof h["name"] === "string" ? (h["name"] as string) : "";
          const val = typeof h["value"] === "string" ? (h["value"] as string) : "";
          headers.set(name.toLowerCase(), val);
        }
      }
    }
  }
  const getHeader = (name: string): string | undefined => headers.get(name.toLowerCase());

  const bodyText = extractBody(payload != null && typeof payload === "object" ? (payload as Record<string, unknown>) : undefined);
  const internal = msg["internalDate"];
  let receivedMs = 0;
  if (typeof internal === "string") {
    const parsed = Number.parseInt(internal, 10);
    if (!Number.isNaN(parsed) && /^-?\d+$/.test(internal.trim())) receivedMs = parsed;
  }
  const from = getHeader("From") ?? "";
  const toRaw = getHeader("To");
  const to = toRaw !== undefined
    ? toRaw.split(",").map((x) => x.trim()).filter((x) => x.length > 0)
    : [];
  const subject = getHeader("Subject") ?? "";
  return emailMessage(id, from, to, subject, bodyText, new Date(receivedMs), unread, labels);
}

/** C# `GmailEmailConnector.ExtractBody` — walks the MIME tree, preferring text/plain. */
function extractBody(payload: Record<string, unknown> | undefined): string {
  if (payload === undefined) return "";
  const body = payload["body"];
  if (body != null && typeof body === "object") {
    const data = (body as Record<string, unknown>)["data"];
    if (typeof data === "string") return decodeBase64Url(data);
  }
  const parts = payload["parts"];
  if (Array.isArray(parts)) {
    for (const partUnknown of parts) {
      const part = partUnknown as Record<string, unknown>;
      const mime = typeof part["mimeType"] === "string" ? (part["mimeType"] as string) : null;
      if (mime !== null && mime.toLowerCase() === "text/plain") return extractBody(part);
    }
    for (const partUnknown of parts) {
      const content = extractBody(partUnknown as Record<string, unknown>);
      if (!isNullOrEmpty(content)) return content;
    }
  }
  return "";
}

/** C# `GmailEmailConnector.DecodeBase64Url`. */
function decodeBase64Url(s: string): string {
  if (isNullOrEmpty(s)) return "";
  let x = s.replace(/-/g, "+").replace(/_/g, "/");
  const padding = x.length % 4;
  if (padding > 0) x = x.padEnd(x.length + 4 - padding, "=");
  try {
    return Buffer.from(x, "base64").toString("utf8");
  } catch {
    return "";
  }
}

// ── Microsoft Graph v1.0 mail ─────────────────────────────────────────────────

/** Options for {@link MsGraphEmailConnector}. Mirrors C# `MsGraphEmailOptions`. */
export interface MsGraphEmailOptions {
  readonly accessTokenProvider: (ct?: AbortSignal) => Promise<string | null>;
}

/** Constructs {@link MsGraphEmailOptions}. */
export function msGraphEmailOptions(
  accessTokenProvider: (ct?: AbortSignal) => Promise<string | null>,
): MsGraphEmailOptions {
  return { accessTokenProvider };
}

const MSGRAPH_MAIL_BASE = "https://graph.microsoft.com/v1.0/";

/** Microsoft Graph v1.0 mail client. Faithful port of C# `MsGraphEmailConnector`. */
export class MsGraphEmailConnector implements IEmailConnector {
  private readonly http: IHttpClient;
  private readonly opts: MsGraphEmailOptions;

  constructor(opts: MsGraphEmailOptions, http: IHttpClient) {
    if (opts == null) throw new Error("opts required");
    if (http == null) throw new Error("http required");
    this.opts = opts;
    this.http = http;
  }

  get providerId(): string {
    return "ms-graph-mail";
  }
  get isConfigured(): boolean {
    return this.opts.accessTokenProvider != null;
  }

  async listUnreadAsync(max: number, ct?: AbortSignal): Promise<readonly EmailMessage[]> {
    const token = await this.ensureAuth(ct);
    const path = `me/mailFolders('Inbox')/messages?$filter=isRead+eq+false&$top=${Math.min(max, 50)}&$orderby=receivedDateTime+desc`;
    const resp = ensureSuccess(await this.get(MSGRAPH_MAIL_BASE + path, token, ct));
    return readMessages(JSON.parse(resp.body) as Record<string, unknown>);
  }

  async searchAsync(query: string, max: number, ct?: AbortSignal): Promise<readonly EmailMessage[]> {
    if (isNullOrWhiteSpace(query)) throw new Error("query required");
    const token = await this.ensureAuth(ct);
    const path = `me/messages?$search=${encodeURIComponent(query)}&$top=${Math.min(max, 50)}`;
    const resp = ensureSuccess(await this.get(MSGRAPH_MAIL_BASE + path, token, ct));
    return readMessages(JSON.parse(resp.body) as Record<string, unknown>);
  }

  async markReadAsync(messageId: string, ct?: AbortSignal): Promise<void> {
    if (isNullOrWhiteSpace(messageId)) throw new Error("messageId required");
    const token = await this.ensureAuth(ct);
    const resp = await this.http.send(
      {
        method: "PATCH",
        url: MSGRAPH_MAIL_BASE + `me/messages/${encodeURIComponent(messageId)}`,
        headers: new Map([
          ["Authorization", `Bearer ${token}`],
          ["Content-Type", "application/json; charset=utf-8"],
        ]),
        body: JSON.stringify({ isRead: true }),
      },
      ct,
    );
    ensureSuccess(resp);
  }

  private async ensureAuth(ct?: AbortSignal): Promise<string> {
    const token = await this.opts.accessTokenProvider(ct);
    if (isNullOrWhiteSpace(token)) throw new Error("Microsoft Graph access token unavailable; refresh OAuth.");
    return token as string;
  }

  private get(url: string, token: string, ct?: AbortSignal): Promise<HttpResponse> {
    return this.http.send({ method: "GET", url, headers: new Map([["Authorization", `Bearer ${token}`]]) }, ct);
  }
}

/** C# `MsGraphEmailConnector.ReadMessages`. */
function readMessages(root: Record<string, unknown>): readonly EmailMessage[] {
  const list: EmailMessage[] = [];
  const arr = root["value"];
  if (!Array.isArray(arr)) return list;
  for (const mUnknown of arr) {
    const m = mUnknown as Record<string, unknown>;
    const to: string[] = [];
    const rcpts = m["toRecipients"];
    if (Array.isArray(rcpts)) {
      for (const rUnknown of rcpts) {
        const r = rUnknown as Record<string, unknown>;
        const ea = r["emailAddress"];
        if (ea != null && typeof ea === "object" && "address" in (ea as object)) {
          const addr = (ea as Record<string, unknown>)["address"];
          to.push(typeof addr === "string" ? addr : "");
        }
      }
    }
    let fromAddr = "";
    const fr = m["from"];
    if (fr != null && typeof fr === "object") {
      const fea = (fr as Record<string, unknown>)["emailAddress"];
      if (fea != null && typeof fea === "object" && "address" in (fea as object)) {
        const a = (fea as Record<string, unknown>)["address"];
        fromAddr = typeof a === "string" ? a : "";
      }
    }
    let received = DateTimeOffsetMinValue;
    const rd = m["receivedDateTime"];
    if (typeof rd === "string") {
      const dto = tryParseDate(rd);
      if (dto !== null) received = dto;
    }
    const labels: string[] = [];
    const cats = m["categories"];
    if (Array.isArray(cats)) for (const c of cats) labels.push(typeof c === "string" ? c : "");
    let body = "";
    const b = m["body"];
    if (b != null && typeof b === "object" && "content" in (b as object)) {
      const bc = (b as Record<string, unknown>)["content"];
      body = typeof bc === "string" ? bc : "";
    } else if (typeof m["bodyPreview"] === "string") {
      body = m["bodyPreview"] as string;
    }
    list.push(
      emailMessage(
        typeof m["id"] === "string" ? (m["id"] as string) : "",
        fromAddr,
        to,
        typeof m["subject"] === "string" ? (m["subject"] as string) : "",
        body,
        received,
        m["isRead"] === false,
        labels,
      ),
    );
  }
  return list;
}

// ── IMAP (over an injected transport) ─────────────────────────────────────────

/** IMAP message flags. Mirrors MailKit `MessageFlags` (the values the C# reads). */
export enum ImapMessageFlags {
  None = 0,
  Seen = 1,
  Answered = 2,
  Flagged = 4,
  Deleted = 8,
  Draft = 16,
  Recent = 32,
}

/** All non-None flag values, in MailKit's `Enum.GetValues<MessageFlags>()` order. */
const IMAP_FLAG_VALUES: ReadonlyArray<[ImapMessageFlags, string]> = [
  [ImapMessageFlags.Seen, "Seen"],
  [ImapMessageFlags.Answered, "Answered"],
  [ImapMessageFlags.Flagged, "Flagged"],
  [ImapMessageFlags.Deleted, "Deleted"],
  [ImapMessageFlags.Draft, "Draft"],
  [ImapMessageFlags.Recent, "Recent"],
];

/** A fetched message summary. Mirrors the MailKit `IMessageSummary` fields the C# reads. */
export interface ImapMessageSummary {
  /** IMAP UID (MailKit `UniqueId.Id`). */
  readonly uid: number;
  readonly from: readonly string[];
  readonly to: readonly string[];
  readonly subject: string;
  /** Envelope date as a UTC instant, or null when absent. */
  readonly date: Date | null;
  /** Flags bitmask, or null when the server returned no flags. */
  readonly flags: number | null;
  /** The full text/HTML body MailKit would surface via GetMessage → TextBody/HtmlBody. */
  readonly body: string | null;
}

/** Which folder-open access MailKit uses (ReadOnly for list/search, ReadWrite to flag). */
export enum ImapFolderAccess {
  ReadOnly = 0,
  ReadWrite = 1,
}

/** IMAP search kinds the connector issues (MailKit `SearchQuery`). */
export type ImapSearchQuery =
  | { readonly kind: "not-seen" }
  | { readonly kind: "body-or-subject-contains"; readonly text: string };

/**
 * The injected IMAP transport — the exact slice of MailKit the connector uses.
 * A production adapter wraps `MailKit.Net.Imap.ImapClient`; tests inject a fake.
 * Semantics mirror the C#: connect → open folder (by access) → search → fetch.
 */
export interface IImapTransport {
  connectAsync(host: string, port: number, useSsl: boolean, ct?: AbortSignal): Promise<void>;
  authenticateAsync(username: string, password: string, ct?: AbortSignal): Promise<void>;
  /** Opens the folder (INBOX or named) with the given access; returns UIDs matching `query`. */
  searchAsync(folder: string, access: ImapFolderAccess, query: ImapSearchQuery, ct?: AbortSignal): Promise<readonly number[]>;
  /** Fetches envelope+flags+body summaries for the given UIDs (already sliced/ordered). */
  fetchAsync(folder: string, uids: readonly number[], ct?: AbortSignal): Promise<readonly ImapMessageSummary[]>;
  /** Adds the Seen flag to `uid` in the (ReadWrite) folder. */
  addSeenFlagAsync(folder: string, uid: number, ct?: AbortSignal): Promise<void>;
  disconnectAsync(quit: boolean, ct?: AbortSignal): Promise<void>;
}

/** Options for {@link ImapEmailConnector}. Mirrors C# `ImapOptions`. */
export interface ImapOptions {
  readonly host: string;
  readonly port: number;
  readonly useSsl: boolean;
  readonly username: string;
  readonly password: string;
  /** Folder to read. Default INBOX. */
  readonly folder: string;
}

/** Constructs {@link ImapOptions} (default folder "INBOX"). */
export function imapOptions(
  host: string,
  port: number,
  useSsl: boolean,
  username: string,
  password: string,
  folder = "INBOX",
): ImapOptions {
  return { host, port, useSsl, username, password, folder };
}

/**
 * Generic IMAP client. Faithful port of C# `ImapEmailConnector` — the MailKit
 * socket work is delegated to the injected {@link IImapTransport}, but the
 * search/slice/fetch/parse orchestration is ported verbatim.
 */
export class ImapEmailConnector implements IEmailConnector {
  private readonly opts: ImapOptions;
  private readonly transport: IImapTransport;

  constructor(opts: ImapOptions, transport: IImapTransport) {
    if (opts == null) throw new Error("opts required");
    if (transport == null) throw new Error("transport required");
    this.opts = opts;
    this.transport = transport;
  }

  get providerId(): string {
    return "imap";
  }
  get isConfigured(): boolean {
    return (
      !isNullOrWhiteSpace(this.opts.host) &&
      !isNullOrWhiteSpace(this.opts.username) &&
      !isNullOrWhiteSpace(this.opts.password)
    );
  }

  async listUnreadAsync(max: number, ct?: AbortSignal): Promise<readonly EmailMessage[]> {
    if (max <= 0) throw new Error("max");
    await this.connect(ct);
    const uids = await this.transport.searchAsync(this.opts.folder, ImapFolderAccess.ReadOnly, { kind: "not-seen" }, ct);
    // C#: uids.OrderByDescending(u => u.Id).Take(max)
    const slice = [...uids].sort((a, b) => b - a).slice(0, max);
    const result = await this.fetch(slice, ct);
    await this.transport.disconnectAsync(true, ct);
    return result;
  }

  async searchAsync(query: string, max: number, ct?: AbortSignal): Promise<readonly EmailMessage[]> {
    if (isNullOrWhiteSpace(query)) throw new Error("query required");
    if (max <= 0) throw new Error("max");
    await this.connect(ct);
    const uids = await this.transport.searchAsync(
      this.opts.folder,
      ImapFolderAccess.ReadOnly,
      { kind: "body-or-subject-contains", text: query },
      ct,
    );
    const slice = [...uids].sort((a, b) => b - a).slice(0, max);
    const result = await this.fetch(slice, ct);
    await this.transport.disconnectAsync(true, ct);
    return result;
  }

  async markReadAsync(messageId: string, ct?: AbortSignal): Promise<void> {
    if (isNullOrWhiteSpace(messageId)) throw new Error("messageId required");
    // C#: uint.TryParse(messageId) — expects an IMAP UID.
    if (!/^\d+$/.test(messageId.trim())) throw new Error("Expected an IMAP UID");
    const raw = Number.parseInt(messageId, 10);
    if (!Number.isFinite(raw) || raw < 0 || raw > 0xffffffff) throw new Error("Expected an IMAP UID");
    await this.connect(ct);
    await this.transport.addSeenFlagAsync(this.opts.folder, raw, ct);
    await this.transport.disconnectAsync(true, ct);
  }

  private async connect(ct?: AbortSignal): Promise<void> {
    await this.transport.connectAsync(this.opts.host, this.opts.port, this.opts.useSsl, ct);
    await this.transport.authenticateAsync(this.opts.username, this.opts.password, ct);
  }

  private async fetch(uids: readonly number[], ct?: AbortSignal): Promise<readonly EmailMessage[]> {
    const messages: EmailMessage[] = [];
    if (uids.length === 0) return messages;
    const summaries = await this.transport.fetchAsync(this.opts.folder, uids, ct);
    for (const summary of summaries) {
      const labels: string[] = [];
      if (summary.flags !== null) {
        for (const [flag, name] of IMAP_FLAG_VALUES) {
          if ((summary.flags & flag) === flag) labels.push(name);
        }
      }
      const bodyText = summary.body ?? "";
      messages.push(
        emailMessage(
          String(summary.uid),
          summary.from.length > 0 ? summary.from[0] : "",
          [...summary.to],
          summary.subject ?? "",
          bodyText,
          summary.date ?? new Date(),
          summary.flags !== null && (summary.flags & ImapMessageFlags.Seen) === 0,
          labels,
        ),
      );
    }
    return messages;
  }
}

export { isSuccessStatusCode };
