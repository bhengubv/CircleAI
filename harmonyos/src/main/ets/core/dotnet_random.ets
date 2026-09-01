/**
 * core/dotnet_random
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// core/dotnet_random.ts
//
// Byte-faithful port of .NET's seeded System.Random — the legacy Knuth
// subtractive generator used by `new Random(int seed)` (a.k.a. the
// Net5CompatSeedImpl / CompatPrng path). Reproduced so that any C# component
// that seeds a deterministic codebook with `new Random(seed)` produces
// identical values here.
//
// Reference: dotnet/runtime  System.Random.Net5CompatSeedImpl.
//   MBIG  = int.MaxValue (2147483647)
//   MSEED = 161803398
//   MZ    = 0
//
// All arithmetic is kept inside signed 32-bit range via `| 0` and Math.imul is
// unnecessary here because the operations are additions/subtractions that the
// subtractive-with-borrow correction keeps within [0, MBIG).

const MBIG = 2147483647; // int.MaxValue
const MSEED = 161803398;

/**
 * Deterministic port of .NET Framework / .NET Core legacy seeded `System.Random`.
 * Only the members ShardKvCodec needs (`nextDouble`) are surfaced, plus the
 * lower-level `internalSample` / `sample` for completeness and testing.
 */
export class DotNetRandom {
  private readonly seedArray: Int32Array = new Int32Array(56);
  private inext = 0;
  private inextp = 0;

  constructor(seed: number) {
    // int Subtractive = (Seed == int.MinValue) ? int.MaxValue : Math.Abs(Seed);
    const seed32 = seed | 0;
    const subtractive =
      seed32 === -2147483648 ? 2147483647 : Math.abs(seed32);

    let mj = (MSEED - subtractive) | 0;
    this.seedArray[55] = mj;
    let mk = 1;

    // for (int i = 1; i < 55; i++) — the famous 21-step scatter.
    for (let i = 1; i < 55; i++) {
      const ii = (21 * i) % 55;
      this.seedArray[ii] = mk;
      mk = (mj - mk) | 0;
      if (mk < 0) mk = (mk + MBIG) | 0;
      mj = this.seedArray[ii];
    }

    for (let k = 1; k < 5; k++) {
      for (let i = 1; i < 56; i++) {
        let n = (i + 30) % 55;
        this.seedArray[i] = (this.seedArray[i] - this.seedArray[1 + n]) | 0;
        if (this.seedArray[i] < 0)
          this.seedArray[i] = (this.seedArray[i] + MBIG) | 0;
      }
    }

    this.inext = 0;
    this.inextp = 21;
  }

  /** Mirrors the protected `InternalSample()`. Returns an int in [0, MBIG). */
  internalSample(): number {
    let locINext = this.inext;
    let locINextp = this.inextp;

    if (++locINext >= 56) locINext = 1;
    if (++locINextp >= 56) locINextp = 1;

    let retVal =
      (this.seedArray[locINext] - this.seedArray[locINextp]) | 0;

    if (retVal === MBIG) retVal = (retVal - 1) | 0;
    if (retVal < 0) retVal = (retVal + MBIG) | 0;

    this.seedArray[locINext] = retVal;
    this.inext = locINext;
    this.inextp = locINextp;

    return retVal;
  }

  /** Mirrors the protected `Sample()` — a double in [0, 1). */
  sample(): number {
    // Including the division at the end gives us significantly improved random
    // number distribution.
    return this.internalSample() * (1.0 / MBIG);
  }

  /** Mirrors `NextDouble()`. */
  nextDouble(): number {
    return this.sample();
  }
}
