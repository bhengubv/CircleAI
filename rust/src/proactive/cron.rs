//! cron.rs
//!
//! `CronExpression` — a minimal 5-field cron parser (`minute hour day-of-month
//! month day-of-week`). Supports `*`, integers, ranges (`1-5`), lists
//! (`1,15,30`), and step values (`*/15`). Day-of-week is `0=Sunday..6=Saturday`.
//! Ported 1:1 from `CronExpression.cs` — same parser, same semantics (day-of-month
//! AND day-of-week must both match).

use std::collections::HashSet;

use chrono::{DateTime, Datelike, Duration, TimeZone, Timelike, Utc};

/// A parsed five-field cron expression.
#[derive(Debug, Clone)]
pub struct CronExpression {
    minutes: HashSet<u32>,
    hours: HashSet<u32>,
    days_of_month: HashSet<u32>,
    months: HashSet<u32>,
    days_of_week: HashSet<u32>,
}

/// A cron parse error (mirrors the C# `FormatException`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CronParseError(pub String);

impl std::fmt::Display for CronParseError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for CronParseError {}

impl CronExpression {
    /// Parses a 5-field cron expression.
    pub fn parse(expression: &str) -> Result<CronExpression, CronParseError> {
        let fields: Vec<&str> = expression
            .split(' ')
            .map(|s| s.trim())
            .filter(|s| !s.is_empty())
            .collect();
        if fields.len() != 5 {
            return Err(CronParseError(format!(
                "Cron expression must have 5 fields, got {}: '{}'",
                fields.len(),
                expression
            )));
        }
        Ok(CronExpression {
            minutes: parse_field(fields[0], 0, 59)?,
            hours: parse_field(fields[1], 0, 23)?,
            days_of_month: parse_field(fields[2], 1, 31)?,
            months: parse_field(fields[3], 1, 12)?,
            days_of_week: parse_field(fields[4], 0, 6)?,
        })
    }

    /// Next UTC time at or after `after` when the expression matches. Bounded to
    /// one year forward; errors if nothing matches (a dead expression).
    pub fn get_next_occurrence(
        &self,
        after: DateTime<Utc>,
    ) -> Result<DateTime<Utc>, CronParseError> {
        // after + 1 minute, truncated to whole minutes.
        let start = after + Duration::minutes(1);
        let mut t = Utc
            .with_ymd_and_hms(
                start.year(),
                start.month(),
                start.day(),
                start.hour(),
                start.minute(),
                0,
            )
            .single()
            .ok_or_else(|| CronParseError("invalid start time".to_string()))?;
        let limit = t + Duration::days(365);
        while t <= limit {
            if self.matches(t) {
                return Ok(t);
            }
            t += Duration::minutes(1);
        }
        Err(CronParseError(
            "Cron expression does not match any time in the next year.".to_string(),
        ))
    }

    /// Whether `moment` matches the expression. Day-of-month AND day-of-week must
    /// both match (the C# settles on AND for predictability).
    pub fn matches(&self, moment: DateTime<Utc>) -> bool {
        if !self.minutes.contains(&moment.minute()) {
            return false;
        }
        if !self.hours.contains(&moment.hour()) {
            return false;
        }
        if !self.days_of_month.contains(&moment.day()) {
            return false;
        }
        if !self.months.contains(&moment.month()) {
            return false;
        }
        // chrono weekday: num_days_from_sunday() gives Sunday=0..Saturday=6.
        if !self
            .days_of_week
            .contains(&moment.weekday().num_days_from_sunday())
        {
            return false;
        }
        true
    }
}

fn parse_field(field: &str, min: u32, max: u32) -> Result<HashSet<u32>, CronParseError> {
    let mut values = HashSet::new();
    for part in field.split(',') {
        expand_part(part.trim(), min, max, &mut values)?;
    }
    if values.is_empty() {
        return Err(CronParseError(format!(
            "Cron field '{field}' resolved to no values."
        )));
    }
    Ok(values)
}

fn expand_part(
    part: &str,
    min: u32,
    max: u32,
    sink: &mut HashSet<u32>,
) -> Result<(), CronParseError> {
    let mut step: u32 = 1;
    let mut base = part;
    if let Some(slash) = part.find('/') {
        let step_str = &part[slash + 1..];
        step = step_str
            .parse::<u32>()
            .ok()
            .filter(|s| *s > 0)
            .ok_or_else(|| CronParseError(format!("Cron step '{part}' is not a positive integer.")))?;
        base = &part[..slash];
    }

    let (range_start, range_end): (u32, u32) = if base == "*" {
        (min, max)
    } else if let Some(dash) = base.find('-') {
        let s = base[..dash]
            .parse::<u32>()
            .map_err(|_| CronParseError(format!("Cron part '{base}' is not an integer.")))?;
        let e = base[dash + 1..]
            .parse::<u32>()
            .map_err(|_| CronParseError(format!("Cron part '{base}' is not an integer.")))?;
        (s, e)
    } else {
        let v = base
            .parse::<u32>()
            .map_err(|_| CronParseError(format!("Cron part '{base}' is not an integer.")))?;
        (v, v)
    };

    if range_start < min || range_end > max || range_start > range_end {
        return Err(CronParseError(format!(
            "Cron part '{base}' out of range [{min},{max}]."
        )));
    }

    let mut v = range_start;
    while v <= range_end {
        sink.insert(v);
        v += step;
    }
    Ok(())
}
