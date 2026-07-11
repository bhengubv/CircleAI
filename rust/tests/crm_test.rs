//! crm_test.rs
//!
//! Ports the behaviour of `CircleAI.CRM`: contact store (name/email substring
//! search, name-ordered), deal pipeline (by stage, value-descending), activity
//! log (per contact, newest-first), plus the fail-closed null backends.

use chrono::{Duration, Utc};
use circle_ai::crm::{
    Activity, Company, Contact, Deal, IActivityLog, IContactStore, IDealPipeline,
    InMemoryActivityLog, InMemoryContactStore, InMemoryDealPipeline, NullActivityLog,
    NullContactStore, NullDealPipeline,
};

#[test]
fn contact_upsert_get_and_backend_id() {
    let store = InMemoryContactStore::new();
    assert_eq!(store.backend_id(), "in-memory");
    assert!(store.get("c1").is_none());
    store.upsert(Contact::new("c1", "Ada Lovelace", Some("ada@x.io".into()), None, None));
    let c = store.get("c1").unwrap();
    assert_eq!(c.full_name, "Ada Lovelace");
    assert_eq!(c.email.as_deref(), Some("ada@x.io"));
}

#[test]
fn contact_search_matches_name_or_email_case_insensitive_ordered() {
    let store = InMemoryContactStore::new();
    store.upsert(Contact::new("c1", "Charlie", Some("c@x.io".into()), None, None));
    store.upsert(Contact::new("c2", "alice", Some("nope@x.io".into()), None, None));
    store.upsert(Contact::new("c3", "Bob", Some("ALICE@work.io".into()), None, None));

    // "alice" hits c2 by name and c3 by email; ordered by name (OrdinalIgnoreCase).
    let hits = store.search("ALICE", 20);
    let ids: Vec<&str> = hits.iter().map(|c| c.contact_id.as_str()).collect();
    assert_eq!(ids, vec!["c2", "c3"]);

    // topK truncates.
    assert_eq!(store.search("alice", 1).len(), 1);
}

#[test]
#[should_panic(expected = "topK")]
fn contact_search_zero_topk_panics() {
    InMemoryContactStore::new().search("x", 0);
}

#[test]
#[should_panic(expected = "ContactId required")]
fn contact_upsert_blank_id_panics() {
    InMemoryContactStore::new().upsert(Contact::new("  ", "X", None, None, None));
}

#[test]
fn company_record_constructs() {
    let co = Company::new("co1", "Acme", Some("Manufacturing".into()));
    assert_eq!(co.name, "Acme");
    assert_eq!(co.industry.as_deref(), Some("Manufacturing"));
}

#[test]
fn deal_pipeline_lists_by_stage_value_descending() {
    let pipe = InMemoryDealPipeline::new();
    pipe.upsert(Deal::new("d1", "co1", "Small", 100.0, "USD", "Open"));
    pipe.upsert(Deal::new("d2", "co1", "Big", 900.0, "USD", "open"));
    pipe.upsert(Deal::new("d3", "co1", "Won", 500.0, "USD", "Closed"));

    let open = pipe.list_by_stage("OPEN");
    let ids: Vec<&str> = open.iter().map(|d| d.deal_id.as_str()).collect();
    assert_eq!(ids, vec!["d2", "d1"]); // value descending, case-insensitive stage
    assert_eq!(pipe.get("d3").unwrap().name, "Won");
}

#[test]
#[should_panic(expected = "stage required")]
fn deal_list_by_blank_stage_panics() {
    InMemoryDealPipeline::new().list_by_stage("");
}

#[test]
fn activity_log_reads_newest_first_and_limits() {
    let log = InMemoryActivityLog::new();
    log.append(Activity::new("a1", "c1", "call", "old", Utc::now() - Duration::hours(2)));
    log.append(Activity::new("a2", "c1", "email", "new", Utc::now()));
    log.append(Activity::new("a3", "c1", "note", "mid", Utc::now() - Duration::hours(1)));
    log.append(Activity::new("a4", "c2", "call", "other", Utc::now()));

    let hist = log.read_for_contact("c1", 100);
    let ids: Vec<&str> = hist.iter().map(|a| a.activity_id.as_str()).collect();
    assert_eq!(ids, vec!["a2", "a3", "a1"]);
    assert_eq!(log.read_for_contact("c1", 2).len(), 2);
    assert!(log.read_for_contact("unknown", 100).is_empty());
}

#[test]
fn null_backends_are_fail_closed() {
    assert_eq!(NullContactStore::INSTANCE.backend_id(), "null");
    NullContactStore::INSTANCE.upsert(Contact::new("c1", "X", None, None, None));
    assert!(NullContactStore::INSTANCE.get("c1").is_none());
    assert!(NullContactStore::INSTANCE.search("x", 20).is_empty());

    assert_eq!(NullDealPipeline::INSTANCE.backend_id(), "null");
    assert!(NullDealPipeline::INSTANCE.list_by_stage("open").is_empty());

    assert_eq!(NullActivityLog::INSTANCE.backend_id(), "null");
    assert!(NullActivityLog::INSTANCE.read_for_contact("c1", 100).is_empty());
}
