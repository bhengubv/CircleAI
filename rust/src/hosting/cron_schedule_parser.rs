//! cron_schedule_parser.rs
//!
//! Minimal 5-field cron expression parser. Ported 1:1 from
//! `CronScheduleParser.cs`. Supports:
//!   * `*`          — every unit
//!   * `N`          — fixed value
//!   * `N,M,...`    — list of values
//!   * `*/N`        — step (every N units)
//!   * `N-M`        — range
//!   * `N-M/S`      — range with step
//!
//! Field order: minute hour dom month dow
//!              0-59   0-23 1-31 1-12  0-6 (0=Sunday)
//!
//! This is a distinct parser from [`crate::proactive::CronExpression`]: it
//! walks forward from the reference time using month/hour skip-advancement
//! (rather than a minute-by-minute scan) and caps iteration at 5 years. The
//! search returns the earliest timestamp strictly after `after`.

use std::collections::HashSet;

use chrono::{DateTime, Datelike, Duration, TimeZone, Timelike, Utc, Weekday};

/// A cron parse / evaluation error (mirrors the C# `ArgumentException` /
/// `InvalidOperationException`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CronScheduleError(pub String);

impl std::fmt::Display for CronScheduleError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for CronScheduleError {}

/// Computes the next occurrence of a 5-field cron expression after a given
/// `DateTime<Utc>`. Handles wildcards, lists, steps, and ranges. 1:1 with the
/// C# static `CronScheduleParser`.
pub struct CronScheduleParser;

