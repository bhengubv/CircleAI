//! dotnet_random_test.rs
//!
//! Verifies [`circle_ai::model_runtime::DotNetRandom`] reproduces .NET's seeded
//! `System.Random` sequence byte-for-byte. The reference values below are the
//! canonical outputs of `new Random(seed).NextDouble()` on .NET (the legacy
//! net-compat seeded algorithm used by all versions including net9/net10).

use circle_ai::model_runtime::DotNetRandom;

const EPS: f64 = 1e-15;

#[test]
fn seed_zero_matches_dotnet_reference() {
    let mut r = DotNetRandom::new(0);
    let expected = [
        0.7262432699679598,
        0.8173253595909687,
        0.7680226893946634,
        0.5581611914365372,
        0.2060331540210327,
    ];
    for &e in &expected {
        let got = r.next_double();
        assert!((got - e).abs() < EPS, "expected {e}, got {got}");
    }
}

#[test]
fn seed_42_matches_dotnet_reference() {
    let mut r = DotNetRandom::new(42);
    let expected = [
        0.6681064659115423,
        0.1409072983734809,
        0.1255182894531257,
    ];
    for &e in &expected {
        let got = r.next_double();
        assert!((got - e).abs() < EPS, "expected {e}, got {got}");
    }
}

#[test]
fn values_are_in_unit_interval() {
    let mut r = DotNetRandom::new(12345);
    for _ in 0..1000 {
        let x = r.next_double();
        assert!((0.0..1.0).contains(&x), "value {x} out of [0,1)");
    }
}

#[test]
fn same_seed_is_deterministic() {
    let mut a = DotNetRandom::new(777);
    let mut b = DotNetRandom::new(777);
    for _ in 0..50 {
        assert_eq!(a.next_double(), b.next_double());
    }
}
