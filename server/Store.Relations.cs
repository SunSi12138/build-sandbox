using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed partial class Store
{
    private static string OwnerTable(string kind) => kind switch
    {
        "point" => "points", "inspection" => "inspections", "issue" => "issues", "activity" => "activities", "matter" => "matters",
        _ => throw new ApiException(400, "记录类型无效。")
    };
    private void RequireOwner(string kind, string id, SqliteTransaction? tx = null)
    {
        using var command = Command($"SELECT COUNT(*) FROM {OwnerTable(kind)} WHERE id=$id;", tx, ("$id", id));
        if (Convert.ToInt32(command.ExecuteScalar()) == 0) throw new ApiException(404, "记录不存在。");
    }
    private static string[] RelationIds(string[]? ids, string label, int maximum = 20)
    {
        var values = ids ?? [];
        if (values.Length > maximum || values.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 100) || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new ApiException(400, $"{label}最多 {maximum} 项，不能包含空编号或重复项。");
        return values;
    }
    private const string TagUsage = "(SELECT COUNT(*) FROM point_tags x WHERE x.tag_id=t.id)+(SELECT COUNT(*) FROM inspection_tags x WHERE x.tag_id=t.id)+(SELECT COUNT(*) FROM activity_tags x WHERE x.tag_id=t.id)+(SELECT COUNT(*) FROM matter_tags x WHERE x.tag_id=t.id)";
    public TagDto[] GetTags()
    {
        lock (_gate)
        {
            using var command = Command("SELECT t.id,t.name,t.disabled," + TagUsage + " FROM tags t ORDER BY t.disabled,t.name,t.rowid;");
            using var reader = command.ExecuteReader();
            var tags = new List<TagDto>();
            while (reader.Read()) tags.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2) != 0, reader.GetInt32(3)));
            return tags.ToArray();
        }
    }
    public TagDto SaveTag(string? id, TagWriteRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            var name = Text(request.Name, "标签名称", 40, true);
            using (var duplicate = Command("SELECT COUNT(*) FROM tags WHERE name=$name AND id<>$id;", null, ("$name", name), ("$id", id ?? "")))
                if (Convert.ToInt32(duplicate.ExecuteScalar()) != 0) throw new ApiException(409, "已有同名标签。");
            var actualId = id ?? NewId();
            using var command = Command(id is null ? "INSERT INTO tags(id,name,disabled) VALUES($id,$name,$disabled);" : "UPDATE tags SET name=$name,disabled=$disabled WHERE id=$id;", null,
                ("$id", actualId), ("$name", name), ("$disabled", request.Disabled ? 1 : 0));
            RequireChanged(command.ExecuteNonQuery(), "标签");
            return GetTags().Single(tag => tag.Id == actualId);
        }
    }
    public void DeleteTag(string id)
    {
        lock (_gate)
        {
            PrepareWrite();
            var tag = GetTags().FirstOrDefault(tag => tag.Id == id) ?? throw new ApiException(404, "标签不存在。");
            if (tag.UsageCount > 0) throw new ApiException(409, "标签仍被记录使用，可以停用以保留历史分类。");
            using var command = Command("DELETE FROM tags WHERE id=$id;", null, ("$id", id)); command.ExecuteNonQuery();
        }
    }
    private TagDto[] ReadTags(string kind, string id, SqliteTransaction? tx = null)
    {
        if (kind == "issue") throw new ApiException(400, "问题使用所属排查的标签。");
        _ = OwnerTable(kind);
        using var command = Command($"SELECT t.id,t.name,t.disabled FROM tags t JOIN {kind}_tags r ON r.tag_id=t.id WHERE r.{kind}_id=$id ORDER BY t.name;", tx, ("$id", id));
        using var reader = command.ExecuteReader(); var result = new List<TagDto>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2) != 0, 0));
        return result.ToArray();
    }
    private void ReplaceTags(string kind, string id, string[]? tagIds, SqliteTransaction tx)
    {
        if (tagIds is null) return;
        var ids = RelationIds(tagIds, "标签");
        var previous = ReadTags(kind, id, tx).Select(tag => tag.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var tagId in ids)
        {
            using var query = Command("SELECT disabled FROM tags WHERE id=$id;", tx, ("$id", tagId));
            var value = query.ExecuteScalar();
            if (value is null) throw new ApiException(400, "标签不存在，请重新选择。");
            if (Convert.ToInt32(value) != 0 && !previous.Contains(tagId)) throw new ApiException(400, "已停用的标签不能添加到新记录。");
        }
        using (var clear = Command($"DELETE FROM {kind}_tags WHERE {kind}_id=$id;", tx, ("$id", id))) clear.ExecuteNonQuery();
        foreach (var tagId in ids)
        {
            using var add = Command($"INSERT INTO {kind}_tags({kind}_id,tag_id) VALUES($id,$tag);", tx, ("$id", id), ("$tag", tagId)); add.ExecuteNonQuery();
        }
    }
    public TagDto[] SetTags(string kind, string id, string[] ids)
    {
        lock (_gate)
        {
            PrepareWrite(); RequireOwner(kind, id);
            using var tx = _db.BeginTransaction(); ReplaceTags(kind, id, ids, tx); tx.Commit();
            return ReadTags(kind, id);
        }
    }
    private static string TagFilterSql(string kind, string owner, string[]? ids)
    {
        _ = OwnerTable(kind);
        return string.Concat(RelationIds(ids, "筛选标签").Select((_, i) => $" AND EXISTS(SELECT 1 FROM {kind}_tags tag{i} WHERE tag{i}.{kind}_id={owner} AND tag{i}.tag_id=$tag{i}) "));
    }
    private static (string Name, object? Value)[] TagParameters(string[]? ids) => RelationIds(ids, "筛选标签").Select((id, i) => ($"$tag{i}", (object?)id)).ToArray();
    private MatterLinkDto[] ReadMatterLinks(string kind, string id, SqliteTransaction? tx = null)
    {
        if (kind is not ("inspection" or "issue" or "activity")) throw new ApiException(400, "事项关联类型无效。");
        using var command = Command($"SELECT m.id,m.title,m.status FROM matters m JOIN {kind}_matters r ON r.matter_id=m.id WHERE r.{kind}_id=$id ORDER BY m.received_date,m.rowid;", tx, ("$id", id));
        using var reader = command.ExecuteReader(); var result = new List<MatterLinkDto>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result.ToArray();
    }
    private void ReplaceMatterLinks(string kind, string id, string[]? matterIds, SqliteTransaction tx)
    {
        if (matterIds is null) return;
        if (kind is not ("inspection" or "issue" or "activity")) throw new ApiException(400, "事项关联类型无效。");
        var ids = RelationIds(matterIds, "关联事项");
        foreach (var matterId in ids) RequireOwner("matter", matterId, tx);
        using (var clear = Command($"DELETE FROM {kind}_matters WHERE {kind}_id=$id;", tx, ("$id", id))) clear.ExecuteNonQuery();
        foreach (var matterId in ids)
        {
            using var add = Command($"INSERT INTO {kind}_matters({kind}_id,matter_id) VALUES($id,$matter); UPDATE matters SET status='processing' WHERE id=$matter AND status='pending';", tx, ("$id", id), ("$matter", matterId)); add.ExecuteNonQuery();
        }
        if (kind == "inspection")
        {
            using var source = Command("UPDATE inspections SET source=CASE WHEN $source='matter' THEN 'matter' WHEN source='matter' THEN 'routine' ELSE source END WHERE id=$id;", tx, ("$id", id), ("$source", ids.Length > 0 ? "matter" : "routine")); source.ExecuteNonQuery();
        }
    }
    public MatterLinkDto[] SetMatterLinks(string kind, string id, string[] ids)
    {
        lock (_gate)
        {
            PrepareWrite(); RequireOwner(kind, id);
            using var tx = _db.BeginTransaction(); ReplaceMatterLinks(kind, id, ids, tx); tx.Commit();
            return ReadMatterLinks(kind, id);
        }
    }
    public void SetRelations(string kind, string id, RecordRelationsRequest request)
    {
        lock (_gate)
        {
            PrepareWrite(); RequireOwner(kind, id);
            using var tx = _db.BeginTransaction();
            ReplaceTags(kind, id, request.TagIds, tx);
            ReplaceMatterLinks(kind, id, request.MatterIds, tx);
            tx.Commit();
        }
    }
    private void ReplaceActivityIssues(string activityId, string[]? issueIds, SqliteTransaction tx)
    {
        if (issueIds is null) return;
        var ids = RelationIds(issueIds, "关联问题", 50);
        foreach (var id in ids) RequireOwner("issue", id, tx);
        using (var clear = Command("DELETE FROM activity_issues WHERE activity_id=$id;", tx, ("$id", activityId))) clear.ExecuteNonQuery();
        foreach (var id in ids) { using var add = Command("INSERT INTO activity_issues VALUES($activity,$issue);", tx, ("$activity", activityId), ("$issue", id)); add.ExecuteNonQuery(); }
    }
}
