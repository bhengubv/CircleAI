//! business — CircleAI business-board primitives.
//!
//! Full Rust port of `src/CircleAI.Business/BusinessPrimitives.cs`:
//!
//! - Records ([`BusinessUnit`], [`KpiSample`], [`QuarterTarget`]) +
//!   [`IBusinessBoard`] with the deterministic in-memory
//!   [`InMemoryBusinessBoard`] (unit tree, KPI samples + latest lookup, quarter
//!   targets + achievement ratio).
//!
//! The C# `ConcurrentDictionary` collapses to `Mutex`-guarded `HashMap`s and the
//! `_kpis` list to a `Mutex<Vec<_>>`. Missing-value queries return
//! [`f64::NAN`], mirroring the C# `double.NaN`.

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// (Business) A business unit in the org tree.
///
/// Mirrors `sealed record BusinessUnit(string UnitId, string Name,
/// string ParentUnitId, IReadOnlyList<string> KpiTags)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BusinessUnit {
    pub unit_id: String,
    pub name: String,
    pub parent_unit_id: String,
    pub kpi_tags: Vec<String>,
}

impl BusinessUnit {
    /// Constructs a unit, mirroring the positional C# record constructor.
    pub fn new(
        unit_id: impl Into<String>,
        name: impl Into<String>,
        parent_unit_id: impl Into<String>,
        kpi_tags: Vec<String>,
    ) -> Self {
        Self {
            unit_id: unit_id.into(),
            name: name.into(),
            parent_unit_id: parent_unit_id.into(),
            kpi_tags,
        }
    }
}

/// (Business) A KPI sample.
///
/// Mirrors `sealed record KpiSample(string UnitId, string Metric, double Value,
/// DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq)]
pub struct KpiSample {
    pub unit_id: String,
    pub metric: String,
    pub value: f64,
    pub at_utc: DateTime<Utc>,
}

impl KpiSample {
    /// Constructs a KPI sample, mirroring the positional C# record constructor.
    pub fn new(
        unit_id: impl Into<String>,
        metric: impl Into<String>,
        value: f64,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            unit_id: unit_id.into(),
            metric: metric.into(),
            value,
            at_utc,
        }
    }
}

/// (Business) A quarterly target.
///
/// Mirrors `sealed record QuarterTarget(string UnitId, string Metric, int Year,
/// int Quarter, double Target)`.
#[derive(Debug, Clone, PartialEq)]
pub struct QuarterTarget {
    pub unit_id: String,
    pub metric: String,
    pub year: i32,
    pub quarter: i32,
    pub target: f64,
}

impl QuarterTarget {
    /// Constructs a quarter target, mirroring the positional C# record constructor.
    pub fn new(
        unit_id: impl Into<String>,
        metric: impl Into<String>,
        year: i32,
        quarter: i32,
        target: f64,
    ) -> Self {
        Self {
            unit_id: unit_id.into(),
            metric: metric.into(),
            year,
            quarter,
            target,
        }
    }
}

/// (Business) The business board contract.
///
/// Mirrors `interface IBusinessBoard`.
pub trait IBusinessBoard {
    /// Adds (or overwrites) a unit.
    fn add(&self, u: BusinessUnit);
    /// Looks up a unit by id.
    fn get_unit(&self, id: &str) -> Option<BusinessUnit>;
    /// Direct children of `parent_unit_id`.
    fn children_of(&self, parent_unit_id: &str) -> Vec<BusinessUnit>;
    /// Records a KPI sample.
    fn record(&self, s: KpiSample);
    /// The most-recent value for `(unit_id, metric)`, or [`f64::NAN`] if none.
    fn latest_kpi(&self, unit_id: &str, metric: &str) -> f64;
    /// Sets (or overwrites) a quarterly target.
    fn set_target(&self, t: QuarterTarget);
    /// The latest KPI divided by the matching quarter target, or [`f64::NAN`]
    /// when there is no target (or the target is `0`).
    fn target_achievement(&self, unit_id: &str, metric: &str, year: i32, quarter: i32) -> f64;
}

/// (Business) In-memory [`IBusinessBoard`].
///
/// Mirrors `sealed class InMemoryBusinessBoard`.
pub struct InMemoryBusinessBoard {
    units: Mutex<HashMap<String, BusinessUnit>>,
    kpis: Mutex<Vec<KpiSample>>,
    targets: Mutex<HashMap<String, QuarterTarget>>,
}

impl InMemoryBusinessBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            units: Mutex::new(HashMap::new()),
            kpis: Mutex::new(Vec::new()),
            targets: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemoryBusinessBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IBusinessBoard for InMemoryBusinessBoard {
    fn add(&self, u: BusinessUnit) {
        self.units.lock().unwrap().insert(u.unit_id.clone(), u);
    }

    fn get_unit(&self, id: &str) -> Option<BusinessUnit> {
        self.units.lock().unwrap().get(id).cloned()
    }

    fn children_of(&self, parent_unit_id: &str) -> Vec<BusinessUnit> {
        self.units
            .lock()
            .unwrap()
            .values()
            .filter(|u| u.parent_unit_id == parent_unit_id)
            .cloned()
            .collect()
    }

    fn record(&self, s: KpiSample) {
        self.kpis.lock().unwrap().push(s);
    }

    fn latest_kpi(&self, unit_id: &str, metric: &str) -> f64 {
        let kpis = self.kpis.lock().unwrap();
        // C# `OrderByDescending(AtUtc).FirstOrDefault()` — stable, so among
        // equal timestamps the earliest-inserted wins. A forward scan keeping a
        // strictly-greater timestamp reproduces that tie-break.
        let mut best: Option<&KpiSample> = None;
        for k in kpis.iter().filter(|k| k.unit_id == unit_id && k.metric == metric) {
            match best {
                Some(b) if k.at_utc > b.at_utc => best = Some(k),
                None => best = Some(k),
                _ => {}
            }
        }
        best.map(|k| k.value).unwrap_or(f64::NAN)
    }

    fn set_target(&self, t: QuarterTarget) {
        let key = format!("{}/{}/{}Q{}", t.unit_id, t.metric, t.year, t.quarter);
        self.targets.lock().unwrap().insert(key, t);
    }

    fn target_achievement(&self, unit_id: &str, metric: &str, year: i32, quarter: i32) -> f64 {
        let key = format!("{unit_id}/{metric}/{year}Q{quarter}");
        let target = {
            let targets = self.targets.lock().unwrap();
            match targets.get(&key) {
                Some(t) if t.target != 0.0 => t.target,
                _ => return f64::NAN,
            }
        };
        self.latest_kpi(unit_id, metric) / target
    }
}
