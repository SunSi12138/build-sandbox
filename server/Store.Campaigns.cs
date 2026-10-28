using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

// Typed projections used by the specialized remediation form/exporter. All records
// and photos are owned by activities; there is no separate campaign persistence.
public sealed partial class Store
{
    public CampaignTypeDto[] GetCampaignTypes()
    {
        lock (_gate)
        {
            using var command = Command("SELECT id,name FROM remediation_types ORDER BY rowid;");
            using var rows = command.ExecuteReader(); var result = new List<CampaignTypeDto>();
            while (rows.Read()) result.Add(new(rows.GetString(0), rows.GetString(1)));
            return result.ToArray();
        }
    }
    private CampaignTypeDto RequireCampaignType(string id, SqliteTransaction? tx = null)
    {
        using var command = Command("SELECT id,name FROM remediation_types WHERE id=$id;", tx, ("$id", id));
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new ApiException(400, "整治类别不存在。");
        return new(row.GetString(0), row.GetString(1));
    }
    public CampaignTypeDto SaveCampaignType(string? id, CampaignTypeWriteRequest request)
    {
        lock (_gate)
        {
            PrepareWrite(); var name = Text(request.Name, "整治类别", 100, true);
            using (var duplicate = Command("SELECT COUNT(*) FROM remediation_types WHERE name=$name AND id<>$id;", null, ("$name", name), ("$id", id ?? "")))
                if (Convert.ToInt32(duplicate.ExecuteScalar()) > 0) throw new ApiException(409, "已有同名整治类别。");
            var actualId = id ?? NewId();
            using var command = Command(id is null ? "INSERT INTO remediation_types(id,name) VALUES($id,$name);" : "UPDATE remediation_types SET name=$name WHERE id=$id;", null, ("$id", actualId), ("$name", name));
            RequireChanged(command.ExecuteNonQuery(), "整治类别"); return new(actualId, name);
        }
    }
    public void DeleteCampaignType(string id)
    {
        lock (_gate)
        {
            PrepareWrite();
            using (var used = Command("SELECT COUNT(*) FROM activity_remediations WHERE type_id=$id;", null, ("$id", id)))
                if (Convert.ToInt32(used.ExecuteScalar()) > 0) throw new ApiException(409, "该类别已被整治活动引用，不能删除。");
            using var command = Command("DELETE FROM remediation_types WHERE id=$id;", null, ("$id", id)); RequireChanged(command.ExecuteNonQuery(), "整治类别");
        }
    }
    private static CampaignDto AsCampaign(ActivityDto activity)
    {
        var detail = activity.Remediation ?? throw new ApiException(400, "该活动使用普通活动模板。");
        return new(activity.Id, activity.Name, detail.TypeId, detail.TypeName, activity.CommunityId!, activity.CommunityName, activity.Location,
            activity.Date, activity.EndDate, activity.Organizer, activity.Participants, activity.ParticipantCount, detail.VehicleCount, detail.GarbageTons,
            detail.AreaSqm, activity.Content, activity.Outcome, activity.Notes, activity.PhotoCount, activity.PointId!, activity.PointName, activity.PointTemporary);
    }
    public CampaignDto GetCampaign(string id) { lock (_gate) return AsCampaign(GetActivity(id)); }
    public CampaignDetailDto GetCampaignDetail(string id) { lock (_gate) return new(GetCampaign(id), ReadPhotos(activityId: id)); }
    public PageDto<CampaignDto> ListCampaigns(CampaignQuery query)
    {
        lock (_gate)
        {
            var page = ListActivities(new(query.Q, query.CommunityId, From: query.From, To: query.To, Page: query.Page, PageSize: query.PageSize,
                PointId: query.PointId, Template: "remediation", RemediationTypeId: query.TypeId, TagIds: query.TagIds, MatterId: query.MatterId));
            return new(page.Items.Select(AsCampaign).ToArray(), page.Total, page.Page, page.PageSize);
        }
    }
    public CampaignDto[] GetLedgerCampaigns(string from, string to, string? communityId = null, string? typeId = null, string[]? pointIds = null, CampaignQuery? filters = null)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(typeId)) _ = RequireCampaignType(typeId);
            var query = new ActivityQuery(Q: filters?.Q, PointId: filters?.PointId, Template: "remediation", RemediationTypeId: typeId, TagIds: filters?.TagIds, MatterId: filters?.MatterId);
            return GetLedgerActivities(from, to, communityId, filters?.ActivityTypeId, query, pointIds).Select(AsCampaign).ToArray();
        }
    }
    public CampaignDetailDto SaveCampaign(string? id, CampaignWriteRequest request)
    {
        lock (_gate)
        {
            var type = GetActivityTypes().Single(item => item.Template == "remediation");
            var result = SaveActivity(id, new(request.Name, type.Id, request.Date, request.Content, EndDate: request.EndDate,
                Organizer: request.Organizer, Participants: request.Participants, ParticipantCount: request.ParticipantCount, Outcome: request.Outcome, Notes: request.Notes,
                PointId: request.PointId, NewPoint: request.NewPoint, Remediation: new(request.TypeId, request.VehicleCount, request.GarbageTons, request.AreaSqm, request.BeforePhotoIds, request.ProcessPhotoIds, request.AfterPhotoIds)));
            return new(AsCampaign(result.Activity), result.Photos);
        }
    }
    public void DeleteCampaign(string id) { lock (_gate) { _ = GetCampaign(id); DeleteActivity(id); } }
}
