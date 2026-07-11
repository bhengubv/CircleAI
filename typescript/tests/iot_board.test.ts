// iot_board.test.ts
// Verifies the CircleAI.IoT board port: device registry (name-ordered),
// telemetry latest/history (NaN fallback, bounded), and per-device command log.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import { InMemoryIoTBoard, ioTDevice, ioTTelemetry, ioTCommand } from "../src/iot/index";

const D = (s: string) => new Date(s);

describe("InMemoryIoTBoard", () => {
  it("registers devices and lists them ordered by name", () => {
    const b = new InMemoryIoTBoard();
    b.register(ioTDevice("d2", "Thermostat", "hvac", "1.0", D("2026-05-01T00:00:00Z")));
    b.register(ioTDevice("d1", "Camera", "cam", "2.0", D("2026-05-01T00:00:00Z")));
    assert.deepEqual(
      b.devices.map((d) => d.name),
      ["Camera", "Thermostat"],
    );
    assert.equal(b.getDevice("d1")?.firmwareVersion, "2.0");
    assert.equal(b.getDevice("nope"), undefined);
  });

  it("returns the newest telemetry value or NaN, and bounded descending history", () => {
    const b = new InMemoryIoTBoard();
    assert.ok(Number.isNaN(b.latestValue("d1", "temp")));
    b.recordTelemetry(ioTTelemetry("d1", "temp", 20, D("2026-05-01T00:00:00Z")));
    b.recordTelemetry(ioTTelemetry("d1", "temp", 22, D("2026-05-02T00:00:00Z")));
    b.recordTelemetry(ioTTelemetry("d1", "temp", 25, D("2026-05-03T00:00:00Z")));
    assert.equal(b.latestValue("d1", "temp"), 25);
    assert.deepEqual(
      b.history("d1", "temp").map((t) => t.value),
      [25, 22, 20],
    );
    assert.deepEqual(
      b.history("d1", "temp", 2).map((t) => t.value),
      [25, 22],
    );
    assert.throws(() => b.history("d1", "temp", 0));
  });

  it("logs commands per device newest-first", () => {
    const b = new InMemoryIoTBoard();
    b.sendCommand(ioTCommand("c1", "d1", "reboot", "{}", D("2026-05-01T00:00:00Z")));
    b.sendCommand(ioTCommand("c2", "d1", "update", "{}", D("2026-05-02T00:00:00Z")));
    b.sendCommand(ioTCommand("c3", "d2", "ping", "{}", D("2026-05-02T00:00:00Z")));
    assert.deepEqual(
      b.commandsFor("d1").map((c) => c.commandId),
      ["c2", "c1"],
    );
  });

  it("rejects null arguments", () => {
    const b = new InMemoryIoTBoard();
    assert.throws(() => b.register(null as never));
    assert.throws(() => b.recordTelemetry(null as never));
    assert.throws(() => b.sendCommand(null as never));
  });
});
