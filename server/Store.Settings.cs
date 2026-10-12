using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed partial class Store
{
    public SettingsDto GetSettings()
    {
        lock (_gate)
        {
            using var command = Command("SELECT street_name,issuing_unit,default_inspector,notice_template,jpeg_quality FROM settings WHERE id=1;");
            using var row = command.ExecuteReader();
            if (!row.Read()) throw new ApiException(500, "数据库缺少设置记录。");
            return new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetInt32(4));
        }
    }

    public SettingsDto SaveSettings(SettingsDto request)
    {
        lock (_gate)
        {
            PrepareWrite();
            var value = new SettingsDto(Text(request.StreetName, "街道全称", 100), Text(request.IssuingUnit, "落款单位", 100),
                Text(request.DefaultInspector, "默认排查人", 100), Text(request.NoticeTemplate, "通知单正文模板", 12000, true), request.JpegQuality);
            if (value.JpegQuality is < 1 or > 100) throw new ApiException(400, "JPEG 质量必须是 1 至 100 的整数。");
            using var command = Command("UPDATE settings SET street_name=$street,issuing_unit=$unit,default_inspector=$inspector,notice_template=$template,jpeg_quality=$quality WHERE id=1;", null,
                ("$street", value.StreetName), ("$unit", value.IssuingUnit), ("$inspector", value.DefaultInspector), ("$template", value.NoticeTemplate), ("$quality", value.JpegQuality));
            command.ExecuteNonQuery();
            return value;
        }
    }

    public BootstrapDto GetBootstrap()
    {
        lock (_gate) return new(GetSettings(), GetCommunities(), GetPointTypes(), Today, GetCampaignTypes());
    }

    public CommunityDto[] GetCommunities()
    {
        lock (_gate)
        {
            using var command = Command("SELECT id,name,title FROM communities ORDER BY name COLLATE NOCASE,rowid;");
            using var rows = command.ExecuteReader();
            var result = new List<CommunityDto>();
            while (rows.Read()) result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2)));
            return result.ToArray();
        }
    }

    private CommunityDto RequireCommunity(string id, SqliteTransaction? transaction = null)
    {
        using var command = Command("SELECT id,name,title FROM communities WHERE id=$id;", transaction, ("$id", id));
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new ApiException(400, "所选社区不存在，请刷新后重新选择。");
        return new(row.GetString(0), row.GetString(1), row.GetString(2));
    }

    public CommunityDto SaveCommunity(string? id, CommunityWriteRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            var name = Text(request.Name, "社区名称", 100, true);
            var title = Text(request.Title, "通知单抬头全称", 150, true);
            using (var duplicate = Command("SELECT COUNT(*) FROM communities WHERE name=$name AND id<>$id;", null, ("$name", name), ("$id", id ?? "")))
                if (Convert.ToInt32(duplicate.ExecuteScalar()) > 0) throw new ApiException(409, "已有同名社区。");
            var actualId = id ?? NewId();
            using var command = id is null
                ? Command("INSERT INTO communities(id,name,title) VALUES($id,$name,$title);", null, ("$id", actualId), ("$name", name), ("$title", title))
                : Command("UPDATE communities SET name=$name,title=$title WHERE id=$id;", null, ("$id", actualId), ("$name", name), ("$title", title));
            RequireChanged(command.ExecuteNonQuery(), "社区");
            return new(actualId, name, title);
        }
    }

    public void DeleteCommunity(string id)
    {
        lock (_gate)
        {
            PrepareWrite();
            using (var used = Command("SELECT (SELECT COUNT(*) FROM points WHERE community_id=$id)+(SELECT COUNT(*) FROM inspections WHERE community_id=$id)+(SELECT COUNT(*) FROM campaigns WHERE community_id=$id);", null, ("$id", id)))
                if (Convert.ToInt32(used.ExecuteScalar()) > 0) throw new ApiException(409, "该社区已被记录引用，不能删除。");
            using var command = Command("DELETE FROM communities WHERE id=$id;", null, ("$id", id));
            RequireChanged(command.ExecuteNonQuery(), "社区");
        }
    }

    public PointTypeDto[] GetPointTypes()
    {
        lock (_gate)
        {
            using var command = Command("SELECT id,name FROM point_types ORDER BY rowid;");
            using var rows = command.ExecuteReader();
            var result = new List<PointTypeDto>();
            while (rows.Read()) result.Add(new(rows.GetString(0), rows.GetString(1)));
            return result.ToArray();
        }
    }

    private PointTypeDto RequirePointType(string id, SqliteTransaction? transaction = null)
    {
        using var command = Command("SELECT id,name FROM point_types WHERE id=$id;", transaction, ("$id", id));
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new ApiException(400, "所选点位类型不存在，请刷新后重新选择。");
        return new(row.GetString(0), row.GetString(1));
    }

    public PointTypeDto SavePointType(string? id, PointTypeWriteRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            var name = Text(request.Name, "点位类型", 100, true);
            using (var duplicate = Command("SELECT COUNT(*) FROM point_types WHERE name=$name AND id<>$id;", null, ("$name", name), ("$id", id ?? "")))
                if (Convert.ToInt32(duplicate.ExecuteScalar()) > 0) throw new ApiException(409, "已有同名点位类型。");
            var actualId = id ?? NewId();
            using var command = id is null
                ? Command("INSERT INTO point_types(id,name) VALUES($id,$name);", null, ("$id", actualId), ("$name", name))
                : Command("UPDATE point_types SET name=$name WHERE id=$id;", null, ("$id", actualId), ("$name", name));
            RequireChanged(command.ExecuteNonQuery(), "点位类型");
            return new(actualId, name);
        }
    }

    public void DeletePointType(string id)
    {
        lock (_gate)
        {
            PrepareWrite();
            using (var used = Command("SELECT (SELECT COUNT(*) FROM points WHERE type_id=$id)+(SELECT COUNT(*) FROM inspections WHERE type_id=$id);", null, ("$id", id)))
                if (Convert.ToInt32(used.ExecuteScalar()) > 0) throw new ApiException(409, "该类型已被点位或历史排查引用，不能删除；可以修改名称。");
            using var command = Command("DELETE FROM point_types WHERE id=$id;", null, ("$id", id));
            RequireChanged(command.ExecuteNonQuery(), "点位类型");
        }
    }
}
