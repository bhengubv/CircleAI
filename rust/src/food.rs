//! food — CircleAI food-board primitives.
//!
//! Full Rust port of `src/CircleAI.Food/FoodPrimitives.cs`:
//!
//! - Records [`Recipe`] / [`MealLog`] / [`PantryItem`], the [`IFoodBoard`]
//!   contract, and the deterministic in-memory [`InMemoryFoodBoard`] (recipe
//!   store + ingredient search + meal log + pantry with consumption + expiry).
//!
//! Sync-only; `DateTimeOffset`/`DateTime?` → [`chrono::DateTime<Utc>`] /
//! `Option<DateTime<Utc>>`.

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// (Food) A recipe.
///
/// Mirrors `sealed record Recipe(string RecipeId, string Title,
/// IReadOnlyList<string> Ingredients, IReadOnlyList<string> Steps,
/// int Servings, int PrepMinutes)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Recipe {
    pub recipe_id: String,
    pub title: String,
    pub ingredients: Vec<String>,
    pub steps: Vec<String>,
    pub servings: i32,
    pub prep_minutes: i32,
}

impl Recipe {
    /// Constructs a recipe, mirroring the positional C# record constructor.
    pub fn new(
        recipe_id: impl Into<String>,
        title: impl Into<String>,
        ingredients: Vec<String>,
        steps: Vec<String>,
        servings: i32,
        prep_minutes: i32,
    ) -> Self {
        Self {
            recipe_id: recipe_id.into(),
            title: title.into(),
            ingredients,
            steps,
            servings,
            prep_minutes,
        }
    }
}

/// (Food) A logged meal.
///
/// Mirrors `sealed record MealLog(string LogId, string UserId, string RecipeId,
/// DateTimeOffset AtUtc, int Servings)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MealLog {
    pub log_id: String,
    pub user_id: String,
    pub recipe_id: String,
    pub at_utc: DateTime<Utc>,
    pub servings: i32,
}

impl MealLog {
    /// Constructs a meal log, mirroring the positional C# record constructor.
    pub fn new(
        log_id: impl Into<String>,
        user_id: impl Into<String>,
        recipe_id: impl Into<String>,
        at_utc: DateTime<Utc>,
        servings: i32,
    ) -> Self {
        Self {
            log_id: log_id.into(),
            user_id: user_id.into(),
            recipe_id: recipe_id.into(),
            at_utc,
            servings,
        }
    }
}

/// (Food) A pantry item.
///
/// Mirrors `sealed record PantryItem(string PantryItemId, string Name,
/// double Quantity, string Unit, DateTime? BestBefore)`.
#[derive(Debug, Clone, PartialEq)]
pub struct PantryItem {
    pub pantry_item_id: String,
    pub name: String,
    pub quantity: f64,
    pub unit: String,
    pub best_before: Option<DateTime<Utc>>,
}

impl PantryItem {
    /// Constructs a pantry item, mirroring the positional C# record constructor.
    pub fn new(
        pantry_item_id: impl Into<String>,
        name: impl Into<String>,
        quantity: f64,
        unit: impl Into<String>,
        best_before: Option<DateTime<Utc>>,
    ) -> Self {
        Self {
            pantry_item_id: pantry_item_id.into(),
            name: name.into(),
            quantity,
            unit: unit.into(),
            best_before,
        }
    }
}

