// home_board.test.ts
// Verifies the CircleAI.Home port: rooms (name-ordered), device toggle + per-room
// + active views, maintenance tasks with completion + upcoming-by-date.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryHomeBoard,
  HomeDomainContext,
  room,
  homeDevice,
  maintenanceTask,
} from "../src/home/index";

const D = (s: string) => new Date(s);

describe("InMemoryHomeBoard", () => {
  it("adds rooms and lists them ordered by name", () => {
    const b = new InMemoryHomeBoard();
    b.addRoom(room("r2", "Kitchen", 12));
    b.addRoom(room("r1", "Bedroom", 15));
    assert.deepEqual(
      b.rooms.map((r) => r.name),
      ["Bedroom", "Kitchen"],
    );
    assert.equal(b.getRoom("r1")?.areaM2, 15);
    assert.equal(b.getRoom("nope"), undefined);
  });

  it("toggles devices and reports per-room + active views", () => {
    const b = new InMemoryHomeBoard();
    b.addDevice(homeDevice("d1", "Lamp", "light", "r1", false));
    b.addDevice(homeDevice("d2", "TV", "media", "r1", true));
    b.addDevice(homeDevice("d3", "Fridge", "appliance", "r2", true));
    assert.deepEqual(
      b.devicesIn("r1").map((d) => d.deviceId),
      ["d1", "d2"],
    );
    assert.deepEqual(
      b.activeDevices.map((d) => d.deviceId),
      ["d2", "d3"],
    );
    b.toggle("d1", true);
    assert.deepEqual(
      b.activeDevices.map((d) => d.deviceId),
      ["d1", "d2", "d3"],
    );
    assert.throws(() => b.toggle("ghost", true), /Unknown device ghost/);
  });

  it("schedules, completes, and lists upcoming tasks by due date", () => {
    const b = new InMemoryHomeBoard();
    b.scheduleTask(maintenanceTask("t1", "Gutters", D("2026-03-01"), false));
    b.scheduleTask(maintenanceTask("t2", "Boiler", D("2026-01-15"), false));
    b.scheduleTask(maintenanceTask("t3", "Roof", D("2026-12-01"), false)); // beyond `by`
    assert.deepEqual(
      b.upcomingTasks(D("2026-06-01")).map((t) => t.taskId),
      ["t2", "t1"], // ordered by due date ascending
    );
    b.completeTask("t2");
    assert.deepEqual(
      b.upcomingTasks(D("2026-06-01")).map((t) => t.taskId),
      ["t1"],
    );
    assert.throws(() => b.completeTask("ghost"), /Unknown task ghost/);
  });

  it("rejects null arguments", () => {
    const b = new InMemoryHomeBoard();
    assert.throws(() => b.addRoom(null as never));
    assert.throws(() => b.addDevice(null as never));
    assert.throws(() => b.scheduleTask(null as never));
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(HomeDomainContext.systemPromptSnippet.includes("[DOMAIN: Home]"));
    assert.deepEqual(HomeDomainContext.complianceFlags, ["NHBRC", "National_Building_Regs", "POPIA"]);
    assert.deepEqual(HomeDomainContext.suggestedTools, ["home_inventory", "task_manager", "web_search", "calculator"]);
  });
});
