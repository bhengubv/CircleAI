//! dotnet_random.rs
//!
//! Faithful reimplementation of .NET's seeded `System.Random` — the subtractive
//! random number generator based on Knuth's algorithm (Numerical Recipes),
//! matching the reference source used by every seeded `new Random(seed)` on
//! .NET Framework and the compatible legacy path on modern .NET.
//!
//! This exists so [`super::shard_kv_codec::ShardKvCodec`]'s deterministic seed
//! codebook (`SeedCodebook` in C#, which calls `new Random(seed)` +
//! `rng.NextDouble()`) reproduces byte-identically in Rust.
//!
//! Reference (Microsoft Reference Source, `System.Random`):
//!   MBIG  = int.MaxValue (2_147_483_647)
//!   MSEED = 161_803_398
//!   MZ    = 0
//!
//! Only the members the codec needs are implemented: construction with an
//! `i32` seed and [`DotNetRandom::next_double`].

const MBIG: i32 = i32::MAX; // 2_147_483_647
const MSEED: i32 = 161_803_398;

/// Deterministic PRNG that reproduces .NET's seeded `System.Random`.
pub struct DotNetRandom {
    seed_array: [i32; 56],
    inext: usize,
    inextp: usize,
}

impl DotNetRandom {
    /// Constructs a generator seeded exactly like `new System.Random(seed)`.
    pub fn new(seed: i32) -> Self {
        let mut seed_array = [0i32; 56];

        // subtraction = (Seed == int.MinValue) ? int.MaxValue : Abs(Seed)
        let subtraction: i32 = if seed == i32::MIN {
            i32::MAX
        } else {
            seed.abs()
        };

        let mut mj = MSEED.wrapping_sub(subtraction);
        seed_array[55] = mj;
        let mut mk: i32 = 1;

        // for (int i = 1; i < 55; i++) — using (21 * i) % 55 index walk.
        let mut ii: usize;
        for i in 1..55usize {
            ii = (21 * i) % 55;
            seed_array[ii] = mk;
            mk = mj.wrapping_sub(mk);
            if mk < 0 {
                mk = mk.wrapping_add(MBIG);
            }
            mj = seed_array[ii];
        }

        for _k in 1..5 {
            for i in 1..56usize {
                seed_array[i] = seed_array[i].wrapping_sub(seed_array[1 + (i + 30) % 55]);
                if seed_array[i] < 0 {
                    seed_array[i] = seed_array[i].wrapping_add(MBIG);
                }
            }
        }

        Self {
            seed_array,
            inext: 0,
            inextp: 21,
        }
    }

    /// Core sample step — mirrors `InternalSample()`.
    fn internal_sample(&mut self) -> i32 {
        let mut loc_inext = self.inext;
        let mut loc_inextp = self.inextp;

        loc_inext += 1;
        if loc_inext >= 56 {
            loc_inext = 1;
        }
        loc_inextp += 1;
        if loc_inextp >= 56 {
            loc_inextp = 1;
        }

        let mut ret_val = self.seed_array[loc_inext].wrapping_sub(self.seed_array[loc_inextp]);

        if ret_val == MBIG {
            ret_val -= 1;
        }
        if ret_val < 0 {
            ret_val = ret_val.wrapping_add(MBIG);
        }

        self.seed_array[loc_inext] = ret_val;
        self.inext = loc_inext;
        self.inextp = loc_inextp;

        ret_val
    }

    /// `Sample()` — returns a double in `[0, 1)`.
    fn sample(&mut self) -> f64 {
        // InternalSample() * (1.0 / MBIG)
        self.internal_sample() as f64 * (1.0 / MBIG as f64)
    }

    /// `NextDouble()` — a double in `[0.0, 1.0)`.
    pub fn next_double(&mut self) -> f64 {
        self.sample()
    }
}
