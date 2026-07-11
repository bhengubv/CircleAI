# hr_primitives.py
#
# Port of CircleAI.HR HRPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the HR vertical: employees,
# leave requests, performance reviews.
#
# C# ConcurrentDictionary stores map to plain dicts guarded by a single lock;
# the review list is guarded by the same lock (mirroring the C# monitor lock).
# C# decimal Salary maps to decimal.Decimal, DateTime -> datetime. The
# `Employees` property orders by Name (ordinal). PendingLeaves matches
# Status == "Pending" case-insensitively. AvgRatingFor uses
# DefaultIfEmpty(0).Average() semantics: an employee with no reviews averages
# 0.0 (not NaN).

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass, replace
from datetime import datetime
from decimal import Decimal
from typing import Dict, List, Optional


@dataclass(frozen=True, slots=True)
class Employee:
    """Mirrors ``CircleAI.HR.Employee`` — ``record(string EmployeeId,
    string Name, string Role, DateTime HiredOn, decimal Salary, string Currency)``.
    """

    employee_id: str
    name: str
    role: str
    hired_on: datetime
    salary: Decimal
    currency: str


@dataclass(frozen=True, slots=True)
class LeaveRequest:
    """Mirrors ``CircleAI.HR.LeaveRequest`` — ``record(string RequestId,
    string EmployeeId, string Kind, DateTime From, DateTime To, string Status)``.
    """

    request_id: str
    employee_id: str
    kind: str
    from_: datetime
    to: datetime
    status: str


@dataclass(frozen=True, slots=True)
class PerformanceReview:
    """Mirrors ``CircleAI.HR.PerformanceReview`` — ``record(string ReviewId,
    string EmployeeId, DateTime ReviewedOn, int RatingOutOf5, string Notes)``.
    """

    review_id: str
    employee_id: str
    reviewed_on: datetime
    rating_out_of5: int
    notes: str


class IHRBoard(ABC):
    """In-memory board for employees, leave and performance reviews."""

    @abstractmethod
    def hire(self, e: Employee) -> None:
        ...

    @abstractmethod
    def get_employee(self, id: str) -> Optional[Employee]:
        ...

    @property
    @abstractmethod
    def employees(self) -> List[Employee]:
        ...

    @abstractmethod
    def request(self, r: LeaveRequest) -> None:
        ...

    @abstractmethod
    def decide_leave(self, request_id: str, decision: str) -> None:
        ...

    @abstractmethod
    def pending_leaves(self) -> List[LeaveRequest]:
        ...

    @abstractmethod
    def review(self, r: PerformanceReview) -> None:
        ...

    @abstractmethod
    def avg_rating_for(self, employee_id: str) -> float:
        ...


class InMemoryHRBoard(IHRBoard):
    """Thread-safe in-memory :class:`IHRBoard`."""

    def __init__(self) -> None:
        self._employees: Dict[str, Employee] = {}
        self._leaves: Dict[str, LeaveRequest] = {}
        self._reviews: List[PerformanceReview] = []
        self._lock = threading.Lock()

    def hire(self, e: Employee) -> None:
        if e is None:
            raise ValueError("employee must not be None")
        with self._lock:
            self._employees[e.employee_id] = e

    def get_employee(self, id: str) -> Optional[Employee]:
        with self._lock:
            return self._employees.get(id)

    @property
    def employees(self) -> List[Employee]:
        with self._lock:
            return sorted(self._employees.values(), key=lambda e: e.name)

    def request(self, r: LeaveRequest) -> None:
        if r is None:
            raise ValueError("leave request must not be None")
        with self._lock:
            self._leaves[r.request_id] = r

    def decide_leave(self, request_id: str, decision: str) -> None:
        with self._lock:
            r = self._leaves.get(request_id)
            if r is None:
                raise RuntimeError(f"Unknown leave request {request_id}")
            self._leaves[request_id] = replace(r, status=decision)

    def pending_leaves(self) -> List[LeaveRequest]:
        with self._lock:
            return [
                r for r in self._leaves.values() if r.status.casefold() == "pending"
            ]

    def review(self, r: PerformanceReview) -> None:
        if r is None:
            raise ValueError("performance review must not be None")
        with self._lock:
            self._reviews.append(r)

    def avg_rating_for(self, employee_id: str) -> float:
        with self._lock:
            ratings = [
                float(r.rating_out_of5)
                for r in self._reviews
                if r.employee_id == employee_id
            ]
        # C# DefaultIfEmpty(0).Average() -> mean, or 0.0 when there are none.
        if len(ratings) == 0:
            return 0.0
        return sum(ratings) / len(ratings)
