using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed partial class Store
{
    private static readonly HashSet<string> CommunityKinds = ["unit", "use", "purpose-type", "inspection-type", "staff", "plan", "draft", "inspection-meta", "issue-meta"];

    private void InitializeCommunityTables(SqliteTransaction transaction)
    {
        using (var command = Command("""
            CREATE TABLE community_records (
              id TEXT PRIMARY KEY, kind TEXT NOT NULL, parent_id TEXT NOT NULL DEFAULT '', name TEXT NOT NULL,
              body TEXT NOT NULL CHECK(json_valid(body)), revision INTEGER NOT NULL DEFAULT 1,
              updated_at TEXT NOT NULL, archived INTEGER NOT NULL DEFAULT 0 CHECK(archived IN(0,1)));
            CREATE INDEX ix_community_records_parent ON community_records(kind,parent_id,archived);
            CREATE UNIQUE INDEX ix_draft_submission ON community_records(json_extract(body,'$.submissionKey')) WHERE kind='draft';
            CREATE TABLE record_photos (
              record_id TEXT NOT NULL REFERENCES community_records(id),
              photo_id TEXT NOT NULL UNIQUE REFERENCES photos(id), active INTEGER NOT NULL DEFAULT 1,
              PRIMARY KEY(record_id,photo_id));
            CREATE TABLE imported_rows(source_key TEXT PRIMARY KEY, record_id TEXT NOT NULL, kind TEXT NOT NULL);
            CREATE TABLE community_history (
              id TEXT PRIMARY KEY, kind TEXT NOT NULL, record_id TEXT NOT NULL, at TEXT NOT NULL,
              action TEXT NOT NULL, before_json TEXT NOT NULL, after_json TEXT NOT NULL);
            CREATE INDEX ix_community_history_record ON community_history(record_id,at);
            CREATE TABLE map_shapes (
              kind TEXT NOT NULL CHECK(kind IN ('point','grid')), record_id TEXT NOT NULL,
              shape TEXT NOT NULL CHECK(json_valid(shape)), revision INTEGER NOT NULL DEFAULT 1,
              PRIMARY KEY(kind,record_id));
            CREATE TABLE map_layers (
              id TEXT PRIMARY KEY, name TEXT NOT NULL, folder TEXT NOT NULL, scheme TEXT NOT NULL CHECK(scheme IN('xyz','tms')),
              enabled INTEGER NOT NULL, is_default INTEGER NOT NULL, opacity REAL NOT NULL,
              min_zoom INTEGER NOT NULL, max_zoom INTEGER NOT NULL, sort_order INTEGER NOT NULL,
              url_template TEXT NOT NULL DEFAULT '', api_key TEXT NOT NULL DEFAULT '', subdomains TEXT NOT NULL DEFAULT '0,1,2,3',
              update_cache INTEGER NOT NULL DEFAULT 0, annotation INTEGER NOT NULL DEFAULT 0, map_key TEXT NOT NULL DEFAULT '', cache_version INTEGER NOT NULL DEFAULT 0, tile_type TEXT NOT NULL DEFAULT 'raster', style_json TEXT NOT NULL DEFAULT '');
            INSERT INTO map_layers(id,name,folder,scheme,enabled,is_default,opacity,min_zoom,max_zoom,sort_order) VALUES('jlyh','卫星图','satellite','xyz',1,1,1,0,20,0);
            INSERT INTO map_layers(id,name,folder,scheme,enabled,is_default,opacity,min_zoom,max_zoom,sort_order,annotation) VALUES('tdt-cva','标注','labels','xyz',1,0,1,0,20,10000,1);
            CREATE VIEW active_inspections AS SELECT rowid AS rowid,* FROM inspections WHERE voided=0;
            CREATE VIEW active_issues AS SELECT x.rowid AS rowid,x.* FROM issues x JOIN inspections n ON n.id=x.inspection_id WHERE x.voided=0 AND n.voided=0;
            """, transaction)) command.ExecuteNonQuery();
        foreach (var name in new[] { "安全检查", "环境卫生", "消防通道", "出租房巡查" })
        {
            using var add = Command("INSERT INTO community_records(id,kind,parent_id,name,body,updated_at) VALUES($id,'inspection-type','',$name,'{\"items\":[]}', $time);", transaction,
                ("$id", NewId()), ("$name", name), ("$time", DateTimeOffset.Now.ToString("O")));
            add.ExecuteNonQuery();
        }
        InitializeMapConfiguration(transaction);
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

    private static CommunityRecord ReadCommunityRecord(SqliteDataReader row)
    {
        using var document = JsonDocument.Parse(row.GetString(4));
        return new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3),
            document.RootElement.Clone(), row.GetInt32(5), row.GetString(6), row.GetInt32(7) != 0);
    }

    public CommunityRecord SaveCommunityRecord(string kind, string? id, CommunityWrite request)
    {
        RequireKind(kind);
        if (request.Data.ValueKind != JsonValueKind.Object || request.Data.GetRawText().Length > 200000) throw new ApiException(400, "资料格式不正确或内容过长。");
        var name = Text(request.Name, "名称", 200, true);
        var parent = Text(request.ParentId, "所属场所", 100);
        lock (_gate)
        {
            PrepareWrite();
            if (kind == "staff" && ListCommunityRecords("staff", includeArchived: true).Any(x => x.Id != id && x.Name == name)) throw new ApiException(409, "已有同名人员；同名时请在姓名后加岗位区分。");
            if (kind == "purpose-type")
            {
                _ = Text(JsonText(request.Data,"icon"),"用途图标",16,true);
                ValidatePurposeFields(request.Data);
                if(ListCommunityRecords(kind).Any(x=>x.Id!=id&&x.Name==name))throw new ApiException(409,"已有同名分类。");
            }
            if (kind == "draft")
            {
                var key = Text(JsonText(request.Data, "submissionKey"), "草稿编号", 100, true);
                if (id is null && ListCommunityRecords("draft", includeArchived: true).Any(x => JsonText(x.Data, "submissionKey") == key)) throw new ApiException(409, "这份草稿已经保存，请从草稿箱继续填写。");
            }
            if (kind is "unit" or "use") { _ = GetPointInternal(parent); RequireUnmergedPoint(parent); }
            if (kind == "unit") ValidateUnit(id, parent, request);
            if (kind == "inspection-type")
            {
                var frequency = JsonText(request.Data, "frequency");
                if (frequency.Length > 0) ValidateFrequency(frequency);
                var grid = JsonText(request.Data, "communityId"); if (grid.Length > 0) _ = RequireCommunity(grid);
                var pointType = JsonText(request.Data, "pointTypeId"); if (pointType.Length > 0) _ = RequirePointType(pointType);
                if (request.Data.TryGetProperty("items", out var checks) && (checks.ValueKind != JsonValueKind.Array || checks.GetArrayLength() > 100 || checks.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(x.GetString())))) throw new ApiException(400, "检查项目请每行填写一项，最多100项。");
            }
            if (kind == "plan")
            {
                var start = DateText(JsonText(request.Data, "from"), "开始日期");
                var end = DateText(JsonText(request.Data, "to"), "完成期限");
                CheckDateRange(start, end);
                _ = Text(JsonText(request.Data, "inspector"), "检查人员", 200, true);
                var typeId = JsonText(request.Data, "typeId");
                if (!ListCommunityRecords("inspection-type").Any(x => x.Id == typeId)) throw new ApiException(400, "请选择有效的检查类型。");
                if (!request.Data.TryGetProperty("pointIds", out var pointIds) || pointIds.ValueKind != JsonValueKind.Array || pointIds.GetArrayLength() == 0) throw new ApiException(400, "请选择检查场所。");
                foreach (var pointId in pointIds.EnumerateArray()) _ = GetPointInternal(pointId.GetString() ?? "");
            }
            if (kind == "use")
            {
                request = request with { Data = PrepareUseData(request.Data) };
                var unitId = JsonText(request.Data, "unitId");
                var state = JsonText(request.Data, "state");
                if (state is not ("使用中" or "暂停使用" or "已结束" or "待核实")) throw new ApiException(400, "请选择使用状态。");
                ValidateUsePurpose(request.Data);
                if (unitId.Length > 0 && !ListCommunityRecords("unit", parent, state == "已结束").Any(x => x.Id == unitId)) throw new ApiException(400, "所选楼层或房间已停用或不属于这个场所。");
                var start = JsonText(request.Data, "startDate");
                var end = JsonText(request.Data, "endDate");
                if (start.Length > 0) _ = DateText(start, "开始日期");
                if (end.Length > 0) _ = DateText(end, "结束日期");
                if (state == "已结束" && end.Length == 0) throw new ApiException(400, "停业或退租时请填写结束日期。");
                if (state != "已结束" && end.Length > 0) throw new ApiException(400, "填写结束日期时，请将状态改为已结束。");
                if (start.Length > 0 && end.Length > 0 && string.CompareOrdinal(end, start) < 0) throw new ApiException(400, "结束日期不能早于开始日期。");
            }
            var actualId = id ?? NewId();
            var before = "";
            var revision = 1;
            if (id is not null)
            {
                using var existing = Command("SELECT body,revision,parent_id,name,archived FROM community_records WHERE id=$id AND kind=$kind;", null, ("$id", id), ("$kind", kind));
                using var row = existing.ExecuteReader();
                if (!row.Read()) throw new ApiException(404, "资料不存在。");
                using var previousBody = JsonDocument.Parse(row.GetString(0));
                if (kind == "draft" && (JsonText(previousBody.RootElement, "submittedInspectionId").Length > 0 || JsonText(previousBody.RootElement, "submissionKey") != JsonText(request.Data, "submissionKey"))) throw new ApiException(409, "这份草稿已提交或编号有变化，请从草稿箱重新打开。");
                if (kind == "plan" && !request.Archived && JsonText(previousBody.RootElement, "handedOffTo").Length > 0) throw new ApiException(409, "这项安排已交接，请在接收人员的新安排中办理。");
                before = JsonSerializer.Serialize(new CommunityWrite(row.GetString(2), row.GetString(3), previousBody.RootElement.Clone(), row.GetInt32(1), row.GetInt32(4) != 0), CommunityJsonContext.Default.CommunityWrite);
                revision = row.GetInt32(1) + 1;
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
            if (kind is "use" or "unit") SaveRecordPhotos(actualId, request.Data, transaction);
            if (kind == "draft") SaveDraftPhotos(actualId, request.Data, request.Archived, transaction);
            AddCommunityHistory(kind, actualId, before, JsonSerializer.Serialize(request, CommunityJsonContext.Default.CommunityWrite), id is null ? "新增" : request.Archived ? "停用" : "修改", transaction);
            transaction.Commit();
            return new(actualId, kind, parent, name, request.Data.Clone(), revision, now, request.Archived);
        }
    }

    public void DeleteDictionaryRecord(string kind, string id)
    {
        if (kind is not ("purpose-type" or "inspection-type" or "staff")) throw new ApiException(400, "不支持删除此资料。");
        lock (_gate)
        {
            PrepareWrite();
            if (!ListCommunityRecords(kind, includeArchived: true).Any(x => x.Id == id))
                throw new ApiException(404, "资料不存在。");
            if (kind == "purpose-type")
            {
                using var references = Command("SELECT COUNT(*) FROM community_records WHERE kind='use' AND json_extract(body,'$.purposeId')=$id;", null, ("$id", id));
                if (Convert.ToInt32(references.ExecuteScalar()) > 0) throw new ApiException(409, "该用途已被场所使用，不能删除。");
            }
            if (kind == "inspection-type")
            {
                using var inspections = Command("SELECT COUNT(*) FROM inspections WHERE inspection_type_id=$id;", null, ("$id", id));
                using var plans = Command("SELECT COUNT(*) FROM community_records WHERE kind='plan' AND json_extract(body,'$.typeId')=$id;", null, ("$id", id));
                if (Convert.ToInt32(inspections.ExecuteScalar()) > 0 || Convert.ToInt32(plans.ExecuteScalar()) > 0)
                    throw new ApiException(409, "该检查类型已被记录引用，不能删除。");
            }
            using var transaction = _db.BeginTransaction();
            using (var history = Command("DELETE FROM community_history WHERE kind=$kind AND record_id=$id;", transaction, ("$kind", kind), ("$id", id))) history.ExecuteNonQuery();
            using (var record = Command("DELETE FROM community_records WHERE kind=$kind AND id=$id;", transaction, ("$kind", kind), ("$id", id))) record.ExecuteNonQuery();
            transaction.Commit();
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
