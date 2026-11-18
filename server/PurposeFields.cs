using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StreetEcoTool;

public sealed partial class Store
{
    private static PurposeField[] ReadPurposeFields(JsonElement data) => data.TryGetProperty("fields", out var fields)
        ? fields.ValueKind == JsonValueKind.Array ? JsonSerializer.Deserialize(fields, CommunityJsonContext.Default.PurposeFieldArray) ?? [] : throw new ApiException(400, "字段格式不正确。") : [];

    private static void ValidatePurposeFields(JsonElement data) => ValidateFieldDefinitions(ReadPurposeFields(data));

    private static void ValidateFieldDefinitions(PurposeField[] fields)
    {
        if (fields.Length > 40 || fields.Select(field => field.Id).Distinct().Count() != fields.Length)
            throw new ApiException(400, "字段最多40项，编号不能重复。");
        foreach (var field in fields)
        {
            if (!Regex.IsMatch(field.Id, "^[a-zA-Z0-9_-]{1,60}$")) throw new ApiException(400, "字段编号不正确。");
            _ = Text(field.Label, "字段名称", 60, true);
            if (field.Type is not ("text" or "number" or "image" or "tags"))
                throw new ApiException(400, "字段类型只能是数字、文本、图片或标签。");
            if (field.Multiline && field.Type != "text") throw new ApiException(400, "仅文本字段支持多行。");
        }
        if (fields.Select(f => f.Label.Trim()).Distinct().Count() != fields.Length)
            throw new ApiException(400, "字段名称不能重复。");
    }

    private static JsonElement NormalizeFieldValues(PurposeField[] fields, JsonElement? input, IEnumerable<string>? photoIds = null)
    {
        ValidateFieldDefinitions(fields);
        var node = new JsonObject();
        var supplied = input is { ValueKind: JsonValueKind.Object };
        foreach (var field in fields)
        {
            var present = supplied && input!.Value.TryGetProperty(field.Id, out _);
            var value = present ? input!.Value.GetProperty(field.Id).ToString().Trim() : "";
            if (field.Required && value.Length == 0) throw new ApiException(400, $"请填写{field.Label}。");
            if (value.Length == 0) continue;
            if (field.Type == "number")
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                    throw new ApiException(400, $"{field.Label}请填写数字。");
                node[field.Id] = number;
            }
            else if (field.Type == "image")
            {
                if (!Regex.IsMatch(value, "^[a-zA-Z0-9_-]{1,100}$") || !(photoIds ?? []).Contains(value))
                    throw new ApiException(400, $"{field.Label}的图片没有关联到本条记录。");
                node[field.Id] = value;
            }
            else if (field.Type == "tags")
            {
                var tags = string.Join(" ", Regex.Split(value, @"[\s，,、；;]+").Where(x => x.Length > 0).Distinct());
                node[field.Id] = Text(tags, field.Label, 4000);
            }
            else node[field.Id] = Text(value, field.Label, field.Multiline ? 12000 : 4000);
        }
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private JsonElement PrepareUseData(JsonElement data)
    {
        var purpose = ListCommunityRecords("purpose-type", includeArchived: true)
            .FirstOrDefault(x => x.Id == JsonText(data, "purposeId")) ?? throw new ApiException(400, "请选择有效用途。");
        var fields = ReadPurposeFields(purpose.Data);
        var node = JsonNode.Parse(data.GetRawText())!.AsObject();
        var ids = data.TryGetProperty("photoIds", out var photos) && photos.ValueKind == JsonValueKind.Array
            ? photos.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToArray() : [];
        var input = data.TryGetProperty("values", out var values) ? values : (JsonElement?)null;
        var normalized = NormalizeFieldValues(fields, input, ids);
        node["values"] = JsonNode.Parse(normalized.GetRawText());
        node["fieldSnapshot"] = JsonNode.Parse(JsonSerializer.Serialize(fields, CommunityJsonContext.Default.PurposeFieldArray));
        using var result = JsonDocument.Parse(node.ToJsonString());
        return result.RootElement.Clone();
    }

    public static (string Label, string PhotoId)[] UseFieldImages(JsonElement data)
    {
        if (!data.TryGetProperty("fieldSnapshot", out var fields) || fields.ValueKind != JsonValueKind.Array ||
            !data.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object) return [];
        return fields.EnumerateArray()
            .Where(field => JsonText(field, "type") == "image")
            .Select(field => (Label: JsonText(field, "label"),
                PhotoId: values.TryGetProperty(JsonText(field, "id"), out var value) ? value.ToString() : ""))
            .Where(x => x.PhotoId.Length > 0).ToArray();
    }

    public static (string Label, string Value)[] UseFieldValues(JsonElement data)
    {
        if (!data.TryGetProperty("fieldSnapshot", out var fields) || fields.ValueKind != JsonValueKind.Array ||
            !data.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object) return [];
        return fields.EnumerateArray()
            .Where(field => JsonText(field, "type") != "image")
            .Select(field => (Label: JsonText(field, "label"),
                Value: values.TryGetProperty(JsonText(field, "id"), out var value) ? value.ToString() : ""))
            .Where(x => x.Value.Length > 0).ToArray();
    }
}
