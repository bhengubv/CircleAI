// integration_contracts.test.ts
// Verifies the CircleAI.Integration (Contracts.cs) port: record factories, the
// DateTimeOffset.MinValue sentinel, the IHttpClient transport helpers
// (ensureSuccess / isSuccessStatusCode / resolveUrl), and the string helpers.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  DateTimeOffsetMinValue,
  isMinValue,
  ensureSuccess,
  isSuccessStatusCode,
  resolveUrl,
  isNullOrWhiteSpace,
  isNullOrEmpty,
  HttpResponseError,
  calendarEvent,
  emailMessage,
  newsItem,
  weatherSample,
  routeEstimate,
  haEntity,
} from "../src/integration/index";

describe("CircleAI.Integration — DateTimeOffset.MinValue", () => {
  it("matches C# DateTimeOffset.MinValue epoch ms and isMinValue", () => {
    assert.equal(DateTimeOffsetMinValue.getTime(), -62135596800000);
    assert.equal(isMinValue(new Date(-62135596800000)), true);
    assert.equal(isMinValue(new Date("2026-01-01T00:00:00Z")), false);
  });
});

describe("CircleAI.Integration — HTTP transport helpers", () => {
  it("isSuccessStatusCode is true only for 2xx", () => {
    assert.equal(isSuccessStatusCode(200), true);
    assert.equal(isSuccessStatusCode(204), true);
    assert.equal(isSuccessStatusCode(299), true);
    assert.equal(isSuccessStatusCode(300), false);
    assert.equal(isSuccessStatusCode(404), false);
    assert.equal(isSuccessStatusCode(500), false);
  });

  it("ensureSuccess returns 2xx responses and throws HttpResponseError otherwise", () => {
    const ok = { statusCode: 200, body: "hi" };
    assert.equal(ensureSuccess(ok), ok);
    assert.throws(() => ensureSuccess({ statusCode: 404, body: "" }), HttpResponseError);
    try {
      ensureSuccess({ statusCode: 503, body: "" });
      assert.fail("expected throw");
    } catch (e) {
      assert.ok(e instanceof HttpResponseError);
      assert.equal(e.statusCode, 503);
    }
  });

  it("resolveUrl honors absolute URLs, relative combine, and leading-slash replace", () => {
    // absolute wins
    assert.equal(resolveUrl("https://base.example/x/", "https://other.example/y"), "https://other.example/y");
    // no base → return as-is
    assert.equal(resolveUrl(undefined, "me/events"), "me/events");
    // relative combine against a directory base
    assert.equal(resolveUrl("https://graph.example/v1.0/", "me/events"), "https://graph.example/v1.0/me/events");
    // base without trailing slash gets one appended
    assert.equal(resolveUrl("https://graph.example/v1.0", "me/events"), "https://graph.example/v1.0/me/events");
    // leading slash replaces the base path
    assert.equal(resolveUrl("https://cal.example/dav/user/cal/", "/foo"), "https://cal.example/foo");
    // .ics combine (CalDAV create)
    assert.equal(
      resolveUrl("https://cal.example/dav/user/cal/", "abc123.ics"),
      "https://cal.example/dav/user/cal/abc123.ics",
    );
  });
});

describe("CircleAI.Integration — string helpers", () => {
  it("isNullOrWhiteSpace / isNullOrEmpty match C# semantics", () => {
    assert.equal(isNullOrWhiteSpace(null), true);
    assert.equal(isNullOrWhiteSpace(undefined), true);
    assert.equal(isNullOrWhiteSpace("   "), true);
    assert.equal(isNullOrWhiteSpace(""), true);
    assert.equal(isNullOrWhiteSpace(" x "), false);
    assert.equal(isNullOrEmpty(""), true);
    assert.equal(isNullOrEmpty(null), true);
    assert.equal(isNullOrEmpty("   "), false); // whitespace is NOT empty
    assert.equal(isNullOrEmpty("x"), false);
  });
});

describe("CircleAI.Integration — record factories", () => {
  it("build every record positionally", () => {
    const ev = calendarEvent("e1", "c1", "T", "d", "L", new Date(0), new Date(1000), false, ["a@x"]);
    assert.equal(ev.eventId, "e1");
    assert.equal(ev.description, "d");
    assert.deepEqual(ev.attendees, ["a@x"]);

    const em = emailMessage("m1", "f@x", ["t@x"], "S", "B", new Date(0), true, ["INBOX"]);
    assert.equal(em.unread, true);
    assert.deepEqual(em.to, ["t@x"]);

    const ni = newsItem("i1", "s1", "T", "sum", "https://x/", new Date(0), ["tag"]);
    assert.equal(ni.url, "https://x/");
    assert.deepEqual(ni.tags, ["tag"]);

    const ws = weatherSample(new Date(0), 20, 19, 0.5, 12, 40, "rain");
    assert.equal(ws.condition, "rain");
    assert.equal(ws.windKph, 12);

    const re = routeEstimate(5.5, 600000, [{ lat: 1, lon: 2 }]);
    assert.equal(re.distanceKm, 5.5);
    assert.deepEqual(re.polyline[0], { lat: 1, lon: 2 });

    const ha = haEntity("light.k", "Kitchen", "light", "on", new Map([["brightness", "255"]]));
    assert.equal(ha.domain, "light");
    assert.equal(ha.attributes.get("brightness"), "255");
  });
});
