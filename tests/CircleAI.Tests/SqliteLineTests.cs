// SqliteLineTests.cs
//
// ONE SQLitePCLRaw LINE IN THE PROCESS. sqlite-net-pcl - which nothing in the product
// used - pulled SQLitePCLRaw 3.0 and SourceGear.sqlite3 in beside the 2.1 line that
// Microsoft.Data.Sqlite is built on, so Microsoft.Data.Sqlite's 2.1 start-up code ran
// against a 3.0 core, and every APK carried two libe_sqlite3.so. It worked only
// because each of the 117 calls the 2.1 side makes happened to exist in 3.0. This test
// is the alarm if a second line ever comes back - or if Microsoft.Data.Sqlite itself
// moves to 3.x, which is the moment to look again on purpose.

using Microsoft.Data.Sqlite;
using Xunit;

namespace CircleAI.Tests;

public sealed class SqliteLineTests
{
    [Fact]
    public void Microsoft_Data_Sqlite_runs_on_the_SQLitePCLRaw_line_it_was_built_for()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();

        using var cmd = db.CreateCommand();
        cmd.CommandText = "create table t(x text); insert into t values('kept'); select x from t;";
        Assert.Equal("kept", cmd.ExecuteScalar());

        // Microsoft.Data.Sqlite 10.0.x is built on SQLitePCLRaw 2.1.
        var core = typeof(SQLitePCL.raw).Assembly.GetName();
        Assert.Equal(2, core.Version!.Major);
    }
}
