// integration_geo.test.ts
// Verifies the CircleAI.Integration.Geo port: Open-Meteo current/hourly (WMO
// decode, m/s→km/h wind, hour clamping) and OSRM routing (mode→profile mapping,
// GeoJSON polyline lat/lon swap, distance/duration conversion), against a fake
// IHttpClient.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import type { HttpRequest, HttpResponse, IHttpClient } from "../src/integration/index";
import {
  OpenMeteoWeatherProvider,
  OsrmRoutingProvider,
  osrmOptions,
} from "../src/integration/geo/index";

class FakeHttp implements IHttpClient {
  readonly requests: HttpRequest[] = [];
  constructor(private handler: (r: HttpRequest) => HttpResponse) {}
  send(request: HttpRequest): Promise<HttpResponse> {
    this.requests.push(request);
    return Promise.resolve(this.handler(request));
  }
}
const ok = (body: string): HttpResponse => ({ statusCode: 200, body });

describe("OpenMeteoWeatherProvider", () => {
  it("provider id", () => {
    assert.equal(new OpenMeteoWeatherProvider(new FakeHttp(() => ok("{}"))).providerId, "open-meteo");
  });

  it("current: decodes WMO code and converts wind m/s→km/h", async () => {
    const http = new FakeHttp((r) => {
      assert.ok(r.url.includes("latitude=-33.9"));
      assert.ok(r.url.includes("current=temperature_2m"));
      return ok(
        JSON.stringify({
          current: {
            time: "2026-07-10T12:00",
            temperature_2m: 18.5,
            apparent_temperature: 17.2,
            precipitation: 0.4,
            wind_speed_10m: 10, // m/s
            cloud_cover: 75,
            weather_code: 61, // rain
          },
        }),
      );
    });
    const p = new OpenMeteoWeatherProvider(http);
    const s = await p.currentAsync(-33.9, 18.4);
    assert.equal(s.tempC, 18.5);
    assert.equal(s.feelsLikeC, 17.2);
    assert.equal(s.precipMm, 0.4);
    assert.equal(s.windKph, 36); // 10 * 3.6
    assert.equal(s.cloudPct, 75);
    assert.equal(s.condition, "rain");
    // zoneless "2026-07-10T12:00" assumed UTC
    assert.equal(s.atUtc.toISOString(), "2026-07-10T12:00:00.000Z");
  });

  it("hourly: clamps to min(array length, hours) and decodes each", async () => {
    const http = new FakeHttp(() =>
      ok(
        JSON.stringify({
          hourly: {
            time: ["2026-07-10T00:00", "2026-07-10T01:00", "2026-07-10T02:00"],
            temperature_2m: [10, 11, 12],
            apparent_temperature: [9, 10, 11],
            precipitation: [0, 0, 1],
            wind_speed_10m: [1, 2, 3],
            cloud_cover: [0, 50, 100],
            weather_code: [0, 3, 95],
          },
        }),
      ),
    );
    const p = new OpenMeteoWeatherProvider(http);
    const samples = await p.hourlyAsync(0, 0, 2); // fewer than the 3 provided
    assert.equal(samples.length, 2);
    assert.equal(samples[0].condition, "clear sky");
    assert.equal(samples[1].condition, "partly cloudy");
    assert.equal(samples[1].windKph, 2 * 3.6);
  });

  it("hourly rejects out-of-range hours", async () => {
    const p = new OpenMeteoWeatherProvider(new FakeHttp(() => ok("{}")));
    await assert.rejects(() => p.hourlyAsync(0, 0, 0), /hours/);
    await assert.rejects(() => p.hourlyAsync(0, 0, 200), /hours/);
  });
});

describe("OsrmRoutingProvider", () => {
  it("provider id + default host", () => {
    const p = new OsrmRoutingProvider(new FakeHttp(() => ok("{}")));
    assert.equal(p.providerId, "osrm");
  });

  it("maps mode→profile, orders coords lon,lat, and returns km + ms + swapped polyline", async () => {
    const http = new FakeHttp((r) => {
      // foot mode → /foot/ profile; coords are lon,lat;lon,lat
      assert.ok(r.url.includes("/route/v1/foot/"));
      assert.ok(r.url.includes("18.4,-33.9;18.5,-34"));
      assert.ok(r.url.includes("geometries=geojson"));
      return ok(
        JSON.stringify({
          code: "Ok",
          routes: [
            {
              distance: 2500, // metres
              duration: 1800, // seconds
              geometry: {
                coordinates: [
                  [18.4, -33.9],
                  [18.45, -33.95],
                  [18.5, -34.0],
                ],
              },
            },
          ],
        }),
      );
    });
    const p = new OsrmRoutingProvider(http);
    const est = await p.routeAsync(-33.9, 18.4, -34.0, 18.5, "foot");
    assert.equal(est.distanceKm, 2.5); // 2500/1000
    assert.equal(est.duration, 1800000); // 1800s → ms
    // GeoJSON is [lon, lat]; the port swaps to {lat, lon}
    assert.deepEqual(est.polyline[0], { lat: -33.9, lon: 18.4 });
    assert.deepEqual(est.polyline[2], { lat: -34.0, lon: 18.5 });
  });

  it("bike/bicycle → bike, car/other → driving", async () => {
    const seen: string[] = [];
    const http = new FakeHttp((r) => {
      seen.push(r.url);
      return ok(JSON.stringify({ code: "Ok", routes: [{ distance: 0, duration: 0, geometry: { coordinates: [] } }] }));
    });
    const p = new OsrmRoutingProvider(http);
    await p.routeAsync(0, 0, 1, 1, "bicycle");
    await p.routeAsync(0, 0, 1, 1, "car");
    await p.routeAsync(0, 0, 1, 1);
    assert.ok(seen[0].includes("/bike/"));
    assert.ok(seen[1].includes("/driving/"));
    assert.ok(seen[2].includes("/driving/"));
  });

  it("throws when OSRM code is not Ok", async () => {
    const http = new FakeHttp(() => ok(JSON.stringify({ code: "NoRoute", routes: [] })));
    const p = new OsrmRoutingProvider(http, osrmOptions("https://osrm.internal"));
    await assert.rejects(() => p.routeAsync(0, 0, 1, 1), /code=NoRoute/);
  });

  it("trims a trailing slash from a custom host", async () => {
    let url = "";
    const http = new FakeHttp((r) => {
      url = r.url;
      return ok(JSON.stringify({ code: "Ok", routes: [{ distance: 0, duration: 0, geometry: { coordinates: [] } }] }));
    });
    const p = new OsrmRoutingProvider(http, osrmOptions("https://osrm.internal/"));
    await p.routeAsync(0, 0, 1, 1);
    assert.ok(url.startsWith("https://osrm.internal/route/v1/"));
  });
});
