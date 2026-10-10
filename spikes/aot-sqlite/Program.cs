using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

var dbPath = Path.Combine(AppContext.BaseDirectory, "spike.db");
SQLitePCL.Batteries_V2.Init();
using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
{
    DataSource = dbPath,
    Pooling = false,
    ForeignKeys = true
}.ToString());
connection.Open();
using (var setup = connection.CreateCommand())
{
    setup.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; CREATE TABLE IF NOT EXISTS point(id TEXT PRIMARY KEY, name TEXT NOT NULL); CREATE TABLE IF NOT EXISTS inspection(id TEXT PRIMARY KEY,point_id TEXT NOT NULL REFERENCES point(id),inspection_date TEXT NOT NULL); PRAGMA user_version=1;";
    setup.ExecuteNonQuery();
}

if (args.Contains("--self-test", StringComparer.Ordinal))
{
    var id = Guid.NewGuid().ToString("N");
    const string expectedName = "测试社区 O'Brien 环保点位";
    var date = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    using (var transaction = connection.BeginTransaction())
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO point(id,name) VALUES($id,$name); INSERT INTO inspection(id,point_id,inspection_date) VALUES($id,$id,$date);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", expectedName);
        command.Parameters.AddWithValue("$date", date);
        command.ExecuteNonQuery();
        transaction.Commit();
    }
    using (var read = connection.CreateCommand())
    {
        read.CommandText = "SELECT p.name,i.inspection_date FROM point p JOIN inspection i ON i.point_id=p.id WHERE p.id=$id;";
        read.Parameters.AddWithValue("$id", id);
        using var reader = read.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != expectedName || reader.GetString(1) != date)
            throw new InvalidOperationException("SQLite 参数化写入/读取失败");
    }
    var expectedCountIndex = Array.IndexOf(args, "--expected-count");
    if (expectedCountIndex >= 0)
    {
        if (expectedCountIndex + 1 >= args.Length || !int.TryParse(args[expectedCountIndex + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var expectedCount))
            throw new InvalidOperationException("--expected-count 需要非负整数");
        using var persisted = connection.CreateCommand();
        persisted.CommandText = "SELECT p.name,i.inspection_date FROM point p JOIN inspection i ON i.point_id=p.id WHERE p.name=$name;";
        persisted.Parameters.AddWithValue("$name", expectedName);
        using var rows = persisted.ExecuteReader();
        var count = 0;
        while (rows.Read())
        {
            if (rows.GetString(0) != expectedName || !DateOnly.TryParseExact(rows.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new InvalidOperationException("上一次进程保存的行无效");
            count++;
        }
        if (count != expectedCount)
            throw new InvalidOperationException($"跨进程持久化失败：预期 {expectedCount} 条，实际 {count} 条");
    }
    using (var fk = connection.CreateCommand())
    {
        fk.CommandText = "INSERT INTO inspection(id,point_id,inspection_date) VALUES($id,$missing,$date);";
        fk.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        fk.Parameters.AddWithValue("$missing", "missing");
        fk.Parameters.AddWithValue("$date", date);
        try
        {
            fk.ExecuteNonQuery();
            throw new InvalidOperationException("外键没有生效");
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) { }
    }
    using (var wal = connection.CreateCommand())
    {
        wal.CommandText = "PRAGMA journal_mode;";
        if (!string.Equals(wal.ExecuteScalar()?.ToString(), "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("WAL 没有生效");
    }
    var snapshot = Path.Combine(AppContext.BaseDirectory, "spike-snapshot.db");
    File.Delete(snapshot);
    using (var backup = connection.CreateCommand())
    {
        backup.CommandText = "VACUUM INTO $path;";
        backup.Parameters.AddWithValue("$path", snapshot);
        backup.ExecuteNonQuery();
    }
    using (var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshot, Pooling = false }.ToString()))
    {
        check.Open();
        using var integrity = check.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        if (integrity.ExecuteScalar()?.ToString() != "ok")
            throw new InvalidOperationException("VACUUM INTO 快照损坏");
    }
    var result = new ProbeResult(true, SQLitePCL.raw.sqlite3_libversion().utf8_to_string(), date, expectedName);
    var json = JsonSerializer.Serialize(result, SpikeJsonContext.Default.ProbeResult);
    if (JsonSerializer.Deserialize(json, SpikeJsonContext.Default.ProbeResult) != result)
        throw new InvalidOperationException("JSON 源生成往返失败");
    Console.WriteLine(json);
    Console.WriteLine("PASS: SQLite read/write, Unicode, SQL parameters, FK, WAL, VACUUM INTO, generated JSON");
    return;
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, SpikeJsonContext.Default));
var app = builder.Build();
app.MapGet("/api/probe", () => TypedResults.Json(
    new ProbeResult(true, SQLitePCL.raw.sqlite3_libversion().utf8_to_string(), DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "中文 SQLite AOT"),
    SpikeJsonContext.Default.ProbeResult));
RequestDelegate saveProbe = async (HttpContext context) =>
{
    var request = await context.Request.ReadFromJsonAsync(SpikeJsonContext.Default.ProbeRequest, context.RequestAborted);
    if (request is null || string.IsNullOrWhiteSpace(request.Name))
    {
        context.Response.StatusCode = 400;
        return;
    }
    var id = Guid.NewGuid().ToString("N");
    using var command = connection.CreateCommand();
    command.CommandText = "INSERT INTO point(id,name) VALUES($id,$name);";
    command.Parameters.AddWithValue("$id", id);
    command.Parameters.AddWithValue("$name", request.Name);
    command.ExecuteNonQuery();
    await context.Response.WriteAsJsonAsync(new ProbeSaved(id, request.Name), SpikeJsonContext.Default.ProbeSaved, contentType: null, cancellationToken: context.RequestAborted);
};
app.MapPost("/api/probe", saveProbe);
await app.RunAsync();

internal sealed record ProbeRequest(string Name);
internal sealed record ProbeSaved(string Id, string Name);
internal sealed record ProbeResult(bool Ok, string SqliteVersion, string Date, string Name);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProbeResult))]
[JsonSerializable(typeof(ProbeRequest))]
[JsonSerializable(typeof(ProbeSaved))]
internal partial class SpikeJsonContext : JsonSerializerContext;
