//! upgrade_test.rs
//!
//! Parity test — 7 upgrade-detection cases + correlation ID auto-synth.
//! Matches C# ModelUpgradeTests byte-for-byte semantics.

use chrono::Utc;
use circle_ai::agents::{AgentMessage, AgentMessageKind};
use circle_ai::catalog::{ModelEntry, ModelRegistry};
use circle_ai::models_v15::{BundleFile, UpgradeReason};
use circle_ai::registry::{write_installed_manifest, ModelRegistryService};
use std::path::PathBuf;

fn temp_dir(label: &str) -> PathBuf {
    let mut p = std::env::temp_dir();
    let nonce = uuid::Uuid::new_v4().simple().to_string();
    p.push(format!("circleai-rust-up-{label}-{nonce}"));
    std::fs::create_dir_all(&p).unwrap();
    p
}

fn make_entry(name: &str, version: &str, files: Vec<BundleFile>) -> ModelEntry {
    let total: i64 = files.iter().map(|f| f.size_bytes).sum();
    ModelEntry {
        name: name.to_string(),
        version: version.to_string(),
        quantization: "Q4".to_string(),
        repo: format!("MNN/{name}"),
        total_bytes: total as u64,
        bundle_files: files,
        capabilities: None,
    }
}

fn registry_with(entries: Vec<ModelEntry>) -> ModelRegistryService {
    let mut svc = ModelRegistryService::new();
    svc.set_registry(ModelRegistry {
        registry_url: "https://stub".to_string(),
        last_updated: Utc::now(),
        models: entries,
    });
    svc
}

#[test]
fn case1_not_installed_yields_empty() {
    let d = temp_dir("c1");
    let svc = registry_with(vec![make_entry(
        "Qwen3-0.6B-MNN",
        "1.0.0",
        vec![
            BundleFile { name: "config.json".into(), sha256: "abc".into(), size_bytes: 100 },
            BundleFile { name: "llm.mnn".into(), sha256: "def".into(), size_bytes: 200 },
        ],
    )]);
    let ups = svc.check_for_upgrades(&d);
    assert_eq!(ups.len(), 0);
}

#[test]
fn case2_no_manifest_yields_unknown() {
    let d = temp_dir("c2");
    let mdir = d.join("Qwen3-0.6B-MNN");
    std::fs::create_dir_all(&mdir).unwrap();
    std::fs::write(mdir.join("config.json"), b"stub").unwrap();
    let svc = registry_with(vec![make_entry(
        "Qwen3-0.6B-MNN",
        "1.0.0",
        vec![BundleFile { name: "config.json".into(), sha256: "abc".into(), size_bytes: 100 }],
    )]);
    let ups = svc.check_for_upgrades(&d);
    assert_eq!(ups.len(), 1);
    assert_eq!(ups[0].reason, UpgradeReason::Unknown);
    assert!(ups[0].installed_version.is_none());
}

#[test]
fn case3_all_shas_match_yields_empty() {
    let d = temp_dir("c3");
    let mdir = d.join("Qwen3-0.6B-MNN");
    write_installed_manifest(
        &mdir,
        "Qwen3-0.6B-MNN",
        "1.0.0",
        Some("MNN/Qwen3-0.6B-MNN"),
        vec![
            BundleFile { name: "config.json".into(), sha256: "abc".into(), size_bytes: 100 },
            BundleFile { name: "llm.mnn".into(), sha256: "def".into(), size_bytes: 200 },
        ],
    );
    let svc = registry_with(vec![make_entry(
        "Qwen3-0.6B-MNN",
        "1.0.0",
        vec![
            BundleFile { name: "config.json".into(), sha256: "abc".into(), size_bytes: 100 },
            BundleFile { name: "llm.mnn".into(), sha256: "def".into(), size_bytes: 200 },
        ],
    )]);
    assert_eq!(svc.check_for_upgrades(&d).len(), 0);
}

#[test]
fn case4_version_drift_yields_version_changed_zero_bytes() {
    let d = temp_dir("c4");
    let mdir = d.join("Qwen3-0.6B-MNN");
    write_installed_manifest(
        &mdir,
        "Qwen3-0.6B-MNN",
        "1.0.0",
        Some("MNN/Qwen3-0.6B-MNN"),
        vec![
            BundleFile { name: "config.json".into(), sha256: "abc".into(), size_bytes: 100 },
            BundleFile { name: "llm.mnn".into(), sha256: "def".into(), size_bytes: 200 },
        ],
    );
    let svc = registry_with(vec![make_entry(
        "Qwen3-0.6B-MNN",
        "1.1.0",
        vec![
            BundleFile { name: "config.json".into(), sha256: "abc".into(), size_bytes: 100 },
            BundleFile { name: "llm.mnn".into(), sha256: "def".into(), size_bytes: 200 },
        ],
    )]);
    let ups = svc.check_for_upgrades(&d);
    assert_eq!(ups.len(), 1);
    assert_eq!(ups[0].reason, UpgradeReason::VersionChanged);
    assert_eq!(ups[0].estimated_download_bytes, 0);
}

