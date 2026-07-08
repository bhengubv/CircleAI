// core_multitenant.test.ts
//
// Exercises the CircleAI.Core.MultiTenant port: NullTenantContext (throws) and
// SingleTenantContext (fixed id).

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  NullTenantContext,
  SingleTenantContext,
} from '../src/core/multitenant';

describe('NullTenantContext', () => {
  it('throws on currentTenantId and reports no tenant', () => {
    const ctx = NullTenantContext.instance;
    assert.equal(ctx.hasTenant, false);
    assert.throws(() => ctx.currentTenantId, /No CircleAI tenant context is in scope/);
  });

  it('is a shared singleton', () => {
    assert.equal(NullTenantContext.instance, NullTenantContext.instance);
  });
});

describe('SingleTenantContext', () => {
  it('returns the fixed tenant id and reports a tenant', () => {
    const ctx = new SingleTenantContext('acme');
    assert.equal(ctx.hasTenant, true);
    assert.equal(ctx.currentTenantId, 'acme');
  });

  it('rejects an empty tenant id', () => {
    assert.throws(() => new SingleTenantContext(''), /tenantId is required/);
    assert.throws(() => new SingleTenantContext('   '), /tenantId is required/);
  });
});
