// integration_calendar.test.ts
// Verifies the CircleAI.Integration.Calendar port: CalDAV REPORT + ICS
// parse/build, Google v3 list/create/delete, and MS Graph calendarView, all
// against a deterministic fake IHttpClient (no real network).

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import type { HttpRequest, HttpResponse, IHttpClient } from "../src/integration/index";
import { DateTimeOffsetMinValue, calendarEvent } from "../src/integration/index";
import {
  CalDavCalendarConnector,
  GoogleCalendarConnector,
  MsGraphCalendarConnector,
  calDavCalendarOptions,
  googleCalendarOptions,
  msGraphCalendarOptions,
} from "../src/integration/calendar/index";

// A programmable fake transport: each send returns the next queued response and
// records the request. A handler function can inspect the request.
class FakeHttp implements IHttpClient {
  readonly requests: HttpRequest[] = [];
  private handler: (r: HttpRequest) => HttpResponse;
  constructor(handler: (r: HttpRequest) => HttpResponse) {
    this.handler = handler;
  }
  send(request: HttpRequest): Promise<HttpResponse> {
    this.requests.push(request);
    return Promise.resolve(this.handler(request));
  }
}

const D = (s: string) => new Date(s);
const ok = (body: string): HttpResponse => ({ statusCode: 200, body });

describe("CalDavCalendarConnector", () => {
  const opts = calDavCalendarOptions("https://cal.example.com/dav/user/cal/", "alice", "app-pw");

  it("provider metadata + isConfigured", () => {
    const c = new CalDavCalendarConnector(opts, new FakeHttp(() => ok("")));
    assert.equal(c.providerId, "caldav");
    assert.equal(c.isConfigured, true);
    const c2 = new CalDavCalendarConnector(calDavCalendarOptions("https://x/", "", ""), new FakeHttp(() => ok("")));
    assert.equal(c2.isConfigured, false);
  });

  it("issues a REPORT with Basic auth + Depth:1 and a time-range filter", async () => {
    const http = new FakeHttp((r) => {
      // Return a multistatus with one VEVENT.
      assert.equal(r.method, "REPORT");
      assert.equal(r.headers.get("Depth"), "1");
      assert.ok(r.headers.get("Authorization")?.startsWith("Basic "));
      assert.ok(r.body?.includes('start="20260710T000000Z"'));
      const ics =
        "BEGIN:VCALENDAR\nBEGIN:VEVENT\nUID:evt-1\nSUMMARY:Standup\nDESCRIPTION:Daily sync\nLOCATION:Room 2\nDTSTART:20260710T090000Z\nDTEND:20260710T093000Z\nEND:VEVENT\nEND:VCALENDAR";
      return ok(
        `<?xml version="1.0"?><D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav"><D:response><D:propstat><D:prop><C:calendar-data>${ics
          .replace(/&/g, "&amp;")
          .replace(/</g, "&lt;")}</C:calendar-data></D:prop></D:propstat></D:response></D:multistatus>`,
      );
    });
    const c = new CalDavCalendarConnector(opts, http);
    const events = await c.listEventsAsync(D("2026-07-10T00:00:00Z"), D("2026-07-11T00:00:00Z"));
    assert.equal(events.length, 1);
    const e = events[0];
    assert.equal(e.eventId, "evt-1");
    assert.equal(e.title, "Standup");
    assert.equal(e.description, "Daily sync");
    assert.equal(e.location, "Room 2");
    assert.equal(e.startUtc.toISOString(), "2026-07-10T09:00:00.000Z");
    assert.equal(e.endUtc.toISOString(), "2026-07-10T09:30:00.000Z");
    assert.equal(e.isAllDay, false);
    assert.equal(e.calendarId, opts.calendarUri);
  });

  it("marks an event all-day when start/end are at UTC midnight", async () => {
    const http = new FakeHttp(() =>
      ok(
        `<multistatus xmlns:C="urn:ietf:params:xml:ns:caldav"><C:calendar-data>BEGIN:VEVENT\nUID:allday\nSUMMARY:Holiday\nDTSTART:20260710T000000Z\nDTEND:20260711T000000Z\nEND:VEVENT</C:calendar-data></multistatus>`,
      ),
    );
    const c = new CalDavCalendarConnector(opts, http);
    const [e] = await c.listEventsAsync(D("2026-07-01T00:00:00Z"), D("2026-08-01T00:00:00Z"));
    assert.equal(e.isAllDay, true);
    assert.equal(e.description, null); // absent DESCRIPTION → null
  });

  it("PUTs a new event with a generated UID and If-None-Match:* on create", async () => {
    let putUrl = "";
    let putBody = "";
    const http = new FakeHttp((r) => {
      if (r.method === "PUT") {
        putUrl = r.url;
        putBody = r.body ?? "";
        assert.equal(r.headers.get("If-None-Match"), "*");
      }
      return { statusCode: 201, body: "" };
    });
    const c = new CalDavCalendarConnector(opts, http);
    const created = await c.createEventAsync(
      calendarEvent("", opts.calendarUri, "New", "desc,with;chars", null, D("2026-07-10T10:00:00Z"), D("2026-07-10T11:00:00Z"), false, []),
    );
    assert.ok(created.eventId.length === 32); // Guid "N"
    assert.ok(putUrl.endsWith(created.eventId + ".ics"));
    assert.ok(putBody.includes("BEGIN:VCALENDAR"));
    assert.ok(putBody.includes(`UID:${created.eventId}`));
    assert.ok(putBody.includes("DTSTART:20260710T100000Z"));
    assert.ok(putBody.includes("SUMMARY:New"));
    assert.ok(putBody.includes("DESCRIPTION:desc\\,with\\;chars")); // ICS escaping
  });

  it("tolerates 404 on delete", async () => {
    const http = new FakeHttp(() => ({ statusCode: 404, body: "" }));
    const c = new CalDavCalendarConnector(opts, http);
    await c.deleteEventAsync(opts.calendarUri, "evt-1"); // must not throw
    assert.equal(http.requests[0].method, "DELETE");
    await assert.rejects(() => c.deleteEventAsync(opts.calendarUri, "  "));
  });
});

