//! agriculture — CircleAI farm-board primitives.
//!
//! Full Rust port of `src/CircleAI.Agriculture/AgriculturePrimitives.cs`:
//!
//! - Records [`Field`] / [`Crop`] / [`YieldRecord`], the [`IFarmBoard`]
//!   contract, and the deterministic in-memory [`InMemoryFarmBoard`] (fields +
//!   crops + yield log + average-yield-by-variety join).
//!
//! Sync-only; `DateTime`/`DateTime?` → [`chrono::DateTime<Utc>`] /
//! `Option<DateTime<Utc>>`.

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// (Agriculture) A field.
///
/// Mirrors `sealed record Field(string FieldId, double AreaHa, string SoilType,
/// string IrrigationKind)`.
#[derive(Debug, Clone, PartialEq)]
pub struct Field {
    pub field_id: String,
    pub area_ha: f64,
    pub soil_type: String,
    pub irrigation_kind: String,
}

impl Field {
    /// Constructs a field, mirroring the positional C# record constructor.
    pub fn new(
        field_id: impl Into<String>,
        area_ha: f64,
        soil_type: impl Into<String>,
        irrigation_kind: impl Into<String>,
    ) -> Self {
        Self {
            field_id: field_id.into(),
            area_ha,
            soil_type: soil_type.into(),
            irrigation_kind: irrigation_kind.into(),
        }
    }
}

/// (Agriculture) A planted crop.
///
/// Mirrors `sealed record Crop(string CropId, string FieldId, string Variety,
/// DateTime PlantedOn, DateTime? ExpectedHarvest)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Crop {
    pub crop_id: String,
    pub field_id: String,
    pub variety: String,
    pub planted_on: DateTime<Utc>,
    pub expected_harvest: Option<DateTime<Utc>>,
}

impl Crop {
    /// Constructs a crop, mirroring the positional C# record constructor.
    pub fn new(
        crop_id: impl Into<String>,
        field_id: impl Into<String>,
        variety: impl Into<String>,
        planted_on: DateTime<Utc>,
        expected_harvest: Option<DateTime<Utc>>,
    ) -> Self {
        Self {
            crop_id: crop_id.into(),
            field_id: field_id.into(),
            variety: variety.into(),
            planted_on,
            expected_harvest,
        }
    }
}

/// (Agriculture) A harvest yield record.
///
/// Mirrors `sealed record YieldRecord(string CropId, double TonsPerHa,
/// DateTime HarvestedOn)`.
#[derive(Debug, Clone, PartialEq)]
pub struct YieldRecord {
    pub crop_id: String,
    pub tons_per_ha: f64,
    pub harvested_on: DateTime<Utc>,
}

impl YieldRecord {
    /// Constructs a yield record, mirroring the positional C# record constructor.
    pub fn new(crop_id: impl Into<String>, tons_per_ha: f64, harvested_on: DateTime<Utc>) -> Self {
        Self {
            crop_id: crop_id.into(),
            tons_per_ha,
            harvested_on,
        }
    }
}

/// (Agriculture) The farm-board contract.
///
/// Mirrors `interface IFarmBoard`.
pub trait IFarmBoard {
    /// Adds (or overwrites) a field.
    fn add_field(&self, f: Field);
    /// Plants (or overwrites) a crop.
    fn plant(&self, c: Crop);
    /// Records a yield.
    fn record_yield(&self, y: YieldRecord);
    /// A field by id, if any.
    fn get_field(&self, id: &str) -> Option<Field>;
    /// Crops planted on a field, earliest-planted first.
    fn crops_for_field(&self, field_id: &str) -> Vec<Crop>;
    /// The average tons/ha across all yields whose crop's variety matches
    /// (case-insensitive); `0.0` when there are none.
    fn avg_yield_of_variety(&self, variety: &str) -> f64;
}

/// (Agriculture) In-memory [`IFarmBoard`].
///
/// Mirrors `sealed class InMemoryFarmBoard`.
pub struct InMemoryFarmBoard {
    fields: Mutex<HashMap<String, Field>>,
    crops: Mutex<HashMap<String, Crop>>,
    yields: Mutex<Vec<YieldRecord>>,
}

impl InMemoryFarmBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            fields: Mutex::new(HashMap::new()),
            crops: Mutex::new(HashMap::new()),
            yields: Mutex::new(Vec::new()),
        }
    }
}

impl Default for InMemoryFarmBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IFarmBoard for InMemoryFarmBoard {
    fn add_field(&self, f: Field) {
        self.fields.lock().unwrap().insert(f.field_id.clone(), f);
    }

    fn plant(&self, c: Crop) {
        self.crops.lock().unwrap().insert(c.crop_id.clone(), c);
    }

