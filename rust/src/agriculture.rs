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
