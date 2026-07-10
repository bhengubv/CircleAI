# companion_runtime_options.py
#
# Tunable settings for CompanionRuntime. All have sensible defaults so a
# host can construct a CompanionRuntime with no options and get a working
# pipeline out of the box.
#
# Ported faithfully from CircleAI.Memory.Runtime.CompanionRuntimeOptions
# (C# — the spec). C# TimeSpan -> Python datetime.timedelta.

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import timedelta


@dataclass(frozen=True, slots=True)
class CompanionRuntimeOptions:
    """Configuration for :class:`CompanionRuntime`."""

    #: Cadence for the daily-tier consolidation pass. Default: every 6 hours.
    #: Setting this to ``timedelta(0)`` disables automatic daily ticks.
    daily_tick_interval: timedelta = field(default_factory=lambda: timedelta(hours=6))

    #: Cadence for the weekly-tier consolidation pass. Default: every 24 hours.
    weekly_tick_interval: timedelta = field(default_factory=lambda: timedelta(hours=24))

    #: Cadence for the monthly-tier (persona-delta) consolidation pass.
    #: Default: every 48 hours.
    monthly_tick_interval: timedelta = field(
        default_factory=lambda: timedelta(hours=48)
    )

    #: Cadence at which the runtime broadcasts its sync state vector to peers.
    #: Default: every 5 minutes. Setting to ``timedelta(0)`` disables periodic
    #: sync (the engine still responds to inbound envelopes; only the
    #: initiating Announce broadcasts are suppressed).
    sync_broadcast_interval: timedelta = field(
        default_factory=lambda: timedelta(minutes=5)
    )

    #: Initial delay before the first consolidator tick after start_async.
    #: Default: 30 seconds. Keeps startup quiet.
    initial_delay: timedelta = field(default_factory=lambda: timedelta(seconds=30))

    #: When true, the runtime runs an OnDemand consolidation pass during
    #: start_async to catch up anything pending before the timer cadence kicks
    #: in. Default: true.
    catch_up_on_start: bool = True
