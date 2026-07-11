// integration_homeassistant.test.ts
// Verifies the CircleAI.Integration.HomeAssistant port: entity listing (domain
// from entity_id, attribute flattening to strings, friendly_name), service
// calls, and the turn_on/turn_off convenience wrappers, against a fake IHttpClient.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import type { HttpRequest, HttpResponse, IHttpClient } from "../src/integration/index";
import { HomeAssistantConnector, homeAssistantOptions } from "../src/integration/homeassistant/index";

class FakeHttp implements IHttpClient {
  readonly requests: HttpRequest[] = [];
  constructor(private handler: (r: HttpRequest) => HttpResponse) {}
  send(request: HttpRequest): Promise<HttpResponse> {
    this.requests.push(request);
    return Promise.resolve(this.handler(request));
  }
}
const ok = (body: string): HttpResponse => ({ statusCode: 200, body });

const opts = homeAssistantOptions("http://ha.local:8123/", "llt-token");

describe("HomeAssistantConnector", () => {
  it("provider metadata + isConfigured", () => {
    const c = new HomeAssistantConnector(opts, new FakeHttp(() => ok("[]")));
    assert.equal(c.providerId, "home-assistant");
    assert.equal(c.isConfigured, true);
    assert.equal(new HomeAssistantConnector(homeAssistantOptions("http://x/", ""), new FakeHttp(() => ok("[]"))).isConfigured, false);
  });

  it("lists states: derives domain, flattens attributes, and reads friendly_name", async () => {
    const http = new FakeHttp((r) => {
      assert.ok(r.url.endsWith("/api/states"));
      assert.equal(r.headers.get("Authorization"), "Bearer llt-token");
      return ok(
        JSON.stringify([
          {
            entity_id: "light.kitchen",
            state: "on",
            attributes: { friendly_name: "Kitchen Light", brightness: 254, supported: true, disabled: false, list: [1, 2] },
          },
          { entity_id: "sensor.temp", state: "21.5", attributes: {} }, // no friendly_name → entity_id
          { entity_id: "", state: "x" }, // empty entity_id → skipped
        ]),
      );
    });
    const c = new HomeAssistantConnector(opts, http);
    const entities = await c.listEntitiesAsync();
    assert.equal(entities.length, 2);
    const light = entities[0];
    assert.equal(light.entityId, "light.kitchen");
    assert.equal(light.domain, "light");
    assert.equal(light.friendlyName, "Kitchen Light");
    assert.equal(light.state, "on");
    // attribute flattening: number → raw text, bool → "true"/"false", array → JSON
    assert.equal(light.attributes.get("brightness"), "254");
    assert.equal(light.attributes.get("supported"), "true");
    assert.equal(light.attributes.get("disabled"), "false");
    assert.equal(light.attributes.get("list"), "[1,2]");
    // no friendly_name → friendlyName defaults to the entity_id
    assert.equal(entities[1].friendlyName, "sensor.temp");
    assert.equal(entities[1].domain, "sensor");
  });

  it("returns empty when the states payload is not an array", async () => {
    const c = new HomeAssistantConnector(opts, new FakeHttp(() => ok("{}")));
    assert.deepEqual(await c.listEntitiesAsync(), []);
  });

  it("callService POSTs to api/services/<domain>/<service> with the data payload", async () => {
    let url = "";
    let body = "";
    const http = new FakeHttp((r) => {
      url = r.url;
      body = r.body ?? "";
      return ok("[]");
    });
    const c = new HomeAssistantConnector(opts, http);
    await c.callServiceAsync("light", "turn_on", new Map<string, unknown>([["entity_id", "light.kitchen"], ["brightness", 128]]));
    assert.ok(url.endsWith("/api/services/light/turn_on"));
    assert.deepEqual(JSON.parse(body), { entity_id: "light.kitchen", brightness: 128 });
  });

  it("callService sends an empty object when data is null and validates args", async () => {
    let body = "";
    const http = new FakeHttp((r) => {
      body = r.body ?? "";
      return ok("[]");
    });
    const c = new HomeAssistantConnector(opts, http);
    await c.callServiceAsync("homeassistant", "restart", null);
    assert.deepEqual(JSON.parse(body), {});
    await assert.rejects(() => c.callServiceAsync("", "s", null), /domain required/);
    await assert.rejects(() => c.callServiceAsync("d", "  ", null), /service required/);
  });

  it("turnOn/turnOff call homeassistant.turn_on/off with entity_id", async () => {
    const calls: Array<{ url: string; body: string }> = [];
    const http = new FakeHttp((r) => {
      calls.push({ url: r.url, body: r.body ?? "" });
      return ok("[]");
    });
    const c = new HomeAssistantConnector(opts, http);
    await c.turnOnAsync("switch.fan");
    await c.turnOffAsync("switch.fan");
    assert.ok(calls[0].url.endsWith("/api/services/homeassistant/turn_on"));
    assert.deepEqual(JSON.parse(calls[0].body), { entity_id: "switch.fan" });
    assert.ok(calls[1].url.endsWith("/api/services/homeassistant/turn_off"));
    assert.deepEqual(JSON.parse(calls[1].body), { entity_id: "switch.fan" });
  });
});