#[test]
fn case5_sha_drift_yields_sha_changed_only_drifted_bytes() {
    let d = temp_dir("c5");
    let mdir = d.join("Qwen3-0.6B-MNN");
    write_installed_manifest(
        &mdir,
        "Qwen3-0.6B-MNN",
        "1.0.0",
        Some("MNN/Qwen3-0.6B-MNN"),
        vec![
            BundleFile { name: "config.json".into(), sha256: "abc".into(), size_bytes: 100 },
            BundleFile { name: "llm.mnn".into(), sha256: "OLD".into(), size_bytes: 200 },
        ],
    );
    let svc = registry_with(vec![make_entry(
        "Qwen3-0.6B-MNN",
        "1.0.0",
        vec![
            BundleFile { name: "config.json".into(), sha256: "abc".into(), size_bytes: 100 },
            BundleFile { name: "llm.mnn".into(), sha256: "NEW".into(), size_bytes: 200 },
        ],
    )]);
    let ups = svc.check_for_upgrades(&d);
    assert_eq!(ups.len(), 1);
    assert_eq!(ups[0].reason, UpgradeReason::ShaChanged);
    assert_eq!(ups[0].estimated_download_bytes, 200);
}

#[test]
fn case6_version_and_sha_drift_yields_both_total_bytes() {
    let d = temp_dir("c6");
    let mdir = d.join("Qwen3-0.6B-MNN");
    write_installed_manifest(
        &mdir,
        "Qwen3-0.6B-MNN",
        "1.0.0",
        Some("MNN/Qwen3-0.6B-MNN"),
        vec![
            BundleFile { name: "config.json".into(), sha256: "abc".into(), size_bytes: 100 },
            BundleFile { name: "llm.mnn".into(), sha256: "OLD".into(), size_bytes: 200 },
        ],
    );
    let svc = registry_with(vec![make_entry(
        "Qwen3-0.6B-MNN",
        "2.0.0",
        vec![
            BundleFile { name: "config.json".into(), sha256: "abc2".into(), size_bytes: 100 },
            BundleFile { name: "llm.mnn".into(), sha256: "NEW".into(), size_bytes: 200 },
        ],
    )]);
    let ups = svc.check_for_upgrades(&d);
    assert_eq!(ups.len(), 1);
    assert_eq!(ups[0].reason, UpgradeReason::Both);
    assert_eq!(ups[0].estimated_download_bytes, 300);
}

#[test]
fn case7_write_installed_manifest_round_trip_yields_empty() {
    let d = temp_dir("c7");
    let mdir = d.join("Qwen3-0.6B-MNN");
    write_installed_manifest(
        &mdir,
        "Qwen3-0.6B-MNN",
        "1.0.0",
        Some("MNN/Qwen3-0.6B-MNN"),
        vec![
            BundleFile { name: "config.json".into(), sha256: "abc".into(), size_bytes: 100 },
            BundleFile { name: "llm.mnn".into(), sha256: "def".into(), size_bytes: 200 },
        ],
    );
    let svc = registry_with(vec![make_entry(
        "Qwen3-0.6B-MNN",
        "1.0.0",
        vec![
            BundleFile { name: "config.json".into(), sha256: "abc".into(), size_bytes: 100 },
            BundleFile { name: "llm.mnn".into(), sha256: "def".into(), size_bytes: 200 },
        ],
    )]);
    assert_eq!(svc.check_for_upgrades(&d).len(), 0);
}

#[test]
fn agent_message_correlation_id_autosynth_is_32_hex() {
    let m1 = AgentMessage::create(
        AgentMessageKind::Greet,
        "a",
        "b",
        "text/plain",
        vec![1, 2, 3],
        vec![4, 5, 6],
        None,
    );
    assert_eq!(m1.correlation_id.len(), 32);
    assert!(m1.correlation_id.chars().all(|c| c.is_ascii_hexdigit()));

    let m2 = AgentMessage::create(
        AgentMessageKind::Greet,
        "a",
        "b",
        "text/plain",
        vec![1, 2, 3],
        vec![4, 5, 6],
        Some("trace-abc".to_string()),
    );
    assert_eq!(m2.correlation_id, "trace-abc");

    let m3 = AgentMessage::create(
        AgentMessageKind::Greet,
        "a",
        "b",
        "text/plain",
        vec![1, 2, 3],
        vec![4, 5, 6],
        None,
    );
    assert_ne!(m1.correlation_id, m3.correlation_id);
}
