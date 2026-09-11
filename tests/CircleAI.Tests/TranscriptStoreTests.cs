// TranscriptStoreTests.cs
//
// Somewhere for a transcript to live.
//
// THIS IS WHAT "SEARCH ACROSS EVERYTHING" WAS ACTUALLY MISSING. The index and
// the ranking were built first, and they had almost nothing to search: a
// transcript was shown on a screen, optionally written out as an .srt to a
// location the person chose, and then discarded. The blocker was never the
// ranking - two of the three sources did not exist.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Assistant.Voice;
using Xunit;

namespace CircleAI.Tests;

public class TranscriptStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "circleai-transcripts-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
        catch { /* a temp folder that will not delete is the OS's problem */ }
    }

    private FileTranscriptStore Store() => new(_folder);

    private static Transcript Sample(string text = "the clinic appointment moved to Friday")
        => new(text,
            [new TranscriptLine(text, TimeSpan.Zero, TimeSpan.FromSeconds(3))],
            "en", 0.8f);

    [Fact]
    public async Task What_is_kept_comes_back()
    {
        var store = Store();
        var id = await store.KeepAsync("Clinic call", Sample());

        Assert.False(string.IsNullOrWhiteSpace(id));

        var back = await store.GetAsync(id);

        Assert.NotNull(back);
        Assert.Equal("Clinic call", back!.Title);
        Assert.Equal("the clinic appointment moved to Friday", back.Transcript.Text);
        Assert.Single(back.Transcript.Lines);
    }

    [Fact]
    public async Task The_timings_and_the_speaker_survive_the_round_trip()
    {
        // A transcript whose timings did not survive being stored would make
        // subtitles unsaveable after the fact, and diarisation pointless.
        var store = Store();
        var transcript = new Transcript("hello",
            [new TranscriptLine("hello", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "Speaker 1")],
            "en", 0.9f);

        var id = await store.KeepAsync("Meeting", transcript);
        var back = await store.GetAsync(id);

        var line = Assert.Single(back!.Transcript.Lines);
        Assert.Equal(TimeSpan.FromSeconds(1), line.Start);
        Assert.Equal(TimeSpan.FromSeconds(2), line.End);
        Assert.Equal("Speaker 1", line.Speaker);
        Assert.Equal("en", back.Transcript.Language);
    }

    [Fact]
    public async Task A_recording_of_silence_is_not_kept()
    {
        // It transcribes to an empty string, and a file for it would leave
        // somebody with blank rows to clear out by hand.
        var store = Store();

        Assert.Equal(string.Empty, await store.KeepAsync("Nothing", new Transcript("", [])));
        Assert.Equal(string.Empty, await store.KeepAsync("Nothing", new Transcript("   ", [])));
        Assert.Empty(await store.AllAsync());
    }

    [Fact]
    public async Task Everything_kept_comes_back_newest_first()
    {
        var store = Store();
        await store.KeepAsync("First", Sample("one"));
        await Task.Delay(1100);   // the id carries a second-resolution timestamp
        await store.KeepAsync("Second", Sample("two"));

        var all = await store.AllAsync();

        Assert.Equal(2, all.Count);
        Assert.Equal("Second", all[0].Title);
    }

    [Fact]
    public async Task Forgetting_one_actually_deletes_it()
    {
        // Somebody deleting a recording of a clinic appointment means it. A
        // tombstone with the text still in it would be lying to them.
        var store = Store();
        var id = await store.KeepAsync("Clinic", Sample());

        Assert.True(await store.ForgetAsync(id));
        Assert.Null(await store.GetAsync(id));
        Assert.Empty(await store.AllAsync());
        Assert.False(await store.ForgetAsync(id));   // and it stays gone
    }

    [Fact]
    public async Task An_id_that_escapes_the_folder_is_refused()
    {
        // An id arrives from a caller. One containing a path separator must not
        // be able to read or delete a file outside this folder.
        var store = Store();
        await store.KeepAsync("Real", Sample());

        foreach (var nasty in new[]
        {
            "../../secrets", @"..\..\secrets", "/etc/passwd", "C:/Windows/win.ini", "a/b",
        })
        {
            Assert.Null(await store.GetAsync(nasty));
            Assert.False(await store.ForgetAsync(nasty));
        }

        Assert.Single(await store.AllAsync());   // and the real one is untouched
    }

    [Fact]
    public async Task An_empty_or_missing_id_is_refused_rather_than_throwing()
    {
        var store = Store();

        Assert.Null(await store.GetAsync(""));
        Assert.Null(await store.GetAsync("   "));
        Assert.Null(await store.GetAsync("never-existed"));
        Assert.False(await store.ForgetAsync(""));
    }

    [Fact]
    public async Task A_store_nothing_has_been_kept_in_reads_as_empty()
    {
        // And must not create the folder just by being asked - a phone that
        // never recorded anything should not carry an empty directory.
        var store = Store();

        Assert.Empty(await store.AllAsync());
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public async Task One_corrupt_file_does_not_make_the_whole_list_unreadable()
    {
        // A phone killed at the wrong moment, or a file copied in by hand. The
        // rest of somebody's recordings are still theirs.
        var store = Store();
        await store.KeepAsync("Good", Sample());

        await File.WriteAllTextAsync(
            Path.Combine(_folder, "broken.transcript.json"), "{ not json at all");

        var all = await store.AllAsync();

        Assert.Equal("Good", Assert.Single(all).Title);
    }

    [Fact]
    public async Task A_half_written_file_is_never_visible()
    {
        // KeepAsync writes beside and moves into place, because a phone is
        // killed mid-write far more often than a desktop and a truncated JSON
        // file would be a transcript that fails to parse for ever.
        var store = Store();
        await store.KeepAsync("Real", Sample());

        Assert.Empty(Directory.EnumerateFiles(_folder, "*.writing"));
    }

    [Fact]
    public async Task A_title_nobody_gave_becomes_something_readable()
    {
        var store = Store();
        var id = await store.KeepAsync("   ", Sample());

        Assert.Equal("Recording", (await store.GetAsync(id))!.Title);
    }

    [Fact]
    public async Task The_summary_says_how_long_and_how_much()
    {
        var store = Store();
        var id = await store.KeepAsync("Clinic call", Sample());
        var back = await store.GetAsync(id);

        Assert.Contains("Clinic call", back!.Summary);
        Assert.Contains("00:00:03", back.Summary);
    }

    [Fact]
    public async Task A_head_that_cannot_write_files_declines_rather_than_pretending()
    {
        // A browser tab has no app-private folder for somebody's meeting. It
        // must not appear to save and then lose it.
        IKeepsTranscripts none = KeepsNoTranscripts.Instance;

        Assert.Equal(string.Empty, await none.KeepAsync("x", Sample()));
        Assert.Empty(await none.AllAsync());
        Assert.Null(await none.GetAsync("anything"));
        Assert.False(await none.ForgetAsync("anything"));
    }
}
