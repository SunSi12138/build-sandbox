using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed partial class Store
{
    private static readonly HashSet<string> CommunityKinds = ["unit", "use", "inspection-type", "staff", "plan", "draft", "inspection-meta", "issue-meta"];

    private void InitializeCommunityTables()
    {
        using var transaction = _db.BeginTransaction();
        using (var command = Command("""
            CREATE TABLE IF NOT EXISTS community_records (
              id TEXT PRIMARY KEY, kind TEXT NOT NULL, parent_id TEXT NOT NULL DEFAULT '', name TEXT NOT NULL,
              body TEXT NOT NULL CHECK(json_valid(body)), revision INTEGER NOT NULL DEFAULT 1,
              updated_at TEXT NOT NULL, archived INTEGER NOT NULL DEFAULT 0 CHECK(archived IN(0,1)));
            CREATE INDEX IF NOT EXISTS ix_community_records_parent ON community_records(kind,parent_id,archived);
            CREATE TABLE IF NOT EXISTS community_history (
              id TEXT PRIMARY KEY, kind TEXT NOT NULL, record_id TEXT NOT NULL, at TEXT NOT NULL,
              action TEXT NOT NULL, before_json TEXT NOT NULL, after_json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_community_history_record ON community_history(record_id,at);
            CREATE TABLE IF NOT EXISTS map_shapes (
              kind TEXT NOT NULL CHECK(kind IN ('point','grid')), record_id TEXT NOT NULL,
              shape TEXT NOT NULL CHECK(json_valid(shape)), revision INTEGER NOT NULL DEFAULT 1,
              PRIMARY KEY(kind,record_id));
            CREATE TABLE IF NOT EXISTS map_layers (
              id TEXT PRIMARY KEY, name TEXT NOT NULL, folder TEXT NOT NULL, scheme TEXT NOT NULL CHECK(scheme IN('xyz','tms')),
              enabled INTEGER NOT NULL, is_default INTEGER NOT NULL, opacity REAL NOT NULL,
              min_zoom INTEGER NOT NULL, max_zoom INTEGER NOT NULL, sort_order INTEGER NOT NULL);
            INSERT OR IGNORE INTO map_layers VALUES('jlyh','吉林一号','jlyh','tms',1,1,1,12,18,0);
            INSERT OR IGNORE INTO map_layers VALUES('tdt-vec','天地图街道','tdt-vec','xyz',0,0,1,0,18,1);
            INSERT OR IGNORE INTO map_layers VALUES('tdt-cva','地名标注','tdt-cva','xyz',0,0,1,0,18,2);
            """, transaction)) command.ExecuteNonQuery();
        using (var count = Command("SELECT COUNT(*) FROM community_records WHERE kind='inspection-type';", transaction))
            if (Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                foreach (var name in new[] { "安全检查", "环境卫生", "消防通道", "出租房巡查" })
                {
                    using var add = Command("INSERT INTO community_records(id,kind,parent_id,name,body,updated_at) VALUES($id,'inspection-type','',$name,'{\"items\":[]}', $time);", transaction,
                        ("$id", NewId()), ("$name", name), ("$time", DateTimeOffset.Now.ToString("O")));
                    add.ExecuteNonQuery();
                }
        transaction.Commit();
    }

    private static void RequireKind(string kind)
    {
        if (!CommunityKinds.Contains(kind)) throw new ApiException(400, "不支持的资料类型。");
    }

    public CommunityRecord[] ListCommunityRecords(string kind, string? parentId = null, bool includeArchived = false)
    {
        RequireKind(kind);
        lock (_gate)
        {
            using var command = Command("SELECT id,kind,parent_id,name,body,revision,updated_at,archived FROM community_records WHERE kind=$kind AND ($parent IS NULL OR parent_id=$parent) AND ($all=1 OR archived=0) ORDER BY updated_at DESC,rowid DESC;", null,
                ("$kind", kind), ("$parent", parentId), ("$all", includeArchived ? 1 : 0));
            using var rows = command.ExecuteReader();
            var result = new List<CommunityRecord>();
            while (rows.Read()) result.Add(ReadCommunityRecord(rows));
            return [.. result];
        }
    }

    private static CommunityRecord ReadCommunityRecord(SqliteDataReader row) => new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3),
        JsonDocument.Parse(row.GetString(4)).RootElement.Clone(), row.GetInt32(5), row.GetString(6), row.GetInt32(7) != 0);

    public CommunityRecord SaveCommunityRecord(string kind, string? id, CommunityWrite request)
    {
        RequireKind(kind);
        if (request.Data.ValueKind != JsonValueKind.Object || request.Data.GetRawText().Length > 200000) throw new ApiException(400, "资料格式不正确或内容过长。");
        var name = Text(request.Name, "名称", 200, true);
        var parent = Text(request.ParentId, "所属场所", 100);
        lock (_gate)
        {
            PrepareWrite();
            if (kind is "unit" or "use") _ = GetPointInternal(parent);
            if (kind == "plan")
            {
                var start = DateText(JsonText(request.Data, "from"), "开始日期");
                var end = DateText(JsonText(request.Data, "to"), "完成期限");
                CheckDateRange(start, end);
                _ = Text(JsonText(request.Data, "inspector"), "检查人员", 200, true);
                if (!request.Data.TryGetProperty("pointIds", out var pointIds) || pointIds.ValueKind != JsonValueKind.Array || pointIds.GetArrayLength() == 0) throw new ApiException(400, "请选择检查场所。");
                foreach (var pointId in pointIds.EnumerateArray()) _ = GetPointInternal(pointId.GetString() ?? "");
            }
            if (kind == "use")
            {
                var unitId = JsonText(request.Data, "unitId");
                if (unitId.Length > 0 && !ListCommunityRecords("unit", parent).Any(x => x.Id == unitId)) throw new ApiException(400, "所选内部单元不属于这个场所。");
                var start = JsonText(request.Data, "startDate");
                var end = JsonText(request.Data, "endDate");
                if (start.Length > 0) _ = DateText(start, "开始日期");
                if (end.Length > 0) _ = DateText(end, "结束日期");
                if (start.Length > 0 && end.Length > 0 && string.CompareOrdinal(end, start) < 0) throw new ApiException(400, "结束日期不能早于开始日期。");
            }
            var actualId = id ?? NewId();
            var before = "";
            var revision = 1;
            if (id is not null)
            {
                using var existing = Command("SELECT body,revision,parent_id FROM community_records WHERE id=$id AND kind=$kind;", null, ("$id", id), ("$kind", kind));
                using var row = existing.ExecuteReader();
                if (!row.Read()) throw new ApiException(404, "资料不存在。");
                before = row.GetString(0); revision = row.GetInt32(1) + 1;
                if (request.Revision != revision - 1) throw new ApiException(409, "这条资料已在其他页面修改，请刷新后再保存。");
                if (kind is "unit" or "use" && parent != row.GetString(2)) throw new ApiException(400, "已有资料不能直接换到其他场所。");
            }
            var now = DateTimeOffset.Now.ToString("O");
            var body = request.Data.GetRawText();
            using var transaction = _db.BeginTransaction();
            using (var command = Command("""
                INSERT INTO community_records(id,kind,parent_id,name,body,revision,updated_at,archived) VALUES($id,$kind,$parent,$name,$body,$revision,$at,$archived)
                ON CONFLICT(id) DO UPDATE SET name=excluded.name,body=excluded.body,revision=excluded.revision,updated_at=excluded.updated_at,archived=excluded.archived;
                """, transaction, ("$id", actualId), ("$kind", kind), ("$parent", parent), ("$name", name), ("$body", body),
                ("$revision", revision), ("$at", now), ("$archived", request.Archived ? 1 : 0))) command.ExecuteNonQuery();
            AddCommunityHistory(kind, actualId, before, body, id is null ? "新增" : request.Archived ? "停用" : "修改", transaction);
            transaction.Commit();
            return new(actualId, kind, parent, name, request.Data.Clone(), revision, now, request.Archived);
        }
    }

    internal static string JsonText(JsonElement data, string property) => data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private void AddCommunityHistory(string kind, string id, string before, string after, string action, SqliteTransaction transaction)
    {
        using var command = Command("INSERT INTO community_history VALUES($id,$kind,$record,$at,$action,$before,$after);", transaction,
            ("$id", NewId()), ("$kind", kind), ("$record", id), ("$at", DateTimeOffset.Now.ToString("O")), ("$action", action), ("$before", before), ("$after", after));
        command.ExecuteNonQuery();
    }

    public HistoryDto[] CommunityHistory(string id)
    {
        lock (_gate)
        {
            using var command = Command("SELECT id,kind,record_id,at,action,before_json,after_json FROM community_history WHERE record_id=$id ORDER BY at DESC,rowid DESC;", null, ("$id", id));
            using var rows = command.ExecuteReader();
            var result = new List<HistoryDto>();
            while (rows.Read()) result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3), rows.GetString(4), rows.GetString(5), rows.GetString(6)));
            return [.. result];
        }
    }
}
