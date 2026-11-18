using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace StreetEcoTool;

public sealed partial class Store
{
    public CampaignTypeDto[] GetCampaignTypes()
    {
        lock (_gate)
        {
            using var command = Command("SELECT id,name,fields FROM campaign_types ORDER BY rowid;");
            using var rows = command.ExecuteReader();
            var result = new List<CampaignTypeDto>();
            while (rows.Read()) result.Add(new(rows.GetString(0), rows.GetString(1), JsonSerializer.Deserialize(rows.GetString(2), CommunityJsonContext.Default.PurposeFieldArray) ?? []));
            return result.ToArray();
        }
    }

    private CampaignTypeDto RequireCampaignType(string id)
    {
        using var command = Command("SELECT id,name,fields FROM campaign_types WHERE id=$id;", null, ("$id", id));
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new ApiException(400, "所选整治类型不存在，请重新选择。");
        return new(row.GetString(0), row.GetString(1), JsonSerializer.Deserialize(row.GetString(2), CommunityJsonContext.Default.PurposeFieldArray) ?? []);
    }

    public CampaignTypeDto SaveCampaignType(string? id, CampaignTypeWriteRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            var name = Text(request.Name, "整治类型", 100, true);
            ValidateFieldDefinitions(request.Fields ?? []);
            var fieldsJson = JsonSerializer.Serialize(request.Fields ?? [], CommunityJsonContext.Default.PurposeFieldArray);
            using (var duplicate = Command("SELECT COUNT(*) FROM campaign_types WHERE name=$name AND id<>$id;", null, ("$name", name), ("$id", id ?? "")))
                if (Convert.ToInt32(duplicate.ExecuteScalar()) > 0) throw new ApiException(409, "已有同名整治类型。");
            var actualId = id ?? NewId();
            using var command = Command(id is null ? "INSERT INTO campaign_types(id,name,fields) VALUES($id,$name,$fields);" : "UPDATE campaign_types SET name=$name,fields=$fields WHERE id=$id;", null,
                ("$id", actualId), ("$name", name), ("$fields", fieldsJson));
            RequireChanged(command.ExecuteNonQuery(), "整治类型");
            return new(actualId, name, request.Fields ?? []);
        }
    }

    public void DeleteCampaignType(string id)
    {
        lock (_gate)
        {
            PrepareWrite();
            using (var used = Command("SELECT COUNT(*) FROM campaigns WHERE type_id=$id;", null, ("$id", id)))
                if (Convert.ToInt32(used.ExecuteScalar()) > 0) throw new ApiException(409, "该类型已被整治记录引用，不能删除。");
            using var command = Command("DELETE FROM campaign_types WHERE id=$id;", null, ("$id", id));
            RequireChanged(command.ExecuteNonQuery(), "整治类型");
        }
    }

    private const string CampaignSelect = """
        SELECT c.id,c.name,c.type_id,c.type_name,c.community_id,c.community_name,c.location,c.campaign_date,c.end_date,
        c.organizer,c.participants,c.participant_count,c.vehicle_count,c.garbage_tons,c.area_sqm,c.content,c.outcome,c.notes,
        (SELECT COUNT(*) FROM photos p WHERE p.campaign_id=c.id),c.point_id,c.point_name,pt.temporary,c.extra_values,c.field_snapshot
        FROM campaigns c JOIN points pt ON pt.id=c.point_id
        """;
    private const string CampaignWhere = """
        WHERE ($q='' OR c.name LIKE $like OR c.location LIKE $like OR c.point_name LIKE $like)
        AND ($point='' OR c.point_id=$point)
        AND ($community='' OR c.community_id=$community) AND ($type='' OR c.type_id=$type)
        AND ($from='' OR c.campaign_date>=$from) AND ($to='' OR c.campaign_date<=$to)
        """;

    private static CampaignDto ReadCampaign(SqliteDataReader row) => new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3),
        row.GetString(4), row.GetString(5), row.GetString(6), row.GetString(7), NullableString(row, 8), row.GetString(9), row.GetString(10),
        row.IsDBNull(11) ? null : row.GetInt32(11), row.IsDBNull(12) ? null : row.GetInt32(12),
        row.IsDBNull(13) ? null : row.GetDecimal(13), row.IsDBNull(14) ? null : row.GetDecimal(14), row.GetString(15), row.GetString(16), row.GetString(17), row.GetInt32(18), row.GetString(19), row.GetString(20), row.GetInt32(21) != 0, JsonDocument.Parse(row.GetString(22)).RootElement.Clone(), JsonSerializer.Deserialize(row.GetString(23), CommunityJsonContext.Default.PurposeFieldArray));

    private CampaignDto[] ReadCampaigns(string suffix, (string Name, object? Value)[] parameters)
    {
        using var command = Command(CampaignSelect + " " + suffix, null, parameters);
        using var rows = command.ExecuteReader();
        var result = new List<CampaignDto>();
        while (rows.Read()) result.Add(ReadCampaign(rows));
        return result.ToArray();
    }

    public CampaignDto GetCampaign(string id)
    {
        lock (_gate)
        {
            var result = ReadCampaigns("WHERE c.id=$id;", [("$id", id)]);
            return result.Length == 0 ? throw new ApiException(404, "整治记录不存在。") : result[0];
        }
    }

    public CampaignDetailDto GetCampaignDetail(string id)
    {
        lock (_gate) return new(GetCampaign(id), ReadPhotos(campaignId: id));
    }

    private static (string Name, object? Value)[] CampaignParameters(CampaignQuery query)
    {
        var from = OptionalDate(query.From, "开始日期");
        var to = OptionalDate(query.To, "结束日期");
        CheckDateRange(from, to);
        var q = Text(query.Q, "搜索内容", 200);
        return [("$point", query.PointId ?? ""), ("$q", q), ("$like", "%" + q + "%"), ("$community", query.CommunityId ?? ""),
            ("$type", query.TypeId ?? ""), ("$from", from ?? ""), ("$to", to ?? "")];
    }

    public PageDto<CampaignDto> ListCampaigns(CampaignQuery query)
    {
        lock (_gate)
        {
            var parameters = CampaignParameters(query);
            var (page, pageSize) = Paging(query.Page, query.PageSize);
            int total;
            using (var count = Command("SELECT COUNT(*) FROM campaigns c " + CampaignWhere, null, parameters)) total = Convert.ToInt32(count.ExecuteScalar());
            var items = ReadCampaigns(CampaignWhere + " ORDER BY c.campaign_date DESC,c.rowid DESC LIMIT $limit OFFSET $offset;",
                [.. parameters, ("$limit", pageSize), ("$offset", (page - 1) * pageSize)]);
            return new(items, total, page, pageSize);
        }
    }

    public CampaignDto[] GetLedgerCampaigns(string from, string to, string? communityId = null, string? typeId = null, string[]? pointIds = null)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(communityId)) _ = RequireCommunity(communityId);
            if (!string.IsNullOrEmpty(typeId)) _ = RequireCampaignType(typeId);
            return ReadCampaigns(CampaignWhere + LedgerPointSql(pointIds?.Length ?? 0, "c.point_id") + " ORDER BY c.campaign_date,c.rowid;",
                [.. CampaignParameters(new(CommunityId: communityId, TypeId: typeId, From: from, To: to)), .. LedgerPointParameters(pointIds)]);
        }
    }

    public CampaignDetailDto SaveCampaign(string? id, CampaignWriteRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            var existing = id is null ? null : GetCampaign(id);
            if (existing is not null && (request.NewPoint is not null || request.PointId != existing.PointId))
                throw new ApiException(400, "不能更换已保存整治的点位。");
            var type = RequireCampaignType(Text(request.TypeId, "整治类型", 100, true));
            var date = DateText(request.Date, "整治日期");
            string? endDate = null;
            const string organizer = "";
            var fieldSnapshot = existing is not null && existing.TypeId == type.Id
                ? existing.FieldSnapshot ?? [] : type.Fields ?? [];
            var extra = NormalizeFieldValues(fieldSnapshot,
                request.ExtraValues ?? (existing is not null && existing.TypeId == type.Id ? existing.ExtraValues : null),
                request.BeforePhotoIds).GetRawText();
            var actualId = id ?? NewId();
            using (var transaction = _db.BeginTransaction())
            {
                var point = existing is null ? ResolveRecordPoint(request.PointId, request.NewPoint, transaction)
                    : GetPointInternal(existing.PointId, transaction);
                (string Name, object? Value)[] parameters = [("$id", actualId), ("$name", Text(request.Name, "整治名称", 200, true)),
                    ("$point", point.Id), ("$pointName", existing?.PointName ?? point.Name),
                    ("$type", type.Id), ("$typeName", existing is not null && existing.TypeId == type.Id ? existing.TypeName : type.Name),
                    ("$community", existing?.CommunityId ?? point.CommunityId), ("$communityName", existing?.CommunityName ?? point.CommunityName),
                    ("$location", existing?.Location ?? point.Address), ("$date", date), ("$end", endDate),
                    ("$organizer", organizer),
                    ("$participants", ""), ("$people", null), ("$vehicles", null), ("$garbage", null), ("$area", null),
                    ("$content", Text(request.Content, "整治内容", 12000, true)), ("$outcome", Text(request.Outcome, "整治成效", 12000)),
                    ("$notes", Text(request.Notes, "备注", 8000)), ("$extra", extra),
                    ("$snapshot", JsonSerializer.Serialize(fieldSnapshot, CommunityJsonContext.Default.PurposeFieldArray))];
                using (var command = Command(id is null ? """
                    INSERT INTO campaigns(id,name,point_id,point_name,type_id,type_name,community_id,community_name,location,campaign_date,end_date,
                    organizer,participants,participant_count,vehicle_count,garbage_tons,area_sqm,content,outcome,notes,extra_values,field_snapshot)
                    VALUES($id,$name,$point,$pointName,$type,$typeName,$community,$communityName,$location,$date,$end,$organizer,$participants,$people,$vehicles,$garbage,$area,$content,$outcome,$notes,$extra,$snapshot);
                    """ : """
                    UPDATE campaigns SET name=$name,type_id=$type,type_name=$typeName,campaign_date=$date,end_date=$end,
                    organizer=$organizer,participants=$participants,participant_count=$people,vehicle_count=$vehicles,
                    garbage_tons=$garbage,area_sqm=$area,content=$content,outcome=$outcome,notes=$notes,extra_values=$extra,field_snapshot=$snapshot WHERE id=$id;
                    """, transaction, parameters)) RequireChanged(command.ExecuteNonQuery(), "整治记录");
                AttachPhotos(request.BeforePhotoIds, "before", null, null, transaction, campaignId: actualId);
                AttachPhotos(request.ProcessPhotoIds, "process", null, null, transaction, campaignId: actualId);
                AttachPhotos(request.AfterPhotoIds, "after", null, null, transaction, campaignId: actualId);
                transaction.Commit();
            }
            return GetCampaignDetail(actualId);
        }
    }

    public void DeleteCampaign(string id)
    {
        lock (_gate)
        {
            PrepareWrite();
            var campaign = GetCampaign(id);
            var removeTemporary = ShouldRecycleTemporaryPoint(campaign.PointId, campaignId: id);
            var photos = ReadPhotos(campaignId: id).Select(photo => photo.Id).ToList();
            if (removeTemporary) photos.AddRange(ReadPhotos(pointId: campaign.PointId).Select(photo => photo.Id));
            ExecuteWithPhotoDeletion(photos.ToArray(), transaction =>
            {
                using var command = Command("DELETE FROM campaigns WHERE id=$id;", transaction, ("$id", id));
                RequireChanged(command.ExecuteNonQuery(), "整治记录");
                if (removeTemporary) RecycleTemporaryPoint(campaign.PointId, transaction);
            });
        }
    }
}
