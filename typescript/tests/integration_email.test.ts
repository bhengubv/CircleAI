// integration_email.test.ts
// Verifies the CircleAI.Integration.Email port: Gmail v1 (list-then-get, MIME
// body extraction, base64url), MS Graph mail (unread/search/mark), and the IMAP
// connector over a fake IImapTransport (search → descending slice → fetch/parse).

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import type { HttpRequest, HttpResponse, IHttpClient } from "../src/integration/index";
import { DateTimeOffsetMinValue } from "../src/integration/index";
import {
  GmailEmailConnector,
  MsGraphEmailConnector,
  ImapEmailConnector,
  gmailOptions,
  msGraphEmailOptions,
  imapOptions,
  ImapFolderAccess,
  ImapMessageFlags,
  type IImapTransport,
  type ImapMessageSummary,
  type ImapSearchQuery,
} from "../src/integration/email/index";

class FakeHttp implements IHttpClient {
  readonly requests: HttpRequest[] = [];
  constructor(private handler: (r: HttpRequest) => HttpResponse) {}
  send(request: HttpRequest): Promise<HttpResponse> {
    this.requests.push(request);
    return Promise.resolve(this.handler(request));
  }
}

const ok = (body: string): HttpResponse => ({ statusCode: 200, body });
const b64url = (s: string) => Buffer.from(s, "utf8").toString("base64").replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");

describe("GmailEmailConnector", () => {
  const mk = (token: string | null) => new GmailEmailConnector(gmailOptions(async () => token), buildGmailHttp());

  function buildGmailHttp(): FakeHttp {
    return new FakeHttp((r) => {
      if (r.url.includes("messages?q=")) {
        return ok(JSON.stringify({ messages: [{ id: "1" }, { id: "2" }] }));
      }
      if (r.url.includes("messages/1")) {
        return ok(
          JSON.stringify({
            id: "1",
            labelIds: ["INBOX", "UNREAD"],
            internalDate: "1720000000000",
            payload: {
              headers: [
                { name: "From", value: "sender@x" },
                { name: "To", value: "a@x, b@x" },
                { name: "Subject", value: "Hello" },
              ],
              parts: [
                { mimeType: "text/html", body: { data: b64url("<b>hi</b>") } },
                { mimeType: "text/plain", body: { data: b64url("plain body") } },
              ],
            },
          }),
        );
      }
      // message 2: simple body, read
      return ok(
        JSON.stringify({
          id: "2",
          labelIds: ["INBOX"],
          internalDate: "1720000001000",
          payload: { headers: [{ name: "Subject", value: "Two" }], body: { data: b64url("direct") } },
        }),
      );
    });
  }

  it("provider metadata", () => {
    assert.equal(mk("t").providerId, "gmail");
    assert.equal(mk("t").isConfigured, true);
  });

  it("list-unread searches is:unread then fetches each message full", async () => {
    const http = buildGmailHttp();
    const c = new GmailEmailConnector(gmailOptions(async () => "tok"), http);
    const msgs = await c.listUnreadAsync(10);
    assert.deepEqual(msgs.map((m) => m.messageId), ["1", "2"]);
    // first message: prefers text/plain part, parses headers, UNREAD label
    assert.equal(msgs[0].from, "sender@x");
    assert.deepEqual(msgs[0].to, ["a@x", "b@x"]);
    assert.equal(msgs[0].subject, "Hello");
    assert.equal(msgs[0].bodyText, "plain body");
    assert.equal(msgs[0].unread, true);
    assert.deepEqual(msgs[0].labels, ["INBOX", "UNREAD"]);
    assert.equal(msgs[0].receivedUtc.toISOString(), new Date(1720000000000).toISOString());
    // second: direct body, read
    assert.equal(msgs[1].bodyText, "direct");
    assert.equal(msgs[1].unread, false);
    // the first outbound request was the is:unread search
    assert.ok(http.requests[0].url.includes("q=is%3Aunread"));
  });

  it("validates args and token", async () => {
    const c = new GmailEmailConnector(gmailOptions(async () => "tok"), buildGmailHttp());
    await assert.rejects(() => c.searchAsync("   ", 5), /query required/);
    await assert.rejects(() => c.searchAsync("x", 0), /max/);
    const cNull = new GmailEmailConnector(gmailOptions(async () => null), buildGmailHttp());
    await assert.rejects(() => cNull.listUnreadAsync(5), /token unavailable/);
  });

  it("mark-read POSTs removeLabelIds UNREAD", async () => {
    let body = "";
    const http = new FakeHttp((r) => {
      body = r.body ?? "";
      return ok("{}");
    });
    const c = new GmailEmailConnector(gmailOptions(async () => "tok"), http);
    await c.markReadAsync("1");
    assert.deepEqual(JSON.parse(body), { removeLabelIds: ["UNREAD"] });
    await assert.rejects(() => c.markReadAsync(""), /messageId required/);
  });
});

