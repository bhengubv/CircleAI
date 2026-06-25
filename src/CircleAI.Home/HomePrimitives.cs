// HomePrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Home;

public sealed record Room(string RoomId, string Name, double AreaM2);
public sealed record HomeDevice(string DeviceId, string Name, string Kind, string? RoomId, bool IsOn);
public sealed record MaintenanceTask(string TaskId, string Description, DateTime DueOn, bool Completed);

public interface IHomeBoard
{
    void AddRoom(Room r);
    Room? GetRoom(string id);
    IReadOnlyList<Room> Rooms { get; }
    void AddDevice(HomeDevice d);
    void Toggle(string deviceId, bool on);
    IReadOnlyList<HomeDevice> DevicesIn(string roomId);
    IReadOnlyList<HomeDevice> ActiveDevices { get; }
    void ScheduleTask(MaintenanceTask t);
    void CompleteTask(string taskId);
    IReadOnlyList<MaintenanceTask> UpcomingTasks(DateTime by);
}

public sealed class InMemoryHomeBoard : IHomeBoard
{
    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HomeDevice> _devices = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MaintenanceTask> _tasks = new(StringComparer.Ordinal);

    public void AddRoom(Room r) { ArgumentNullException.ThrowIfNull(r); _rooms[r.RoomId] = r; }
    public Room? GetRoom(string id) => _rooms.GetValueOrDefault(id);
    public IReadOnlyList<Room> Rooms => _rooms.Values.OrderBy(r => r.Name).ToArray();
    public void AddDevice(HomeDevice d) { ArgumentNullException.ThrowIfNull(d); _devices[d.DeviceId] = d; }
    public void Toggle(string deviceId, bool on)
    {
        if (!_devices.TryGetValue(deviceId, out var d)) throw new InvalidOperationException($"Unknown device {deviceId}");
        _devices[deviceId] = d with { IsOn = on };
    }
    public IReadOnlyList<HomeDevice> DevicesIn(string roomId)
        => _devices.Values.Where(d => d.RoomId == roomId).ToArray();
    public IReadOnlyList<HomeDevice> ActiveDevices => _devices.Values.Where(d => d.IsOn).ToArray();
    public void ScheduleTask(MaintenanceTask t) { ArgumentNullException.ThrowIfNull(t); _tasks[t.TaskId] = t; }
    public void CompleteTask(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var t)) throw new InvalidOperationException($"Unknown task {taskId}");
        _tasks[taskId] = t with { Completed = true };
    }
    public IReadOnlyList<MaintenanceTask> UpcomingTasks(DateTime by)
        => _tasks.Values.Where(t => !t.Completed && t.DueOn <= by).OrderBy(t => t.DueOn).ToArray();
}
