// catalog.ts — ModelEntry, ModelRegistry, ModelScopeCatalogClient.
//
// HTTP fetch is left to the host on HarmonyOS — they'd plug in
// @ohos.net.http. We provide a disk cache + signature verifier hook so the
// rest of the SDK is testable.

import { BundleFile } from './models_v15';

export enum CatalogCadence {
  Never = 0,
  Manual = 1,
  OnStartup = 2,
  Daily = 3,
}

export interface ModelEntry {
  readonly name: string;
  readonly version: string;
  readonly quantization: string;
  readonly repo: string;
  readonly totalBytes: number;
  readonly bundleFiles: ReadonlyArray<BundleFile>;
  readonly capabilities: string | null;
}

export interface ModelRegistry {
  readonly registryUrl: string;
  /** ISO-8601 string in UTC. */
  readonly lastUpdated: string;
  readonly models: ReadonlyArray<ModelEntry>;
}

export interface ICatalogSignatureVerifier {
  verify(bytes: Uint8Array): boolean;
}

/** Default — accepts nothing. Real deployments should provide one. */
export class NullCatalogSignatureVerifier implements ICatalogSignatureVerifier {
  verify(_bytes: Uint8Array): boolean { return false; }
}

export class ModelScopeCatalogClient {
  constructor(
    public readonly cachePath: string,
    public readonly cadence: CatalogCadence,
    public readonly verifier: ICatalogSignatureVerifier,
  ) {}

  async loadFromDisk(): Promise<ModelRegistry | null> {
    let fs: { readFile: (p: string) => Promise<Buffer> } | null = null;
    try {
      // @ts-expect-error — node:fs is optional under ArkTS
      // eslint-disable-next-line @typescript-eslint/no-require-imports
      fs = require('node:fs/promises');
    } catch { return null; }
    if (!fs) return null;
    try {
      const buf = await fs.readFile(this.cachePath);
      return JSON.parse(buf.toString('utf-8')) as ModelRegistry;
    } catch { return null; }
  }

  async saveToDisk(reg: ModelRegistry): Promise<void> {
    let fs: { writeFile: (p: string, d: string) => Promise<void>; mkdir: (p: string, o: object) => Promise<void> } | null = null;
    let path: { dirname: (p: string) => string } | null = null;
    try {
      // @ts-expect-error
      // eslint-disable-next-line @typescript-eslint/no-require-imports
      fs = require('node:fs/promises');
      // @ts-expect-error
      // eslint-disable-next-line @typescript-eslint/no-require-imports
      path = require('node:path');
    } catch { return; }
    if (!fs || !path) return;
    await fs.mkdir(path.dirname(this.cachePath), { recursive: true });
    await fs.writeFile(this.cachePath, JSON.stringify(reg, null, 2));
  }
}
