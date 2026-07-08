// core_auditing.test.ts
//
// Exercises the CircleAI.Core.Auditing port: NoopAuditLog, LoggerAuditLog,
// CircleAIAuditing ambient sink.

import { describe, it, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import {
  NoopAuditLog,
  LoggerAuditLog,
  CircleAIAuditing,
  type IAuditLogger,
  type CircleAIAuditEntry,
} from '../src/core/auditing';

function entry(overrides: Partial<CircleAIAuditEntry> = {}): CircleAIAuditEntry {
  return {
    at: new Date('2026-07-07T10:20:30.000Z'),
    component: 'JsonPersonaProvider',
    operation: 'GetAsync',
    outcome: 'success',
    durationMs: 12.5,
    ...overrides,
  };
}

async function drain(
  it2: AsyncIterable<CircleAIAuditEntry>,
): Promise<CircleAIAuditEntry[]> {
  const out: CircleAIAuditEntry[] = [];
  for await (const e of it2) out.push(e);
  return out;
}

describe('NoopAuditLog', () => {
  it('records without throwing and queries empty', async () => {
    const log = NoopAuditLog.instance;
    await log.recordAsync(entry());
    assert.deepEqual(await drain(log.queryAsync({})), []);
  });

  it('exposes a shared singleton', () => {
    assert.equal(NoopAuditLog.instance, NoopAuditLog.instance);
  });
});

describe('LoggerAuditLog', () => {
  it('writes a structured line to the injected logger', async () => {
    const lines: string[] = [];
    const logger: IAuditLogger = { logInformation: (m) => lines.push(m) };
    const log = new LoggerAuditLog(logger);
    await log.recordAsync(
      entry({
        tenantId: 't1',
        uhidIdentityId: 'u9',
        correlationId: 'corr-1',
        outcome: 'failure',
        errorType: 'InvalidOperationException',
        errorCode: 'E42',
        payloadSha256Hex: 'deadbeef',
      }),
    );
    assert.equal(lines.length, 1);
    const line = lines[0];
    assert.match(line, /CircleAI audit JsonPersonaProvider\.GetAsync failure/);
    assert.match(line, /tenant=t1/);
    assert.match(line, /uhid=u9/);
    assert.match(line, /corr=corr-1/);
    assert.match(line, /duration_ms=12\.5/);
    assert.match(line, /error=InvalidOperationException\(E42\)/);
    assert.match(line, /payload_sha256=deadbeef/);
    assert.match(line, /at=2026-07-07T10:20:30\.000Z/);
  });

  it('renders null optionals as "-"', async () => {
    const lines: string[] = [];
    const log = new LoggerAuditLog({ logInformation: (m) => lines.push(m) });
    await log.recordAsync(entry());
    assert.match(lines[0], /tenant=- uhid=- corr=-/);
    assert.match(lines[0], /error=-\(-\)/);
    assert.match(lines[0], /payload_sha256=-/);
  });

  it('queryAsync yields nothing (logger sink cannot read back)', async () => {
    const log = new LoggerAuditLog({ logInformation: () => {} });
    assert.deepEqual(await drain(log.queryAsync({})), []);
  });

  it('rejects a null logger', () => {
    assert.throws(
      () => new LoggerAuditLog(null as unknown as IAuditLogger),
      /logger is required/,
    );
  });
});

describe('CircleAIAuditing', () => {
  afterEach(() => CircleAIAuditing.resetToNoop());

  it('defaults to NoopAuditLog', () => {
    assert.equal(CircleAIAuditing.default, NoopAuditLog.instance);
  });

  it('setDefault swaps the ambient sink; resetToNoop restores it', async () => {
    const seen: CircleAIAuditEntry[] = [];
    const custom = {
      recordAsync: (e: CircleAIAuditEntry) => {
        seen.push(e);
        return Promise.resolve();
      },
      // eslint-disable-next-line @typescript-eslint/require-await
      queryAsync: async function* () {
        return;
      },
    };
    CircleAIAuditing.setDefault(custom);
    await CircleAIAuditing.default.recordAsync(entry());
    assert.equal(seen.length, 1);

    CircleAIAuditing.resetToNoop();
    assert.equal(CircleAIAuditing.default, NoopAuditLog.instance);
  });

  it('setDefault rejects null', () => {
    assert.throws(
      () => CircleAIAuditing.setDefault(null as unknown as NoopAuditLog),
      /audit is required/,
    );
  });
});
