//! food_test.rs
//!
//! Ports the behaviour of `CircleAI.Food`: recipe store + case-insensitive
//! ingredient search + meal log + pantry consumption + expiry filter.

use chrono::{Duration, TimeZone, Utc};
use circle_ai::food::{IFoodBoard, InMemoryFoodBoard, MealLog, PantryItem, Recipe};

#[test]
fn recipe_add_get_and_ingredient_search() {
    let board = InMemoryFoodBoard::new();
    board.add_recipe(Recipe::new(
        "r1",
        "Tomato Soup",
        vec!["Tomato".into(), "Basil".into()],
        vec!["Simmer".into()],
        4,
        30,
    ));
    board.add_recipe(Recipe::new("r2", "Salad", vec!["Lettuce".into()], vec![], 2, 10));

    assert_eq!(board.get_recipe("r1").unwrap().title, "Tomato Soup");
    // Case-insensitive substring.
    let hits = board.search_by_ingredient("tomato");
    assert_eq!(hits.len(), 1);
    assert_eq!(hits[0].recipe_id, "r1");
}

#[test]
#[should_panic(expected = "ingredient required")]
fn search_blank_ingredient_panics() {
    InMemoryFoodBoard::new().search_by_ingredient("   ");
}

#[test]
fn logs_since_ordered() {
    let board = InMemoryFoodBoard::new();
    let base = Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap();
    board.log(MealLog::new("l2", "u", "r1", base + Duration::hours(2), 1));
    board.log(MealLog::new("l1", "u", "r1", base, 2));
    board.log(MealLog::new("l3", "other", "r1", base, 1));

    let logs = board.logs_since("u", base);
    assert_eq!(logs.len(), 2);
    assert_eq!(logs[0].log_id, "l1"); // earliest first
}

#[test]
fn pantry_consumption_floors_at_zero() {
    let board = InMemoryFoodBoard::new();
    board.stock_pantry(PantryItem::new("p1", "Flour", 1000.0, "g", None));
    board.use_item("p1", 300.0);
    let pantry = board.pantry();
    assert_eq!(pantry.len(), 1);
    assert!((pantry[0].quantity - 700.0).abs() < 1e-9);

    // Consume more than remaining → floored at 0 → dropped from `pantry()`.
    board.use_item("p1", 5000.0);
    assert_eq!(board.pantry().len(), 0);
}

#[test]
#[should_panic(expected = "Unknown pantry item")]
fn use_unknown_item_panics() {
    InMemoryFoodBoard::new().use_item("nope", 1.0);
}

#[test]
fn expiring_filters_and_orders() {
    let board = InMemoryFoodBoard::new();
    let d = |day| Utc.with_ymd_and_hms(2026, 1, day, 0, 0, 0).unwrap();
    board.stock_pantry(PantryItem::new("p1", "Milk", 1.0, "l", Some(d(5))));
    board.stock_pantry(PantryItem::new("p2", "Eggs", 6.0, "ct", Some(d(3))));
    board.stock_pantry(PantryItem::new("p3", "Salt", 1.0, "kg", None)); // no expiry

    let exp = board.expiring(d(10));
    assert_eq!(exp.len(), 2);
    assert_eq!(exp[0].pantry_item_id, "p2"); // earliest best-before first
    assert_eq!(exp[1].pantry_item_id, "p1");
}