describe("MsGraphEmailConnector", () => {
  it("reads unread messages with from/to/labels/isRead mapping", async () => {
    const http = new FakeHttp((r) => {
      assert.ok(r.url.includes("mailFolders('Inbox')"));
      return ok(
        JSON.stringify({
          value: [
            {
              id: "g1",
              subject: "Sub",
              from: { emailAddress: { address: "from@x" } },
              toRecipients: [{ emailAddress: { address: "to@x" } }],
              receivedDateTime: "2026-07-10T12:00:00Z",
              isRead: false,
              categories: ["Work"],
              body: { content: "html body" },
            },
          ],
        }),
      );
    });
    const c = new MsGraphEmailConnector(msGraphEmailOptions(async () => "t"), http);
    const [m] = await c.listUnreadAsync(10);
    assert.equal(m.from, "from@x");
    assert.deepEqual(m.to, ["to@x"]);
    assert.equal(m.subject, "Sub");
    assert.equal(m.bodyText, "html body");
    assert.equal(m.unread, true); // isRead:false → unread
    assert.deepEqual(m.labels, ["Work"]);
    assert.equal(m.receivedUtc.toISOString(), "2026-07-10T12:00:00.000Z");
  });

  it("falls back to bodyPreview and MinValue date, and read when isRead true", async () => {
    const http = new FakeHttp(() =>
      ok(JSON.stringify({ value: [{ id: "g2", subject: "S", bodyPreview: "prev", isRead: true }] })),
    );
    const c = new MsGraphEmailConnector(msGraphEmailOptions(async () => "t"), http);
    const [m] = await c.searchAsync("x", 5);
    assert.equal(m.bodyText, "prev");
    assert.equal(m.unread, false);
    assert.equal(m.receivedUtc.getTime(), DateTimeOffsetMinValue.getTime());
  });

  it("mark-read PATCHes isRead:true", async () => {
    let method = "";
    let body = "";
    const http = new FakeHttp((r) => {
      method = r.method;
      body = r.body ?? "";
      return ok("{}");
    });
    const c = new MsGraphEmailConnector(msGraphEmailOptions(async () => "t"), http);
    await c.markReadAsync("g1");
    assert.equal(method, "PATCH");
    assert.deepEqual(JSON.parse(body), { isRead: true });
  });
});

// ── IMAP over a fake transport ────────────────────────────────────────────────

class FakeImap implements IImapTransport {
  connected = false;
  authed = false;
  disconnected = false;
  seenFlagged: number[] = [];
  lastAccess: ImapFolderAccess | null = null;
  lastQuery: ImapSearchQuery | null = null;
  constructor(private store: ImapMessageSummary[]) {}
  connectAsync(): Promise<void> {
    this.connected = true;
    return Promise.resolve();
  }
  authenticateAsync(): Promise<void> {
    this.authed = true;
    return Promise.resolve();
  }
  searchAsync(_folder: string, access: ImapFolderAccess, query: ImapSearchQuery): Promise<readonly number[]> {
    this.lastAccess = access;
    this.lastQuery = query;
    let hits = this.store;
    if (query.kind === "not-seen") hits = hits.filter((m) => ((m.flags ?? 0) & ImapMessageFlags.Seen) === 0);
    else hits = hits.filter((m) => m.subject.includes(query.text) || (m.body ?? "").includes(query.text));
    return Promise.resolve(hits.map((m) => m.uid));
  }
  fetchAsync(_folder: string, uids: readonly number[]): Promise<readonly ImapMessageSummary[]> {
    // Return in the exact order requested (already sliced/ordered by the connector).
    const byId = new Map(this.store.map((m) => [m.uid, m]));
    return Promise.resolve(uids.map((u) => byId.get(u)!).filter(Boolean));
  }
  addSeenFlagAsync(_folder: string, uid: number): Promise<void> {
    this.seenFlagged.push(uid);
    return Promise.resolve();
  }
  disconnectAsync(): Promise<void> {
    this.disconnected = true;
    return Promise.resolve();
  }
}

