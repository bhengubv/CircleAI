// HRPrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.HR;

public sealed record Employee(string EmployeeId, string Name, string Role, DateTime HiredOn, decimal Salary, string Currency);
public sealed record LeaveRequest(string RequestId, string EmployeeId, string Kind, DateTime From, DateTime To, string Status);
public sealed record PerformanceReview(string ReviewId, string EmployeeId, DateTime ReviewedOn, int RatingOutOf5, string Notes);

public interface IHRBoard
{
    void Hire(Employee e);
    Employee? GetEmployee(string id);
    IReadOnlyList<Employee> Employees { get; }
    void Request(LeaveRequest r);
    void DecideLeave(string requestId, string decision);
    IReadOnlyList<LeaveRequest> PendingLeaves();
    void Review(PerformanceReview r);
    double AvgRatingFor(string employeeId);
}

public sealed class InMemoryHRBoard : IHRBoard
{
    private readonly ConcurrentDictionary<string, Employee> _employees = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LeaveRequest> _leaves = new(StringComparer.Ordinal);
    private readonly List<PerformanceReview> _reviews = new();
    private readonly object _lock = new();

    public void Hire(Employee e) { ArgumentNullException.ThrowIfNull(e); _employees[e.EmployeeId] = e; }
    public Employee? GetEmployee(string id) => _employees.GetValueOrDefault(id);
    public IReadOnlyList<Employee> Employees => _employees.Values.OrderBy(e => e.Name).ToArray();
    public void Request(LeaveRequest r) { ArgumentNullException.ThrowIfNull(r); _leaves[r.RequestId] = r; }
    public void DecideLeave(string requestId, string decision)
    {
        if (!_leaves.TryGetValue(requestId, out var r)) throw new InvalidOperationException($"Unknown leave request {requestId}");
        _leaves[requestId] = r with { Status = decision };
    }
    public IReadOnlyList<LeaveRequest> PendingLeaves()
        => _leaves.Values.Where(r => string.Equals(r.Status, "Pending", StringComparison.OrdinalIgnoreCase)).ToArray();
    public void Review(PerformanceReview r) { ArgumentNullException.ThrowIfNull(r); lock (_lock) _reviews.Add(r); }
    public double AvgRatingFor(string employeeId)
    { lock (_lock) return _reviews.Where(r => r.EmployeeId == employeeId).Select(r => (double)r.RatingOutOf5).DefaultIfEmpty(0).Average(); }
}
