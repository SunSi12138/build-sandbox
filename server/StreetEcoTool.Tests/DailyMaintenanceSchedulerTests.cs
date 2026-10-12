using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace StreetEcoTool.Tests;

public sealed class DailyMaintenanceSchedulerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hbtool-midnight-" + Guid.NewGuid().ToString("N"));
    private static FakeTimeProvider Clock()
    {
        var fake = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 15, 59, 59, TimeSpan.Zero));
        fake.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("Asia/Shanghai", TimeSpan.FromHours(8), "China", "China"));
        return fake;
    }

    [Fact]
    public void SchedulesFromLocalMidnightAndCapturesBeforeMidnightDataOnly()
    {
        var fake = Clock();
        using var store = new Store(Path.Combine(_root, "data"));
        store.SaveCommunity(null, new("午夜前社区", "午夜前社区居民委员会"));
        Assert.Equal(TimeSpan.FromSeconds(1), DailyMaintenanceScheduler.UntilNextMidnight(fake));
        using var scheduler = new DailyMaintenanceScheduler(fake, store.RunDailyMaintenance);
        var snapshot = Path.Combine(_root, "backup", "auto-2026-10-12.db");
        fake.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(File.Exists(snapshot));
        fake.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(File.Exists(snapshot));
        store.SaveCommunity(null, new("午夜后社区", "午夜后社区居民委员会"));
        using var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshot, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        database.Open();
        using var query = database.CreateCommand();
        query.CommandText = "SELECT name FROM communities ORDER BY name;";
        using var rows = query.ExecuteReader();
        var names = new List<string>();
        while (rows.Read()) names.Add(rows.GetString(0));
        Assert.Contains("午夜前社区", names);
        Assert.DoesNotContain("午夜后社区", names);
        Assert.Equal(TimeSpan.FromDays(1), DailyMaintenanceScheduler.UntilNextMidnight(fake));
    }

    [Fact]
    public void RecomputesEachMidnightAndStopsAfterDisposal()
    {
        var fake = Clock();
        var dates = new List<DateOnly>();
        using (var scheduler = new DailyMaintenanceScheduler(fake, dates.Add))
        {
            fake.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal([new DateOnly(2026, 10, 13)], dates);
            fake.Advance(TimeSpan.FromHours(23));
            Assert.Single(dates);
            fake.Advance(TimeSpan.FromHours(1));
            Assert.Equal(new DateOnly(2026, 10, 14), dates[1]);
        }
        fake.Advance(TimeSpan.FromDays(3));
        Assert.Equal(2, dates.Count);
    }

    [Fact]
    public void FailedMaintenanceIsReportedAndDoesNotDisableNextMidnight()
    {
        var fake = Clock();
        var calls = 0;
        var errors = 0;
        using var scheduler = new DailyMaintenanceScheduler(fake, _ => { calls++; if (calls == 1) throw new IOException("磁盘空间不足"); }, _ => errors++);
        fake.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, errors);
        fake.Advance(TimeSpan.FromDays(1));
        Assert.Equal(2, calls);
        Assert.Equal(1, errors);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        GC.SuppressFinalize(this);
    }
}
