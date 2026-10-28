using Microsoft.Data.Sqlite;
namespace StreetEcoTool;
public sealed partial class Store
{
    public IssueTypeDto[] GetIssueTypes()
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT id,name FROM issue_types ORDER BY rowid;");
            using var reader = cmd.ExecuteReader();
            var result = new List<IssueTypeDto>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1)));
            return result.ToArray();
        }
    }
    private IssueTypeDto RequireIssueType(string? id, SqliteTransaction? transaction = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ApiException(400, "请选择问题类型。");
        using var cmd = Command("SELECT id,name FROM issue_types WHERE id=$id;", transaction, ("$id", id));
        using var row = cmd.ExecuteReader();
        if (!row.Read()) throw new ApiException(400, "问题类型不存在，请重新选择。");
        return new(row.GetString(0), row.GetString(1));
    }
    public IssueTypeDto SaveIssueType(string? id, IssueTypeWriteRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            var name = Text(request.Name, "问题类型", 100, true);
            using (var duplicate = Command("SELECT COUNT(*) FROM issue_types WHERE name=$name AND id<>$id;", null, ("$name", name), ("$id", id ?? "")))
                if (Convert.ToInt32(duplicate.ExecuteScalar()) > 0) throw new ApiException(409, "已有同名问题类型。");
            var actualId = id ?? NewId();
            using var command = Command(id is null ? "INSERT INTO issue_types(id,name) VALUES($id,$name);" : "UPDATE issue_types SET name=$name WHERE id=$id;", null, ("$id", actualId), ("$name", name));
            RequireChanged(command.ExecuteNonQuery(), "问题类型");
            return new(actualId, name);
        }
    }
    public void DeleteIssueType(string id)
    {
        lock (_gate)
        {
            PrepareWrite();
            using (var used = Command("SELECT COUNT(*) FROM issues WHERE issue_type_id=$id;", null, ("$id", id)))
                if (Convert.ToInt32(used.ExecuteScalar()) > 0) throw new ApiException(409, "该类型已被问题引用，不能删除。");
            using var cmd = Command("DELETE FROM issue_types WHERE id=$id;", null, ("$id", id));
            RequireChanged(cmd.ExecuteNonQuery(), "问题类型");
        }
    }
}