describe("GoogleCalendarConnector", () => {
  const mkOpts = (token: string | null) => googleCalendarOptions(async () => token, "primary");

  it("lists events, skipping cancelled, parsing dateTime + all-day date", async () => {
    const http = new FakeHttp((r) => {
      assert.ok(r.url.includes("calendars/primary/events"));
      assert.ok(r.url.includes("singleEvents=true"));
      assert.equal(r.headers.get("Authorization"), "Bearer tok");
      return ok(
        JSON.stringify({
          items: [
            { id: "cancelled1", status: "cancelled", summary: "gone" },
            {
              id: "g1",
              summary: "Meeting",
              description: "notes",
              location: "HQ",
              start: { dateTime: "2026-07-10T09:00:00+02:00" },
              end: { dateTime: "2026-07-10T10:00:00+02:00" },
              attendees: [{ email: "a@x" }, { email: "b@x" }],
            },
            { id: "g2", summary: "AllDay", start: { date: "2026-07-11" }, end: { date: "2026-07-12" } },
          ],
        }),
      );
    });
    const c = new GoogleCalendarConnector(mkOpts("tok"), http);
    const events = await c.listEventsAsync(D("2026-07-10T00:00:00Z"), D("2026-07-12T00:00:00Z"));
    assert.deepEqual(events.map((e) => e.eventId), ["g1", "g2"]);
    // +02:00 → UTC
    assert.equal(events[0].startUtc.toISOString(), "2026-07-10T07:00:00.000Z");
    assert.deepEqual(events[0].attendees, ["a@x", "b@x"]);
    assert.equal(events[1].isAllDay, true);
    assert.equal(events[1].startUtc.toISOString(), "2026-07-11T00:00:00.000Z");
  });

  it("throws when the token provider yields null", async () => {
    const c = new GoogleCalendarConnector(mkOpts(null), new FakeHttp(() => ok("{}")));
    await assert.rejects(() => c.listEventsAsync(D("2026-07-10T00:00:00Z"), D("2026-07-12T00:00:00Z")), /token unavailable/);
  });

  it("creates an event and returns the server id", async () => {
    const http = new FakeHttp((r) => {
      if (r.method === "POST") {
        const body = JSON.parse(r.body ?? "{}");
        assert.equal(body.summary, "New");
        assert.equal(body.start.timeZone, "UTC");
        return ok(JSON.stringify({ id: "srv-99" }));
      }
      return ok("{}");
    });
    const c = new GoogleCalendarConnector(mkOpts("tok"), http);
    const created = await c.createEventAsync(
      calendarEvent("", "primary", "New", null, null, D("2026-07-10T10:00:00Z"), D("2026-07-10T11:00:00Z"), false, []),
    );
    assert.equal(created.eventId, "srv-99");
  });

  it("tolerates 410 Gone on delete", async () => {
    const http = new FakeHttp(() => ({ statusCode: 410, body: "" }));
    const c = new GoogleCalendarConnector(mkOpts("tok"), http);
    await c.deleteEventAsync("primary", "g1"); // no throw
  });
});

