namespace StreetEcoTool;

public sealed partial class Store
{
    // Change only the parent association and its denormalized display fields.
    // Issue actions, photos, matter links, tags and recorded site geometry are historical evidence.
    public InspectionDetailDto ReassignInspectionPoint(string id, PointReassignRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            var existing = GetInspection(id);
            var targetId = Text(request.PointId, "目标点位", 100, true);
            if (existing.PointId == targetId) throw new ApiException(400, "目标点位与当前点位相同。");
            using var transaction = _db.BeginTransaction();
            var target = GetPointInternal(targetId, transaction);
            if (target.Disabled) throw new ApiException(409, "目标点位已停用，请先启用。");
            var community = RequireCommunity(target.CommunityId, transaction);
            using (var update = Command("""
                UPDATE inspections SET point_id=$point,point_name=$name,
                    community_id=$community,community_name=$communityName,community_title=$communityTitle,
                    type_id=$type,type_name=$typeName,address=$address,contact=$contact,phone=$phone
                WHERE id=$id AND point_id=$previous;
                """, transaction,
                ("$id", id), ("$previous", existing.PointId), ("$point", target.Id),
                ("$name", target.Name), ("$community", target.CommunityId),
                ("$communityName", target.CommunityName), ("$communityTitle", community.Title),
                ("$type", target.TypeId), ("$typeName", target.TypeName), ("$address", target.Address),
                ("$contact", target.Contact), ("$phone", target.Phone)))
                RequireChanged(update.ExecuteNonQuery(), "排查记录");
            transaction.Commit();
            return GetInspectionDetail(id);
        }
    }

    public ActivityDetailDto ReassignActivityPoint(string id, PointReassignRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            var existing = GetActivity(id);
            var targetId = Text(request.PointId, "目标点位", 100, true);
            if (existing.PointId == targetId) throw new ApiException(400, "目标点位与当前点位相同。");
            using var transaction = _db.BeginTransaction();
            var target = GetPointInternal(targetId, transaction);
            if (target.Disabled) throw new ApiException(409, "目标点位已停用，请先启用。");
            // Ordinary activities have an independently entered location; preserve it.
            // Remediation locations were originally filled from their points.
            using (var update = Command("""
                UPDATE activities SET point_id=$point,point_name=$name,
                    community_id=$community,community_name=$communityName,location=$location
                WHERE id=$id;
                """, transaction, ("$id", id), ("$point", target.Id), ("$name", target.Name),
                ("$community", target.CommunityId), ("$communityName", target.CommunityName),
                ("$location", existing.Template == "remediation" ? target.Address : existing.Location)))
                RequireChanged(update.ExecuteNonQuery(), "活动记录");
            transaction.Commit();
            return GetActivityDetail(id);
        }
    }
}
