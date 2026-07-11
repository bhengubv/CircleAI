//! retail_test.rs
//!
//! Ports the behaviour of `CircleAI.Retail`: product catalog, stock levels
//! (0 when unknown), sales ledger + stock decrement, today's revenue,
//! top-sellers-since.

use chrono::{Duration, Utc};
use circle_ai::retail::{
    IRetailBoard, InMemoryRetailBoard, Product, Sale, StockLevel,
};

fn seed() -> InMemoryRetailBoard {
    let board = InMemoryRetailBoard::new();
    board.add_product(Product::new("A", "Apple", 2.0, "USD", Some("Fruit".into())));
    board.add_product(Product::new("B", "Bread", 3.0, "USD", None));
    board
}

#[test]
fn stock_defaults_zero_and_set() {
    let board = seed();
    assert_eq!(board.stock("A"), 0);
    board.set_stock(StockLevel::new("A", 50));
    assert_eq!(board.stock("A"), 50);
    assert_eq!(board.get_product("A").unwrap().name, "Apple");
}

#[test]
fn record_sale_decrements_stock() {
    let board = seed();
    board.set_stock(StockLevel::new("A", 10));
    board.record_sale(Sale::new("s1", "A", 3, 2.0, Utc::now()));
    assert_eq!(board.stock("A"), 7);
}

#[test]
#[should_panic(expected = "Unknown SKU")]
fn record_sale_unknown_sku_panics() {
    seed().record_sale(Sale::new("s1", "ZZ", 1, 1.0, Utc::now()));
}

#[test]
fn revenue_today_sums_matching_date() {
    let board = seed();
    let now = Utc::now();
    board.record_sale(Sale::new("s1", "A", 2, 2.0, now));
    board.record_sale(Sale::new("s2", "B", 1, 3.0, now));
    board.record_sale(Sale::new("s3", "A", 5, 2.0, now - Duration::days(2))); // other day
    assert_eq!(board.revenue_today(now), 2.0 * 2.0 + 3.0);
}

#[test]
fn top_sellers_since_orders_by_units() {
    let board = seed();
    let now = Utc::now();
    let since = now - Duration::hours(1);
    board.record_sale(Sale::new("s1", "A", 2, 2.0, now));
    board.record_sale(Sale::new("s2", "A", 3, 2.0, now));
    board.record_sale(Sale::new("s3", "B", 10, 3.0, now));
    board.record_sale(Sale::new("s4", "A", 100, 2.0, now - Duration::days(1))); // before `since`

    let top = board.top_sellers_since(since, 5);
    assert_eq!(top, vec![("B".to_string(), 10), ("A".to_string(), 5)]);
    assert_eq!(board.top_sellers_since(since, 1), vec![("B".to_string(), 10)]);
}
