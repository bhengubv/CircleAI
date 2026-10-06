// EducationPrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Education;

public sealed record Course(string CourseId, string Name, string Subject, string GradeBand);
public sealed record Lesson(string LessonId, string CourseId, string Title, TimeSpan Duration, int OrderIndex);
public sealed record StudentRecord(string StudentId, string Name, string CourseId, double ProgressPct);

public interface IEducationBoard
{
    void AddCourse(Course c);
    Course? GetCourse(string id);
    void AddLesson(Lesson l);
    IReadOnlyList<Lesson> LessonsFor(string courseId);
    void Enrol(StudentRecord r);
    void UpdateProgress(string studentId, double pct);
    IReadOnlyList<StudentRecord> StudentsFor(string courseId);
    double AvgProgressFor(string courseId);
}

public sealed class InMemoryEducationBoard : IEducationBoard
{
    private readonly ConcurrentDictionary<string, Course> _courses = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lesson> _lessons = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StudentRecord> _students = new(StringComparer.Ordinal);

    public void AddCourse(Course c) { ArgumentNullException.ThrowIfNull(c); _courses[c.CourseId] = c; }
    public Course? GetCourse(string id) => _courses.GetValueOrDefault(id);
    public void AddLesson(Lesson l) { ArgumentNullException.ThrowIfNull(l); _lessons[l.LessonId] = l; }
    public IReadOnlyList<Lesson> LessonsFor(string courseId)
        => _lessons.Values.Where(l => l.CourseId == courseId).OrderBy(l => l.OrderIndex).ToArray();
    public void Enrol(StudentRecord r) { ArgumentNullException.ThrowIfNull(r); _students[r.StudentId] = r; }

    public void UpdateProgress(string studentId, double pct)
    {
        if (!_students.TryGetValue(studentId, out var r)) throw new InvalidOperationException($"Unknown student {studentId}");
        _students[studentId] = r with { ProgressPct = pct };
    }

    public IReadOnlyList<StudentRecord> StudentsFor(string courseId)
        => _students.Values.Where(s => s.CourseId == courseId).ToArray();
    public double AvgProgressFor(string courseId)
    {
        var rows = StudentsFor(courseId);
        return rows.Count == 0 ? 0.0 : rows.Average(r => r.ProgressPct);
    }
}
