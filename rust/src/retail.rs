//! retail — CircleAI retail-board primitives.
//!
//! Full Rust port of `src/CircleAI.Retail/RetailPrimitives.cs`:
//!
//! - Records ([`Product`], [`StockLevel`], [`Sale`]) + [`IRetailBoard`] with the
//!   deterministic in-memory [`InMemoryRetailBoard`] (product catalog, stock
//!   levels, sales ledger, today's revenue, top-sellers-since).
//!
//! `decimal` price maps to [`f64`]. `RevenueToday` compares the **date** part of
//! `AtUtc` (like the C# `DateTimeOffset.Date`). The C# value tuple
//! `(string Sku, int Sold)` maps to `(String, i32)`.

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// (Retail) A product.
///
/// Mirrors `sealed record Product(string Sku, string Name, decimal Price,
/// string Currency, string? Category)`.
#[derive(Debug, Clone, PartialEq)]
pub struct Product {
    pub sku: String,
    pub name: String,
    pub price: f64,
    pub currency: String,
    pub category: Option<String>,
}

impl Product {
    /// Constructs a product, mirroring the positional C# record constructor.
    pub fn new(
        sku: impl Into<String>,
        name: impl Into<String>,
        price: f64,
        currency: impl Into<String>,
        category: Option<String>,
    ) -> Self {
        Self {
            sku: sku.into(),
            name: name.into(),
            price,
            currency: currency.into(),
            category,
        }
    }
}

/// (Retail) A stock level.
///
/// Mirrors `sealed record StockLevel(string Sku, int Quantity)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StockLevel {
    pub sku: String,
    pub quantity: i32,
}

impl StockLevel {
    /// Constructs a stock level, mirroring the positional C# record constructor.
    pub fn new(sku: impl Into<String>, quantity: i32) -> Self {
        Self {
            sku: sku.into(),
            quantity,
        }
    }
}

/// (Retail) A recorded sale.
///
/// Mirrors `sealed record Sale(string SaleId, string Sku, int Quantity,
/// decimal UnitPrice, DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq)]
pub struct Sale {
    pub sale_id: String,
    pub sku: String,
    pub quantity: i32,
    pub unit_price: f64,
    pub at_utc: DateTime<Utc>,
}

impl Sale {
    /// Constructs a sale, mirroring the positional C# record constructor.
    pub fn new(
        sale_id: impl Into<String>,
        sku: impl Into<String>,
        quantity: i32,
        unit_price: f64,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            sale_id: sale_id.into(),
            sku: sku.into(),
            quantity,
            unit_price,
            at_utc,
        }
    }
}

/// (Retail) The retail board contract.
///
/// Mirrors `interface IRetailBoard`.
pub trait IRetailBoard {
    /// Adds (or overwrites) a product.
    fn add_product(&self, p: Product);
    /// Looks up a product by SKU.
    fn get_product(&self, sku: &str) -> Option<Product>;
    /// Sets the stock level for a SKU.
    fn set_stock(&self, l: StockLevel);
    /// The stock for `sku`, or `0` when unknown.
    fn stock(&self, sku: &str) -> i32;
    /// Records a sale and decrements stock. Panics on an unknown SKU (mirrors
    /// the C# `InvalidOperationException`).
    fn record_sale(&self, s: Sale);
    /// Total revenue for sales whose date matches `now`'s date.
    fn revenue_today(&self, now: DateTime<Utc>) -> f64;
    /// Up to `top_k` `(sku, sold)` pairs for sales at/after `since`, ordered by
    /// units sold descending.
    fn top_sellers_since(&self, since: DateTime<Utc>, top_k: usize) -> Vec<(String, i32)>;
}

/// (Retail) In-memory [`IRetailBoard`].
///
/// Mirrors `sealed class InMemoryRetailBoard`.
pub struct InMemoryRetailBoard {
    products: Mutex<HashMap<String, Product>>,
    stock: Mutex<HashMap<String, i32>>,
    sales: Mutex<Vec<Sale>>,
}

impl InMemoryRetailBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            products: Mutex::new(HashMap::new()),
            stock: Mutex::new(HashMap::new()),
            sales: Mutex::new(Vec::new()),
        }
    }
}

impl Default for InMemoryRetailBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IRetailBoard for InMemoryRetailBoard {
    fn add_product(&self, p: Product) {
        self.products.lock().unwrap().insert(p.sku.clone(), p);
    }

    fn get_product(&self, sku: &str) -> Option<Product> {
        self.products.lock().unwrap().get(sku).cloned()
    }

    fn set_stock(&self, l: StockLevel) {
        self.stock.lock().unwrap().insert(l.sku.clone(), l.quantity);
    }

    fn stock(&self, sku: &str) -> i32 {
        self.stock.lock().unwrap().get(sku).copied().unwrap_or(0)
    }

    fn record_sale(&self, s: Sale) {
        if !self.products.lock().unwrap().contains_key(&s.sku) {
            panic!("Unknown SKU {}", s.sku);
        }
        let mut stock = self.stock.lock().unwrap();
        let current = stock.get(&s.sku).copied().unwrap_or(0);
        stock.insert(s.sku.clone(), current - s.quantity);
        drop(stock);
        self.sales.lock().unwrap().push(s);
    }

    fn revenue_today(&self, now: DateTime<Utc>) -> f64 {
        let today = now.date_naive();
        self.sales
            .lock()
            .unwrap()
            .iter()
            .filter(|s| s.at_utc.date_naive() == today)
            .map(|s| s.unit_price * s.quantity as f64)
            .sum()
    }

    fn top_sellers_since(&self, since: DateTime<Utc>, top_k: usize) -> Vec<(String, i32)> {
        if top_k == 0 {
            panic!("topK out of range");
        }
        let sales = self.sales.lock().unwrap();
        // GroupBy(Sku).Select(Sum(Quantity)).OrderByDescending(Sold).Take(topK).
        // A BTreeMap keyed by SKU makes the pre-sort grouping deterministic; the
        // descending sort by units is then stable across the sorted keys.
        let mut totals: std::collections::BTreeMap<String, i32> = std::collections::BTreeMap::new();
        for s in sales.iter().filter(|s| s.at_utc >= since) {
            *totals.entry(s.sku.clone()).or_insert(0) += s.quantity;
        }
        let mut grouped: Vec<(String, i32)> = totals.into_iter().collect();
        grouped.sort_by(|a, b| b.1.cmp(&a.1));
        grouped.truncate(top_k);
        grouped
    }
}