describe("ImapEmailConnector", () => {
  const opts = imapOptions("imap.example.com", 993, true, "alice", "pw", "INBOX");
  const store: ImapMessageSummary[] = [
    { uid: 5, from: ["s5@x"], to: ["a@x"], subject: "alpha", date: new Date("2026-07-01T00:00:00Z"), flags: ImapMessageFlags.Recent, body: "hello alpha" },
    { uid: 9, from: ["s9@x"], to: ["b@x", "c@x"], subject: "beta", date: new Date("2026-07-02T00:00:00Z"), flags: ImapMessageFlags.Seen | ImapMessageFlags.Flagged, body: "beta body" },
    { uid: 7, from: ["s7@x"], to: [], subject: "gamma alpha", date: null, flags: null, body: null },
  ];

  it("provider metadata + isConfigured", () => {
    const c = new ImapEmailConnector(opts, new FakeImap([]));
    assert.equal(c.providerId, "imap");
    assert.equal(c.isConfigured, true);
    assert.equal(new ImapEmailConnector(imapOptions("", 993, true, "", ""), new FakeImap([])).isConfigured, false);
  });

  it("list-unread searches NotSeen read-only, slices descending by uid, and maps fields", async () => {
    const t = new FakeImap(store);
    const c = new ImapEmailConnector(opts, t);
    const msgs = await c.listUnreadAsync(10);
    assert.equal(t.lastAccess, ImapFolderAccess.ReadOnly);
    assert.equal(t.lastQuery?.kind, "not-seen");
    // uids 5 and 7 are not-seen (9 is Seen). Descending: 7, 5.
    assert.deepEqual(msgs.map((m) => m.messageId), ["7", "5"]);
    // uid 7 has null flags → unread stays false (C#: Flags.HasValue && …)
    assert.equal(msgs[0].unread, false);
    assert.deepEqual(msgs[0].to, []);
    assert.equal(msgs[0].bodyText, "");
    // uid 5: Recent flag → label "Recent", not seen → unread true
    assert.equal(msgs[1].unread, true);
    assert.deepEqual(msgs[1].labels, ["Recent"]);
    assert.equal(msgs[1].from, "s5@x");
    assert.ok(t.disconnected);
  });

  it("search matches subject/body, respects max, and derives flag labels", async () => {
    const t = new FakeImap(store);
    const c = new ImapEmailConnector(opts, t);
    const msgs = await c.searchAsync("alpha", 1); // matches uid5 (subj+body) & uid7 (subj); desc→7 first, max 1
    assert.deepEqual(msgs.map((m) => m.messageId), ["7"]);
    // Verify flag decoding on the Seen|Flagged message via a direct fetch path.
    const all = await c.searchAsync("beta", 10);
    assert.deepEqual(all[0].labels, ["Seen", "Flagged"]);
    assert.equal(all[0].unread, false); // Seen set
  });

  it("mark-read requires a numeric UID and flags Seen", async () => {
    const t = new FakeImap(store);
    const c = new ImapEmailConnector(opts, t);
    await c.markReadAsync("9");
    assert.deepEqual(t.seenFlagged, [9]);
    await assert.rejects(() => c.markReadAsync("not-a-uid"), /IMAP UID/);
    await assert.rejects(() => c.markReadAsync(""), /messageId required/);
  });
});
