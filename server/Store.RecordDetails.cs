using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed partial class Store
{
    private void ValidateUnit(string? id, string parent, CommunityWrite request)
    {
        var units = ListCommunityRecords("unit", parent, true);
        var parentUnitId = JsonText(request.Data, "parentUnitId");
        var cursor = parentUnitId;
        var seen = new HashSet<string>();
        while (cursor.Length > 0)
        {
            if (cursor == id || !seen.Add(cursor)) throw new ApiException(400, "楼层、房间不能互相包含。");
            var ancestor = units.FirstOrDefault(x => x.Id == cursor) ?? throw new ApiException(400, "上级单元不属于这个场所。");
            if (ancestor.Archived && !request.Archived) throw new ApiException(400, "上级单元已停用，请先恢复。");
            cursor = JsonText(ancestor.Data, "parentUnitId");
        }
        var area = JsonText(request.Data, "area");
        if (area.Length > 0 && (!decimal.TryParse(area, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) || number < 0)) throw new ApiException(400, "面积请填写大于等于零的数字。");
        if (request.Archived && id is not null)
        {
            if (units.Any(x => !x.Archived && JsonText(x.Data, "parentUnitId") == id)) throw new ApiException(409, "请先停用下面的房间或分区。");
            if (ListCommunityRecords("use", parent).Any(x => JsonText(x.Data, "unitId") == id && JsonText(x.Data, "state") != "已结束")) throw new ApiException(409, "这个单元还有使用记录，请先登记停业、退租或调整所在单元。");
        }
    }

    private void SaveRecordPhotos(string id, JsonElement data, SqliteTransaction transaction)
    {
        if (!data.TryGetProperty("photoIds", out var value)) return;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 20) throw new ApiException(400, "最多保存20张照片。");
        var ids = new HashSet<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(item.GetString()) || !ids.Add(item.GetString()!)) throw new ApiException(400, "照片编号重复或无效。");
            var photoId = item.GetString()!;
            using var query = Command("""
                SELECT p.relative_path FROM photos p
                WHERE p.id=$photo AND p.phase='point' AND p.point_id IS NULL AND p.inspection_id IS NULL
                  AND p.issue_id IS NULL AND p.action_id IS NULL AND p.campaign_id IS NULL
                  AND NOT EXISTS(SELECT 1 FROM record_photos r WHERE r.photo_id=p.id AND r.record_id<>$record);
                """, transaction, ("$photo", photoId), ("$record", id));
            if (query.ExecuteScalar() is not string path || !File.Exists(PhotoStorage.ResolvePath(PhotosDir, photoId, path))) throw new ApiException(409, "照片已被其他记录使用或已丢失，请重新上传。");
        }
        // Removed photos stay referenced for historical records and full backups.
        using (var clear = Command("UPDATE record_photos SET active=0 WHERE record_id=$id;", transaction, ("$id", id))) clear.ExecuteNonQuery();
        foreach (var photo in ids)
        {
            using var add = Command("INSERT INTO record_photos(record_id,photo_id,active) VALUES($record,$photo,1) ON CONFLICT(record_id,photo_id) DO UPDATE SET active=1;", transaction, ("$record", id), ("$photo", photo));
            add.ExecuteNonQuery();
        }
    }

    public PhotoDto[] GetRecordPhotos(string id)
    {
        lock (_gate)
        {
            using var command = Command($"SELECT {PhotoColumns} FROM photos p JOIN record_photos r ON r.photo_id=p.id WHERE r.record_id=$id AND r.active=1 ORDER BY p.uploaded_at,p.rowid;", null, ("$id", id));
            using var row = command.ExecuteReader();
            var result = new List<PhotoDto>();
            while (row.Read()) result.Add(new(row.GetString(0), row.GetInt32(1), row.GetInt32(2), row.GetString(3), "/photos/" + row.GetString(0), row.GetString(4), null, null, null, null, null, row.GetInt32(10) != 0));
            return [.. result];
        }
    }
}
