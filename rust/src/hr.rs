//! hr — CircleAI HR primitives.
//!
//! Full Rust port of `src/CircleAI.HR/HRPrimitives.cs`:
//!
//! - Records ([`Employee`], [`LeaveRequest`], [`PerformanceReview`]) +
//!   [`IHRBoard`] with the deterministic in-memory [`InMemoryHRBoard`] (hire /
//!   employee registry, leave requests + decisions, performance reviews +
//!   average rating).
//!
//! The C# `ConcurrentDictionary` collapses to `Mutex`-guarded `HashMap`s and the
//! `_reviews` list to a `Mutex<Vec<_>>`. `decimal` salary maps to [`f64`].
//! `DateTime` fields (which carry no offset in the C#) map to [`DateTime<Utc>`].

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// (HR) An employee.
///
/// Mirrors `sealed record Employee(string EmployeeId, string Name, string Role,
/// DateTime HiredOn, decimal Salary, string Currency)`.
#[derive(Debug, Clone, PartialEq)]
pub struct Employee {
    pub employee_id: String,
    pub name: String,
    pub role: String,
    pub hired_on: DateTime<Utc>,
    pub salary: f64,
    pub currency: String,
}

impl Employee {
    /// Constructs an employee, mirroring the positional C# record constructor.
    pub fn new(
        employee_id: impl Into<String>,
        name: impl Into<String>,
        role: impl Into<String>,
        hired_on: DateTime<Utc>,
        salary: f64,
        currency: impl Into<String>,
    ) -> Self {
        Self {
            employee_id: employee_id.into(),
            name: name.into(),
            role: role.into(),
            hired_on,
            salary,
            currency: currency.into(),
        }
    }
}

/// (HR) A leave request.
///
/// Mirrors `sealed record LeaveRequest(string RequestId, string EmployeeId,
/// string Kind, DateTime From, DateTime To, string Status)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LeaveRequest {
    pub request_id: String,
    pub employee_id: String,
    pub kind: String,
    pub from: DateTime<Utc>,
    pub to: DateTime<Utc>,
    pub status: String,
}

impl LeaveRequest {
    /// Constructs a leave request, mirroring the positional C# record constructor.
    pub fn new(
        request_id: impl Into<String>,
        employee_id: impl Into<String>,
        kind: impl Into<String>,
        from: DateTime<Utc>,
        to: DateTime<Utc>,
        status: impl Into<String>,
    ) -> Self {
        Self {
            request_id: request_id.into(),
            employee_id: employee_id.into(),
            kind: kind.into(),
            from,
            to,
            status: status.into(),
        }
    }
}

/// (HR) A performance review.
///
/// Mirrors `sealed record PerformanceReview(string ReviewId, string EmployeeId,
/// DateTime ReviewedOn, int RatingOutOf5, string Notes)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PerformanceReview {
    pub review_id: String,
    pub employee_id: String,
    pub reviewed_on: DateTime<Utc>,
    pub rating_out_of_5: i32,
    pub notes: String,
}

impl PerformanceReview {
    /// Constructs a review, mirroring the positional C# record constructor.
    pub fn new(
        review_id: impl Into<String>,
        employee_id: impl Into<String>,
        reviewed_on: DateTime<Utc>,
        rating_out_of_5: i32,
        notes: impl Into<String>,
    ) -> Self {
        Self {
            review_id: review_id.into(),
            employee_id: employee_id.into(),
            reviewed_on,
            rating_out_of_5,
            notes: notes.into(),
        }
    }
}

/// (HR) The HR board contract.
///
/// Mirrors `interface IHRBoard`.
pub trait IHRBoard {
    /// Hires (or overwrites) an employee.
    fn hire(&self, e: Employee);
    /// Looks up an employee by id.
    fn get_employee(&self, id: &str) -> Option<Employee>;
    /// All employees, ordered by name ascending.
    fn employees(&self) -> Vec<Employee>;
    /// Files (or overwrites) a leave request.
    fn request(&self, r: LeaveRequest);
    /// Sets a leave request's status to `decision`. Panics on an unknown id
    /// (mirrors the C# `InvalidOperationException`).
    fn decide_leave(&self, request_id: &str, decision: &str);
    /// All leave requests whose status is `"Pending"` (case-insensitive).
    fn pending_leaves(&self) -> Vec<LeaveRequest>;
    /// Records a performance review.
    fn review(&self, r: PerformanceReview);
    /// The mean rating for an employee, or `0.0` when they have no reviews.
    fn avg_rating_for(&self, employee_id: &str) -> f64;
}

/// (HR) In-memory [`IHRBoard`].
///
/// Mirrors `sealed class InMemoryHRBoard`.
pub struct InMemoryHRBoard {
    employees: Mutex<HashMap<String, Employee>>,
    leaves: Mutex<HashMap<String, LeaveRequest>>,
    reviews: Mutex<Vec<PerformanceReview>>,
}

impl InMemoryHRBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            employees: Mutex::new(HashMap::new()),
            leaves: Mutex::new(HashMap::new()),
            reviews: Mutex::new(Vec::new()),
        }
    }
}

impl Default for InMemoryHRBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IHRBoard for InMemoryHRBoard {
    fn hire(&self, e: Employee) {
        self.employees.lock().unwrap().insert(e.employee_id.clone(), e);
    }

    fn get_employee(&self, id: &str) -> Option<Employee> {
        self.employees.lock().unwrap().get(id).cloned()
    }

    fn employees(&self) -> Vec<Employee> {
        let mut out: Vec<Employee> = self.employees.lock().unwrap().values().cloned().collect();
        out.sort_by(|a, b| a.name.cmp(&b.name));
        out
    }

    fn request(&self, r: LeaveRequest) {
        self.leaves.lock().unwrap().insert(r.request_id.clone(), r);
    }

    fn decide_leave(&self, request_id: &str, decision: &str) {
        let mut leaves = self.leaves.lock().unwrap();
        match leaves.get(request_id) {
            Some(r) => {
                let updated = LeaveRequest {
                    status: decision.to_string(),
                    ..r.clone()
                };
                leaves.insert(request_id.to_string(), updated);
            }
            None => panic!("Unknown leave request {request_id}"),
        }
    }

    fn pending_leaves(&self) -> Vec<LeaveRequest> {
        self.leaves
            .lock()
            .unwrap()
            .values()
            .filter(|r| r.status.eq_ignore_ascii_case("Pending"))
            .cloned()
            .collect()
    }

    fn review(&self, r: PerformanceReview) {
        self.reviews.lock().unwrap().push(r);
    }

    fn avg_rating_for(&self, employee_id: &str) -> f64 {
        let reviews = self.reviews.lock().unwrap();
        let ratings: Vec<f64> = reviews
            .iter()
            .filter(|r| r.employee_id == employee_id)
            .map(|r| r.rating_out_of_5 as f64)
            .collect();
        if ratings.is_empty() {
            // DefaultIfEmpty(0).Average() → 0.
            0.0
        } else {
            ratings.iter().sum::<f64>() / ratings.len() as f64
        }
    }
}