describe("MsGraphCalendarConnector", () => {
  const mkOpts = (token: string | null) => msGraphCalendarOptions(async () => token, "primary");

  it("lists calendarView events with attendees + isAllDay + location displayName", async () => {
    const http = new FakeHttp((r) => {
      assert.ok(r.url.includes("me/calendar/calendarView"));
      return ok(
        JSON.stringify({
          value: [
            {
              id: "m1",
              subject: "Sync",
              bodyPreview: "prev",
              isAllDay: false,
              start: { dateTime: "2026-07-10T09:00:00.0000000" },
              end: { dateTime: "2026-07-10T09:30:00.0000000" },
              location: { displayName: "Room A" },
              attendees: [{ emailAddress: { address: "x@y" } }],
            },
          ],
        }),
      );
    });
    const c = new MsGraphCalendarConnector(mkOpts("t"), http);
    const [e] = await c.listEventsAsync(D("2026-07-10T00:00:00Z"), D("2026-07-11T00:00:00Z"));
    assert.equal(e.eventId, "m1");
    assert.equal(e.title, "Sync");
    assert.equal(e.description, "prev");
    assert.equal(e.location, "Room A");
    // zoneless dateTime is assumed UTC
    assert.equal(e.startUtc.toISOString(), "2026-07-10T09:00:00.000Z");
    assert.deepEqual(e.attendees, ["x@y"]);
  });

  it("returns MinValue when a Graph time is missing", async () => {
    const http = new FakeHttp(() => ok(JSON.stringify({ value: [{ id: "m2", subject: "NoTime" }] })));
    const c = new MsGraphCalendarConnector(mkOpts("t"), http);
    const [e] = await c.listEventsAsync(D("2026-07-10T00:00:00Z"), D("2026-07-11T00:00:00Z"));
    assert.equal(e.startUtc.getTime(), DateTimeOffsetMinValue.getTime());
  });

  it("creates via me/events and reads back the id", async () => {
    const http = new FakeHttp((r) => (r.method === "POST" ? ok(JSON.stringify({ id: "ms-1" })) : ok("{}")));
    const c = new MsGraphCalendarConnector(mkOpts("t"), http);
    const created = await c.createEventAsync(
      calendarEvent("", "primary", "T", "body", "Loc", D("2026-07-10T10:00:00Z"), D("2026-07-10T11:00:00Z"), false, ["a@b"]),
    );
    assert.equal(created.eventId, "ms-1");
    assert.ok(http.requests.some((q) => q.url.endsWith("me/events")));
  });
});
