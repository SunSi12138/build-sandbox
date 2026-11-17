using Microsoft.Data.Sqlite;
using System.Text.Json;

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
            WordExporter.ValidateNoticeTemplate(value.NoticeTemplate);
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
            using var command = Command("SELECT id,name,title,parent_id,color FROM communities ORDER BY name COLLATE NOCASE,rowid;");
            using var rows = command.ExecuteReader();
            var result = new List<CommunityDto>();
            while (rows.Read()) result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3), rows.GetString(4)));
            return result.ToArray();
        }
    }

    private CommunityDto RequireCommunity(string id, SqliteTransaction? transaction = null)
    {
        using var command = Command("SELECT id,name,title,parent_id,color FROM communities WHERE id=$id;", transaction, ("$id", id));
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new ApiException(400, "所选社区不存在，请刷新后重新选择。");
        return new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetString(4));
    }

    public CommunityDto SaveCommunity(string? id, CommunityWriteRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            var name = Text(request.Name, "网格名称", 100, true);
            var title = Text(request.Title, "网格名称", 150);
            var parent = Text(request.ParentId, "父网格", 100);
            var color = MapColor(request.Color);
            var grids = GetCommunities();
            if (parent.Length > 0 && !grids.Any(x => x.Id == parent)) throw new ApiException(400, "父网格不存在。");
            var ancestor = parent;
            while (ancestor.Length > 0)
            {
                if (ancestor == id) throw new ApiException(400, "父网格不能是自身或其子网格。");
                ancestor = grids.FirstOrDefault(x => x.Id == ancestor)?.ParentId ?? "";
            }
            if (grids.Any(x => x.Id != id && x.Name == name)) throw new ApiException(409, "已有同名网格。");
            if (request.Shape is not null) { ValidateShape(request.Shape); if (request.Shape.Type == "Point") throw new ApiException(400, "网格请圈画区域。"); }
            if (id is not null && request.Shape is not null) throw new ApiException(400, "已有网格请通过地图更新边界。");
            var actualId = id ?? NewId();
            using var transaction = _db.BeginTransaction();
            using (var command = id is null
                ? Command("INSERT INTO communities(id,name,title,parent_id,color) VALUES($id,$name,$title,$parent,$color);", transaction, ("$id", actualId), ("$name", name), ("$title", title.Length == 0 ? name : title), ("$parent", parent), ("$color",color))
                : Command("UPDATE communities SET name=$name,title=$title,parent_id=$parent,color=$color WHERE id=$id;", transaction, ("$id", actualId), ("$name", name), ("$title", title.Length == 0 ? name : title), ("$parent", parent), ("$color",color)))
                RequireChanged(command.ExecuteNonQuery(), "网格");
            if (request.Shape is not null)
            {
                using var shape = Command("INSERT INTO map_shapes(kind,record_id,shape,revision) VALUES('grid',$id,$shape,1);", transaction, ("$id", actualId), ("$shape", System.Text.Json.JsonSerializer.Serialize(request.Shape, CommunityJsonContext.Default.GeoShape)));
                shape.ExecuteNonQuery();
            }
            transaction.Commit();
            return new(actualId, name, title.Length == 0 ? name : title, parent, color);
        }
    }

    public void DeleteCommunity(string id)
    {
        lock (_gate)
        {
            PrepareWrite();
            using (var used = Command("SELECT (SELECT COUNT(*) FROM points WHERE community_id=$id)+(SELECT COUNT(*) FROM inspections WHERE community_id=$id)+(SELECT COUNT(*) FROM campaigns WHERE community_id=$id);", null, ("$id", id)))
                if (Convert.ToInt32(used.ExecuteScalar()) > 0) throw new ApiException(409, "该社区已被记录引用，不能删除。");
            if (GetCommunities().Any(x => x.ParentId == id)) throw new ApiException(409, "该网格还有子网格，请先调整子网格。");
            using var command = Command("DELETE FROM communities WHERE id=$id;", null, ("$id", id));
            RequireChanged(command.ExecuteNonQuery(), "社区");
        }
    }

    public PointTypeDto[] GetPointTypes()
    {
        lock (_gate)
        {
            using var command = Command("SELECT id,name,fields FROM point_types ORDER BY rowid;");
            using var rows = command.ExecuteReader();
            var result = new List<PointTypeDto>();
            while (rows.Read()) result.Add(new(rows.GetString(0), rows.GetString(1), JsonSerializer.Deserialize(rows.GetString(2), CommunityJsonContext.Default.PurposeFieldArray) ?? []));
            return result.ToArray();
        }
    }

    private PointTypeDto RequirePointType(string id, SqliteTransaction? transaction = null)
    {
        using var command = Command("SELECT id,name,fields FROM point_types WHERE id=$id;", transaction, ("$id", id));
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new ApiException(400, "所选点位类型不存在，请刷新后重新选择。");
        return new(row.GetString(0), row.GetString(1), JsonSerializer.Deserialize(row.GetString(2), CommunityJsonContext.Default.PurposeFieldArray) ?? []);
    }

    public PointTypeDto SavePointType(string? id, PointTypeWriteRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            var name = Text(request.Name, "点位类型", 100, true);
            ValidateFieldDefinitions(request.Fields ?? []);
            var fieldsJson = JsonSerializer.Serialize(request.Fields ?? [], CommunityJsonContext.Default.PurposeFieldArray);
            using (var duplicate = Command("SELECT COUNT(*) FROM point_types WHERE name=$name AND id<>$id;", null, ("$name", name), ("$id", id ?? "")))
                if (Convert.ToInt32(duplicate.ExecuteScalar()) > 0) throw new ApiException(409, "已有同名点位类型。");
            var actualId = id ?? NewId();
            using var command = id is null
                ? Command("INSERT INTO point_types(id,name,fields) VALUES($id,$name,$fields);", null, ("$id", actualId), ("$name", name), ("$fields", fieldsJson))
                : Command("UPDATE point_types SET name=$name,fields=$fields WHERE id=$id;", null, ("$id", actualId), ("$name", name), ("$fields", fieldsJson));
            RequireChanged(command.ExecuteNonQuery(), "点位类型");
            return new(actualId, name, request.Fields ?? []);
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
