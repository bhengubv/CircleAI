//! commerce_primitives.rs
//!
//! (3.3.0) Real domain types + in-memory store for the Commerce vertical — Rust
//! port of `src/CircleAI.Commerce/CommercePrimitives.cs`: customers, orders,
//! line items, lifetime value.
//!
//! `decimal` money → [`f64`]. The C# store mixes a
//! `ConcurrentDictionary<string, T>` (customers, orders) with a `List<T>` +
//! `object _lock` (line items); here `Mutex`-guarded `HashMap`s / `Vec` mirror
//! that. `OrdersFor` reproduces `OrderByDescending(o => o.AtUtc)` (stable).

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// (3.3.0) A commerce customer.
///
/// Mirrors `sealed record CommerceCustomer(string CustomerId, string Name,
/// string? Email, DateTimeOffset CreatedUtc)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CommerceCustomer {
    pub customer_id: String,
    pub name: String,
    pub email: Option<String>,
    pub created_utc: DateTime<Utc>,
}

impl CommerceCustomer {
    /// Constructs a customer, mirroring the positional C# record constructor.
    pub fn new(
        customer_id: impl Into<String>,
        name: impl Into<String>,
        email: Option<String>,
        created_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            customer_id: customer_id.into(),
            name: name.into(),
            email,
            created_utc,
        }
    }
}

/// (3.3.0) An order.
///
/// Mirrors `sealed record CommerceOrder(string OrderId, string CustomerId,
/// decimal Total, string Currency, string Status, DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq)]
pub struct CommerceOrder {
    pub order_id: String,
    pub customer_id: String,
    pub total: f64,
    pub currency: String,
    pub status: String,
    pub at_utc: DateTime<Utc>,
}

impl CommerceOrder {
    /// Constructs an order, mirroring the positional C# record constructor.
    pub fn new(
        order_id: impl Into<String>,
        customer_id: impl Into<String>,
        total: f64,
        currency: impl Into<String>,
        status: impl Into<String>,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            order_id: order_id.into(),
            customer_id: customer_id.into(),
            total,
            currency: currency.into(),
            status: status.into(),
            at_utc,
        }
    }
}

/// (3.3.0) A line item on an order.
///
/// Mirrors `sealed record CommerceLineItem(string LineId, string OrderId,
/// string Sku, int Quantity, decimal UnitPrice)`.
#[derive(Debug, Clone, PartialEq)]
pub struct CommerceLineItem {
    pub line_id: String,
    pub order_id: String,
    pub sku: String,
    pub quantity: i32,
    pub unit_price: f64,
}

impl CommerceLineItem {
    /// Constructs a line item, mirroring the positional C# record constructor.
    pub fn new(
        line_id: impl Into<String>,
        order_id: impl Into<String>,
        sku: impl Into<String>,
        quantity: i32,
        unit_price: f64,
    ) -> Self {
        Self {
            line_id: line_id.into(),
            order_id: order_id.into(),
            sku: sku.into(),
            quantity,
            unit_price,
        }
    }
}

/// (3.3.0) The Commerce board contract.
///
/// Mirrors `interface ICommerceBoard`.
pub trait ICommerceBoard {
    /// Adds (or overwrites) a customer.
    fn add_customer(&self, c: CommerceCustomer);
    /// Looks up a customer by id.
    fn get_customer(&self, id: &str) -> Option<CommerceCustomer>;
    /// Places (or overwrites) an order.
    fn place(&self, o: CommerceOrder);
    /// Appends a line item.
    fn add_line(&self, l: CommerceLineItem);
    /// Updates an order's status. Panics on an unknown id (C#
    /// `InvalidOperationException`).
    fn update_status(&self, order_id: &str, status: &str);
    /// Orders for a customer, newest-first.
    fn orders_for(&self, customer_id: &str) -> Vec<CommerceOrder>;
    /// Line items for an order (insertion order).
    fn lines_for(&self, order_id: &str) -> Vec<CommerceLineItem>;
    /// Sum of a customer's order totals.
    fn lifetime_value(&self, customer_id: &str) -> f64;
}

/// (3.3.0) In-memory [`ICommerceBoard`].
pub struct InMemoryCommerceBoard {
    customers: Mutex<HashMap<String, CommerceCustomer>>,
    orders: Mutex<HashMap<String, CommerceOrder>>,
    lines: Mutex<Vec<CommerceLineItem>>,
}

impl InMemoryCommerceBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            customers: Mutex::new(HashMap::new()),
            orders: Mutex::new(HashMap::new()),
            lines: Mutex::new(Vec::new()),
        }
    }
}

impl Default for InMemoryCommerceBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl ICommerceBoard for InMemoryCommerceBoard {
    fn add_customer(&self, c: CommerceCustomer) {
        self.customers
            .lock()
            .unwrap()
            .insert(c.customer_id.clone(), c);
    }

    fn get_customer(&self, id: &str) -> Option<CommerceCustomer> {
        self.customers.lock().unwrap().get(id).cloned()
    }

    fn place(&self, o: CommerceOrder) {
        self.orders.lock().unwrap().insert(o.order_id.clone(), o);
    }

    fn add_line(&self, l: CommerceLineItem) {
        self.lines.lock().unwrap().push(l);
    }

    fn update_status(&self, order_id: &str, status: &str) {
        let mut orders = self.orders.lock().unwrap();
        match orders.get(order_id) {
            Some(o) => {
                let updated = CommerceOrder {
                    status: status.to_string(),
                    ..o.clone()
                };
                orders.insert(order_id.to_string(), updated);
            }
            None => panic!("Unknown order {order_id}"),
        }
    }

    fn orders_for(&self, customer_id: &str) -> Vec<CommerceOrder> {
        let mut out: Vec<CommerceOrder> = self
            .orders
            .lock()
            .unwrap()
            .values()
            .filter(|o| o.customer_id == customer_id)
            .cloned()
            .collect();
        out.sort_by(|a, b| b.at_utc.cmp(&a.at_utc));
        out
    }

    fn lines_for(&self, order_id: &str) -> Vec<CommerceLineItem> {
        self.lines
            .lock()
            .unwrap()
            .iter()
            .filter(|l| l.order_id == order_id)
            .cloned()
            .collect()
    }

    fn lifetime_value(&self, customer_id: &str) -> f64 {
        self.orders_for(customer_id).iter().map(|o| o.total).sum()
    }
}
