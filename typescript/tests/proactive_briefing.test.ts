// proactive_briefing.test.ts
//
// Verifies ProactiveBriefingService (ProactiveBriefingService.cs): context
// assembly from calendar / email / news / weather connectors, the LLM summary
// (or raw context fallback), notifier delivery, the "no signals → no fire" path,
// and the next-fire-time computation.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  ProactiveBriefingService,
  DEFAULT_FIRE_TIMES_UTC_MINUTES,
  type ICalendarConnector,
  type IEmailConnector,
  type INewsSource,
  type IWeatherProvider,
  type IBriefingNotifier,
  type IBriefingAIService,
  type BriefingCalendarEvent,
} from '../src/companion/index';

class CapturingNotifier implements IBriefingNotifier {
  deliveries: { headline: string; body: string; address: string | null }[] = [];
  async deliverAsync(headline: string, body: string, address: string | null): Promise<void> {
    this.deliveries.push({ headline, body, address });
  }
}

function calendar(events: readonly BriefingCalendarEvent[], configured = true): ICalendarConnector {
  return {
    providerId: 'google',
    isConfigured: configured,
    async listEventsAsync() {
      return events;
    },
  };
}

function email(configured = true): IEmailConnector {
  return {
    providerId: 'gmail',
    isConfigured: configured,
    async listUnreadAsync() {
      return [{ from: 'Boss', subject: 'Q3 numbers' }];
    },
  };
}

function news(): INewsSource {
  return {
    sourceId: 'hn',
    isConfigured: true,
    async fetchLatestAsync() {
      return [{ title: 'Rust 2.0 released' }];
    },
  };
}

function weather(): IWeatherProvider {
  return {
    providerId: 'openmeteo',
    async currentAsync() {
      return { tempC: 21.6, feelsLikeC: 20.4, windKph: 12.9, condition: 'Cloudy' };
    },
  };
}

describe('ProactiveBriefingService.fireOnceAsync — context assembly', () => {
  it('assembles calendar + email + news + weather and delivers via notifier', async () => {
    const notifier = new CapturingNotifier();
    const startUtc = new Date(Date.UTC(2026, 6, 8, 9, 0, 0));
    const svc = new ProactiveBriefingService(
      { latitude: -26, longitude: 28, headline: 'Morning' },
      {
        calendars: [calendar([{ title: 'Standup', location: 'Zoom', startUtc }])],
        emails: [email()],
        news: [news()],
        weather: weather(),
        notifiers: [notifier],
        // No AI → summary is the raw context.
      },
    );
    await svc.fireOnceAsync();
    assert.equal(notifier.deliveries.length, 1);
    const body = notifier.deliveries[0].body;
    assert.ok(body.includes('### Calendar (google)'));
    assert.ok(body.includes('Standup @ Zoom'));
    assert.ok(body.includes('### Unread email (gmail)'));
    assert.ok(body.includes('- Boss: Q3 numbers'));
    assert.ok(body.includes('### News (hn)'));
    assert.ok(body.includes('- Rust 2.0 released'));
    assert.ok(body.includes('### Weather (openmeteo)'));
    // F0 rounding: 21.6→22, 20.4→20, 12.9→13.
    assert.ok(body.includes('- 22°C Cloudy, feels 20°C, wind 13 km/h'));
    assert.equal(notifier.deliveries[0].headline, 'Morning');
  });

  it('routes context through the AI service when one is present', async () => {
    const notifier = new CapturingNotifier();
    const ai: IBriefingAIService = {
      async chatAsync(messages) {
        // The prompt is the last user message; return a canned summary.
        assert.equal(messages[0].role, 'user');
        assert.ok(messages[0].content.includes('### News (hn)'));
        return 'Your day: one unread email, one headline.';
      },
    };
    const svc = new ProactiveBriefingService(
      {},
      { news: [news()], emails: [email()], notifiers: [notifier], ai },
    );
    await svc.fireOnceAsync();
    assert.equal(notifier.deliveries[0].body, 'Your day: one unread email, one headline.');
  });

  it('falls back to raw context if the AI call throws', async () => {
    const notifier = new CapturingNotifier();
    const ai: IBriefingAIService = {
      async chatAsync() {
        throw new Error('LLM offline');
      },
    };
    const svc = new ProactiveBriefingService({}, { news: [news()], notifiers: [notifier], ai });
    await svc.fireOnceAsync();
    assert.ok(notifier.deliveries[0].body.includes('### News (hn)'));
  });

  it('no signals → no fire (notifier not called)', async () => {
    const notifier = new CapturingNotifier();
    // Unconfigured connectors contribute nothing.
    const svc = new ProactiveBriefingService(
      {},
      { calendars: [calendar([], false)], emails: [email(false)], notifiers: [notifier] },
    );
    await svc.fireOnceAsync();
    assert.equal(notifier.deliveries.length, 0);
  });

  it('caps calendar events at 8 and orders them by start time', async () => {
    const notifier = new CapturingNotifier();
    const base = Date.UTC(2026, 6, 8, 6, 0, 0);
    // 10 events out of order; only the earliest 8 should appear.
    const events: BriefingCalendarEvent[] = [];
    for (let i = 9; i >= 0; i--) {
      events.push({ title: `E${i}`, location: null, startUtc: new Date(base + i * 60_000) });
    }
    const svc = new ProactiveBriefingService({}, { calendars: [calendar(events)], notifiers: [notifier] });
    await svc.fireOnceAsync();
    const lines = notifier.deliveries[0].body.split('\n').filter((l) => l.startsWith('- '));
    assert.equal(lines.length, 8);
    // First listed is E0 (earliest); E8/E9 dropped.
    assert.ok(lines[0].includes('E0'));
    assert.ok(!notifier.deliveries[0].body.includes('E9'));
  });

  it('rejects a null options object', () => {
    // @ts-expect-error deliberate null
    assert.throws(() => new ProactiveBriefingService(null), /opts required/);
  });
});

describe('ProactiveBriefingService.timeUntilNextFireMs', () => {
  it('uses the default 06:30 / 18:00 UTC fire times', () => {
    const svc = new ProactiveBriefingService({}, {});
    // At 05:00 UTC the next fire is 06:30 → 90 minutes.
    const now = new Date(Date.UTC(2026, 6, 8, 5, 0, 0));
    const ms = svc.timeUntilNextFireMs(now);
    assert.equal(ms, 90 * 60_000);
    assert.deepEqual(DEFAULT_FIRE_TIMES_UTC_MINUTES, [6 * 60 + 30, 18 * 60]);
  });

  it('rolls to the next day when past the last fire (and > 30s guard)', () => {
    const svc = new ProactiveBriefingService({ fireTimesUtcMinutes: [6 * 60 + 30] }, {});
    // At 06:30:10 (just past 06:30) → rolls to tomorrow's 06:30.
    const now = new Date(Date.UTC(2026, 6, 8, 6, 30, 10));
    const ms = svc.timeUntilNextFireMs(now);
    // ~ 24h minus 10s.
    assert.ok(ms > 23 * 3_600_000);
  });

  it('no fire times → 1 hour', () => {
    const svc = new ProactiveBriefingService({ fireTimesUtcMinutes: [] }, {});
    assert.equal(svc.timeUntilNextFireMs(new Date()), 3_600_000);
  });
});