    fn record_yield(&self, y: YieldRecord) {
        self.yields.lock().unwrap().push(y);
    }

    fn get_field(&self, id: &str) -> Option<Field> {
        self.fields.lock().unwrap().get(id).cloned()
    }

    fn crops_for_field(&self, field_id: &str) -> Vec<Crop> {
        let mut hits: Vec<Crop> = self
            .crops
            .lock()
            .unwrap()
            .values()
            .filter(|c| c.field_id == field_id)
            .cloned()
            .collect();
        hits.sort_by(|a, b| a.planted_on.cmp(&b.planted_on));
        hits
    }

    fn avg_yield_of_variety(&self, variety: &str) -> f64 {
        let crops = self.crops.lock().unwrap();
        let yields = self.yields.lock().unwrap();
        let target = variety.to_lowercase();
        let rows: Vec<f64> = yields
            .iter()
            .filter(|y| {
                crops
                    .get(&y.crop_id)
                    .is_some_and(|c| c.variety.to_lowercase() == target)
            })
            .map(|y| y.tons_per_ha)
            .collect();
        if rows.is_empty() {
            0.0
        } else {
            rows.iter().sum::<f64>() / rows.len() as f64
        }
    }
}

/// StubGuard parity additions — concrete-only helpers on the in-memory board
/// (mirroring the C# members added to `InMemoryFarmBoard`/`IFarmBoard`).
impl InMemoryFarmBoard {
    /// Number of fields. Mirrors `FieldCount`.
    pub fn field_count(&self) -> usize {
        self.fields.lock().unwrap().len()
    }

    /// Removes a field by id. Returns `true` if present. Mirrors `RemoveField`.
    pub fn remove_field(&self, field_id: &str) -> bool {
        self.fields.lock().unwrap().remove(field_id).is_some()
    }

    /// Total area (ha) across every field. Mirrors `TotalAreaHa`.
    pub fn total_area_ha(&self) -> f64 {
        self.fields.lock().unwrap().values().map(|f| f.area_ha).sum()
    }

    /// Fields of a given soil type (case-insensitive), largest area first. Mirrors
    /// `FieldsBySoil`.
    pub fn fields_by_soil(&self, soil_type: &str) -> Vec<Field> {
        let mut hits: Vec<Field> = self
            .fields
            .lock()
            .unwrap()
            .values()
            .filter(|f| f.soil_type.eq_ignore_ascii_case(soil_type))
            .cloned()
            .collect();
        hits.sort_by(|a, b| b.area_ha.partial_cmp(&a.area_ha).unwrap_or(std::cmp::Ordering::Equal));
        hits
    }

    /// Crops whose expected harvest is on or before `as_of`, earliest-harvest
    /// first. Crops without an expected-harvest date are excluded. Mirrors
    /// `DueForHarvest`.
    pub fn due_for_harvest(&self, as_of: DateTime<Utc>) -> Vec<Crop> {
        let mut hits: Vec<Crop> = self
            .crops
            .lock()
            .unwrap()
            .values()
            .filter(|c| c.expected_harvest.is_some_and(|h| h <= as_of))
            .cloned()
            .collect();
        hits.sort_by(|a, b| a.expected_harvest.cmp(&b.expected_harvest));
        hits
    }

    /// The crop variety with the highest average tons/ha across recorded yields,
    /// if any (grouped case-insensitively, first-seen casing kept). Mirrors
    /// `BestYieldingVariety`.
    pub fn best_yielding_variety(&self) -> Option<String> {
        let crops = self.crops.lock().unwrap();
        let yields = self.yields.lock().unwrap();
        // Sum tons/ha per variety (case-insensitive key, first-seen display casing).
        let mut order: Vec<String> = Vec::new();
        let mut acc: HashMap<String, (String, f64, usize)> = HashMap::new();
        for y in yields.iter() {
            if let Some(c) = crops.get(&y.crop_id) {
                let key = c.variety.to_lowercase();
                match acc.get_mut(&key) {
                    Some(e) => {
                        e.1 += y.tons_per_ha;
                        e.2 += 1;
                    }
                    None => {
                        order.push(key.clone());
                        acc.insert(key, (c.variety.clone(), y.tons_per_ha, 1));
                    }
                }
            }
        }
        let mut ranked: Vec<(String, f64)> = order
            .into_iter()
            .map(|k| {
                let (name, sum, n) = acc.remove(&k).unwrap();
                (name, sum / n as f64)
            })
            .collect();
        ranked.sort_by(|a, b| b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal));
        ranked.into_iter().next().map(|(name, _)| name)
    }
}
