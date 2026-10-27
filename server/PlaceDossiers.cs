using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed partial class Store
{
    private void InitializeMapConfiguration(SqliteTransaction transaction)
    {
        var columns = new HashSet<string>();
        using (var info = Command("PRAGMA table_info(map_layers);", transaction))
        using (var rows = info.ExecuteReader()) while (rows.Read()) columns.Add(rows.GetString(1));
        foreach (var (name, definition) in new[] { ("url_template", "TEXT NOT NULL DEFAULT ''"), ("api_key", "TEXT NOT NULL DEFAULT ''"), ("subdomains", "TEXT NOT NULL DEFAULT '0,1,2,3'"), ("update_cache", "INTEGER NOT NULL DEFAULT 0"), ("annotation", "INTEGER NOT NULL DEFAULT 0"), ("map_key", "TEXT NOT NULL DEFAULT ''"), ("cache_version", "INTEGER NOT NULL DEFAULT 0") })
            if (!columns.Contains(name)) { using var add = Command($"ALTER TABLE map_layers ADD COLUMN {name} {definition};", transaction); add.ExecuteNonQuery(); }
        InitializeGridAndProviderDefaults(transaction);
        foreach (var (id, name, icon, extra) in new[] {
            ("shop", "商店", "🏪", new[]{ new PurposeField("business","经营内容","text",false) }),
            ("warehouse", "仓库", "📦", new[]{ new PurposeField("storedGoods","存放物品","text",false) }),
            ("factory", "生产加工", "🏭", new[]{ new PurposeField("business","生产内容","text",false) }),
            ("rental", "出租房", "🔑", new[]{ new PurposeField("owner","房东","text",false),new PurposeField("ownerPhone","房东电话","text",false),new PurposeField("tenantCount","居住人数","number",false) }),
            ("home", "居住", "🏠", new[]{ new PurposeField("tenantCount","居住人数","number",false) }),
            ("office", "办公", "🏢", Array.Empty<PurposeField>()), ("other", "其他", "📌", Array.Empty<PurposeField>()) })
        {
            var fields = JsonSerializer.Serialize(extra, CommunityJsonContext.Default.PurposeFieldArray);
            var body = "{\"icon\":\"" + icon + "\",\"fields\":" + fields + "}";
            using var add = Command("INSERT OR IGNORE INTO community_records(id,kind,parent_id,name,body,updated_at) VALUES($id,'purpose-type','',$name,$body,$at);", transaction,
                ("$id", "purpose-" + id), ("$name", name), ("$body", body), ("$at", DateTimeOffset.Now.ToString("O")));
            add.ExecuteNonQuery();
            using var define = Command("UPDATE community_records SET body=json_set(json_remove(body,'$.profile'),'$.fields',json($fields)) WHERE id=$id AND json_type(body,'$.fields') IS NULL;", transaction,("$id","purpose-"+id),("$fields",fields)); define.ExecuteNonQuery();
        }
    }

    private void ValidateUsePurpose(JsonElement data)
    {
        var purpose = ListCommunityRecords("purpose-type", includeArchived: true).FirstOrDefault(x => x.Id == JsonText(data, "purposeId"));
        if (purpose is null) throw new ApiException(400, "请选择有效用途。");
        _ = Text(JsonText(data, "position"), "用途位置", 200, true);
    }

    public string PurposeName(JsonElement data) => ListCommunityRecords("purpose-type", includeArchived: true).FirstOrDefault(x => x.Id == JsonText(data, "purposeId"))?.Name ?? "未分类";

    public PointDto CreatePlaceDossier(PlaceDossierWrite request)
    {
        lock (_gate)
        {
            PrepareWrite();
            if (request.Point.Temporary || request.Uses is null || request.Uses.Length > 50) throw new ApiException(400, "场所用途最多登记 50 条。");
            request = request with { Uses = request.Uses.Select(use => use with { Data = PrepareUseData(use.Data) }).ToArray() };
            foreach (var use in request.Uses)
            {
                if (use.Data.ValueKind != JsonValueKind.Object || use.Data.GetRawText().Length > 200000) throw new ApiException(400, "使用情况内容过长或格式不正确。");
                _ = Text(use.Name, "使用名称", 200, true); ValidateUsePurpose(use.Data);
                if (JsonText(use.Data, "unitId").Length > 0) throw new ApiException(400, "新建场所时请直接填写用途位置。");
                var state = JsonText(use.Data, "state");
                if (state is not ("使用中" or "暂停使用" or "待核实")) throw new ApiException(400, "新增使用情况请选择在用状态。");
                var start = JsonText(use.Data, "startDate"); if (start.Length > 0) _ = DateText(start, "开始日期");
                if (JsonText(use.Data, "endDate").Length > 0) throw new ApiException(400, "新增使用情况不能填写结束日期。");
            }
            using var transaction = _db.BeginTransaction();
            var pointId = SavePointCore(null, request.Point, transaction);
            foreach (var use in request.Uses)
            {
                var id = NewId(); var now = DateTimeOffset.Now.ToString("O");
                using var add = Command("INSERT INTO community_records(id,kind,parent_id,name,body,updated_at) VALUES($id,'use',$parent,$name,$body,$at);", transaction,
                    ("$id", id), ("$parent", pointId), ("$name", use.Name.Trim()), ("$body", use.Data.GetRawText()), ("$at", now));
                add.ExecuteNonQuery(); SaveRecordPhotos(id, use.Data, transaction);
                AddCommunityHistory("use", id, "", JsonSerializer.Serialize(use with { ParentId = pointId }, CommunityJsonContext.Default.CommunityWrite), "新增", transaction);
            }
            transaction.Commit(); return GetPoint(pointId);
        }
    }
}
