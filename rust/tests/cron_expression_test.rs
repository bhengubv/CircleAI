//! cron_expression_test.rs
//!
//! Verifies the 5-field CronExpression parser: `*`, integers, ranges, lists,
//! steps, day-of-month AND day-of-week matching, and next-occurrence search.
//! Mirrors the C# CronExpression.

use chrono::{Datelike, TimeZone, Timelike, Utc};
use circle_ai::proactive::CronExpression;

#[test]
fn parses_and_matches_every_minute() {
    let c = CronExpression::parse("* * * * *").unwrap();
    let t = Utc.with_ymd_and_hms(2026, 7, 8, 12, 34, 0).unwrap();
    assert!(c.matches(t));
}

#[test]
fn matches_specific_minute_and_hour() {
    // 06:30 daily.
    let c = CronExpression::parse("30 6 * * *").unwrap();
    assert!(c.matches(Utc.with_ymd_and_hms(2026, 7, 8, 6, 30, 0).unwrap()));
    assert!(!c.matches(Utc.with_ymd_and_hms(2026, 7, 8, 6, 31, 0).unwrap()));
    assert!(!c.matches(Utc.with_ymd_and_hms(2026, 7, 8, 7, 30, 0).unwrap()));
}

#[test]
fn matches_step_values() {
    // Every 15 minutes.
    let c = CronExpression::parse("*/15 * * * *").unwrap();
    for m in [0, 15, 30, 45] {
        assert!(c.matches(Utc.with_ymd_and_hms(2026, 7, 8, 0, m, 0).unwrap()));
    }
    assert!(!c.matches(Utc.with_ymd_and_hms(2026, 7, 8, 0, 7, 0).unwrap()));
}

#[test]
fn matches_ranges_and_lists() {
    // Minutes 1,15,30; hours 9-17.
    let c = CronExpression::parse("1,15,30 9-17 * * *").unwrap();
    assert!(c.matches(Utc.with_ymd_and_hms(2026, 7, 8, 9, 15, 0).unwrap()));
    assert!(c.matches(Utc.with_ymd_and_hms(2026, 7, 8, 17, 30, 0).unwrap()));
    assert!(!c.matches(Utc.with_ymd_and_hms(2026, 7, 8, 8, 15, 0).unwrap()));
    assert!(!c.matches(Utc.with_ymd_and_hms(2026, 7, 8, 9, 2, 0).unwrap()));
}

#[test]
fn day_of_week_uses_sunday_zero() {
    // 2026-07-08 is a Wednesday (dow = 3).
    let wed = CronExpression::parse("0 0 * * 3").unwrap();
    assert!(wed.matches(Utc.with_ymd_and_hms(2026, 7, 8, 0, 0, 0).unwrap()));
    // Sunday-only must not match a Wednesday.
    let sun = CronExpression::parse("0 0 * * 0").unwrap();
    assert!(!sun.matches(Utc.with_ymd_and_hms(2026, 7, 8, 0, 0, 0).unwrap()));
    // 2026-07-12 is a Sunday.
    assert!(sun.matches(Utc.with_ymd_and_hms(2026, 7, 12, 0, 0, 0).unwrap()));
}

#[test]
fn day_of_month_and_day_of_week_are_anded() {
    // Only the 8th AND a Wednesday. 2026-07-08 is a Wednesday → matches.
    let c = CronExpression::parse("0 0 8 * 3").unwrap();
    assert!(c.matches(Utc.with_ymd_and_hms(2026, 7, 8, 0, 0, 0).unwrap()));
    // The 8th of a month where the 8th is not Wednesday → no match (AND).
    // 2026-08-08 is a Saturday.
    assert!(!c.matches(Utc.with_ymd_and_hms(2026, 8, 8, 0, 0, 0).unwrap()));
}

#[test]
fn next_occurrence_finds_the_upcoming_slot() {
    let c = CronExpression::parse("30 6 * * *").unwrap();
    let after = Utc.with_ymd_and_hms(2026, 7, 8, 5, 0, 0).unwrap();
    let next = c.get_next_occurrence(after).unwrap();
    assert_eq!(next.hour(), 6);
    assert_eq!(next.minute(), 30);
    assert_eq!(next.day(), 8);
}

#[test]
fn next_occurrence_advances_past_the_current_minute() {
    let c = CronExpression::parse("* * * * *").unwrap();
    let after = Utc.with_ymd_and_hms(2026, 7, 8, 5, 0, 30).unwrap();
    let next = c.get_next_occurrence(after).unwrap();
    // Must be strictly after `after` (+1 min, truncated to 0 seconds).
    assert_eq!(next.minute(), 1);
    assert_eq!(next.second(), 0);
}

#[test]
fn rejects_wrong_field_count() {
    assert!(CronExpression::parse("* * *").is_err());
    assert!(CronExpression::parse("* * * * * *").is_err());
}

#[test]
fn rejects_out_of_range_values() {
    // Minute 60 is out of [0,59].
    assert!(CronExpression::parse("60 * * * *").is_err());
    // Hour 24 is out of [0,23].
    assert!(CronExpression::parse("0 24 * * *").is_err());
    // Bad step.
    assert!(CronExpression::parse("*/0 * * * *").is_err());
}
