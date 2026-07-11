"""test_home_board.py — CircleAI.Home port.

Covers the domain records, InMemoryHomeBoard (room upsert + name ordering, device
add/toggle, devices-in-room, active devices, task schedule/complete, upcoming
tasks filtered incomplete + due<=by ordered by DueOn) and the static
HomeDomainContext. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta

import pytest

from circle_ai import (
    HomeDevice,
    HomeDomainContext,
    IHomeBoard,
    InMemoryHomeBoard,
    MaintenanceTask,
    Room,
)

_T0 = datetime(2026, 1, 1)


def _at(days: int) -> datetime:
    return _T0 + timedelta(days=days)


def test_board_is_ihomeboard():
    assert isinstance(InMemoryHomeBoard(), IHomeBoard)


def test_rooms_ordered_by_name():
    board = InMemoryHomeBoard()
    board.add_room(Room("r2", "Kitchen", 12.0))
    board.add_room(Room("r1", "Bedroom", 15.0))
    board.add_room(Room("r3", "Lounge", 20.0))
    assert [r.name for r in board.rooms] == ["Bedroom", "Kitchen", "Lounge"]
    assert board.get_room("r1").name == "Bedroom"


def test_add_room_none_raises():
    with pytest.raises(ValueError):
        InMemoryHomeBoard().add_room(None)  # type: ignore[arg-type]


def test_toggle_and_active_devices():
    board = InMemoryHomeBoard()
    board.add_device(HomeDevice("d1", "Lamp", "light", "r1", False))
    board.add_device(HomeDevice("d2", "Fan", "fan", "r1", False))
    board.add_device(HomeDevice("d3", "TV", "media", "r2", True))
    board.toggle("d1", True)
    assert {d.device_id for d in board.active_devices} == {"d1", "d3"}
    assert {d.device_id for d in board.devices_in("r1")} == {"d1", "d2"}


def test_toggle_unknown_raises():
    with pytest.raises(RuntimeError):
        InMemoryHomeBoard().toggle("nope", True)


def test_complete_and_upcoming_tasks():
    board = InMemoryHomeBoard()
    board.schedule_task(MaintenanceTask("t1", "Gutters", _at(3), False))
    board.schedule_task(MaintenanceTask("t2", "Filter", _at(1), False))
    board.schedule_task(MaintenanceTask("t3", "Paint", _at(10), False))
    board.schedule_task(MaintenanceTask("t4", "Done", _at(2), False))
    board.complete_task("t4")
    upcoming = board.upcoming_tasks(_at(5))
    # incomplete + due <= by(day5), ordered by DueOn: t2(day1), t1(day3). t3 too
    # far out; t4 completed.
    assert [t.task_id for t in upcoming] == ["t2", "t1"]


def test_complete_unknown_raises():
    with pytest.raises(RuntimeError):
        InMemoryHomeBoard().complete_task("nope")


def test_home_domain_context():
    assert HomeDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Home]")
    assert list(HomeDomainContext.ComplianceFlags) == [
        "NHBRC",
        "National_Building_Regs",
        "POPIA",
    ]
    assert list(HomeDomainContext.SuggestedTools) == [
        "home_inventory",
        "task_manager",
        "web_search",
        "calculator",
    ]