impl CronScheduleParser {
    /// Returns the earliest UTC timestamp strictly after `after` that satisfies
    /// `cron_expression`.
    ///
    /// `cron_expression` is a 5-field expression: minute (0-59), hour (0-23),
    /// day-of-month (1-31), month (1-12), day-of-week (0-6, 0=Sunday). The
    /// returned value is strictly greater than `after`.
    pub fn get_next_occurrence(
        cron_expression: &str,
        after: DateTime<Utc>,
    ) -> Result<DateTime<Utc>, CronScheduleError> {
        if cron_expression.trim().is_empty() {
            return Err(CronScheduleError(
                "cronExpression must not be null or whitespace.".to_string(),
            ));
        }

        let parts: Vec<&str> = cron_expression
            .trim()
            .split(' ')
            .filter(|s| !s.is_empty())
            .collect();
        if parts.len() != 5 {
            return Err(CronScheduleError(format!(
                "Cron expression must have exactly 5 fields, got {}: '{}'",
                parts.len(),
                cron_expression
            )));
        }

        let minute_set = parse_field(parts[0], 0, 59)?;
        let hour_set = parse_field(parts[1], 0, 23)?;
        let dom_set = parse_field(parts[2], 1, 31)?;
        let month_set = parse_field(parts[3], 1, 12)?;
        let dow_set = parse_field(parts[4], 0, 6)?;

        // Start searching from the next whole minute after `after`.
        let mut candidate = truncate_to_minute(after)
            .checked_add_signed(Duration::minutes(1))
            .ok_or_else(|| CronScheduleError("time overflow computing candidate".to_string()))?;

        // Cap iteration to prevent infinite loops on impossible expressions
        // (e.g., "0 9 31 2 *" — Feb 31 never exists).
        let limit = add_years(candidate, 5)?;

        while candidate <= limit {
            // Month check.
            if !month_set.contains(&candidate.month()) {
                candidate = advance_to_next_month(candidate, &month_set)?;
                continue;
            }

            // Day-of-month check.
            if !dom_set.contains(&candidate.day()) {
                candidate = date_only(add_days(candidate, 1)?);
                continue;
            }

            // Day-of-week check (0=Sunday).
            if !dow_set.contains(&day_of_week_sunday0(candidate)) {
                candidate = date_only(add_days(candidate, 1)?);
                continue;
            }

            // Hour check.
            if !hour_set.contains(&candidate.hour()) {
                candidate = advance_to_next_hour(candidate, &hour_set)?;
                continue;
            }

            // Minute check.
            if !minute_set.contains(&candidate.minute()) {
                candidate = candidate
                    .checked_add_signed(Duration::minutes(1))
                    .ok_or_else(|| CronScheduleError("time overflow".to_string()))?;
                continue;
            }

            // All fields match.
            return Ok(candidate);
        }

        Err(CronScheduleError(format!(
            "No occurrence found within 5 years for cron expression '{cron_expression}'."
        )))
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Parsing helpers
// ─────────────────────────────────────────────────────────────────────────────

/// Parses one cron field into the set of matching integer values.
fn parse_field(field: &str, min: u32, max: u32) -> Result<HashSet<u32>, CronScheduleError> {
    let mut result = HashSet::new();
    for part in field.split(',') {
        parse_part(part.trim(), min, max, &mut result)?;
    }
    Ok(result)
}

fn parse_part(
    part: &str,
    min: u32,
    max: u32,
    result: &mut HashSet<u32>,
) -> Result<(), CronScheduleError> {
    // */N  or  N-M/S  or  N-M
    let mut step: Option<u32> = None;
    let mut core = part;

    if let Some(slash_idx) = part.find('/') {
        let s: u32 = part[slash_idx + 1..]
            .parse()
            .ok()
            .filter(|v| *v >= 1)
            .ok_or_else(|| {
                CronScheduleError(format!("Invalid step in cron field part '{part}'."))
            })?;
        step = Some(s);
        core = &part[..slash_idx];
    }

    let (range_min, range_max): (u32, u32) = if core == "*" {
        (min, max)
    } else if let Some(dash_idx) = core.find('-') {
        let lo = core[..dash_idx]
            .parse::<u32>()
            .map_err(|_| CronScheduleError(format!("Invalid range in cron field part '{part}'.")))?;
        let hi = core[dash_idx + 1..]
            .parse::<u32>()
            .map_err(|_| CronScheduleError(format!("Invalid range in cron field part '{part}'.")))?;
        (lo, hi)
    } else {
        let v = core
            .parse::<u32>()
            .map_err(|_| CronScheduleError(format!("Invalid value in cron field part '{part}'.")))?;
        (v, v)
    };

    if range_min < min || range_max > max || range_min > range_max {
        return Err(CronScheduleError(format!(
            "Cron field value {range_min}-{range_max} out of range [{min},{max}]."
        )));
    }

    let effective_step = step.unwrap_or(1);
    let mut v = range_min;
    while v <= range_max {
        result.insert(v);
        v += effective_step;
    }
    Ok(())
}

// ─────────────────────────────────────────────────────────────────────────────
// Advancement helpers
// ─────────────────────────────────────────────────────────────────────────────

fn advance_to_next_month(
    dt: DateTime<Utc>,
    month_set: &HashSet<u32>,
) -> Result<DateTime<Utc>, CronScheduleError> {
    let mut year = dt.year();
    let mut month = dt.month() + 1;
    if month > 12 {
        month = 1;
        year += 1;
    }

    while year < dt.year() + 6 {
        if month_set.contains(&month) {
            return ymd_hms(year, month, 1, 0, 0, 0);
        }
        month += 1;
        if month > 12 {
            month = 1;
            year += 1;
        }
    }

    Err(CronScheduleError(
        "No valid month found in cron expression.".to_string(),
    ))
}

fn advance_to_next_hour(
    dt: DateTime<Utc>,
    hour_set: &HashSet<u32>,
) -> Result<DateTime<Utc>, CronScheduleError> {
    // Try subsequent hours today.
    for h in (dt.hour() + 1)..=23 {
        if hour_set.contains(&h) {
            return ymd_hms(dt.year(), dt.month(), dt.day(), h, 0, 0);
        }
    }
    // No valid hour today — move to next day, first valid hour.
    let next_day = date_only(add_days(dt, 1)?);
    let min_hour = *hour_set.iter().min().expect("hour set is non-empty");
    ymd_hms(
        next_day.year(),
        next_day.month(),
        next_day.day(),
        min_hour,
        0,
        0,
    )
}

// ─────────────────────────────────────────────────────────────────────────────
// DateTime helpers (mirror the C# DateTimeOffset arithmetic)
// ─────────────────────────────────────────────────────────────────────────────

/// C# `DayOfWeek` — Sunday = 0 .. Saturday = 6.
fn day_of_week_sunday0(dt: DateTime<Utc>) -> u32 {
    match dt.weekday() {
        Weekday::Sun => 0,
        Weekday::Mon => 1,
        Weekday::Tue => 2,
        Weekday::Wed => 3,
        Weekday::Thu => 4,
        Weekday::Fri => 5,
        Weekday::Sat => 6,
    }
}

/// Truncates to whole minutes (drops seconds + sub-second).
fn truncate_to_minute(dt: DateTime<Utc>) -> DateTime<Utc> {
    Utc.with_ymd_and_hms(dt.year(), dt.month(), dt.day(), dt.hour(), dt.minute(), 0)
        .single()
        .unwrap_or(dt)
}

/// Midnight UTC of the given instant's date (mirrors the C# `Date()` helper).
fn date_only(dt: DateTime<Utc>) -> DateTime<Utc> {
    Utc.with_ymd_and_hms(dt.year(), dt.month(), dt.day(), 0, 0, 0)
        .single()
        .unwrap_or(dt)
}

fn add_days(dt: DateTime<Utc>, days: i64) -> Result<DateTime<Utc>, CronScheduleError> {
    dt.checked_add_signed(Duration::days(days))
        .ok_or_else(|| CronScheduleError("time overflow adding days".to_string()))
}

/// Adds whole calendar years, clamping Feb-29 down to Feb-28 on non-leap
/// targets (matches .NET `AddYears`).
fn add_years(dt: DateTime<Utc>, years: i32) -> Result<DateTime<Utc>, CronScheduleError> {
    let target_year = dt.year() + years;
    let mut day = dt.day();
    // Clamp day for the target month/year.
    let max_day = days_in_month(target_year, dt.month());
    if day > max_day {
        day = max_day;
    }
    Utc.with_ymd_and_hms(
        target_year,
        dt.month(),
        day,
        dt.hour(),
        dt.minute(),
        dt.second(),
    )
    .single()
    .ok_or_else(|| CronScheduleError("invalid date computed adding years".to_string()))
}

fn ymd_hms(
    year: i32,
    month: u32,
    day: u32,
    hour: u32,
    minute: u32,
    second: u32,
) -> Result<DateTime<Utc>, CronScheduleError> {
    Utc.with_ymd_and_hms(year, month, day, hour, minute, second)
        .single()
        .ok_or_else(|| {
            CronScheduleError(format!(
                "invalid date {year:04}-{month:02}-{day:02} {hour:02}:{minute:02}:{second:02}"
            ))
        })
}

fn days_in_month(year: i32, month: u32) -> u32 {
    match month {
        1 | 3 | 5 | 7 | 8 | 10 | 12 => 31,
        4 | 6 | 9 | 11 => 30,
        2 => {
            if (year % 4 == 0 && year % 100 != 0) || year % 400 == 0 {
                29
            } else {
                28
            }
        }
        _ => 30,
    }
}
