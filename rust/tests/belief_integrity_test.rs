//! belief_integrity_test.rs
//!
//! Verifies the memory-integrity core: attribution discipline (self/other/world),
//! and SelfBeliefStore filtering, revision (supersede), correction (retract), and
//! provenance. The headline guarantee: "my mother is diabetic" never becomes a
//! fact about the user. Mirrors the TS pilot suite tests/belief_integrity.test.ts
//! and the Go suite belief_integrity_test.go 1:1.

use chrono::Utc;
use circle_ai::companion::belief::{
    Attribution, HeuristicBeliefExtractor, IBeliefExtractor, PersonalBelief, SelfBeliefStore,
};

fn one_belief(ex: &HeuristicBeliefExtractor, text: &str) -> PersonalBelief {
    let beliefs = ex.extract(text, Some("turn")).expect("Extract");
    assert_eq!(beliefs.len(), 1, "expected one belief from {text:?}, got {}", beliefs.len());
    beliefs.into_iter().next().unwrap()
}

fn record_all(store: &SelfBeliefStore, ex: &HeuristicBeliefExtractor, text: &str, src: Option<&str>) {
    let bs = ex.extract(text, src).expect("Extract");
    for b in bs {
        store.record(b).expect("Record");
    }
}

fn non_self_has(store: &SelfBeliefStore, obj: &str) -> bool {
    store.non_self().iter().any(|b| b.object == obj)
}

// ── Attribution ──────────────────────────────────────────────────────────────

#[test]
fn my_mother_is_diabetic_other_about_the_mother() {
    let ex = HeuristicBeliefExtractor::new();
    let b = one_belief(&ex, "my mother is diabetic");
    assert_eq!(b.attribution, Attribution::Other);
    assert_eq!(b.subject, "mother");
    assert_eq!(b.object, "diabetic");
}

#[test]
fn i_am_vegetarian_self_about_the_user() {
    let ex = HeuristicBeliefExtractor::new();
    let b = one_belief(&ex, "i am vegetarian");
    assert_eq!(b.attribution, Attribution::Self_);
    assert_eq!(b.subject, "user");
    assert_eq!(b.object, "vegetarian");
}

#[test]
fn my_car_is_fast_my_plus_non_relation_self() {
    let ex = HeuristicBeliefExtractor::new();
    let b = one_belief(&ex, "my car is fast");
    assert_eq!(b.attribution, Attribution::Self_);
    assert_eq!(b.subject, "user");
}

#[test]
fn a_bare_relation_as_subject_other() {
    let ex = HeuristicBeliefExtractor::new();
    let b = one_belief(&ex, "brother lives in Cape Town");
    assert_eq!(b.attribution, Attribution::Other);
    assert_eq!(b.subject, "brother");
}

#[test]
fn a_general_statement_world() {
    let ex = HeuristicBeliefExtractor::new();
    let b = one_belief(&ex, "paris is beautiful");
    assert_eq!(b.attribution, Attribution::World);
    assert_eq!(b.subject, "paris");
}

// ── SelfBeliefStore ──────────────────────────────────────────────────────────

#[test]
fn only_self_beliefs_become_user_facts_other_world_are_audited() {
    let ex = HeuristicBeliefExtractor::new();
    let store = SelfBeliefStore::new();
    record_all(&store, &ex, "my mother is diabetic", Some("t1"));
    record_all(&store, &ex, "i am vegetarian", Some("t2"));

    let facts = store.self_facts();
    assert_eq!(facts.len(), 1);
    assert_eq!(facts[0].object, "vegetarian");
    for f in &facts {
        assert!(!f.object.contains("diabetic"), "mother's fact leaked into user facts");
    }
    assert!(non_self_has(&store, "diabetic"), "mother's fact should be in the audit trail");
}

#[test]
fn a_newer_self_belief_supersedes_the_older_one_on_the_same_predicate() {
    let store = SelfBeliefStore::new();
    let mk = |obj: &str| PersonalBelief {
        attribution: Attribution::Self_,
        subject: "user".to_string(),
        predicate: "isAbout".to_string(),
        object: obj.to_string(),
        confidence: 0.6,
        source: Some("t".to_string()),
        recorded_at_utc: Utc::now(),
    };
    store.record(mk("vegetarian")).expect("Record");
    store.record(mk("vegan")).expect("Record");

    let facts = store.self_facts();
    assert_eq!(facts.len(), 1);
    assert_eq!(facts[0].object, "vegan");
}

#[test]
fn retract_removes_user_facts_mentioning_the_text() {
    let ex = HeuristicBeliefExtractor::new();
    let store = SelfBeliefStore::new();
    record_all(&store, &ex, "i am vegetarian", Some("t1"));
    let removed = store.retract("vegetarian");
    assert_eq!(removed, 1);
    assert_eq!(store.self_facts().len(), 0);
}

#[test]
fn provenance_returns_the_distinct_source_turns_behind_user_facts() {
    let store = SelfBeliefStore::new();
    let mk = |obj: &str, predicate: &str, source: &str| PersonalBelief {
        attribution: Attribution::Self_,
        subject: "user".to_string(),
        predicate: predicate.to_string(),
        object: obj.to_string(),
        confidence: 0.6,
        source: Some(source.to_string()),
        recorded_at_utc: Utc::now(),
    };
    store.record(mk("vegetarian", "diet", "t1")).expect("Record");
    store.record(mk("hiking", "hobby", "t2")).expect("Record");
    let mut prov = store.provenance();
    prov.sort();
    assert_eq!(prov, vec!["t1", "t2"]);
}
