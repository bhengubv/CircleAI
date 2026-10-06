// HospitalityPrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Hospitality;

public sealed record HotelRoom(string RoomId, string Type, decimal NightlyRate, string Currency, bool IsClean);
public sealed record GuestReservation(string ReservationId, string GuestName, string RoomId, DateTime CheckIn, DateTime CheckOut);
public sealed record FrontDeskNote(string NoteId, string ReservationId, string Body, DateTimeOffset AtUtc);

public interface IHospitalityBoard
{
    void AddRoom(HotelRoom r);
    HotelRoom? GetRoom(string id);
    IReadOnlyList<HotelRoom> AvailableOn(DateTime date);
    void Reserve(GuestReservation r);
    void CheckOut(string reservationId, bool roomNeedsCleaning);
    GuestReservation? GetReservation(string id);
    void AddNote(FrontDeskNote n);
    IReadOnlyList<FrontDeskNote> NotesFor(string reservationId);
}

public sealed class InMemoryHospitalityBoard : IHospitalityBoard
{
    private readonly ConcurrentDictionary<string, HotelRoom> _rooms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GuestReservation> _res = new(StringComparer.Ordinal);
    private readonly List<FrontDeskNote> _notes = new();
    private readonly object _lock = new();

    public void AddRoom(HotelRoom r) { ArgumentNullException.ThrowIfNull(r); _rooms[r.RoomId] = r; }
    public HotelRoom? GetRoom(string id) => _rooms.GetValueOrDefault(id);
    public IReadOnlyList<HotelRoom> AvailableOn(DateTime date)
    {
        var booked = _res.Values.Where(r => r.CheckIn <= date && r.CheckOut > date).Select(r => r.RoomId).ToHashSet();
        return _rooms.Values.Where(r => !booked.Contains(r.RoomId) && r.IsClean).ToArray();
    }
    public void Reserve(GuestReservation r) { ArgumentNullException.ThrowIfNull(r); _res[r.ReservationId] = r; }
    public void CheckOut(string reservationId, bool roomNeedsCleaning)
    {
        if (!_res.TryGetValue(reservationId, out var r)) throw new InvalidOperationException($"Unknown reservation {reservationId}");
        if (roomNeedsCleaning && _rooms.TryGetValue(r.RoomId, out var room))
            _rooms[r.RoomId] = room with { IsClean = false };
    }
    public GuestReservation? GetReservation(string id) => _res.GetValueOrDefault(id);
    public void AddNote(FrontDeskNote n) { ArgumentNullException.ThrowIfNull(n); lock (_lock) _notes.Add(n); }
    public IReadOnlyList<FrontDeskNote> NotesFor(string reservationId)
    { lock (_lock) return _notes.Where(n => n.ReservationId == reservationId).OrderByDescending(n => n.AtUtc).ToArray(); }
}