/// (Food) The food-board contract.
///
/// Mirrors `interface IFoodBoard`.
pub trait IFoodBoard {
    /// Adds (or overwrites) a recipe.
    fn add_recipe(&self, r: Recipe);
    /// A recipe by id, if any.
    fn get_recipe(&self, id: &str) -> Option<Recipe>;
    /// Recipes whose ingredient list contains `ingredient` (case-insensitive
    /// substring). Panics on blank input (mirrors the C# `ArgumentException`).
    fn search_by_ingredient(&self, ingredient: &str) -> Vec<Recipe>;
    /// Logs a meal.
    fn log(&self, m: MealLog);
    /// Meal logs for a user since `since`, earliest first.
    fn logs_since(&self, user_id: &str, since: DateTime<Utc>) -> Vec<MealLog>;
    /// Stocks (or overwrites) a pantry item.
    fn stock_pantry(&self, p: PantryItem);
    /// Consumes `quantity` of a pantry item (floored at 0). Panics on an unknown
    /// item id (mirrors the C# `InvalidOperationException`).
    fn use_item(&self, pantry_item_id: &str, quantity: f64);
    /// Pantry items with quantity remaining.
    fn pantry(&self) -> Vec<PantryItem>;
    /// Pantry items with a best-before at/before `before`, earliest first.
    fn expiring(&self, before: DateTime<Utc>) -> Vec<PantryItem>;
}

/// (Food) In-memory [`IFoodBoard`].
///
/// Mirrors `sealed class InMemoryFoodBoard`.
pub struct InMemoryFoodBoard {
    recipes: Mutex<HashMap<String, Recipe>>,
    logs: Mutex<Vec<MealLog>>,
    pantry: Mutex<HashMap<String, PantryItem>>,
}

impl InMemoryFoodBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            recipes: Mutex::new(HashMap::new()),
            logs: Mutex::new(Vec::new()),
            pantry: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemoryFoodBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IFoodBoard for InMemoryFoodBoard {
    fn add_recipe(&self, r: Recipe) {
        self.recipes.lock().unwrap().insert(r.recipe_id.clone(), r);
    }

    fn get_recipe(&self, id: &str) -> Option<Recipe> {
        self.recipes.lock().unwrap().get(id).cloned()
    }

    fn search_by_ingredient(&self, ingredient: &str) -> Vec<Recipe> {
        if ingredient.trim().is_empty() {
            panic!("ingredient required");
        }
        let needle = ingredient.to_lowercase();
        self.recipes
            .lock()
            .unwrap()
            .values()
            .filter(|r| r.ingredients.iter().any(|i| i.to_lowercase().contains(&needle)))
            .cloned()
            .collect()
    }

    fn log(&self, m: MealLog) {
        self.logs.lock().unwrap().push(m);
    }

    fn logs_since(&self, user_id: &str, since: DateTime<Utc>) -> Vec<MealLog> {
        let mut hits: Vec<MealLog> = self
            .logs
            .lock()
            .unwrap()
            .iter()
            .filter(|l| l.user_id == user_id && l.at_utc >= since)
            .cloned()
            .collect();
        hits.sort_by(|a, b| a.at_utc.cmp(&b.at_utc));
        hits
    }

    fn stock_pantry(&self, p: PantryItem) {
        self.pantry.lock().unwrap().insert(p.pantry_item_id.clone(), p);
    }

    fn use_item(&self, pantry_item_id: &str, quantity: f64) {
        let mut pantry = self.pantry.lock().unwrap();
        match pantry.get(pantry_item_id) {
            Some(p) => {
                let updated = PantryItem {
                    quantity: (p.quantity - quantity).max(0.0),
                    ..p.clone()
                };
                pantry.insert(pantry_item_id.to_string(), updated);
            }
            None => panic!("Unknown pantry item {pantry_item_id}"),
        }
    }

    fn pantry(&self) -> Vec<PantryItem> {
        self.pantry
            .lock()
            .unwrap()
            .values()
            .filter(|p| p.quantity > 0.0)
            .cloned()
            .collect()
    }

    fn expiring(&self, before: DateTime<Utc>) -> Vec<PantryItem> {
        let mut hits: Vec<PantryItem> = self
            .pantry
            .lock()
            .unwrap()
            .values()
            .filter(|p| p.best_before.is_some_and(|bb| bb <= before))
            .cloned()
            .collect();
        // OrderBy(BestBefore) — all Some here.
        hits.sort_by(|a, b| a.best_before.cmp(&b.best_before));
        hits
    }
}
