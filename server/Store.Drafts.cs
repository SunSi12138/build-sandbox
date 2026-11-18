using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed partial class Store
{
    private void SaveDraftPhotos(string id, JsonElement data, bool archived, SqliteTransaction transaction)
    {
        // The draft JSON keeps the position of each photo. Retain all positions
        // in record_photos, including inspection-type images and temporary-point
        // images, until the draft is submitted or discarded.
        var photos = new Dictionary<string, string>(StringComparer.Ordinal);
        void AddPhoto(string photoId, string phase)
        {
            if (photoId.Length == 0 || !photos.TryAdd(photoId, phase))
                throw new ApiException(400, "同一张照片不能重复放入草稿中的多个位置。");
        }
        void ReadList(JsonElement value)
        {
            if (!value.TryGetProperty("photos", out var items)) return;
            if (items.ValueKind != JsonValueKind.Array) throw new ApiException(400, "草稿照片格式不正确。");
            foreach (var photo in items.EnumerateArray())
            {
                if (photo.ValueKind != JsonValueKind.Object) throw new ApiException(400, "草稿照片格式不正确。");
                AddPhoto(JsonText(photo, "id"), "site");
            }
        }
        void ReadIds(JsonElement parent, string property, string phase)
        {
            if (!parent.TryGetProperty(property, out var items)) return;
            if (items.ValueKind != JsonValueKind.Array) throw new ApiException(400, "草稿字段照片格式不正确。");
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) throw new ApiException(400, "草稿字段照片格式不正确。");
                AddPhoto(item.GetString() ?? "", phase);
            }
        }
        if (!archived)
        {
            ReadList(data);
            if (data.TryGetProperty("issues", out var issues))
            {
                if (issues.ValueKind != JsonValueKind.Array || issues.GetArrayLength() > 50) throw new ApiException(400, "问题列表格式不正确。");
                foreach (var issue in issues.EnumerateArray())
                {
                    if (issue.ValueKind != JsonValueKind.Object) throw new ApiException(400, "问题格式不正确。");
                    ReadList(issue);
                }
            }
            if (data.TryGetProperty("scope", out var scope))
            {
                if (scope.ValueKind != JsonValueKind.Object) throw new ApiException(400, "草稿检查范围格式不正确。");
                ReadIds(scope, "extraPhotoIds", "site");
            }
            if (data.TryGetProperty("newPoint", out var newPoint))
            {
                if (newPoint.ValueKind != JsonValueKind.Object) throw new ApiException(400, "草稿临时场所格式不正确。");
                ReadIds(newPoint, "photoIds", "point");
            }
            if (photos.Count > 500) throw new ApiException(400, "草稿照片过多，请拆分检查记录。");
            foreach (var (photo, phase) in photos)
            {
                using var query = Command("""
                    SELECT p.relative_path FROM photos p WHERE p.id=$photo AND p.phase=$phase AND p.point_id IS NULL
                      AND p.inspection_id IS NULL AND p.issue_id IS NULL AND p.action_id IS NULL AND p.campaign_id IS NULL
                      AND NOT EXISTS(SELECT 1 FROM record_photos r WHERE r.photo_id=p.id AND r.record_id<>$id);
                    """, transaction, ("$photo", photo), ("$phase", phase), ("$id", id));
                if (query.ExecuteScalar() is not string path || !File.Exists(PhotoStorage.ResolvePath(PhotosDir, photo, path)))
                    throw new ApiException(409, "草稿照片已失效或被其他记录使用，请重新上传。");
            }
        }
        using (var clear = Command("DELETE FROM record_photos WHERE record_id=$id;", transaction, ("$id", id))) clear.ExecuteNonQuery();
        foreach (var photo in photos.Keys)
        {
            using var add = Command("INSERT INTO record_photos(record_id,photo_id,active) VALUES($id,$photo,1);", transaction, ("$id", id), ("$photo", photo));
            add.ExecuteNonQuery();
        }
    }

    private void ConsumeSharedDraft(InspectionCreateRequest request, string inspectionId, SqliteTransaction transaction)
    {
        if (string.IsNullOrWhiteSpace(request.DraftId)) return;
        string body;
        using (var query = Command("SELECT body,revision,archived FROM community_records WHERE id=$id AND kind='draft';", transaction, ("$id", request.DraftId)))
        using (var row = query.ExecuteReader())
        {
            if (!row.Read() || row.GetInt32(2) != 0) throw new ApiException(409, "草稿已提交或移除，请刷新草稿箱。");
            CheckRevision(request.DraftRevision, row.GetInt32(1)); body = row.GetString(0);
        }
        using var document = JsonDocument.Parse(body);
        if (JsonText(document.RootElement, "submissionKey") != request.SubmissionKey) throw new ApiException(409, "草稿编号不一致，请重新打开草稿。");
        using (var unlink = Command("DELETE FROM record_photos WHERE record_id=$id;", transaction, ("$id", request.DraftId))) unlink.ExecuteNonQuery();
        using (var close = Command("UPDATE community_records SET archived=1,revision=revision+1,updated_at=$at,body=json_set(body,'$.submittedInspectionId',$inspection) WHERE id=$id;", transaction,
            ("$id", request.DraftId), ("$inspection", inspectionId), ("$at", DateTimeOffset.Now.ToString("O")))) close.ExecuteNonQuery();
        AddCommunityHistory("draft", request.DraftId, "", "{}", "已提交检查", transaction);
    }
}
