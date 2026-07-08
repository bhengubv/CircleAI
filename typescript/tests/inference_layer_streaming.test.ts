// inference_layer_streaming.test.ts
//
// Exercises the layer-streaming orchestrator, the null runner, and shard
// discovery (layer_NNN parse + sort).

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  LayerStreamingOrchestrator,
  NullLayerStreamingRunner,
  InMemoryLayerShardListing,
  discoverLayerShards,
  type ILayerStreamingRunner,
  type LayerActivations,
  type LayerStreamingPlan,
  type LayerWeightShard,
} from '../src/inference/layer_streaming';

/** Adds the layer index to every hidden element so we can trace the pass. */
class IncrementRunner implements ILayerStreamingRunner {
  readonly backendId = 'increment';
  readonly isAvailable = true;
  evicted: number[] = [];
  async runLayer(shard: LayerWeightShard, input: Float32Array): Promise<LayerActivations> {
    const hidden = Float32Array.from(input, (v) => v + shard.layerIndex + 1);
    return { layerIndex: shard.layerIndex, hidden };
  }
  async evict(layerIndex: number): Promise<void> {
    this.evicted.push(layerIndex);
  }
}

describe('LayerStreamingOrchestrator', () => {
  it('runs every layer in order and evicts after each', async () => {
    const runner = new IncrementRunner();
    const orch = new LayerStreamingOrchestrator(runner);
    const plan: LayerStreamingPlan = {
      modelId: 'm',
      totalLayers: 3,
      shards: [
        { layerIndex: 0, weightShardPath: 'a', approxBytes: 1 },
        { layerIndex: 1, weightShardPath: 'b', approxBytes: 1 },
        { layerIndex: 2, weightShardPath: 'c', approxBytes: 1 },
      ],
      approxParameterBytes: 3,
    };
    const completed: number[] = [];
    const out = await orch.forward(plan, Float32Array.from([0]), (a) => completed.push(a.layerIndex));
    // hidden = 0 -> +1 -> +2 -> +3 = 6.
    assert.equal(out.hidden[0], 6);
    assert.deepEqual(completed, [0, 1, 2]);
    assert.deepEqual(runner.evicted, [0, 1, 2]);
  });

  it('rejects an empty plan', async () => {
    const orch = new LayerStreamingOrchestrator(new IncrementRunner());
    await assert.rejects(
      () => orch.forward({ modelId: 'm', totalLayers: 0, shards: [], approxParameterBytes: 0 }, new Float32Array()),
      /no layer shards/,
    );
  });
});

describe('NullLayerStreamingRunner', () => {
  it('is unavailable and throws on use', async () => {
    const r = NullLayerStreamingRunner.instance;
    assert.equal(r.isAvailable, false);
    assert.equal(r.backendId, 'null');
    await assert.rejects(() => r.runLayer({ layerIndex: 0, weightShardPath: 'x', approxBytes: 1 }, new Float32Array()));
    // evict is a no-op.
    await r.evict(0);
  });
});

describe('discoverLayerShards', () => {
  it('parses layer_NNN files and sorts by index', () => {
    const listing = new InMemoryLayerShardListing()
      .addDir('/m')
      .addFile('/m/layer_2.safetensors', 20)
      .addFile('/m/layer_0.safetensors', 10)
      .addFile('/m/layer_10.bin', 100)
      .addFile('/m/notalayer.json', 5);
    const plan = discoverLayerShards('m', '/m', listing);
    assert.equal(plan.totalLayers, 3);
    assert.deepEqual(plan.shards.map((s) => s.layerIndex), [0, 2, 10]);
    assert.equal(plan.approxParameterBytes, 130);
  });

  it('throws when the directory is missing', () => {
    const listing = new InMemoryLayerShardListing();
    assert.throws(() => discoverLayerShards('m', '/nope', listing), /not found/);
  });
});
